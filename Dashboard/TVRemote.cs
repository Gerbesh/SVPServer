using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class RemoteResult {public int Code;public string Text="";}
class RemoteBackend : IDisposable {
    public readonly string Scrcpy,Adb,TVHost;
    public string SavedSerial;
    public int SavedQuality;
    public bool SavedPhysical,SavedSeparate,SavedMouse;
    [DllImport("iphlpapi.dll")] static extern int GetBestInterface(uint destination,out uint index);
    [StructLayout(LayoutKind.Sequential)] struct JobLimits {public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinSet,MaxSet;public uint MaxProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)] struct JobIo {public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)] struct JobInfo {public JobLimits Limits;public JobIo Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int infoClass,ref JobInfo info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    IntPtr captureJob=IntPtr.Zero;
    public void TrackCapture(Process process){
        if(captureJob==IntPtr.Zero){
            captureJob=CreateJobObject(IntPtr.Zero,null);if(captureJob==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var info=new JobInfo();info.Limits.Flags=0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if(!SetInformationJobObject(captureJob,9,ref info,(uint)Marshal.SizeOf(typeof(JobInfo)))){int error=Marshal.GetLastWin32Error();Dispose();throw new System.ComponentModel.Win32Exception(error);}
        }
        if(!AssignProcessToJobObject(captureJob,process.Handle))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Не удалось привязать захват экрана к окну управления");
    }
    public void Dispose(){if(captureJob!=IntPtr.Zero){CloseHandle(captureJob);captureJob=IntPtr.Zero;}}
    public RemoteBackend(string tvUrl){
        object settings=Data.Read(Path.Combine(Data.Root,"remote.json"));
        TVHost=new Uri(tvUrl).Host;
        Scrcpy=Data.S(settings,"scrcpy_path",Path.Combine(Data.Root,@"Tools\scrcpy-win64-v5.0.1\scrcpy.exe"));
        string sdk=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Android\Sdk\platform-tools\adb.exe");
        Adb=Data.S(settings,"adb_path",File.Exists(sdk)?sdk:Path.Combine(Path.GetDirectoryName(Scrcpy),"adb.exe"));
        SavedSerial=Data.S(settings,"adb_device",TVHost+":5555");
        SavedQuality=Data.Get(settings,"quality")==null?2:(int)Math.Max(0,Math.Min(3,Data.N(settings,"quality")));
        SavedPhysical=Data.S(settings,"physical_keyboard")=="True";SavedSeparate=Data.S(settings,"separate_window")=="True";
        SavedMouse=Data.Get(settings,"physical_mouse")==null || Data.S(settings,"physical_mouse")=="True";
    }
    public static bool ValidSerial(string serial){
        Match match=Regex.Match(serial??"",@"^([0-9.]+):([0-9]+)$");IPAddress ip;int port;
        if(!match.Success || !IPAddress.TryParse(match.Groups[1].Value,out ip) || !int.TryParse(match.Groups[2].Value,out port) || port<1 || port>65535)return false;
        byte[] b=ip.GetAddressBytes();return b.Length==4 && (b[0]==10 || b[0]==172 && b[1]>=16 && b[1]<=31 || b[0]==192 && b[1]==168);
    }
    public static string LocalRoute(string serial){
        if(!ValidSerial(serial))throw new Exception("Подключение разрешено только по локалке: укажи частный адрес ТВ, например 192.168.1.20:5555. Адреса Tailscale не используются.");
        var ip=IPAddress.Parse(serial.Split(':')[0]);uint index;
        if(GetBestInterface(BitConverter.ToUInt32(ip.GetAddressBytes(),0),out index)!=0)throw new Exception("Не найден локальный маршрут до ТВ");
        foreach(var nic in NetworkInterface.GetAllNetworkInterfaces()){
            if(nic.OperationalStatus!=OperationalStatus.Up || nic.NetworkInterfaceType!=NetworkInterfaceType.Ethernet && nic.NetworkInterfaceType!=NetworkInterfaceType.Wireless80211)continue;
            if((nic.Name+" "+nic.Description).IndexOf("tailscale",StringComparison.OrdinalIgnoreCase)>=0)continue;
            var props=nic.GetIPProperties();var v4=props.GetIPv4Properties();if(v4==null || v4.Index!=index)continue;
            foreach(var local in props.UnicastAddresses){
                if(local.Address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork || local.IPv4Mask==null)continue;
                byte[] a=local.Address.GetAddressBytes(),b=ip.GetAddressBytes(),mask=local.IPv4Mask.GetAddressBytes();bool same=true;
                for(int i=0;i<4;i++)if((a[i]&mask[i])!=(b[i]&mask[i]))same=false;
                if(same)return local.Address+" → "+serial+" · "+nic.Name;
            }
        }
        throw new Exception("Маршрут до телевизора не идёт напрямую через локальный Ethernet/Wi-Fi. Подключение через VPN/Tailscale запрещено.");
    }
    public static string Quote(string s){return "\""+s.Replace("\"","\\\"")+"\"";}
    public async Task<RemoteResult> AdbRun(string args,int timeout=7000){
        if(!File.Exists(Adb))throw new FileNotFoundException("Не найден adb.exe",Adb);
        using(var p=Process.Start(new ProcessStartInfo(Adb,args){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8})){
            var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
            bool exited=await Task.Run(()=>p.WaitForExit(timeout));
            if(!exited){try{p.Kill();}catch{}throw new TimeoutException("ADB не ответил. Проверь адрес и включение отладки на телевизоре.");}
            return new RemoteResult{Code=p.ExitCode,Text=(await stdout)+(await stderr)};
        }
    }
    public async Task<string> Discover(){
        var devices=await AdbRun("devices -l");
        var line=devices.Text.Split('\n').Select(x=>x.Trim()).FirstOrDefault(x=>x.StartsWith(TVHost+":") && Regex.IsMatch(x,@"\sdevice(?:\s|$)"));
        if(line!=null)return Regex.Split(line,@"\s+")[0];
        var mdns=await AdbRun("mdns services");
        foreach(string row in mdns.Text.Split('\n')){
            if(!row.Contains("_adb-tls-connect"))continue;
            var found=Regex.Match(row,Regex.Escape(TVHost)+@":\d+");if(found.Success)return found.Value;
        }
        throw new Exception("Адрес не найден автоматически. Введи IP:порт из раздела «Беспроводная отладка» на ТВ. Для обычной сетевой отладки порт — 5555.");
    }
    public async Task Connect(string serial,int quality=2,bool physical=false,bool separate=false,bool physicalMouse=true){
        if(!ValidSerial(serial))throw new Exception("Нужен адрес вида 192.168.1.20:5555. Используй порт подключения, а не сопряжения.");
        LocalRoute(serial);
        var result=await AdbRun("connect "+Quote(serial));
        var devices=await AdbRun("devices -l");
        string row=devices.Text.Split('\n').FirstOrDefault(x=>x.TrimStart().StartsWith(serial+" ") || x.StartsWith(serial+"\t"))??"";
        if(row.Contains("unauthorized"))throw new Exception("Разреши отладку для этого ПК в появившемся окне на телевизоре, затем нажми «Подключить» ещё раз.");
        if(!Regex.IsMatch(row,@"\sdevice(?:\s|$)"))throw new Exception("ТВ недоступен по ADB. "+result.Text.Trim()+" Проверь отладку и IP:порт на ТВ.");
        SavedSerial=serial;
        File.WriteAllText(Path.Combine(Data.Root,"remote.json"),new JavaScriptSerializer().Serialize(new{adb_device=serial,adb_path=Adb,scrcpy_path=Scrcpy,quality=quality,physical_keyboard=physical,separate_window=separate,physical_mouse=physicalMouse}),Encoding.UTF8);
    }
    public Task<RemoteResult> Key(string serial,int key){return AdbRun("-s "+Quote(serial)+" shell input keyevent "+key);}
    public Process Start(string serial,int quality,bool physical,bool separate,bool physicalMouse,string title,Action<string> log){
        if(!File.Exists(Scrcpy))throw new FileNotFoundException("Не найден scrcpy.exe",Scrcpy);
        LocalRoute(serial);
        string[] profiles={"--max-size=1024 --max-fps=15 --video-bit-rate=2M","--max-size=1920 --max-fps=30 --video-bit-rate=8M","--max-size=1920 --max-fps=60 --video-bit-rate=16M","--max-fps=60 --video-bit-rate=24M"};
        string options=profiles[Math.Max(0,Math.Min(3,quality))];
        string args="--serial="+Quote(serial)+" --no-audio --no-power-on --print-fps --video-buffer=0 --video-codec=h264 --video-encoder=c2.mtk.avc.encoder --mouse-bind=bhsn:++++ --mouse="+(physicalMouse?"uhid":"sdk")+" --keyboard="+(physical?"uhid":"sdk")+" --window-title="+Quote(title)+" --window-width=1024 --window-height=576 --shortcut-mod=lalt,ralt "+options+(separate?"":" --window-borderless --no-window-aspect-ratio-lock");
        var start=new ProcessStartInfo(Scrcpy,args){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(Scrcpy),RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        start.EnvironmentVariables["ADB"]=Adb;
        var p=new Process{StartInfo=start,EnableRaisingEvents=true};
        p.OutputDataReceived+=(s,e)=>{if(e.Data!=null)log(e.Data);};p.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)log(e.Data);};
        p.Start();try{TrackCapture(p);}catch{try{p.Kill();}catch{}p.Dispose();throw;}p.BeginOutputReadLine();p.BeginErrorReadLine();return p;
    }
}

