using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

static class Data {
    public static readonly string Root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));
    public static string ConfigPath(string key,string fallback) { return S(Read(Path.Combine(Root,"config.json")),key,fallback); }
    public static string Server { get { return ConfigPath("torrserver_dir",@"G:\TorrServer"); } }
    public static string ServerExe { get { return ConfigPath("torrserver_exe",Path.Combine(Server,"TorrServer-windows-amd64.exe")); } }
    public static string ServerData { get { return ConfigPath("torrserver_data",Path.Combine(Server,"data-141")); } }
    public static string Python { get { return ConfigPath("pythonw",@"G:\SVP\mpv64\pythonw.exe"); } }
    public static Dictionary<string,object> Map(object o) { return o as Dictionary<string,object> ?? new Dictionary<string,object>(); }
    public static object Get(object o,string key) { object v; return Map(o).TryGetValue(key,out v)?v:null; }
    public static string S(object o,string key,string fallback="") { object v=Get(o,key); return v==null?fallback:Convert.ToString(v); }
    public static double N(object o,string key) {
        object raw=Get(o,key);if(raw==null)return 0;
        if(!(raw is string)){try{return Convert.ToDouble(raw,System.Globalization.CultureInfo.InvariantCulture);}catch{return 0;}}
        double v;return double.TryParse((string)raw,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out v)?v:0;
    }
    public static object Parse(string s) { try { return new JavaScriptSerializer().DeserializeObject(s); } catch { return null; } }
    public static object Read(string p) {
        try{using(var f=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var r=new StreamReader(f,Encoding.UTF8,true))return Parse(r.ReadToEnd());}catch{return null;}
    }
    public static string Tail(string path,int lines=150) {
        try {
            using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                bool cut=f.Length>65536; if(cut) f.Seek(-65536,SeekOrigin.End);
                using(var r=new StreamReader(f,Encoding.UTF8,true)) {
                    var a=r.ReadToEnd().Replace("\r","").Split('\n');
                    return string.Join(Environment.NewLine,a.Skip(Math.Max(cut?1:0,a.Length-lines)));
                }
            }
        } catch { return "Журнал пока недоступен: "+path; }
    }
    public static void Event(string s) { try { File.AppendAllText(Path.Combine(Root,"dashboard.log"),DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+"  "+s+Environment.NewLine,Encoding.UTF8); } catch {} }
    public static string Clock(double n) { return TimeSpan.FromSeconds(Math.Max(0,n)).ToString(@"hh\:mm\:ss"); }
    public static string Size(double b) { return (b/1048576).ToString("N0")+" МБ"; }
}

class Reply { public bool Ok; public object Json; public string Text="",Error=""; }
class ProcMetric { public string Name; public int Count; public double Cpu,Bytes; }
class Metrics {
    public double Cpu,RamUsed,RamTotal,Down,Up;
    public string GpuName="NVIDIA недоступна";
    public double Gpu,Encoder,Decoder,VramUsed,VramTotal,Temp;
    public bool GpuAvailable;
    public List<ProcMetric> Processes=new List<ProcMetric>();
}
class Snapshot {
    public Reply Server,Cast,Progress,TV,Torrents;
    public Metrics Metrics;
    public object Health,HelperState;
    public bool HelperAlive;
    public string[] Logs;
}

