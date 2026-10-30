import datetime
import sys
import unittest
from pathlib import Path
from unittest.mock import patch
from urllib.parse import parse_qs, urlsplit
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import bridge

CONFIG = {'tv': 'http://192.168.1.20:8080', 'torrserve': 'http://192.168.1.10:8090', 'svp_tv': 'http://192.168.1.10:9900', 'svp_local': 'http://127.0.0.1:9900', 'manager': 'C:\\SVP\\SVPManager.exe', 'lan_subnet': '192.168.1.0/24', 'enabled': True}

class BridgeTests(unittest.TestCase):
    def test_open_with_spaces_and_html(self):
        line = '02:54:32.138 [I]: Open: http://192.168.1.10:8090/stream/Mission Impossible [HEVC].mkv?link=x&amp;index=1&play'
        key, url = bridge.latest_open({'list': ['noise', line]})
        self.assertTrue(key)
        self.assertEqual(url, 'http://192.168.1.10:8090/stream/Mission Impossible [HEVC].mkv?link=x&index=1&play')
        self.assertEqual(bridge.latest_open({'list': ['noise']}), (None, None))
        nested = parse_qs(urlsplit(bridge.native_cast_url(url, CONFIG)).query)['url'][0]
        self.assertIn('Mission%20Impossible%20%5BHEVC%5D.mkv?link=x&index=1&play', nested)
    def test_midnight(self):
        now = datetime.datetime(2026, 10, 30, 0, 0, 3).timestamp()
        found = bridge.latest_open_time({'list': ['23:59:59.100 [I]: Open: http://x']}, now)
        self.assertAlmostEqual(now - found, 4)
    def test_source_host(self):
        self.assertTrue(bridge.is_source('http://192.168.1.10:8090/stream/file', CONFIG))
        self.assertFalse(bridge.is_source('http://192.168.1.11:8090/stream/file', CONFIG))
        self.assertFalse(bridge.is_source('http://192.168.1.10:8090/streaming/file', CONFIG))
        self.assertFalse(bridge.is_source('http://user@192.168.1.10:8090/stream/file', CONFIG))
        with self.assertRaises(ValueError): bridge.native_cast_url('http://evil.example/stream/file', CONFIG)
    def test_config_and_lan_fail_closed(self):
        bridge.validate_config(CONFIG)
        with self.assertRaisesRegex(ValueError, 'lan_subnet'): bridge.validate_config({k:v for k,v in CONFIG.items() if k != 'lan_subnet'})
        with self.assertRaisesRegex(ValueError, 'manager'): bridge.validate_config({k:v for k,v in CONFIG.items() if k != 'manager'})
        with self.assertRaises(ValueError): bridge.validate_config(dict(CONFIG, tv='http://10.3.0.1:8080'))
        with self.assertRaises(ValueError): bridge.validate_config(dict(CONFIG, poll_seconds=0))
    def test_route_not_at_start_and_checked_before_use(self):
        with patch.object(bridge, 'best_route', side_effect=[1, 2]) as route:
            bridge.validate_config(CONFIG)
            route.assert_not_called()
            with self.assertRaisesRegex(ValueError, 'VPN'): bridge.lan_policy(CONFIG, check_route=True)
    def test_health_throttled_but_changes_written(self):
        import tempfile
        from pathlib import Path
        with tempfile.TemporaryDirectory() as tmp, patch.object(bridge, 'ROOT', Path(tmp)), patch.object(bridge.time, 'monotonic', side_effect=[100, 101, 101, 111, 111, 112]):
            b=bridge.Bridge(CONFIG)
            b.health(True, 'ready'); first=(Path(tmp)/'health.json').read_bytes()
            b.health(True, 'ready'); self.assertEqual(first, (Path(tmp)/'health.json').read_bytes())
            b.health(True, 'ready'); b.health(False, 'error')
            self.assertIn(b'error', (Path(tmp)/'health.json').read_bytes())

if __name__ == '__main__': unittest.main()
