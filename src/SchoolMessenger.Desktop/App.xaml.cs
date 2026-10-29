using System.Windows;
namespace SchoolMessenger.Desktop;
public partial class App : System.Windows.Application
{
    static bool AdministratorMode=>Environment.GetCommandLineArgs().Contains("--server-manager")||(new DirectoryInfo(AppContext.BaseDirectory).Name.Equals("admin",StringComparison.OrdinalIgnoreCase)&&File.Exists(Path.Combine(AppContext.BaseDirectory,"..","VERSION.txt"))&&File.Exists(Path.Combine(AppContext.BaseDirectory,"..","docs","server-installer.md")));
    readonly System.Threading.Mutex running = new(false, AdministratorMode?@"Local\YeoyangMessengerServerManager.Running":@"Local\YeoyangSchoolMessenger.Running");
    System.Threading.Mutex? serverExclusive;
    bool ownsServer;
    protected override void OnExit(ExitEventArgs e) { if(ownsServer)serverExclusive?.ReleaseMutex();serverExclusive?.Dispose();running.Dispose();base.OnExit(e); }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Contains("--configure-startup"))
        {
            try
            {
                var manager=AdministratorMode;var entry=manager?"YeoyangMessengerServerManager":"YeoyangSchoolMessenger";
                var index=Array.IndexOf(e.Args,"--startup-entry-name");
                if(index>=0)
                {
                    var candidate=e.Args.ElementAtOrDefault(index+1)??"";const string test="YyMessengerInstallerTest-";
                    if(candidate!=entry&&(!candidate.StartsWith(test,StringComparison.Ordinal)||!Guid.TryParse(candidate[test.Length..],out _)))throw new InvalidOperationException("자동 실행 항목 이름을 확인하세요.");entry=candidate;
                }
                new WindowsStartup(entryName:entry,command:manager?LocalServerWindow.StartupCommand:WindowsStartup.Command).SetEnabled(e.Args.Contains("enable"));Shutdown();
            }
            catch(Exception error){MessageBox.Show(error.Message,"자동 실행 설정 실패");Shutdown(1);}
            return;
        }
        Appearance.Apply(Appearance.Selected, Appearance.Glass);
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast))
                Dispatcher.Invoke(() => Appearance.Apply(Appearance.Selected, Appearance.Glass));
        };
        if(e.Args.Contains("--verify-server-manager"))
        {
            var root=Environment.GetEnvironmentVariable("YY_MANAGER_ROOT")??throw new InvalidOperationException("시험 배포 경로가 필요합니다.");
            var state=Environment.GetEnvironmentVariable("YY_MANAGER_STATE")??throw new InvalidOperationException("시험 저장 경로가 필요합니다.");
            var window=new LocalServerWindow(root,state,@"Software\YeoyangSchoolMessenger\ManagerTests\"+Path.GetFileName(state));MainWindow=window;
            window.Loaded+=async(_,_)=>await window.Verify(Environment.GetEnvironmentVariable("YY_MANAGER_OUTPUT")??state,e.Args.Contains("--owner-exit"));window.Show();
        }
        else if(AdministratorMode)
        {
            serverExclusive=new System.Threading.Mutex(true,@"Local\YeoyangMessengerServerManager.Exclusive",out ownsServer);
            if(!ownsServer){MessageBox.Show("관리자 프로그램이 이미 실행 중입니다. 알림 영역에서 ‘교무톡 관리자’를 여세요.");Shutdown();return;}
            MainWindow=new LocalServerWindow();MainWindow.Show();
        }
        else{MainWindow=new MainWindow();MainWindow.Show();}
    }
    void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(Window.GetWindow((DependencyObject)sender));
    void MaximizeWindow(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow((DependencyObject)sender);
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
        else SystemCommands.MaximizeWindow(window);
    }
    void CloseWindow(object sender, RoutedEventArgs e) => Window.GetWindow((DependencyObject)sender).Close();
}