class MonitorBackend : IDisposable {
    readonly HttpClient http=new HttpClient(new HttpClientHandler{UseProxy=false});
    readonly Dictionary<int,Tuple<DateTime,double>> samples=new Dictionary<int,Tuple<DateTime,double>>();
    ulong oldIdle,oldKernel,oldUser;
    long oldRx,oldTx; DateTime netTime=DateTime.UtcNow;
    Metrics gpuCache=new Metrics(); DateTime gpuTime=DateTime.MinValue;
    public string TorrUrl,CastUrl,TVUrl,ManagerExe;
    [StructLayout(LayoutKind.Sequential)] class Mem { public uint length=64,load; public ulong total,avail,pageTotal,pageAvail,virtTotal,virtAvail,ext; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In,Out] Mem m);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle,out long kernel,out long user);
    public MonitorBackend() {
        http.Timeout=TimeSpan.FromSeconds(3);
        object c=Data.Read(Path.Combine(Data.Root,"config.json"));
        TorrUrl=Data.S(c,"torrserve","http://192.168.1.10:8090");
        CastUrl=Data.S(c,"svp_local","http://127.0.0.1:9900");
        TVUrl=Data.S(c,"tv","http://192.168.1.20:8080");
        ManagerExe=Data.S(c,"manager",@"G:\SVP\SVPManager.exe");
    }
    public async Task<Reply> Request(string url,string body=null) {
        var r=new Reply();
        try {
            var endpoint=new Uri(url);if(!IPAddress.IsLoopback(IPAddress.Parse(endpoint.Host)))RemoteBackend.LocalRoute(endpoint.Host+":"+endpoint.Port);
            using(var response=body==null?await http.GetAsync(url):await http.PostAsync(url,new StringContent(body,Encoding.UTF8,"application/json"))) {
                r.Text=await response.Content.ReadAsStringAsync();
                r.Ok=response.IsSuccessStatusCode;
                if(r.Ok) r.Json=Data.Parse(r.Text); else r.Error="HTTP "+(int)response.StatusCode;
            }
        } catch(TaskCanceledException) { r.Error="Нет ответа за 3 с"; }
        catch(Exception ex) { r.Error=ex.GetBaseException().Message; }
        return r;
    }
    public static int HelperPid() { return (int)Data.N(Data.Read(Path.Combine(Data.Root,"pid.json")),"pid"); }
    public static bool HelperAlive() {
        try { using(var p=Process.GetProcessById(HelperPid())) { return !p.HasExited && p.ProcessName.Equals("pythonw",StringComparison.OrdinalIgnoreCase); } } catch { return false; }
    }
    public async Task<Snapshot> Collect(bool pollTV) {
        var a=Request(TorrUrl+"/echo"); var b=Request(CastUrl+"/r/state"); var c=Request(CastUrl+"/r/progress");
        var d=pollTV?Request(TVUrl+"/r/log?last=0"):Task.FromResult(new Reply{Error="Опрос выключен"});
        var e=Request(TorrUrl+"/torrents","{\"action\":\"list\"}");
        var m=Task.Run(()=>CollectMetrics());
        await Task.WhenAll(a,b,c,d,e);
        var logs=await Task.Run(()=>new [] {Data.Tail(Path.Combine(Data.Root,"dashboard.log")),Data.Tail(Path.Combine(Data.Root,"bridge.log")),Data.Tail(Path.Combine(Data.Server,"server.log")),Data.Tail(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"SVP4\logs\active.log"))});
        return new Snapshot{Server=a.Result,Cast=b.Result,Progress=c.Result,TV=d.Result,Torrents=e.Result,Metrics=await m,Health=Data.Read(Path.Combine(Data.Root,"health.json")),HelperState=Data.Read(Path.Combine(Data.Root,"state.json")),HelperAlive=HelperAlive(),Logs=logs};
    }
    Metrics CollectMetrics() {
        var m=new Metrics(); long idle,kernel,user;
        if(GetSystemTimes(out idle,out kernel,out user)) {
            ulong total=(ulong)kernel-oldKernel+(ulong)user-oldUser,wait=(ulong)idle-oldIdle;
            if(oldKernel!=0 && total>0) m.Cpu=Math.Max(0,Math.Min(100,100.0*(total-wait)/total));
            oldKernel=(ulong)kernel; oldUser=(ulong)user; oldIdle=(ulong)idle;
        }
        var mem=new Mem(); if(GlobalMemoryStatusEx(mem)) { m.RamTotal=mem.total; m.RamUsed=mem.total-mem.avail; }
        var groups=new Dictionary<string,string[]> {
            {"TorrServe",new[]{"TorrServer-windows-amd64"}}, {"Помощник",new[]{"#helper"}},
            {"SVP Manager",new[]{"SVPManager"}}, {"Обработка видео",new[]{"mpv","mpv64","vspipe"}},
            {"Экран ТВ",new[]{"scrcpy"}}, {"Это окно",new[]{"#self"}}
        };
        DateTime now=DateTime.UtcNow;
        foreach(var group in groups) {
            var metric=new ProcMetric{Name=group.Key};
            foreach(string name in group.Value) {
                Process[] list;
                try { list=name=="#helper"?new[]{Process.GetProcessById(HelperPid())}:name=="#self"?new[]{Process.GetCurrentProcess()}:Process.GetProcessesByName(name); } catch { continue; }
                foreach(var p in list) using(p) try {
                    double cpu=p.TotalProcessorTime.TotalSeconds; Tuple<DateTime,double> prev;
                    if(samples.TryGetValue(p.Id,out prev)) { double sec=(now-prev.Item1).TotalSeconds; if(sec>0) metric.Cpu+=Math.Max(0,(cpu-prev.Item2)*100/sec/Environment.ProcessorCount); }
                    samples[p.Id]=Tuple.Create(now,cpu); metric.Bytes+=p.WorkingSet64; metric.Count++;
                } catch {}
            }
            m.Processes.Add(metric);
        }
        foreach(int id in samples.Where(x=>(now-x.Value.Item1).TotalMinutes>2).Select(x=>x.Key).ToArray()) samples.Remove(id);
        try {
            string host=new Uri(TorrUrl).Host;
            var nic=NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x=>x.OperationalStatus==OperationalStatus.Up && x.GetIPProperties().UnicastAddresses.Any(v=>v.Address.ToString()==host));
            if(nic!=null) { var s=nic.GetIPv4Statistics(); double sec=(now-netTime).TotalSeconds; if(oldRx!=0 && sec>0) {m.Down=Math.Max(0,s.BytesReceived-oldRx)/sec; m.Up=Math.Max(0,s.BytesSent-oldTx)/sec;} oldRx=s.BytesReceived;oldTx=s.BytesSent;netTime=now; }
        } catch {}
        if((now-gpuTime).TotalSeconds>=6) {
            gpuTime=now; gpuCache=new Metrics();
            try {
                var info=new ProcessStartInfo("nvidia-smi.exe","--query-gpu=name,utilization.gpu,utilization.encoder,utilization.decoder,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                using(var p=Process.Start(info)) {
                    if(p.WaitForExit(2200)) {
                        string[] values=p.StandardOutput.ReadToEnd().Trim().Split(','); double n;
                        if(values.Length>=7) {
                            gpuCache.GpuAvailable=true;gpuCache.GpuName=values[0].Trim();
                            double[] v=new double[6];for(int i=0;i<6;i++)v[i]=double.TryParse(values[i+1].Trim(),out n)?n:0;
                            gpuCache.Gpu=v[0];gpuCache.Encoder=v[1];gpuCache.Decoder=v[2];gpuCache.VramUsed=v[3];gpuCache.VramTotal=v[4];gpuCache.Temp=v[5];
                        }
                    } else p.Kill();
                }
            } catch {}
        }
        m.GpuAvailable=gpuCache.GpuAvailable;m.GpuName=gpuCache.GpuName;m.Gpu=gpuCache.Gpu;m.Encoder=gpuCache.Encoder;m.Decoder=gpuCache.Decoder;m.VramUsed=gpuCache.VramUsed;m.VramTotal=gpuCache.VramTotal;m.Temp=gpuCache.Temp;
        return m;
    }
    public async Task StopHelper() {
        File.WriteAllText(Path.Combine(Data.Root,"stop.flag"),"");
        for(int n=0;n<30;n++) { if(!HelperAlive()) return; await Task.Delay(500); }
        throw new Exception("Помощник ещё завершает запрос. Повторите через несколько секунд; процесс принудительно не остановлен.");
    }
    public static void StartHidden(string file,string args,string dir) { using(var p=Process.Start(new ProcessStartInfo(file,args){WorkingDirectory=dir,UseShellExecute=false,CreateNoWindow=true})){} }
    public void StartHelper() {
        if(HelperAlive()) return;
        object cfg=Data.Read(Path.Combine(Data.Root,"config.json"));
        if(Data.S(cfg,"enabled")!="True" && Data.S(cfg,"enabled")!="true") throw new Exception("Помощник выключен в config.json");
        if(!File.Exists(Data.Python))throw new FileNotFoundException("Не найден pythonw.exe; укажите pythonw в config.json",Data.Python);
        StartHidden(Data.Python,"\""+Path.Combine(Data.Root,"bridge.py")+"\"",Data.Root);
    }
    public async Task EnsureCast() {
        var current=await Request(CastUrl+"/r/state");
        if(current.Ok && Data.S(current.Json,"ok")=="True")return;
        if(!File.Exists(ManagerExe))throw new FileNotFoundException("Не найден SVP Manager",ManagerExe);
        bool running=false;
        foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ManagerExe)))using(p){
            if(string.Equals(p.MainModule.FileName,ManagerExe,StringComparison.OrdinalIgnoreCase))running=true;
        }
        if(!running){Data.Event("Запуск SVP Manager для SVPcast");StartHidden(ManagerExe,"",Path.GetDirectoryName(ManagerExe));}
        DateTime deadline=DateTime.UtcNow.AddSeconds(25);
        do{
            var ready=await Request(CastUrl+"/r/state");
            if(ready.Ok && Data.S(ready.Json,"ok")=="True"){Data.Event("SVPcast готов: "+CastUrl);return;}
            await Task.Delay(750);
        }while(DateTime.UtcNow<deadline);
        throw new Exception("SVP Manager запущен, но SVPcast не отвечает. В SVP включите: Трансляция → Веб-интерфейс → Включить. Адрес: "+CastUrl);
    }
    public async Task StopCast() {
        var state=await Request(CastUrl+"/r/state");
        if(state.Ok) {
            var cancelled=await Request(CastUrl+"/r/cancel");
            if(!cancelled.Ok)throw new Exception("Не удалось остановить обработку SVPcast: "+cancelled.Error);
        }
        await Task.Run(()=>{
            foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ManagerExe)))using(p){
                if(!string.Equals(p.MainModule.FileName,ManagerExe,StringComparison.OrdinalIgnoreCase))continue;
                if(!p.CloseMainWindow() || !p.WaitForExit(1500))p.Kill();
                if(!p.WaitForExit(5000))throw new Exception("SVP Manager не завершился вовремя");
            }
        });
    }
    public async Task RestartCast() {
        await StopCast();
        await EnsureCast();
    }
    public void StopServer() {
        string exe=Data.ServerExe;
        foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) using(p) {
            if(!string.Equals(p.MainModule.FileName,exe,StringComparison.OrdinalIgnoreCase)) continue;
            p.Kill(); if(!p.WaitForExit(5000)) throw new Exception("TorrServe не завершился вовремя");
        }
    }
    public void StartServer() {
        string exe=Data.ServerExe;
        if(!File.Exists(exe))throw new FileNotFoundException("Не найден TorrServer; укажите torrserver_exe в config.json",exe);
        foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) using(p) if(string.Equals(p.MainModule.FileName,exe,StringComparison.OrdinalIgnoreCase)) return;
        string host=new Uri(TorrUrl).Host;int port=new Uri(TorrUrl).Port;
        StartHidden(exe,"--ip "+host+" --port "+port+" --path \""+Data.ServerData+"\" --logpath \""+Path.Combine(Data.Server,"server.log")+"\"",Data.Server);
    }
    public void Dispose() { http.Dispose(); }
}

