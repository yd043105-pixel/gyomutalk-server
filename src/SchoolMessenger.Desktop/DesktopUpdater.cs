using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

// WinSparkle verifies every payload with the embedded product-specific Ed25519 key.
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
    static string product="",setting="",error="";
    static bool initialized,prepared,enabled=true;
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
            Native.win_sparkle_set_app_details("Gyomutalk",manager?"교무톡 관리자":"교무톡",typeof(App).Assembly.GetName().Version!.ToString(3));
            Native.win_sparkle_set_appcast_url("https://github.com/"+repository+"/releases/latest/download/appcast.xml");
            if(Native.win_sparkle_set_eddsa_public_key(config.RootElement.GetProperty("publicKey").GetString()!)!=1)throw new InvalidOperationException("업데이트 서명 키를 확인하세요.");
            Native.win_sparkle_set_lang("ko");Native.win_sparkle_set_update_check_interval(86400);
            Native.win_sparkle_set_automatic_check_for_updates(enabled?1:0);
            Native.win_sparkle_set_can_shutdown_callback(canShutdown);Native.win_sparkle_set_shutdown_request_callback(shutdown);
            Native.win_sparkle_set_user_run_installer_callback(installer);
            Native.win_sparkle_set_error_callback(failed);
            Native.win_sparkle_init();initialized=true;
            var result=Path.Combine(Path.GetDirectoryName(setting)!,"last-result.json");
            if(File.Exists(result))
            {
                try
                {
                    using var outcome=JsonDocument.Parse(File.ReadAllText(result));var success=outcome.RootElement.GetProperty("success").GetBoolean();File.Delete(result);
                    Application.Current.Dispatcher.BeginInvoke(new Action(()=>MessageBox.Show(window,success?"프로그램 업데이트 설치를 완료했습니다. 서버관리자는 두 서버의 가동 상태를 확인하세요.":"업데이트 설치가 완료되지 않았습니다. 기존 프로그램으로 돌아왔습니다. 공식 릴리즈 설치파일과 Windows 권한을 확인하세요.","프로그램 업데이트")));
                }
                catch(Exception e)when(e is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { }
            }
        }
        catch(Exception e) when(e is IOException or JsonException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException){error=e.Message;}
    }
    internal static void Settings(Window window)
    {
        if(!initialized){MessageBox.Show(window,"업데이트 확인을 시작할 수 없습니다.\n"+error,"프로그램 업데이트");return;}
        var dialog=new Window{Title="프로그램 업데이트",Owner=window,Width=420,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Thickness(24)};
        panel.Children.Add(new TextBlock{Text="GitHub의 정식 공개 릴리즈를 확인합니다. 설치파일은 게시자 서명을 검증한 뒤 실행합니다.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)});
        var automatic=new CheckBox{Content="하루에 한 번 새 버전 자동 확인",IsChecked=enabled};
        automatic.Click+=(_,_)=>{try{Directory.CreateDirectory(Path.GetDirectoryName(setting)!);File.WriteAllText(setting,automatic.IsChecked==true?"true":"false");enabled=automatic.IsChecked==true;Native.win_sparkle_set_automatic_check_for_updates(enabled?1:0);}catch(IOException e){automatic.IsChecked=enabled;MessageBox.Show(dialog,e.Message,"설정 저장 실패");}};panel.Children.Add(automatic);
        panel.Children.Add(new TextBlock{Text="첫 공개 릴리즈 전에는 새 버전이 제공되지 않습니다. 학교 네트워크에서 GitHub 연결을 허용해야 합니다.",TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,16,0,16)});
        var check=new Button{Content="지금 업데이트 확인",Padding=new Thickness(14,9,14,9)};check.Click+=(_,_)=>{dialog.Close();Native.win_sparkle_check_update_with_ui();};panel.Children.Add(check);dialog.Content=panel;dialog.ShowDialog();
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
        if(owner.OwnedWindows.Cast<Window>().Any(w=>w.IsVisible))
        {MessageBox.Show(owner,"작성 중인 메시지와 다른 업무 창을 저장하고 닫은 뒤 업데이트를 다시 실행하세요.","업데이트 대기");preparation=null;return false;}
        return prepared=true;
    }
    static int Install(string verifiedFile)
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
            foreach(var arg in new[]{"-NoProfile","-NonInteractive","-File",copy,"-ParentId",parent.Id.ToString(),"-ParentStart",parent.StartTime.ToUniversalTime().Ticks.ToString(),"-Installer",staged,"-ExpectedHash",hash,"-Application",Environment.ProcessPath!,"-Product",product})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)??throw new IOException("업데이트 설치 도우미를 열지 못했습니다.");return 1;
        }
        catch(Exception e)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(async()=>{if(owner is LocalServerWindow manager)await manager.CancelUpdate();preparation=null;prepared=false;MessageBox.Show(owner,e.Message,"업데이트 시작 실패");}));return -1;
        }
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
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_check_update_with_ui();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_can_shutdown_callback(CanShutdown callback);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_shutdown_request_callback(Shutdown callback);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_user_run_installer_callback(RunInstaller callback);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern void win_sparkle_set_error_callback(Shutdown callback);
    }
}