class TVRemoteWindow:Form {
    readonly RemoteBackend backend;
    readonly Color bg=Color.FromArgb(15,21,30),card=Color.FromArgb(26,34,46),mint=Color.FromArgb(81,213,169),muted=Color.FromArgb(154,171,192);
    readonly string windowTitle="SVPServer — экран ТВ";
    readonly object logLock=new object();
    TextBox address,console;Button connect,disconnect,find;ComboBox quality;CheckBox physical,separate,physicalMouse;
    Label status,hint;Panel screen;Process mirror;IntPtr child=IntPtr.Zero;
    bool connecting,closing,intentional;string serial;
    [DllImport("user32.dll")]static extern IntPtr SetParent(IntPtr child,IntPtr parent);
    [DllImport("user32.dll")]static extern IntPtr GetParent(IntPtr child);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]static extern IntPtr SetWindowLongPtr(IntPtr h,int index,IntPtr value);
    [DllImport("user32.dll")]static extern bool MoveWindow(IntPtr h,int x,int y,int w,int height,bool redraw);
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr h,int a,ref int v,int size);
    public TVRemoteWindow(string tvUrl,bool autoConnect=true,string proof=null){
        backend=new RemoteBackend(tvUrl);Text="SVPServer — экран и пульт телевизора";BackColor=bg;ForeColor=Color.White;
        Font=new Font("Segoe UI",10);Size=new Size(1220,880);MinimumSize=new Size(1040,740);StartPosition=FormStartPosition.CenterParent;
        try{Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{}
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=6};
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,42));root.RowStyles.Add(new RowStyle(SizeType.Absolute,38));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,78));root.RowStyles.Add(new RowStyle(SizeType.Absolute,88));root.RowStyles.Add(new RowStyle(SizeType.Absolute,26));Controls.Add(root);
        var top=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};top.Controls.Add(new Label{Text="Адрес ADB",AutoSize=true,Margin=new Padding(0,8,8,0)});
        address=new TextBox{Text=backend.SavedSerial,Width=178,Margin=new Padding(0,5,8,0),BackColor=card,ForeColor=Color.White,BorderStyle=BorderStyle.FixedSingle};top.Controls.Add(address);
        connect=Btn("Подключить",async()=>await ConnectScreen(),true);disconnect=Btn("Отключить",async()=>await Disconnect(),false);find=Btn("Найти ТВ",async()=>{try{SetStatus("Поиск адреса…",muted);address.Text=await backend.Discover();SetStatus("Адрес найден. Нажми «Подключить».",mint);}catch(Exception ex){SetStatus(ex.Message,Color.Gold);}},false);
        top.Controls.Add(connect);top.Controls.Add(disconnect);top.Controls.Add(find);root.Controls.Add(top,0,0);
        var options=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};quality=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=260,BackColor=card,ForeColor=Color.White};quality.Items.AddRange(new object[]{"Лёгкий · 1024 / 15 кадров/с","Full HD · до 30 кадров/с","Full HD · до 60 кадров/с · 16 Мбит/с","Исходное разрешение · до 60 кадров/с"});quality.SelectedIndex=backend.SavedQuality;
        physical=new CheckBox{Text="Физическая клавиатура",Checked=backend.SavedPhysical,AutoSize=true,Margin=new Padding(14,4,12,0)};physicalMouse=new CheckBox{Text="Мышь ТВ",Checked=backend.SavedMouse,AutoSize=true,Margin=new Padding(0,4,12,0)};separate=new CheckBox{Text="В отдельном окне",Checked=backend.SavedSeparate,AutoSize=true,Margin=new Padding(0,4,0,0)};options.Controls.Add(quality);options.Controls.Add(physical);options.Controls.Add(physicalMouse);options.Controls.Add(separate);options.Controls.Add(Btn("Раскладка ТВ",async()=>{if(!Ready())return;if(!physical.Checked){SetStatus("Для настройки раскладки отключись, включи «Физическая клавиатура» и подключись снова.",muted);return;}await RunKeyAction(()=>backend.AdbRun("-s "+RemoteBackend.Quote(serial)+" shell am start -a android.settings.HARD_KEYBOARD_SETTINGS"));},false));root.Controls.Add(options,0,1);
        screen=new Panel{Dock=DockStyle.Fill,BackColor=Color.Black,Margin=new Padding(0,0,0,8)};hint=new Label{Dock=DockStyle.Fill,Text="Здесь появится экран телевизора\nНужна включённая отладка по сети",TextAlign=ContentAlignment.MiddleCenter,ForeColor=muted,Font=new Font("Segoe UI",14)};screen.Controls.Add(hint);screen.Resize+=(s,e)=>ResizeVideo();root.Controls.Add(screen,0,2);
        var remote=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true};
        foreach(var k in new[]{Tuple.Create("Домой",3),Tuple.Create("Назад",4),Tuple.Create("↑",19),Tuple.Create("↓",20),Tuple.Create("←",21),Tuple.Create("→",22),Tuple.Create("OK",23),Tuple.Create("▶ / Ⅱ",85),Tuple.Create("Громкость −",25),Tuple.Create("Громкость +",24),Tuple.Create("Без звука",164)}){int key=k.Item2;remote.Controls.Add(Btn(k.Item1,async()=>{if(Ready())await RunKeyAction(()=>backend.Key(serial,key));},false));}
        remote.Controls.Add(new Label{Text="Клик по экрану — мышь · Alt — освободить мышь · Стрелки/Enter — пульт · Ctrl+V — вставить текст",AutoSize=true,ForeColor=muted,Margin=new Padding(0,7,0,0)});root.Controls.Add(remote,0,3);
        console=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=card,ForeColor=muted,Font=new Font("Consolas",8),BorderStyle=BorderStyle.None};root.Controls.Add(console,0,4);
        status=new Label{Dock=DockStyle.Fill,ForeColor=muted,AutoEllipsis=true,Text="Готово к подключению",TextAlign=ContentAlignment.MiddleLeft};root.Controls.Add(status,0,5);
        Shown+=async(s,e)=>{try{int v=1;DwmSetWindowAttribute(Handle,20,ref v,4);}catch{}if(autoConnect)await ConnectScreen();
            if(proof!=null){await Task.Delay(5000);if(closing || IsDisposed)return;File.WriteAllText(proof,new JavaScriptSerializer().Serialize(new{running=mirror!=null && !mirror.HasExited,embedded=child!=IntPtr.Zero && GetParent(child)==screen.Handle,serial=serial,status=status.Text,log=console.Text}),Encoding.UTF8);}
        };
        FormClosing+=async(s,e)=>{if(closing)return;Form parent=e.CloseReason==CloseReason.FormOwnerClosing?Owner:null;e.Cancel=true;closing=true;intentional=true;await Disconnect(false);Close();if(parent!=null && !parent.IsDisposed)parent.Close();};
        FormClosed+=(s,e)=>backend.Dispose();
        Action arrange=()=>{if(separate.Checked){root.RowStyles[2].SizeType=SizeType.Absolute;root.RowStyles[2].Height=0;screen.Visible=false;MinimumSize=new Size(1040,380);Size=new Size(1160,400);}else{root.RowStyles[2].SizeType=SizeType.Percent;root.RowStyles[2].Height=100;screen.Visible=true;MinimumSize=new Size(1040,740);Size=new Size(1220,880);}};
        separate.CheckedChanged+=(s,e)=>arrange();arrange();
    }
    Button Btn(string text,Func<Task> action,bool primary){var b=new Button{Text=text,AutoSize=true,Height=31,FlatStyle=FlatStyle.Flat,BackColor=primary?Color.FromArgb(44,151,121):card,ForeColor=Color.White,Padding=new Padding(7,1,7,1),Margin=new Padding(0,0,7,5),Cursor=Cursors.Hand};b.Click+=async(s,e)=>{try{await action();}catch(Exception ex){if(!closing)SetStatus(ex.Message,Color.Gold);}};return b;}
    void SetStatus(string text,Color color){if(closing || IsDisposed)return;status.Text=text;status.ForeColor=color;}
    void Log(string text){
        lock(logLock){try{string path=Path.Combine(Data.Root,"remote.log");if(File.Exists(path) && new FileInfo(path).Length>512000)File.Move(path,path+"."+DateTime.Now.ToString("yyyyMMddHHmmss"));File.AppendAllText(path,DateTime.Now.ToString("HH:mm:ss ")+text+Environment.NewLine,Encoding.UTF8);}catch{}}
        if(closing || IsDisposed || !IsHandleCreated)return;
        try{BeginInvoke((Action)(()=>{if(closing)return;console.AppendText(text+Environment.NewLine);if(console.TextLength>14000)console.Text=console.Text.Substring(console.TextLength-10000);console.SelectionStart=console.TextLength;console.ScrollToCaret();var texture=Regex.Match(text,@"Texture(?: \([^)]*\))?: (\d+x\d+)");if(texture.Success)SetStatus("Экран подключён · "+texture.Groups[1].Value+" · закрытие этого окна остановит захват",mint);}));}catch{}
    }
    bool Ready(){if(string.IsNullOrEmpty(serial) || mirror==null || mirror.HasExited){SetStatus("Сначала подключись к телевизору",Color.Gold);return false;}return true;}
    async Task RunKeyAction(Func<Task<RemoteResult>> action){try{var r=await action();if(r.Code!=0){Log(r.Text);SetStatus("Не удалось отправить команду: "+r.Text.Trim(),Color.Gold);}}catch(Exception ex){SetStatus(ex.Message,Color.Gold);}}
    async Task ConnectScreen(){
        if(connecting || closing)return;Exception failure=null;connecting=true;connect.Enabled=false;find.Enabled=false;disconnect.Enabled=false;
        try{
            await Disconnect(false);if(closing)return;quality.Enabled=false;physical.Enabled=false;physicalMouse.Enabled=false;separate.Enabled=false;address.Enabled=false;
            string target=address.Text.Trim();Log("LAN: "+RemoteBackend.LocalRoute(target));SetStatus("Подключение к "+target+"…",muted);await backend.Connect(target,quality.SelectedIndex,physical.Checked,separate.Checked,physicalMouse.Checked);if(closing)return;serial=target;
            SetStatus("Запуск передачи экрана…",muted);hint.Visible=true;hint.Text="Запускаю экран ТВ…";intentional=false;
            var p=backend.Start(serial,quality.SelectedIndex,physical.Checked,separate.Checked,physicalMouse.Checked,windowTitle,Log);mirror=p;
            p.Exited+=(s,e)=>{if(closing || intentional || !IsHandleCreated)return;try{BeginInvoke((Action)(()=>{if(mirror==p){child=IntPtr.Zero;hint.Visible=true;hint.Text="Передача экрана завершена\nНажми «Подключить», чтобы открыть снова";SetStatus("Связь завершена. Подробности — в журнале выше.",Color.Gold);}}));}catch{}};
            for(int i=0;i<100;i++){
                if(closing)return;if(p.HasExited)throw new Exception("scrcpy завершился. Подробности — в журнале выше.");
                p.Refresh();IntPtr h=p.MainWindowHandle;
                if(h!=IntPtr.Zero){
                    if(!separate.Checked){long style=GetWindowLongPtr(h,-16).ToInt64();style=(style&~0x80000000L&~0x00C00000L&~0x00040000L)|0x40000000L;SetWindowLongPtr(h,-16,new IntPtr(style));SetParent(h,screen.Handle);
                        if(GetParent(h)!=screen.Handle)throw new Exception("Не удалось встроить окно. Выбери «В отдельном окне» и подключись снова.");child=h;hint.Visible=false;ResizeVideo();}
                    else{hint.Text="Экран открыт в отдельном окне\nКнопки пульта здесь тоже работают";SetForegroundWindow(h);}
                    quality.Enabled=false;physical.Enabled=false;separate.Enabled=false;address.Enabled=false;SetStatus("Экран подключён · мышь и клавиатура доступны",mint);Data.Event("Экран ТВ подключён: "+serial+(separate.Checked?" · отдельно":" · встроен"));return;
                }
                await Task.Delay(120);
            }
            throw new Exception("Окно scrcpy не появилось. Проверь журнал подключения.");
        }catch(Exception ex){failure=ex;if(!closing){Log(ex.Message);SetStatus(ex.Message,Color.Gold);hint.Visible=true;hint.Text="Подключение не удалось\nПроверь адрес отладки и журнал ниже";}}
        finally{if(failure==null){connecting=false;if(!closing){connect.Enabled=true;find.Enabled=true;disconnect.Enabled=true;}}}
        if(failure!=null){await Disconnect(false);connecting=false;if(!closing){connect.Enabled=true;find.Enabled=true;disconnect.Enabled=true;}}
    }
    void ResizeVideo(){if(child!=IntPtr.Zero)MoveWindow(child,0,0,Math.Max(1,screen.ClientSize.Width),Math.Max(1,screen.ClientSize.Height),true);}
    async Task Disconnect(bool show=true){
        intentional=true;child=IntPtr.Zero;var p=mirror;mirror=null;
        if(p!=null){try{if(!p.HasExited){PostMessage(p.MainWindowHandle,0x10,IntPtr.Zero,IntPtr.Zero);bool done=await Task.Run(()=>p.WaitForExit(1600));if(!done){p.Kill();await Task.Run(()=>p.WaitForExit(1000));}}p.Dispose();}catch{}}
        if(closing)return;quality.Enabled=true;physical.Enabled=true;physicalMouse.Enabled=true;separate.Enabled=true;address.Enabled=true;hint.Visible=true;hint.Text="Здесь появится экран телевизора";if(show)SetStatus("Захват экрана отключён",muted);
    }
}
