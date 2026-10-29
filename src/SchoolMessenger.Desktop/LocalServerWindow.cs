using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SchoolMessenger.Desktop;

public sealed partial class LocalServerWindow:Window
{
    readonly string root,directory;
    readonly List<ServerCard> cards=new();
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(3)};
    readonly System.Windows.Forms.NotifyIcon tray;
    readonly WindowsStartup startup;
    readonly Grid serverViews=new();
    bool quitting,loadingSettings;
    readonly CheckBox autoProgram=new(){Content="Windows 로그인 시 서버 관리 프로그램 자동 실행",Margin=new Thickness(0,8,0,12)};
    sealed class ServerCard(string service,LocalServerProcess host)
    {
        public string Service=service;
        public LocalServerProcess Host=host;
        public ComboBox Mode=new(){ItemsSource=new[]{"이 PC 시험 · 외부 접속 없음","학교 운영 · 준비된 운영 설정 사용"},SelectedIndex=0};
        public TextBox Data=new(),Config=new(){IsReadOnly=true},Log=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=100,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        public TextBlock Status=Text("중지됨",16),Address=Text("",12);
        public PasswordBox Password=new(),Confirm=new();
        public StackPanel PasswordFields=new(),TrialFields=new(),ProductionFields=new();
        public StackPanel Controls=new();
        public Grid View=new();
        public Button Navigation=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch};
        public TextBlock NavigationStatus=Text("중지됨",12);
        public Expander Details=new(){Header="상세 실행 기록",Margin=new Thickness(0,6,0,0)};
        public CheckBox Auto=new(){Content="관리 프로그램을 열면 이 서버도 자동 시작",Margin=new Thickness(0,8,0,8)};
        public Button Start=new(){Content="서버 시작"},Stop=new(){Content="서버 중지"},Admin=new(){Content="관리자 기능 열기"};
        public bool Busy;
    }
    record Settings(string Service,bool Trial,string Data,string Config,bool Auto);
    static TextBlock Text(string value,int size=13)=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,5)};
    internal static string StartupCommand=>WindowsStartup.Command[..^" --startup".Length]+" --server-manager --startup";
    internal static WindowsStartup Startup(string? registry=null)=>new(registry??@"Software\Microsoft\Windows\CurrentVersion\Run","YeoyangMessengerServerManager",StartupCommand);
    public LocalServerWindow():this(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..")),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"YeoyangMessengerServer"),null){}
    internal LocalServerWindow(string installedRoot,string settingsDirectory,string? registry)
    {
        root=Path.GetFullPath(installedRoot);directory=settingsDirectory;Directory.CreateDirectory(directory);startup=Startup(registry);
        Title="교무톡 관리자";Width=1060;Height=780;MinWidth=760;MinHeight=560;MaxHeight=SystemParameters.WorkArea.Height-25;
        Style=(Style)FindResource("SchoolWindow");
        BuildDashboard();
        autoProgram.IsChecked=startup.Enabled;
        autoProgram.Click+=(_,_)=>{try{startup.SetEnabled(autoProgram.IsChecked==true);}catch(Exception e){autoProgram.IsChecked=startup.Enabled;MessageBox.Show(this,e.Message,"자동 실행 설정 실패");}};
        LoadSettings();
        tray=new System.Windows.Forms.NotifyIcon{Icon=Branding.TrayIcon,Text="교무톡 관리자",Visible=true};var menu=new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("관리자 프로그램 열기",null,(_,_)=>Dispatcher.Invoke(()=>{Show();WindowState=WindowState.Normal;Activate();}));menu.Items.Add("서버 중지 후 프로그램 종료",null,async(_,_)=>await Dispatcher.InvokeAsync(Exit).Task.Unwrap());tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(()=>{Show();Activate();});
        Closing+=(_,e)=>{if(!quitting){e.Cancel=true;SaveSettings();Hide();_ = SignOutAdministrators();}};Closed+=(_,_)=>{timer.Stop();sessionTimer.Stop();foreach(var source in adminSessions.Values)source.Dispose();tray.Dispose();};
        sessionTimer.Tick+=async(_,_)=>await CheckAdministratorSessions();
        timer.Tick+=(_,_)=>Refresh();Loaded+=async(_,_)=>{Refresh();timer.Start();sessionTimer.Start();if(!Environment.GetCommandLineArgs().Contains("--verify-server-manager"))await StartSelected();if(cards.All(c=>!c.Host.Installed))await OpenAdministration("members","office");};
    }
    void Refresh()
    {
        autoProgram.IsChecked=startup.Enabled;
        foreach(var c in cards)
        {
            var trial=c.Mode.SelectedIndex==0;var running=c.Host.Running;
            c.Controls.Visibility=c.Host.Installed?Visibility.Visible:Visibility.Collapsed;
            c.Status.Text=!c.Host.Installed?"이 역할은 설치되지 않았습니다":c.Busy?"처리 중…":running?"실행 중":"중지됨";
            c.NavigationStatus.Text=c.Status.Text;c.Status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,running?"Blue":"Muted");
            c.TrialFields.Visibility=trial?Visibility.Visible:Visibility.Collapsed;c.ProductionFields.Visibility=trial?Visibility.Collapsed:Visibility.Visible;
            c.PasswordFields.Visibility=trial&&!File.Exists(Path.Combine(c.Data.Text,".initialized"))?Visibility.Visible:Visibility.Collapsed;
            c.Data.IsEnabled=c.TrialFields.IsEnabled=c.ProductionFields.IsEnabled=c.Mode.IsEnabled=!running&&!c.Busy;
            c.Start.IsEnabled=c.Host.Installed&&!running&&!c.Busy;c.Stop.IsEnabled=running&&!c.Busy;c.Admin.IsEnabled=running&&!c.Busy;
            c.Admin.Content=adminSessions.ContainsKey(c.Service)?"관리 기능 열기":"관리자 로그인";
            c.Auto.IsEnabled=!c.Busy&&(!trial||File.Exists(Path.Combine(c.Data.Text,".initialized")));c.Log.Text=c.Host.Output;c.Details.Visibility=string.IsNullOrEmpty(c.Log.Text)?Visibility.Collapsed:Visibility.Visible;
            c.Address.Text=trial?(c.Service=="office"?"이 PC 접속 주소: http://localhost:5080":"이 PC 학교 소식 주소: http://localhost:5081"):"학교 운영 설정의 HTTPS 주소로 접속합니다. 실행 후 관리자 로그인으로 연결을 확인하세요.";
        }
    }
    async Task StartSelected(){foreach(var card in cards.Where(c=>c.Host.Installed&&c.Auto.IsChecked==true))await Start(card);}
    async Task Start(ServerCard c)
    {
        if(c.Busy||c.Host.Running)return;c.Busy=true;c.Details.IsExpanded=false;Refresh();
        try
        {
            if(c.Mode.SelectedIndex==0&&c.PasswordFields.Visibility==Visibility.Visible&&c.Password.Password!=c.Confirm.Password)throw new InvalidOperationException("두 비밀번호가 일치하지 않습니다.");
            c.Host.Start(c.Mode.SelectedIndex==0,c.Data.Text,c.Config.Text,c.Password.Password,c.Mode.SelectedIndex==0?LocalServerProcess.TrialBridgeKey(directory):"");
            await Task.Delay(700);if(!c.Host.Running)throw new InvalidOperationException("서버를 시작하지 못했습니다. 아래 오류를 확인하세요.");SaveSettings();
        }
        catch(Exception e){c.Host.WriteOutput(e.Message);SelectCard(c);c.Details.IsExpanded=true;_ = Dispatcher.BeginInvoke(new Action(()=>c.Details.BringIntoView()));}
        finally{c.Password.Clear();c.Confirm.Clear();c.Busy=false;Refresh();}
    }
    async Task Stop(ServerCard c)
    {
        if(c.Busy)return;
        if(MessageBox.Show(this,"연결된 사용자의 서비스가 중단됩니다. 이 서버를 중지할까요?","서버 중지",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        c.Busy=true;Refresh();try{await SignOutAdministrator(c.Service);await c.Host.Stop();}catch(Exception e){c.Host.WriteOutput(e.Message);}finally{c.Busy=false;Refresh();}
    }
    void OpenAdmin(ServerCard c)
    {
        _ = OpenAdministration(c.Service=="office"?"members":"portal",c.Service);
    }
    async Task Exit()
    {
        if(cards.Any(c=>c.Busy))return;
        if(cards.Any(c=>c.Host.Running)&&MessageBox.Show(this,"가동 중인 서버를 정상 중지한 뒤 관리 프로그램을 종료할까요?","프로그램 종료",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        try{await SignOutAdministrators();foreach(var c in cards){c.Busy=true;Refresh();await c.Host.Stop();}SaveSettings();quitting=true;Close();Application.Current.Shutdown();}
        catch(Exception e){MessageBox.Show(this,e.Message,"종료 대기");}finally{foreach(var c in cards)c.Busy=false;Refresh();}
    }
    void SaveSettings()
    {
        if(loadingSettings)return;
        try{var path=Path.Combine(directory,"manager.json");var temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(cards.Select(c=>new Settings(c.Service,c.Mode.SelectedIndex==0,c.Data.Text,c.Config.Text,c.Auto.IsChecked==true))));File.Move(temp,path,true);}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){MessageBox.Show(this,"서버 관리 설정을 저장하지 못했습니다. 저장 공간과 폴더 권한을 확인하세요.\n"+e.Message,"설정 저장 실패");}
    }
    void LoadSettings()
    {
        loadingSettings=true;
        try
        {
            var path=Path.Combine(directory,"manager.json");if(!File.Exists(path))return;var values=JsonSerializer.Deserialize<Settings[]>(File.ReadAllText(path));
            foreach(var s in values??[]){var c=cards.FirstOrDefault(c=>c.Service==s.Service);if(c is null)continue;c.Data.Text=s.Data;c.Config.Text=s.Config;c.Mode.SelectedIndex=s.Trial?0:1;c.Auto.IsChecked=s.Auto;}
        }catch(Exception e)when(e is IOException or JsonException){MessageBox.Show(this,"서버 관리 설정을 읽지 못했습니다. 데이터·운영 설정 경로를 다시 확인하세요.");}
        finally{loadingSettings=false;}
    }
}
