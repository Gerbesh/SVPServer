"""Local ByLampa -> native SVPlayer CastManager hand-off. Standard library only."""
from __future__ import annotations

import argparse
import ctypes
import datetime
import hashlib
import html
import ipaddress
import json
import logging
import os
import sys
from logging.handlers import RotatingFileHandler
from pathlib import Path
import re
import subprocess
import time
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parent
LOG = logging.getLogger("svp-tv-bridge")
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def request(base, path, data=None, timeout=6):
    body = urllib.parse.urlencode(data).encode() if data is not None else None
    req = urllib.request.Request(base + path, data=body)
    with OPENER.open(req, timeout=timeout) as response:
        return json.load(response)


def latest_open(reply):
    for line in reversed(reply.get("list", [])):
        match = re.search(r"\[I\]: Open: ((?:https?|cast)://[^\r\n]+)", line)
        if match:
            return hashlib.sha256(line.encode()).hexdigest(), html.unescape(match[1].strip())
    return None, None


def latest_open_time(reply, now=None):
    now = time.time() if now is None else now
    for line in reversed(reply.get("list", [])):
        if "[I]: Open: " not in line:
            continue
        match = re.search(r"(\d{2}:\d{2}:\d{2})(?:\.\d+)? \[I\]: Open:", line)
        if not match:
            return None
        clock = datetime.datetime.strptime(match[1], "%H:%M:%S").time()
        day = datetime.datetime.fromtimestamp(now).date()
        candidates = [datetime.datetime.combine(day + datetime.timedelta(days=n), clock).timestamp()
                      for n in (-1, 0, 1)]
        return min(candidates, key=lambda value: abs(value - now))
    return None


def is_source(url, config):
    if not url:
        return False
    parsed = urllib.parse.urlsplit(url)
    source = urllib.parse.urlsplit(config["torrserve"])
    return (parsed.scheme in ("http", "https") and
            parsed.hostname == source.hostname and parsed.port == source.port and
            parsed.path.startswith("/stream"))


def best_route(address):
    ip = ipaddress.ip_address(address)
    index = ctypes.c_uint32()
    function = ctypes.WinDLL("iphlpapi").GetBestInterface
    function.argtypes = (ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32))
    function.restype = ctypes.c_uint32
    result = function(int.from_bytes(ip.packed, sys.byteorder), ctypes.byref(index))
    if result:
        raise OSError(result, "No direct LAN route to " + address)
    return index.value


def lan_policy(config, check_route=False):
    if not config.get("lan_only", False):
        return
    subnet = ipaddress.ip_network(config["lan_subnet"])
    hosts = {}
    for name in ("tv", "torrserve", "svp_tv"):
        host = urllib.parse.urlsplit(config[name]).hostname
        address = ipaddress.ip_address(host)
        if address.version != 4 or address not in subnet or not address.is_private:
            raise ValueError(name + " must use the direct local subnet " + str(subnet))
        hosts[name] = host
    if hosts["torrserve"] != hosts["svp_tv"]:
        raise ValueError("TorrServe and SVPcast must use this PC's same LAN address")
    local_host = urllib.parse.urlsplit(config["svp_local"]).hostname
    if local_host not in ("127.0.0.1", "::1", hosts["svp_tv"]):
        raise ValueError("The local SVPcast API must use loopback or the PC LAN IP")
    if check_route and best_route(hosts["tv"]) != best_route(hosts["svp_tv"]):
        raise ValueError("TV route is not on the PC LAN interface; VPN fallback refused")


def native_cast_url(source, config):
    """Enter SVPlayer's native CastManager, not its plain HLS player.

    SVPlayer Lite 1.7.1 reads the cast URI host/port and the `url` query item.
    Its CastManager sends the original URL to SVPcast and owns seeking,
    stream restarts, audio selection and the full movie timeline.
    URI compatibility is established by inspecting the installed official APK;
    a real-device playback/seek check is required before enabling this helper.
    """
    lan_policy(config)
    if not is_source(source, config):
        raise ValueError("Only the configured TorrServe source is accepted")
    server = urllib.parse.urlsplit(config["svp_tv"])
    if server.scheme != "http" or not server.hostname or not server.port:
        raise ValueError("SVPcast requires an explicit HTTP host and port")
    if server.username or server.password or server.query or server.fragment:
        raise ValueError("Unexpected SVPcast address components")
    host = server.hostname
    if ":" in host:
        host = "[" + host + "]"
    source = urllib.parse.quote(source, safe=":/?#@!$&'()*+,;=%")
    query = urllib.parse.urlencode({"url": source}, quote_via=urllib.parse.quote)
    return urllib.parse.urlunsplit(("cast", f"{host}:{server.port}", "/", query, ""))


