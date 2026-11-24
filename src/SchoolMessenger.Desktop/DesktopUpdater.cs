using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;

namespace SchoolMessenger.Desktop;

// WinSparkle downloads and verifies every payload with the embedded product-specific Ed25519 key.
// The app reads the same feed itself only to offer the update in its own window.
internal static class DesktopUpdater
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CanShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl,CharSet=CharSet.Unicode)] delegate int RunInstaller([MarshalAs(UnmanagedType.LPWStr)] string file);
    static readonly CanShutdown canShutdown=Prepare;
    static readonly Shutdown shutdown=Quit;
    static readonly Shutdown failed=OnFailure;
    static readonly RunInstaller installer=Install;
    static Window? owner;
    static string product="",setting="",error="",appcast="";
    static readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(20)};
    static bool checking;
    static bool initialized,prepared,enabled=true;
    // Server PCs whose installer registered the SYSTEM task install updates on their own (see Install-ServerUpdate.ps1).
    const string UpdateTask="GyomuTalk Server Update";
    static bool taskReady,autoInstall=true,installing;
    static UpdateOffer? shown,pending;
    static readonly HttpClient download=new(){Timeout=TimeSpan.FromMinutes(30)};
    static Task<bool>? preparation;
    static string? staged;
    internal static void Start(Window window,bool manager)
    {
        if(Environment.GetCommandLineArgs().Any(a=>a.StartsWith("--verify",StringComparison.Ordinal)))return;
        owner=window;
        try
        {
            using var stream=(manager?typeof(DesktopUpdater).Assembly.GetManifestResourceStream("SchoolMessenger.ServerUpdatePublisher.json"):null)??typeof(DesktopUpdater).Assembly.GetManifestResourceStream("SchoolMessenger.UpdatePublisher.json")??throw new InvalidOperationException("업데이트 게시자 설정이 없습니다.");
            using var config=JsonDocument.Parse(stream);product=config.RootElement.GetProperty("product").GetString()!;
            if(product!=(manager?"server":"windows"))throw new InvalidOperationException("이 설치본의 역할과 업데이트 채널이 일치하지 않습니다.");
            var repository="yd043105-pixel/gyomutalk-"+product;
            if(config.RootElement.GetProperty("repository").GetString()!=repository)throw new InvalidOperationException("업데이트 저장소가 일치하지 않습니다.");
            setting=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GyomutalkUpdater",product,"enabled.txt");
            enabled=!File.Exists(setting)||File.ReadAllText(setting)!="false";
            if(manager){taskReady=TaskRegistered();var choice=Path.Combine(Folder,"automatic.txt");autoInstall=!File.Exists(choice)||File.ReadAllText(choice)!="false";}
            Native.win_sparkle_set_app_details("Gyomutalk",manager?"교무톡 관리자":"교무톡",typeof(App).Assembly.GetName().Version!.ToString(3));
            appcast="https://github.com/"+repository+"/releases/latest/download/appcast.xml";Native.win_sparkle_set_appcast_url(appcast);
            if(Native.win_sparkle_set_eddsa_public_key(config.RootElement.GetProperty("publicKey").GetString()!)!=1)throw new InvalidOperationException("업데이트 서명 키를 확인하세요.");
            Native.win_sparkle_set_lang("ko");Native.win_sparkle_set_update_check_interval(86400);
            // The app schedules its own checks so the offer appears in its own window, not WinSparkle's.
            Native.win_sparkle_set_automatic_check_for_updates(0);
            Native.win_sparkle_set_can_shutdown_callback(canShutdown);Native.win_sparkle_set_shutdown_request_callback(shutdown);
            Native.win_sparkle_set_user_run_installer_callback(installer);
            Native.win_sparkle_set_error_callback(failed);
            Native.win_sparkle_init();initialized=true;
            // Looked for as soon as the program opens, on the sign-in screen, so the offer does not pop up over a name being typed.
            var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
            // Every start looks for a new version once; a program left running looks again a day after the last look.
            var started=true;
            timer.Tick+=(_,_)=>
            {
                timer.Interval=TimeSpan.FromHours(1);
                if(enabled&&(started||DateTimeOffset.UtcNow-LastCheck()>TimeSpan.FromDays(1)))_=Check(owner,false,null,started);
                else if(pending is not null&&Quiet())_=AutoInstall();
                started=false;
            };timer.Start();
            var result=Path.Combine(Path.GetDirectoryName(setting)!,"last-result.json");
            if(File.Exists(result))
            {
                try
                {
                    using var outcome=JsonDocument.Parse(File.ReadAllText(result));var success=outcome.RootElement.GetProperty("success").GetBoolean();File.Delete(result);
                    // A version installed automatically once is tried automatically only once; after a failure it is offered instead.
                    if(success)File.Delete(Path.Combine(Folder,"automatic-attempt.txt"));
                    Application.Current.Dispatcher.BeginInvoke(new Action(()=>Notice.Show(window,success?"프로그램 업데이트 설치를 완료했습니다. 서버관리자는 두 서버의 가동 상태를 확인하세요.":"업데이트 설치가 완료되지 않았습니다. 기존 프로그램으로 돌아왔습니다. 공식 릴리즈 설치파일과 Windows 권한을 확인하세요.","프로그램 업데이트")));
                }
                catch(Exception e)when(e is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { }
            }
        }
        catch(Exception e) when(e is IOException or JsonException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException){error=e.Message;}
    }
    internal static void Settings(Window window)
    {
        if(!initialized){Notice.Show(window,"업데이트 확인을 시작할 수 없습니다.\n"+error,"프로그램 업데이트");return;}
        SettingsWindow(window).ShowDialog();
    }
    // The server-manager check captures the dialog as a server PC with and without the automatic-update task sees it.
    internal static Window PreviewSettings(Window window,bool ready)
    {
        product="server";taskReady=ready;autoInstall=true;setting=Path.Combine(Path.GetTempPath(),"gyomutalk-update-preview","enabled.txt");
        return SettingsWindow(window);
    }
    static Window SettingsWindow(Window window)
    {
        var dialog=new Window{Title="프로그램 업데이트",Owner=window,Width=460,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Style=(Style)Application.Current.FindResource("SchoolWindow")};
        var page=new StackPanel{Margin=new Thickness(24)};var heading=AdminUi.PageTitle("프로그램 업데이트","지금 버전 "+typeof(App).Assembly.GetName().Version!.ToString(3)+" · GitHub의 정식 릴리즈를 확인하고, 설치파일은 서명을 검증한 뒤 실행합니다.");heading.Margin=new Thickness(0,0,0,16);page.Children.Add(heading);
        var panel=new StackPanel();page.Children.Add(AdminUi.Card(panel));dialog.SetResourceReference(Window.BackgroundProperty,"Soft");
        var automatic=new CheckBox{Content="시작할 때와 하루에 한 번 새 버전 자동 확인",IsChecked=enabled};
        automatic.Click+=(_,_)=>{try{Directory.CreateDirectory(Path.GetDirectoryName(setting)!);File.WriteAllText(setting,automatic.IsChecked==true?"true":"false");enabled=automatic.IsChecked==true;}catch(IOException e){automatic.IsChecked=enabled;Notice.Show(dialog,e.Message,"설정 저장 실패");}};panel.Children.Add(automatic);
        if(product=="server")
        {
            var install=new CheckBox{Content="새 버전을 자동으로 설치",IsChecked=taskReady&&autoInstall,IsEnabled=taskReady,Margin=new Thickness(0,10,0,0)};
            install.Click+=(_,_)=>{try{Directory.CreateDirectory(Folder);File.WriteAllText(Path.Combine(Folder,"automatic.txt"),install.IsChecked==true?"true":"false");autoInstall=install.IsChecked==true;if(!autoInstall)pending=null;}catch(IOException e){install.IsChecked=autoInstall;Notice.Show(dialog,e.Message,"설정 저장 실패");}};panel.Children.Add(install);
            var when=AdminUi.Label(taskReady?"관리자 프로그램을 켤 때나 평일 저녁 6시~아침 7시·주말에 새 버전이 보이면, 서버를 멈추고 백업한 뒤 설치하고 다시 켭니다. Windows 확인 창은 뜨지 않습니다.":"이 PC에서는 쓸 수 없습니다. 서버 설치파일을 기본 설치 폴더에 한 번 다시 설치하면 켜집니다.","Muted",12);
            when.TextWrapping=TextWrapping.Wrap;when.Margin=new Thickness(26,4,0,0);panel.Children.Add(when);
        }
        var note=AdminUi.Label("학교 네트워크에서 GitHub 연결을 허용해야 합니다.","Muted",12);note.Margin=new Thickness(0,8,0,0);panel.Children.Add(note);
        var check=new Button{Content="지금 확인",HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};check.SetResourceReference(FrameworkElement.StyleProperty,"PrimaryButton");check.Click+=async(_,_)=>{check.IsEnabled=false;check.Content="확인하는 중…";await Check(dialog,true,dialog.Close);};panel.Children.Add(check);dialog.Content=page;return dialog;
    }
    static string Folder=>Path.GetDirectoryName(setting)!;
    static DateTimeOffset LastCheck(){try{return DateTimeOffset.FromUnixTimeSeconds(long.Parse(File.ReadAllText(Path.Combine(Folder,"last-check.txt"))));}catch(Exception e)when(e is IOException or FormatException or OverflowException or ArgumentException){return DateTimeOffset.MinValue;}}
    static async Task Check(Window? from,bool manual,Action? before=null,bool startup=false)
    {
        if(checking){before?.Invoke();return;}checking=true;
        var current=typeof(App).Assembly.GetName().Version!.ToString(3);
        try
        {
            var offer=UpdateOffer.Parse(await http.GetStringAsync(appcast));
            try{Directory.CreateDirectory(Folder);File.WriteAllText(Path.Combine(Folder,"last-check.txt"),DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());}catch(IOException){}
            before?.Invoke();from=from is {IsVisible:true}?from:owner;
            if(offer is null||offer.Version<=Version.Parse(current)){if(manual)Notice.Show(from,"최신 버전("+current+")을 사용하고 있습니다.","프로그램 업데이트");return;}
            if(!manual&&taskReady&&autoInstall&&!TriedAutomatically(offer))
            {
                // Installed right away when the program has just opened, otherwise in the evening, at night or at the weekend.
                pending=offer;if(startup||Quiet())await AutoInstall();return;
            }
            var skipped=Path.Combine(Folder,"skipped.txt");
            if(!manual&&File.Exists(skipped)&&File.ReadAllText(skipped)==offer.Version.ToString())return;
            shown=offer;
            switch(ShowOffer(from,offer,current,product=="server"?"교무톡 관리자":"교무톡"))
            {
                // WinSparkle fetches the same feed again, verifies the signature and shows download progress.
                case "install":Native.win_sparkle_check_update_with_ui_and_install();break;
                case "skip":try{File.WriteAllText(skipped,offer.Version.ToString());}catch(IOException){}break;
            }
        }
        catch(Exception e)when(e is HttpRequestException or TaskCanceledException or XmlException or System.Text.Json.JsonException or FormatException or ArgumentException or OverflowException or IOException)
        {before?.Invoke();if(manual)Notice.Show(from is {IsVisible:true}?from:owner,"새 버전을 확인하지 못했습니다. 학교 네트워크에서 GitHub 연결을 허용했는지 확인하세요.","프로그램 업데이트");}
        finally{checking=false;}
    }
    internal sealed record UpdateOffer(Version Version,IReadOnlyList<(string Name,string[] Items)> Sections,string Url="",string Signature="",long Length=0)
    {
        const string Sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle",Notes="https://gyomutalk-downloads.vercel.app/ns/update";
        internal static UpdateOffer? Parse(string feed)
        {
            using var reader=XmlReader.Create(new StringReader(feed),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});
            return XDocument.Load(reader).Descendants("item").Select(item=>
            {
                var enclosure=item.Element("enclosure");
                var version=enclosure?.Attribute(XName.Get("shortVersionString",Sparkle))?.Value;
                if(version is null)return null;
                var notes=item.Element(XName.Get("notes",Notes))?.Value;
                var sections=new List<(string,string[])>();
                if(!string.IsNullOrWhiteSpace(notes)){using var json=System.Text.Json.JsonDocument.Parse(notes);foreach(var section in json.RootElement.EnumerateObject())sections.Add((section.Name,section.Value.EnumerateArray().Select(n=>n.GetString()!).ToArray()));}
                return new UpdateOffer(Version.Parse(version),sections,enclosure!.Attribute("url")?.Value??"",enclosure.Attribute(XName.Get("edSignature",Sparkle))?.Value??"",long.TryParse(enclosure.Attribute("length")?.Value,out var length)?length:0);
            }).Where(o=>o is not null).MaxBy(o=>o!.Version);
        }
    }
    // Returns "install", "skip" or "later".
    internal static string ShowOffer(Window? from,UpdateOffer offer,string current,string app,Action<Window>? shown=null)
    {
        var choice="later";
        var dialog=new Window{Title="프로그램 업데이트",Width=460,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=from is null,
            WindowStartupLocation=from is {IsLoaded:true}?WindowStartupLocation.CenterOwner:WindowStartupLocation.CenterScreen,Style=(Style)Application.Current.FindResource("SchoolWindow")};
        if(from is {IsLoaded:true})dialog.Owner=from;
        TextBlock Text(string text,double size,string brush,FontWeight weight)
        {var block=new TextBlock{Text=text,FontSize=size,FontWeight=weight,TextWrapping=TextWrapping.Wrap};block.SetResourceReference(TextBlock.ForegroundProperty,brush);return block;}
        var layout=new DockPanel{Margin=new Thickness(24,22,24,20)};
        var header=new StackPanel{Margin=new Thickness(0,0,0,16)};DockPanel.SetDock(header,Dock.Top);
        var badge=new Border{CornerRadius=new CornerRadius(10),Padding=new Thickness(9,2,9,3),HorizontalAlignment=HorizontalAlignment.Left,Child=Text("새 버전 "+offer.Version.ToString(3),12,"Blue",FontWeights.SemiBold)};
        badge.SetResourceReference(Border.BackgroundProperty,"Pale");header.Children.Add(badge);
        var title=Text(app+"을 업데이트할 수 있습니다",18,"Ink",FontWeights.SemiBold);title.Margin=new Thickness(0,10,0,3);header.Children.Add(title);
        header.Children.Add(Text("지금 사용 중인 버전은 "+current+"입니다.",12.5,"Muted",FontWeights.Normal));
        var footer=new StackPanel{Margin=new Thickness(0,14,0,0)};DockPanel.SetDock(footer,Dock.Bottom);
        footer.Children.Add(Text("설치파일의 게시자 서명을 확인한 뒤 설치합니다. 설치하는 동안 프로그램이 잠시 닫힙니다.",12,"Muted",FontWeights.Normal));
        var actions=new DockPanel{Margin=new Thickness(0,18,0,0),LastChildFill=false};footer.Children.Add(actions);
        Button Action(string label,string value,string? style,Dock dock)
        {var button=new Button{Content=label,MinWidth=76,Margin=new Thickness(dock==Dock.Right?8:0,0,0,0)};if(style is not null)button.SetResourceReference(FrameworkElement.StyleProperty,style);button.Click+=(_,_)=>{choice=value;dialog.Close();};DockPanel.SetDock(button,dock);actions.Children.Add(button);return button;}
        Action("이 버전 건너뛰기","skip","SubtleButton",Dock.Left);
        Action("업데이트 설치","install","PrimaryButton",Dock.Right).IsDefault=true;
        Action("나중에","later",null,Dock.Right).IsCancel=true;
        var notes=new StackPanel();
        foreach(var (name,items) in offer.Sections)
        {
            var heading=Text(name,13,"Blue",FontWeights.SemiBold);heading.Margin=new Thickness(0,notes.Children.Count==0?0:12,0,4);notes.Children.Add(heading);
            foreach(var item in items)
            {
                var row=new DockPanel{Margin=new Thickness(0,2,0,2)};var dot=Text("•",13,"Muted",FontWeights.Normal);dot.Width=14;DockPanel.SetDock(dot,Dock.Left);row.Children.Add(dot);
                var line=Text(item,13,"Ink",FontWeights.Normal);line.LineHeight=20;row.Children.Add(line);notes.Children.Add(row);
            }
        }
        if(notes.Children.Count==0)notes.Children.Add(Text("변경 내용은 GitHub 릴리즈 페이지에서 확인할 수 있습니다.",13,"Muted",FontWeights.Normal));
        var card=new Border{CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),Padding=new Thickness(16,14,16,14),
            Child=new ScrollViewer{MaxHeight=Math.Min(320,SystemParameters.WorkArea.Height-320),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=notes}};
        card.SetResourceReference(Border.BackgroundProperty,"Canvas");card.SetResourceReference(Border.BorderBrushProperty,"Line");
        layout.Children.Add(header);layout.Children.Add(footer);layout.Children.Add(card);dialog.Content=layout;
        if(shown is not null)dialog.ContentRendered+=(_,_)=>shown(dialog);
        dialog.ShowDialog();return choice;
    }
    static int Prepare()
    {
        try{var result=Application.Current.Dispatcher.InvokeAsync(()=>preparation??=PrepareOnUi()).Task.Unwrap().GetAwaiter().GetResult();if(!result)Application.Current.Dispatcher.Invoke(()=>preparation=null);return result?1:0;}
        catch{return 0;}
    }
    static async Task<bool> PrepareOnUi()
    {
        if(owner is LocalServerWindow manager)return prepared=await manager.PrepareForUpdate();
        if(owner is null)return false;
        if(owner is MainWindow messenger ? messenger.HasOpenWork : owner.OwnedWindows.Cast<Window>().Any(w=>w.IsVisible))
        {Notice.Show(owner,"작성 중인 메시지와 다른 업무 창을 저장하고 닫은 뒤 업데이트를 다시 실행하세요.","업데이트 대기");preparation=null;return false;}
        return prepared=true;
    }
    static int Install(string verifiedFile)=>Stage(verifiedFile,shown?.Signature??"",false);
    static int Stage(string verifiedFile,string signature,bool unattended)
    {
        // Run a shipped helper after this process exits, avoiding installer file locks.
        try
        {
            if(!prepared)return -1;
            var cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GyomutalkUpdater",product,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(cache);
            staged=Path.Combine(cache,"update.exe");File.Copy(verifiedFile,staged,false);
            using var payload=File.OpenRead(staged);var hash=Convert.ToHexString(SHA256.HashData(payload));
            var helper=Path.Combine(AppContext.BaseDirectory,"Finish-DesktopUpdate.ps1");
            var copy=Path.Combine(cache,"Finish-DesktopUpdate.ps1");File.Copy(helper,copy,false);
            using var parent=Process.GetCurrentProcess();
            var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true};
            foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",copy,"-ParentId",parent.Id.ToString(),"-ParentStart",parent.StartTime.ToUniversalTime().Ticks.ToString(),"-Installer",staged,"-ExpectedHash",hash,"-Application",Environment.ProcessPath!,"-Product",product})start.ArgumentList.Add(arg);
            // The SYSTEM task checks this signature itself; the helper falls back to the administrator prompt without it.
            if(signature.Length>0){start.ArgumentList.Add("-Signature");start.ArgumentList.Add(signature);}
            if(unattended)start.ArgumentList.Add("-Unattended");
            using var process=Process.Start(start)??throw new IOException("업데이트 설치 도우미를 열지 못했습니다.");return 1;
        }
        catch(Exception e)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(async()=>{if(owner is LocalServerWindow manager)await manager.CancelUpdate();preparation=null;prepared=false;Notice.Show(owner,e.Message,"업데이트 시작 실패");}));return -1;
        }
    }
    static bool Quiet(){var now=DateTime.Now;return now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday||now.Hour>=18||now.Hour<7;}
    static bool TriedAutomatically(UpdateOffer offer){try{return File.ReadAllText(Path.Combine(Folder,"automatic-attempt.txt"))==offer.Version.ToString();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}}
    static bool TaskRegistered()
    {
        try
        {
            using var query=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"schtasks.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,ArgumentList={"/Query","/TN",UpdateTask}});
            if(query is null)return false;query.StandardOutput.ReadToEnd();query.StandardError.ReadToEnd();return query.WaitForExit(10000)&&query.ExitCode==0;
        }
        catch(Exception e)when(e is System.ComponentModel.Win32Exception or IOException){return false;}
    }
    // Downloads the release named in the feed, then the 관리자 프로그램 stops and backs up the servers as for a clicked update.
    static async Task AutoInstall()
    {
        if(installing||pending is not {} offer||owner is not LocalServerWindow manager)return;
        installing=true;string? cache=null;
        try
        {
            if(!offer.Url.StartsWith("https://github.com/yd043105-pixel/gyomutalk-"+product+"/releases/download/",StringComparison.Ordinal)||offer.Signature.Length==0||offer.Length<=0){pending=null;return;}
            // Someone working in the program: look again in an hour.
            if(manager.UpdateBusy)return;
            cache=Path.Combine(Folder,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(cache);var file=Path.Combine(cache,"update.exe");
            using(var response=await download.GetAsync(offer.Url,HttpCompletionOption.ResponseHeadersRead))
            {response.EnsureSuccessStatusCode();await using var target=File.Create(file);await response.Content.CopyToAsync(target);}
            if(new FileInfo(file).Length!=offer.Length)return;
            if(manager.UpdateBusy)return;
            pending=null;
            if(!(prepared=await manager.PrepareForUpdate(true)))return;
            File.WriteAllText(Path.Combine(Folder,"automatic-attempt.txt"),offer.Version.ToString());
            if(Stage(file,offer.Signature,true)==1)Quit();
        }
        catch(Exception e)when(e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException){}
        finally{installing=false;if(cache is not null)try{Directory.Delete(cache,true);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){}}
    }
    static void Quit()=>Application.Current.Dispatcher.BeginInvoke(new Action(()=>{if(owner is LocalServerWindow manager)manager.CompleteUpdateShutdown();else Application.Current.Shutdown();}));
    static void OnFailure()=>Application.Current.Dispatcher.BeginInvoke(new Action(async()=>{if(prepared&&owner is LocalServerWindow manager)await manager.CancelUpdate();prepared=false;preparation=null;}));
    internal static void Cleanup(){if(initialized){Native.win_sparkle_cleanup();initialized=false;}}
    static class Native
    {
        const string Library="WinSparkle.dll";
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_init();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_cleanup();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]internal static extern void win_sparkle_set_app_details(string company,string app,string version);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_appcast_url([MarshalAs(UnmanagedType.LPUTF8Str)]string url);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int win_sparkle_set_eddsa_public_key([MarshalAs(UnmanagedType.LPUTF8Str)]string key);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_lang([MarshalAs(UnmanagedType.LPUTF8Str)]string lang);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_update_check_interval(int seconds);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_automatic_check_for_updates(int enabled);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_check_update_with_ui_and_install();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_can_shutdown_callback(CanShutdown callback);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_shutdown_request_callback(Shutdown callback);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_user_run_installer_callback(RunInstaller callback);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_error_callback(Shutdown callback);
    }
}