class StatusCard:Panel {
    Label title,value,detail;
    public StatusCard(string caption) {
        BackColor=Color.FromArgb(26,34,46);Margin=new Padding(0,0,10,0);Dock=DockStyle.Fill;Padding=new Padding(16,10,10,10);
        title=new Label{Text=caption,ForeColor=Color.FromArgb(154,171,192),Font=new Font("Segoe UI",9),Dock=DockStyle.Top,Height=22};
        value=new Label{Text="Проверка…",ForeColor=Color.White,Font=new Font("Segoe UI Semibold",16),Dock=DockStyle.Top,Height=32,AutoEllipsis=true};
        detail=new Label{Text="",ForeColor=Color.FromArgb(154,171,192),Dock=DockStyle.Fill,AutoEllipsis=true,Font=new Font("Segoe UI",9)};
        Controls.Add(detail);Controls.Add(value);Controls.Add(title);
    }
    public void Set(string text,string sub,Color color) {value.Text=text;detail.Text=sub;value.ForeColor=color;}
}

class MainWindow:Form {
    readonly MonitorBackend backend=new MonitorBackend();
    readonly Color bg=Color.FromArgb(15,21,30),panel=Color.FromArgb(26,34,46),muted=Color.FromArgb(154,171,192),green=Color.FromArgb(81,213,169),amber=Color.FromArgb(244,187,91);
    readonly List<Button> actions=new List<Button>();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=2200};
    StatusCard serverCard,helperCard,castCard,tvCard;
    Label streamTitle,streamInfo,cpuLabel,gpuLabel,ramLabel,netLabel,foot;
    ProgressBar processed;
    Chart chart;
    ListView processes;
    RichTextBox[] logs=new RichTextBox[5];
    CheckBox follow,pollTV;
    DataGridView torrentsGrid;
    readonly ToolTip tips=new ToolTip{AutoPopDelay=18000};
    bool polling,busy; int point;
    Snapshot last;
    TVRemoteWindow remoteWindow;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h,int attr,ref int v,int size);
    public MainWindow(string preview=null,bool openTV=false) {
        Text="SVPServer — TorrServe · SVPcast";BackColor=bg;ForeColor=Color.White;
        try{Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{}
        Font=new Font("Segoe UI",10);MinimumSize=new Size(1080,700);Size=new Size(Math.Min(1280,Screen.PrimaryScreen.WorkingArea.Width-20),Math.Min(880,Screen.PrimaryScreen.WorkingArea.Height-20));StartPosition=FormStartPosition.CenterScreen;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20,14,20,10),ColumnCount=1,RowCount=6,BackColor=bg};
        foreach(float h in new[]{96f,112f,86f,174f})root.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,30));Controls.Add(root);
        var header=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=new Padding(0)};header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,690));
        var heading=new Panel{Dock=DockStyle.Fill};heading.Controls.Add(new Label{Dock=DockStyle.Top,Height=24,Text="Сервер, сглаживание и телевизор",Font=new Font("Segoe UI",10),ForeColor=muted});heading.Controls.Add(new Label{Dock=DockStyle.Top,Height=34,Text="SVPServer",Font=new Font("Segoe UI Semibold",20),ForeColor=Color.White});header.Controls.Add(heading,0,0);
        var topActions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,8,0,0),WrapContents=false};
        topActions.Controls.Add(ActionButton("Перезапустить всё",async()=>{await backend.StopHelper();try{await Task.Run(()=>{backend.StopServer();backend.StartServer();});await backend.RestartCast();}finally{backend.StartHelper();}},true));
        topActions.Controls.Add(ActionButton("SVPcast ↻",()=>backend.RestartCast(),false));
        topActions.Controls.Add(ActionButton("Помощник ↻",async()=>{await backend.StopHelper();backend.StartHelper();},false));
        topActions.Controls.Add(ActionButton("TorrServe ↻",()=>Task.Run(()=>{backend.StopServer();backend.StartServer();}),false));
        var screenButton=ButtonStyle("Управлять ТВ",true);screenButton.Height=29;screenButton.Font=new Font("Segoe UI",9);screenButton.Click+=(s,e)=>OpenTVScreen();
        var serviceActions=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,Margin=new Padding(0)};
        serviceActions.RowStyles.Add(new RowStyle(SizeType.Absolute,45));serviceActions.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
        serviceActions.Controls.Add(topActions,0,0);
        var stopActions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Padding=new Padding(0,2,0,0)};
        stopActions.Controls.Add(StopButton("Остановить всё",async()=>{await backend.StopHelper();await backend.StopCast();await Task.Run(()=>backend.StopServer());}));
        stopActions.Controls.Add(StopButton("Стоп SVPcast",()=>backend.StopCast()));
        stopActions.Controls.Add(StopButton("Стоп помощник",()=>backend.StopHelper()));
        stopActions.Controls.Add(StopButton("Стоп TorrServe",()=>Task.Run(()=>backend.StopServer())));
        serviceActions.Controls.Add(stopActions,0,1);
        header.Controls.Add(serviceActions,1,0);root.Controls.Add(header,0,0);
        var cards=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,Margin=new Padding(0,0,0,10)};for(int i=0;i<4;i++)cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
        serverCard=new StatusCard("TORRSERVE");helperCard=new StatusCard("ПОМОЩНИК");castCard=new StatusCard("SVPCAST / ПК");tvCard=new StatusCard("ТЕЛЕВИЗОР");
        cards.Controls.Add(serverCard,0,0);cards.Controls.Add(helperCard,1,0);cards.Controls.Add(castCard,2,0);cards.Controls.Add(tvCard,3,0);root.Controls.Add(cards,0,1);
        var stream=new TableLayoutPanel{Dock=DockStyle.Fill,BackColor=panel,Padding=new Padding(14,7,14,7),Margin=new Padding(0,0,10,10),RowCount=3};
        stream.RowStyles.Add(new RowStyle(SizeType.Absolute,24));stream.RowStyles.Add(new RowStyle(SizeType.Absolute,22));stream.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        streamTitle=new Label{Dock=DockStyle.Fill,Text="Трансляция не запущена",Font=new Font("Segoe UI Semibold",11),AutoEllipsis=true};streamInfo=new Label{Dock=DockStyle.Fill,ForeColor=muted,AutoEllipsis=true};
        processed=new ProgressBar{Dock=DockStyle.Fill,Maximum=1000,Height=6,Margin=new Padding(0,2,0,0)};stream.Controls.Add(streamTitle,0,0);stream.Controls.Add(streamInfo,0,1);stream.Controls.Add(processed,0,2);root.Controls.Add(stream,0,2);
        var metrics=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=new Padding(0,0,10,10)};metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,57));metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,43));
        var graphPanel=new TableLayoutPanel{Dock=DockStyle.Fill,BackColor=panel,RowCount=3,Padding=new Padding(8,3,8,0)};graphPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,24));graphPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,22));graphPanel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var metricRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3};for(int i=0;i<3;i++)metricRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333f));
        cpuLabel=SmallLabel();gpuLabel=SmallLabel();ramLabel=SmallLabel();metricRow.Controls.Add(cpuLabel,0,0);metricRow.Controls.Add(gpuLabel,1,0);metricRow.Controls.Add(ramLabel,2,0);graphPanel.Controls.Add(metricRow,0,0);
        netLabel=SmallLabel();netLabel.ForeColor=muted;graphPanel.Controls.Add(netLabel,0,1);
        chart=new Chart{Dock=DockStyle.Fill,BackColor=panel};var area=new ChartArea("load"){BackColor=panel};area.AxisY.Minimum=0;area.AxisY.Maximum=100;area.AxisY.Interval=50;area.AxisY.LabelStyle.ForeColor=muted;area.AxisY.MajorGrid.LineColor=Color.FromArgb(48,59,73);area.AxisX.Enabled=AxisEnabled.False;area.AxisY.LineColor=panel;chart.ChartAreas.Add(area);
        foreach(var pair in new[]{Tuple.Create("CPU",Color.FromArgb(81,213,169)),Tuple.Create("GPU",Color.FromArgb(109,164,255)),Tuple.Create("NVENC",Color.FromArgb(209,153,255))}) {var s=new Series(pair.Item1){ChartType=SeriesChartType.FastLine,BorderWidth=2,Color=pair.Item2};chart.Series.Add(s);}
        var legend=new Legend{BackColor=panel,ForeColor=muted,Docking=Docking.Bottom,Font=new Font("Segoe UI",8)};chart.Legends.Add(legend);graphPanel.Controls.Add(chart,0,2);metrics.Controls.Add(graphPanel,0,0);
        processes=new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,BackColor=panel,ForeColor=Color.White,BorderStyle=BorderStyle.None,HeaderStyle=ColumnHeaderStyle.Nonclickable,Margin=new Padding(10,0,0,0)};processes.Columns.Add("Процесс",175);processes.Columns.Add("CPU ПК",75);processes.Columns.Add("Память",100);metrics.Controls.Add(processes,1,0);root.Controls.Add(metrics,0,3);
        var logArea=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,Margin=new Padding(0,0,10,0)};logArea.RowStyles.Add(new RowStyle(SizeType.Absolute,38));logArea.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var tools=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};follow=new CheckBox{Text="Следить за журналами",Checked=true,AutoSize=true,Margin=new Padding(0,8,14,0)};pollTV=new CheckBox{Text="Опрос ТВ",Checked=true,AutoSize=true,Margin=new Padding(0,8,14,0)};tools.Controls.Add(follow);tools.Controls.Add(pollTV);
        tools.Controls.Add(screenButton);
        tools.Controls.Add(SimpleButton("Web TorrServe",()=>Open(backend.TorrUrl)));
        tools.Controls.Add(SimpleButton("Web SVPcast",()=>Open(backend.CastUrl)));
        tools.Controls.Add(SimpleButton("Папка журналов",()=>Open(Data.Root)));
        var more=new ContextMenuStrip();more.Items.Add("Запустить помощник",null,async(s,e)=>await DoAction("Запуск помощника",()=>{backend.StartHelper();return Task.FromResult(0);}));more.Items.Add("Остановить помощник",null,async(s,e)=>await DoAction("Остановка помощника",()=>backend.StopHelper()));more.Items.Add("Запустить TorrServe",null,async(s,e)=>await DoAction("Запуск TorrServe",()=>Task.Run(()=>backend.StartServer())));more.Items.Add("Остановить TorrServe",null,async(s,e)=>await DoAction("Остановка TorrServe",()=>Task.Run(()=>backend.StopServer())));more.Items.Add("Открыть папку TorrServe",null,(s,e)=>Open(Data.Server));
        var moreButton=SimpleButton("Ещё ▾",()=>{});moreButton.Click+=(s,e)=>more.Show(moreButton,new Point(0,moreButton.Height));tools.Controls.Add(moreButton);logArea.Controls.Add(tools,0,0);
        more.Items.Insert(0,new ToolStripMenuItem("Запустить SVPcast",null,async(s,e)=>await DoAction("Запуск SVPcast",()=>backend.EnsureCast())));
        more.Items.Insert(1,new ToolStripMenuItem("Перезапустить SVPcast",null,async(s,e)=>await DoAction("Перезапуск SVPcast",()=>backend.RestartCast())));
        more.Items.Insert(2,new ToolStripMenuItem("Остановить SVPcast",null,async(s,e)=>await DoAction("Остановка SVPcast",()=>backend.StopCast())));
        more.Items.Insert(2,new ToolStripMenuItem("Экран и пульт ТВ",null,(s,e)=>OpenTVScreen()));
        var tabs=new TabControl{Dock=DockStyle.Fill};string[] names={"События","Помощник","TorrServe","SVPcast","Телевизор"};
        for(int i=0;i<5;i++){var page=new TabPage(names[i]){BackColor=panel,Padding=new Padding(5)};logs[i]=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,BackColor=Color.FromArgb(17,24,34),ForeColor=Color.FromArgb(190,204,220),Font=new Font("Consolas",9),BorderStyle=BorderStyle.None,WordWrap=false,DetectUrls=false,HideSelection=false};page.Controls.Add(logs[i]);tabs.TabPages.Add(page);}
        torrentsGrid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=panel,BorderStyle=BorderStyle.None,EnableHeadersVisualStyles=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect};
        torrentsGrid.DefaultCellStyle.BackColor=panel;torrentsGrid.DefaultCellStyle.ForeColor=Color.White;torrentsGrid.DefaultCellStyle.SelectionBackColor=Color.FromArgb(42,78,94);torrentsGrid.DefaultCellStyle.SelectionForeColor=Color.White;torrentsGrid.ColumnHeadersDefaultCellStyle.BackColor=bg;torrentsGrid.ColumnHeadersDefaultCellStyle.ForeColor=muted;torrentsGrid.GridColor=Color.FromArgb(48,59,73);
        string[] tc={"Название","Состояние","Скачивание","Отдача","Пиры","Загружено"};foreach(string name in tc)torrentsGrid.Columns.Add(name,name);torrentsGrid.Columns[0].FillWeight=300;torrentsGrid.Columns[1].FillWeight=150;
        var torrentsPage=new TabPage("Торренты"){BackColor=panel};torrentsPage.Controls.Add(torrentsGrid);tabs.TabPages.Add(torrentsPage);
        logArea.Controls.Add(tabs,0,1);root.Controls.Add(logArea,0,4);
        foot=new Label{Dock=DockStyle.Fill,ForeColor=muted,Text="Подключение…",TextAlign=ContentAlignment.MiddleLeft,Font=new Font("Segoe UI",9)};root.Controls.Add(foot,0,5);
        timer.Tick+=async(s,e)=>await RefreshSnapshot();Shown+=async(s,e)=>{try{int v=1;DwmSetWindowAttribute(Handle,20,ref v,4);}catch{}await RefreshSnapshot();
            if(preview!=null){await Task.Delay(2600);await RefreshSnapshot();PerformLayout();using(var image=new Bitmap(Width,Height)){DrawToBitmap(image,new Rectangle(0,0,Width,Height));image.Save(preview,System.Drawing.Imaging.ImageFormat.Png);}
                File.WriteAllText(preview+".json",new JavaScriptSerializer().Serialize(new{logCharacters=logs.Select(x=>x.TextLength).ToArray(),chartSamples=chart.Series[0].Points.Count,torrentRows=torrentsGrid.Rows.Count,progress=processed.Value}),Encoding.UTF8);Close();}
            else{timer.Start();await DoAction("Запуск SVPcast",()=>backend.EnsureCast());if(openTV)OpenTVScreen();}};FormClosed+=(s,e)=>{timer.Stop();Data.Event("Окно закрыто; фоновые службы продолжают работать");};
    }
    Label SmallLabel() {return new Label{Dock=DockStyle.Fill,ForeColor=Color.White,Font=new Font("Segoe UI",9),AutoEllipsis=true};}
    void OpenTVScreen(){if(remoteWindow==null || remoteWindow.IsDisposed){remoteWindow=new TVRemoteWindow(backend.TVUrl);remoteWindow.Show(this);}else{if(remoteWindow.WindowState==FormWindowState.Minimized)remoteWindow.WindowState=FormWindowState.Normal;remoteWindow.Activate();}}
    Button SimpleButton(string text,Action action) {var b=ButtonStyle(text,false);b.Height=29;b.Font=new Font("Segoe UI",9);b.Click+=(s,e)=>{try{action();}catch(Exception ex){foot.Text=ex.Message;}};return b;}
    Button ButtonStyle(string text,bool primary) {return new Button{Text=text,AutoSize=true,Height=35,FlatStyle=FlatStyle.Flat,BackColor=primary?Color.FromArgb(44,151,121):panel,ForeColor=Color.White,Padding=new Padding(9,2,9,2),Margin=new Padding(6,0,0,0),Cursor=Cursors.Hand};}
    Button ActionButton(string text,Func<Task> action,bool primary){var b=ButtonStyle(text,primary);actions.Add(b);b.Click+=async(s,e)=>await DoAction(text,action);return b;}
    Button StopButton(string text,Func<Task> action){var b=ActionButton(text,action,false);b.ForeColor=Color.FromArgb(255,173,173);b.FlatAppearance.BorderColor=Color.FromArgb(143,78,78);return b;}
    void Open(string path){Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
    async Task DoAction(string name,Func<Task> action){
        if(busy)return;busy=true;foreach(var b in actions)b.Enabled=false;foot.Text=name+"…";Data.Event(name+": начало");
        try{await action();Data.Event(name+": выполнено");foot.Text=name+": выполнено";}
        catch(Exception ex){Data.Event(name+": ошибка — "+ex.GetBaseException().Message);MessageBox.Show(this,ex.GetBaseException().Message,"Действие не выполнено",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        finally{busy=false;foreach(var b in actions)b.Enabled=true;}
        await RefreshSnapshot();
    }
    async Task RefreshSnapshot(){
        if(polling || IsDisposed)return;polling=true;
        try{last=await backend.Collect(pollTV.Checked);if(!IsDisposed)Render(last);}
        catch(Exception ex){Data.Event("Обновление: "+ex.Message);if(!IsDisposed)foot.Text="Ошибка обновления: "+ex.Message;}
        finally{polling=false;}
    }
    void PutLog(int i,string text){if(!follow.Checked || logs[i].Text==text)return;int old=logs[i].SelectionStart;bool end=logs[i].SelectionStart>=logs[i].TextLength-3;logs[i].Text=text;if(end || !logs[i].Focused){logs[i].SelectionStart=logs[i].TextLength;logs[i].ScrollToCaret();}else logs[i].SelectionStart=Math.Min(old,logs[i].TextLength);}
    void Render(Snapshot s){
        serverCard.Set(s.Server.Ok?"Работает":"Нет связи",s.Server.Ok?s.Server.Text.Trim()+" · "+new Uri(backend.TorrUrl).Authority:s.Server.Error,s.Server.Ok?green:amber);
        double heartbeat=Data.N(s.Health,"heartbeat"),age=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-heartbeat;bool fresh=heartbeat>0 && age<18;string phase=Data.S(s.Health,"phase");
        bool helperError=Data.S(s.Health,"last_error")!="";
        string helperText=!s.HelperAlive?"Остановлен":!fresh?"Нет сигнала":phase=="waiting_for_player"?"Ждёт плеер":helperError?"Проверить журнал":"Готов";
        string helperSub=s.HelperAlive?"PID "+MonitorBackend.HelperPid()+" · "+(fresh?"сигнал "+Math.Max(0,age).ToString("0")+" с назад":"проверить журнал"):"Запуск: меню «Ещё»";
        helperCard.Set(helperText,helperSub,s.HelperAlive && fresh && !helperError?green:amber);
        tips.SetToolTip(helperCard,helperError?Data.S(s.Health,"last_error"):"Помощник подключает новые фильмы к штатному режиму SVPcast");
        string state=Data.S(s.Progress.Json,"state");bool streaming=s.Progress.Ok && state=="streaming";
        castCard.Set(!s.Cast.Ok?"Нет связи":streaming?"Трансляция":"Ожидает фильм",s.Cast.Ok?"Веб-интерфейс "+new Uri(backend.CastUrl).Authority:s.Cast.Error,s.Cast.Ok?green:amber);
        tvCard.Set(s.TV.Ok?"Плеер доступен":pollTV.Checked?"Нет связи":"Опрос выключен",s.TV.Ok?new Uri(backend.TVUrl).Authority:"ТВ выключен, плеер закрыт или нет сети",s.TV.Ok?green:muted);
        string title=Data.S(s.Cast.Json,"title");streamTitle.Text=streaming?(title==""?"Обработка видео на ПК":title):"Трансляция не запущена";
        double pos=Data.N(s.Progress.Json,"pos"),dur=Data.N(s.Progress.Json,"dur");processed.Value=streaming && dur>0?(int)Math.Max(0,Math.Min(1000,1000*pos/dur)):0;
        string format=Data.S(s.Cast.Json,"cs");
        int torrents=(s.Torrents.Json as object[]??new object[0]).Length;
        streamInfo.Text=streaming?format+" · Обработано "+Data.Clock(pos)+" / "+Data.Clock(dur)+" · "+(Data.S(s.Progress.Json,"pause")=="True"?"Пауза":"Поток SVPcast"):"Выбери SVPlayer Lite в ByLampa · В базе TorrServe: "+torrents+" · Прогресс показывает обработку на ПК";
        var m=s.Metrics;cpuLabel.Text="CPU ПК: "+m.Cpu.ToString("0")+"%";ramLabel.Text="RAM: "+(m.RamUsed/1073741824).ToString("0.0")+" / "+(m.RamTotal/1073741824).ToString("0")+" ГБ";
        gpuLabel.Text=m.GpuAvailable?"GPU: "+m.Gpu.ToString("0")+"% · "+m.Temp.ToString("0")+" °C":"GPU: нет данных";
        tips.SetToolTip(gpuLabel,m.GpuName+" · NVDEC "+m.Decoder.ToString("0")+"%");
        netLabel.Text=m.GpuAvailable?"VRAM "+m.VramUsed.ToString("0")+" / "+m.VramTotal.ToString("0")+" МБ · NVENC "+m.Encoder.ToString("0")+"% · LAN ↓ "+(m.Down/1048576).ToString("0.0")+" ↑ "+(m.Up/1048576).ToString("0.0")+" МБ/с":"LAN ↓ "+(m.Down/1048576).ToString("0.0")+" ↑ "+(m.Up/1048576).ToString("0.0")+" МБ/с";
        double[] values={m.Cpu,m.Gpu,m.Encoder};for(int i=0;i<3;i++){var series=chart.Series[i];series.Points.AddXY(point,values[i]);while(series.Points.Count>75)series.Points.RemoveAt(0);}point++;
        processes.BeginUpdate();processes.Items.Clear();foreach(var p in m.Processes){var row=new ListViewItem(p.Name+(p.Count>1?" ×"+p.Count:""));row.SubItems.Add(p.Count==0?"—":p.Cpu.ToString("0.0")+"%");row.SubItems.Add(p.Count==0?"—":Data.Size(p.Bytes));if(p.Count==0)row.ForeColor=muted;processes.Items.Add(row);}processes.EndUpdate();
        for(int i=0;i<4;i++)PutLog(i,s.Logs[i]);
        var tvLines=Data.Get(s.TV.Json,"list") as object[];
        string selected=torrentsGrid.CurrentRow==null?null:Convert.ToString(torrentsGrid.CurrentRow.Cells[0].Value);
        torrentsGrid.Rows.Clear();foreach(object torrent in s.Torrents.Json as object[]??new object[0]){int row=torrentsGrid.Rows.Add(Data.S(torrent,"title"),Data.S(torrent,"stat_string"),(Data.N(torrent,"download_speed")/1048576).ToString("0.00")+" МБ/с",(Data.N(torrent,"upload_speed")/1048576).ToString("0.00")+" МБ/с",Data.S(torrent,"active_peers","0")+" / "+Data.S(torrent,"total_peers","0"),Data.Size(Data.N(torrent,"loaded_size")));if(Data.S(torrent,"title")==selected)torrentsGrid.Rows[row].Selected=true;}
        PutLog(4,s.TV.Ok?string.Join(Environment.NewLine,(tvLines??new object[0]).Select(x=>WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(Convert.ToString(x),"<[^>]+>",""))).TakeLastCompat(150)):"Веб-интерфейс SVPlayer недоступен.\r\n"+s.TV.Error+"\r\nЭто нормально, когда плеер закрыт или телевизор выключен.");
        if(!busy)foot.Text="Обновлено "+DateTime.Now.ToString("HH:mm:ss")+" · Прямая локальная сеть · CPU от мощности всего ПК · Закрытие окна оставит службы работать";
    }
    protected override void Dispose(bool disposing){if(disposing)timer.Dispose();base.Dispose(disposing);}
}
static class EnumerableCompat {public static IEnumerable<T> TakeLastCompat<T>(this IEnumerable<T> input,int count){var a=input.ToArray();return a.Skip(Math.Max(0,a.Length-count));}}
static class Program {
    [STAThread] static void Main(string[] args){
        if(args.Contains("--self-test")){SelfTest();return;}
        if(args.Contains("--action-self-test")){ActionSelfTest();return;}
        if(args.Contains("--cast-self-test")){CastSelfTest();return;}
        if(args.Contains("--remote-self-test")){RemoteSelfTest();return;}
        if(args.Contains("--capture-cleanup-self-test")){try{VerifyCaptureCleanup();Data.Event("CAPTURE CLEANUP SELF-TEST OK");}catch(Exception ex){Data.Event("CAPTURE CLEANUP SELF-TEST FAILED: "+ex);Environment.ExitCode=1;}return;}
        string remoteProof=args.FirstOrDefault(x=>x.StartsWith("--remote-proof="));
        if(remoteProof!=null){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);object cfg=Data.Read(Path.Combine(Data.Root,"config.json"));Application.Run(new TVRemoteWindow(Data.S(cfg,"tv","http://192.168.1.20:8080"),true,remoteProof.Substring(15)));return;}
        bool first;using(var mutex=new Mutex(true,@"Local\TVControlDashboard",out first)) {
            if(!first){MessageBox.Show("TV Control уже открыт. Найдите окно на панели задач.","TV Control");return;}
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException+=(s,e)=>Data.Event("Ошибка интерфейса: "+e.Exception);
            AppDomain.CurrentDomain.UnhandledException+=(s,e)=>Data.Event("Ошибка приложения: "+e.ExceptionObject);
            string preview=args.FirstOrDefault(x=>x.StartsWith("--preview="));
            Data.Event("TV Control запущен");Application.Run(new MainWindow(preview==null?null:preview.Substring(10),args.Contains("--open-tv")));
        }
    }
    static void SelfTest(){
        try{
            if(Data.S(Data.Parse("{\"a\":\"тест\"}"),"a")!="тест")throw new Exception("JSON UTF-8");
            if(Data.N(Data.Parse("{\"pos\":2.5}"),"pos")!=2.5)throw new Exception("JSON number");
            if(Data.Clock(3661)!="01:01:01")throw new Exception("Timeline formatter");
            using(var b=new MonitorBackend()){
                var offline=b.Request("http://127.0.0.1:1/").GetAwaiter().GetResult();
                if(offline.Ok || offline.Error=="")throw new Exception("Offline handling failed");
                var s=b.Collect(true).GetAwaiter().GetResult();
                if(s.Metrics.RamTotal<=0)throw new Exception("Memory measurement");
                if(!s.Server.Ok)throw new Exception("TorrServe: "+s.Server.Error);
                if(!s.HelperAlive)throw new Exception("Helper not running");
                if(Data.N(s.Health,"heartbeat")<=0)throw new Exception("Missing heartbeat");
                Data.Event("SELF-TEST OK: JSON, process, RAM, HTTP, heartbeat; GPU="+s.Metrics.GpuAvailable+"; SVPcast="+s.Cast.Ok+"; TV="+s.TV.Ok);
                File.WriteAllText(Path.Combine(Data.Root,"Dashboard","self-test.json"),new JavaScriptSerializer().Serialize(new {ok=true,gpu=s.Metrics.GpuAvailable,svpcast=s.Cast.Ok,tv=s.TV.Ok,helper=s.HelperAlive,server=s.Server.Text.Trim(),ram=s.Metrics.RamTotal}),Encoding.UTF8);
            }
        }catch(Exception ex){Data.Event("SELF-TEST FAILED: "+ex);Environment.ExitCode=1;}
    }
    static void ActionSelfTest(){
        try{using(var b=new MonitorBackend()){
            int old=MonitorBackend.HelperPid();b.StopHelper().GetAwaiter().GetResult();
            if(MonitorBackend.HelperAlive())throw new Exception("Cooperative stop failed");
            b.StartHelper();for(int i=0;i<30 && !MonitorBackend.HelperAlive();i++)Thread.Sleep(500);
            if(!MonitorBackend.HelperAlive() || MonitorBackend.HelperPid()==old)throw new Exception("Helper restart failed");
            b.StartServer();var r=b.Request(b.TorrUrl+"/echo").GetAwaiter().GetResult();if(!r.Ok)throw new Exception("Server not available");
            Data.Event("ACTION SELF-TEST OK: helper stopped/restarted with a new PID; server start is idempotent");
        }}catch(Exception ex){Data.Event("ACTION SELF-TEST FAILED: "+ex);Environment.ExitCode=1;}
    }
    static void CastSelfTest(){
        try{using(var b=new MonitorBackend()){
            b.EnsureCast().GetAwaiter().GetResult();
            int[] before=Process.GetProcessesByName(Path.GetFileNameWithoutExtension(b.ManagerExe)).Select(p=>{using(p)return p.Id;}).OrderBy(x=>x).ToArray();
            b.EnsureCast().GetAwaiter().GetResult();
            int[] after=Process.GetProcessesByName(Path.GetFileNameWithoutExtension(b.ManagerExe)).Select(p=>{using(p)return p.Id;}).OrderBy(x=>x).ToArray();
            if(before.Length!=1 || !before.SequenceEqual(after))throw new Exception("Запуск SVP Manager создал дубликат процесса");
            var response=b.Request(b.CastUrl+"/r/state").GetAwaiter().GetResult();
            if(!response.Ok || Data.S(response.Json,"ok")!="True")throw new Exception("SVPcast API не готов");
            Data.Event("CAST SELF-TEST OK: API ready; repeated start kept one Manager PID "+after[0]);
            File.WriteAllText(Path.Combine(Data.Root,"Dashboard","cast-self-test.json"),new JavaScriptSerializer().Serialize(new{ok=true,pid=after[0],state=Data.S(response.Json,"state"),url=b.CastUrl}),Encoding.UTF8);
        }}catch(Exception ex){Data.Event("CAST SELF-TEST FAILED: "+ex);Environment.ExitCode=1;}
    }
    static void RemoteSelfTest(){
        try{
            foreach(string invalid in new[]{"100.64.0.1:5555","8.8.8.8:5555","127.0.0.1:5555","192.168.1.20:0","192.168.1.20:5555;exit"})if(RemoteBackend.ValidSerial(invalid))throw new Exception("Non-LAN address accepted: "+invalid);
            var b=new RemoteBackend("http://192.168.1.20:8080");string route=RemoteBackend.LocalRoute(b.SavedSerial);
            var result=b.AdbRun("-s "+RemoteBackend.Quote(b.SavedSerial)+" shell getprop ro.product.model").GetAwaiter().GetResult();
            if(result.Code!=0 || result.Text.Trim()=="")throw new Exception("ADB model query failed: "+result.Text);
            VerifyCaptureCleanup();
            Data.Event("REMOTE SELF-TEST OK: "+route+"; "+result.Text.Trim()+"; Tailscale rejected");
            File.WriteAllText(Path.Combine(Data.Root,"Dashboard","remote-self-test.json"),new JavaScriptSerializer().Serialize(new{ok=true,route=route,model=result.Text.Trim(),lanOnly=true,captureCleanup=true}),Encoding.UTF8);
        }catch(Exception ex){Data.Event("REMOTE SELF-TEST FAILED: "+ex);Environment.ExitCode=1;}
    }
    static void VerifyCaptureCleanup(){
        using(var owner=new RemoteBackend("http://192.168.1.20:8080"))using(var child=Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Data.Python),"python.exe"),"-c \"import time;time.sleep(30)\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})){
            try{owner.TrackCapture(child);owner.Dispose();if(!child.WaitForExit(2000))throw new Exception("Захват не завершился вместе с владельцем");}finally{try{if(!child.HasExited)child.Kill();}catch{}}
        }
    }
}