class Bridge:
    def __init__(self, config):
        self.config = config
        self.tv = config["tv"]
        self.pc = config["svp_local"]
        self.state_file = ROOT / "state.json"
        self.last_key = None
        self.last_error = None
        self.started_at = time.time()
        self.opened_at = None

    def remember(self, key, **extra):
        self.last_key = key
        state = {"last_event": key, "updated": time.strftime("%Y-%m-%d %H:%M:%S"), **extra}
        temp = self.state_file.with_suffix(".tmp")
        temp.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
        temp.replace(self.state_file)

    def health(self, reachable, phase):
        value = {"pid": os.getpid(), "heartbeat": time.time(),
                 "updated": time.strftime("%Y-%m-%d %H:%M:%S"),
                 "tv_reachable": reachable, "phase": phase,
                 "last_error": self.last_error}
        target = ROOT / "health.json"
        tmp = target.with_suffix(".tmp")
        tmp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
        tmp.replace(target)

    def current(self):
        lan_policy(self.config, check_route=True)
        reply = request(self.tv, "/r/log?last=0", timeout=3)
        self.opened_at = latest_open_time(reply)
        return latest_open(reply)

    def ensure_manager(self):
        try:
            request(self.pc, "/r/state", timeout=2)
            return
        except Exception:
            pass
        LOG.info("Starting installed SVP Manager")
        subprocess.Popen([self.config["manager"]], cwd=str(Path(self.config["manager"]).parent),
                         creationflags=subprocess.CREATE_NO_WINDOW,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(20):
            if (ROOT / "stop.flag").exists():
                raise RuntimeError("Stopped")
            time.sleep(2)
            try:
                request(self.pc, "/r/state", timeout=2)
                return
            except Exception:
                continue
        raise RuntimeError("SVPcast unavailable; enable its Web UI in SVP Manager")

    def handoff(self, key, source):
        lan_policy(self.config, check_route=True)
        self.ensure_manager()
        # Do not switch back to a stale video after a slow Manager startup.
        now_key, _ = self.current()
        if now_key != key or (ROOT / "stop.flag").exists():
            LOG.info("Cancelled hand-off: source changed or helper stopped")
            return False
        url = native_cast_url(source, self.config)
        result = request(self.tv, "/r/play?" + urllib.parse.urlencode({"url": url}))
        if not result.get("ok"):
            raise RuntimeError("TV rejected native SVPcast URI")
        title = urllib.parse.unquote(urllib.parse.urlsplit(source).path.rsplit("/", 1)[-1])
        self.remember(key, status="native_requested", title=title)
        # An accepted /r/play request does not prove interpolation or HW decode.
        LOG.info("Native CastManager requested; playback verification pending: %s", title)
        return True

    def run(self):
        primed = False
        # Attaching the helper must leave an already playing movie alone.
        try:
            key, _ = self.current()
            self.remember(key, status="ready")
            primed = True
        except Exception:
            try:
                self.last_key = json.loads(self.state_file.read_text(encoding="utf-8"))["last_event"]
            except Exception:
                pass
        LOG.info("Ready; native CastManager hand-off for NEW TorrServe playback only")
        while not (ROOT / "stop.flag").exists():
            try:
                key, source = self.current()
                self.health(True, "waiting_for_new_video")
                if not primed:
                    primed = True
                    # A player appearing long after helper startup is a NEW launch.
                    # Protect genuinely pre-existing playback, not the first movie
                    # opened later while the player/TV had been offline.
                    if self.opened_at is None or self.opened_at <= self.started_at + 5:
                        self.remember(key, status="ready")
                        continue
                if key and key != self.last_key:
                    self.remember(key, status="observed")
                    if is_source(source, self.config):
                        for attempt in range(2):
                            try:
                                if self.handoff(key, source):
                                    break
                                break
                            except Exception:
                                self.last_error = time.strftime("%H:%M:%S") + " " + str(sys.exc_info()[1])
                                self.health(True, "handoff_error")
                                LOG.exception("Hand-off attempt %s failed", attempt + 1)
                                time.sleep(3)
                                if self.current()[0] != key:
                                    break
            except Exception:
                # TV off or player closed is an expected idle state.
                self.health(False, "waiting_for_player")
            time.sleep(self.config.get("poll_seconds", 2))
        self.health(False, "stopped")
        LOG.info("Helper stopped; current playback left unchanged")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--stop", action="store_true")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    if args.stop:
        (ROOT / "stop.flag").touch()
        return
    config = json.loads((ROOT / "config.json").read_text(encoding="utf-8"))
    if args.check:
        try:
            print("TV:", bool(request(config["tv"], "/r/log?last=0").get("ok")))
        except Exception:
            print("TV: player closed/offline; helper will wait for it")
        print("SVPcast:", request(config["svp_local"], "/r/state").get("state"))
        return
    if not config.get("enabled", False):
        print("Disabled pending real-device playback/seek verification; current movie left alone")
        return
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.argtypes = (ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p)
    kernel.CreateMutexW.restype = ctypes.c_void_p
    ctypes.set_last_error(0)
    mutex = kernel.CreateMutexW(None, False, "Local\\SVP-TV-Bridge")
    if not mutex or ctypes.get_last_error() == 183:
        return
    (ROOT / "stop.flag").unlink(missing_ok=True)
    handler = RotatingFileHandler(ROOT / "bridge.log", maxBytes=1000000, backupCount=2, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    LOG.addHandler(handler)
    LOG.setLevel(logging.INFO)
    (ROOT / "pid.json").write_text(json.dumps({"pid": os.getpid(), "started": time.time()}), encoding="utf-8")
    try:
        Bridge(config).run()
    finally:
        (ROOT / "pid.json").unlink(missing_ok=True)
        kernel.CloseHandle.argtypes = (ctypes.c_void_p,)
        kernel.CloseHandle(mutex)


if __name__ == "__main__":
    main()
