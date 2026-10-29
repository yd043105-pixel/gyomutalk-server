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
        // Index order is kept for saved settings: 0 trial, 1 operation file, 2 simple setup.
        public ComboBox Mode=new();
        public TextBox School=new(),AdminUsername=new(){Text="admin"},Approval=new(){IsReadOnly=true},SimpleData=new(),Backup=new();
        public string ApprovalSha256="",SchoolId="",Fingerprint="";
        public DateTimeOffset? ReviewedAt;
        public StackPanel SimpleFields=new(),Checklist=new();
        public TextBlock Connection=Text("",13);
        public CheckBox Ups=new(){Content="정전 대비 UPS를 설치하고 시험했습니다",Margin=new Thickness(0,6,0,0)};
        public Button Activate=new(){Content="설치하기"};
        public bool Activated=true;
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
        // Dashboard pieces from the administrator design: status badge, connection columns, checklist card and the not-installed state.
        public Border StatusBadge=new(),StatusCard=new(),Empty=new(),ChecklistCard=new(),SettingsCard=new(),ChecklistBadge=AdminUi.Badge("","Neutral",false);
        public TextBlock Summary=AdminUi.Label(""),AddressValue=new(),SchoolValue=AdminUi.Heading(""),SchoolNote=AdminUi.Label(""),EmptyNote=AdminUi.Label("","Muted",13);
        public Grid ConnectionPanel=new(),DetailsGrid=new();
        public string ChecklistKey="";
    }
    record Settings(string Service,bool Trial,string Data,string Config,bool Auto,int Mode=-1,string School="",string AdminUsername="admin",string SimpleData="",string Backup="",string Approval="",string ApprovalSha256="",DateTimeOffset? ReviewedAt=null,string SchoolId="",bool Activated=true,bool Ups=false);
    static bool Simple(ServerCard c)=>c.Mode.SelectedIndex==2;
    static string DataPath(ServerCard c)=>Simple(c)?c.SimpleData.Text:c.Data.Text;
    static bool Initialized(ServerCard c)=>File.Exists(Path.Combine(DataPath(c),".initialized"));
    static TextBlock Text(string value,int size=13)=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,5)};
    internal static string StartupCommand=>WindowsStartup.Command[..^" --startup".Length]+" --server-manager --startup";
    internal static WindowsStartup Startup(string? registry=null)=>new(registry??@"Software\Microsoft\Windows\CurrentVersion\Run","YeoyangMessengerServerManager",StartupCommand);
    public LocalServerWindow():this(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..")),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"YeoyangMessengerServer"),null){}
    internal LocalServerWindow(string installedRoot,string settingsDirectory,string? registry)
    {
        root=Path.GetFullPath(installedRoot);directory=settingsDirectory;Directory.CreateDirectory(directory);startup=Startup(registry);
        Title="교무톡 관리자";Width=Math.Min(1280,SystemParameters.WorkArea.Width-40);Height=Math.Min(820,SystemParameters.WorkArea.Height-25);MinWidth=760;MinHeight=560;MaxHeight=SystemParameters.WorkArea.Height-25;
        Style=(Style)FindResource("SchoolWindow");
        BuildDashboard();
        autoProgram.IsChecked=startup.Enabled;
        autoProgram.Click+=(_,_)=>{try{startup.SetEnabled(autoProgram.IsChecked==true);}catch(Exception e){autoProgram.IsChecked=startup.Enabled;MessageBox.Show(this,e.Message,"자동 실행 설정 실패");}};
        LoadSettings();
        tray=new System.Windows.Forms.NotifyIcon{Icon=Branding.TrayIcon,Text="교무톡 관리자",Visible=true};var menu=new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("관리자 프로그램 열기",null,(_,_)=>Dispatcher.Invoke(()=>{Show();WindowState=WindowState.Normal;Activate();}));menu.Items.Add("서버 중지 후 프로그램 종료",null,async(_,_)=>await Dispatcher.InvokeAsync(Exit).Task.Unwrap());tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(()=>{Show();Activate();});
        Closing+=(_,e)=>{if(!quitting){e.Cancel=true;SaveSettings();Hide();_ = SignOutAdministrators();}};Closed+=(_,_)=>{timer.Stop();sessionTimer.Stop();foreach(var source in adminSessions.Values)source.Dispose();tray.Dispose();};
        sessionTimer.Tick+=async(_,_)=>await CheckAdministratorSessions();
        timer.Tick+=(_,_)=>Refresh();Loaded+=async(_,_)=>{Refresh();timer.Start();sessionTimer.Start();if(!Environment.GetCommandLineArgs().Contains("--verify-server-manager"))await ResumeAfterUpdate();if(cards.All(c=>!c.Host.Installed))await OpenAdministration("members","office");};
    }
    void Refresh()
    {
        autoProgram.IsChecked=startup.Enabled;
        foreach(var c in cards)
        {
            var trial=c.Mode.SelectedIndex==0;var simple=Simple(c);var running=c.Host.Running;var available=c.Host.Installed&&c.Activated;
            c.Controls.Visibility=available?Visibility.Visible:Visibility.Collapsed;
            c.Activate.Visibility=c.Host.Installed&&!c.Activated?Visibility.Visible:Visibility.Collapsed;
            c.Status.Text=!available?"미설치":c.Busy?"처리 중…":running?"실행 중":"중지됨";
            c.NavigationStatus.Text=c.Status.Text;c.Status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,running?"Blue":"Muted");
            c.TrialFields.Visibility=trial?Visibility.Visible:Visibility.Collapsed;c.ProductionFields.Visibility=c.Mode.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;c.SimpleFields.Visibility=simple?Visibility.Visible:Visibility.Collapsed;
            c.PasswordFields.Visibility=(trial||simple)&&!Initialized(c)?Visibility.Visible:Visibility.Collapsed;
            c.AdminUsername.IsEnabled=!Initialized(c);
            c.Data.IsEnabled=c.TrialFields.IsEnabled=c.ProductionFields.IsEnabled=c.SimpleFields.IsEnabled=c.PasswordFields.IsEnabled=c.Mode.IsEnabled=!running&&!c.Busy;
            c.Start.IsEnabled=available&&!running&&!c.Busy;c.Stop.IsEnabled=running&&!c.Busy;c.Admin.IsEnabled=running&&!c.Busy;
            c.Start.Visibility=c.Stop.Visibility=c.Admin.Visibility=available?Visibility.Visible:Visibility.Collapsed;
            c.Admin.Content=adminSessions.ContainsKey(c.Service)?"관리 기능 열기":"관리자 로그인";
            c.Auto.IsEnabled=!c.Busy&&(c.Mode.SelectedIndex==1||Initialized(c));c.Log.Text=c.Host.Output;c.Details.Visibility=string.IsNullOrEmpty(c.Log.Text)?Visibility.Collapsed:Visibility.Visible;
            c.Address.Text=!available?(c.Host.Installed?"필요할 때 ‘설치하기’를 누르면 이 PC에서 사용할 수 있습니다.":"서버 설치 프로그램을 다시 실행해 이 서버를 추가하세요."):trial?(c.Service=="office"?"이 PC 접속 주소: http://localhost:5080":"이 PC 학교 소식 주소: http://localhost:5081"):simple?"교사 PC 접속 주소: "+string.Join(" 또는 ",SimpleOperation.Addresses(c.Service)):"학교 운영 설정의 HTTPS 주소로 접속합니다. 실행 후 관리자 로그인으로 연결을 확인하세요.";
            AdminUi.SetBadge(c.StatusBadge,c.Status.Text,!available?"Neutral":c.Busy?"Warning":running?"Success":"Neutral");c.NavigationStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,running?"Success":"Faint");
            c.Summary.Text=!available?"":trial?"이 PC 시험 · 외부 접속 없음":simple?"학교 운영 · 간편 설정":"학교 운영 · 운영 설정 파일";
            c.StatusCard.Visibility=c.DetailsGrid.Visibility=available?Visibility.Visible:Visibility.Collapsed;c.Empty.Visibility=available?Visibility.Collapsed:Visibility.Visible;
            c.EmptyNote.Text=!c.Host.Installed?"서버 설치 프로그램을 다시 실행해 이 서버를 추가하세요.":"필요할 때 설치하면 됩니다. 교내 업무 서버와 따로 실행되며, 설치 파일은 이미 이 PC에 들어 있습니다.";
            var connected=simple&&c.Fingerprint.Length>0;c.ConnectionPanel.Visibility=connected?Visibility.Visible:Visibility.Collapsed;c.Address.Visibility=connected?Visibility.Collapsed:Visibility.Visible;
            if(connected){c.AddressValue.Text=SimpleOperation.Addresses(c.Service)[0];c.Connection.Text=CertificatePins.Code(c.Fingerprint);c.SchoolValue.Text=c.School.Text.Trim();c.SchoolNote.Text="관리자 아이디 "+c.AdminUsername.Text.Trim();}
            if(simple)BuildChecklist(c);
            ArrangeDetails(c);
        }
        var office=cards.First(c=>c.Service=="office");
        railSchool.Text=Simple(office)&&office.School.Text.Trim().Length>0?office.School.Text.Trim():"교무톡 서버";
        railStatus.Text="교내 업무 서버 "+office.Status.Text;railDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,office.Host.Running?"Success":"Faint");
    }
    async Task StartSelected(){foreach(var card in cards.Where(c=>c.Host.Installed&&c.Auto.IsChecked==true))await Start(card);}
    async Task Start(ServerCard c)
    {
        if(c.Busy||c.Host.Running)return;c.Busy=true;c.Details.IsExpanded=false;Refresh();
        try
        {
            if(c.PasswordFields.Visibility==Visibility.Visible&&c.Password.Password!=c.Confirm.Password)throw new InvalidOperationException("두 비밀번호가 일치하지 않습니다.");
            if(Simple(c)){var (setup,thumbprint,review)=PrepareSimple(c);c.Host.StartSimple(setup,c.Password.Password,thumbprint,review);}
            else c.Host.Start(c.Mode.SelectedIndex==0,c.Data.Text,c.Config.Text,c.Password.Password,c.Mode.SelectedIndex==0&&(c.Service=="announcements"||cards.Any(n=>n.Service=="announcements"&&n.Activated))?LocalServerProcess.TrialBridgeKey(directory):"");
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
        try{var path=Path.Combine(directory,"manager.json");var temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(cards.Select(c=>new Settings(c.Service,c.Mode.SelectedIndex==0,c.Data.Text,c.Config.Text,c.Auto.IsChecked==true,c.Mode.SelectedIndex,c.School.Text.Trim(),c.AdminUsername.Text.Trim(),c.SimpleData.Text,c.Backup.Text,c.Approval.Text,c.ApprovalSha256,c.ReviewedAt,c.SchoolId,c.Activated,c.Ups.IsChecked==true))));File.Move(temp,path,true);}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){MessageBox.Show(this,"서버 관리 설정을 저장하지 못했습니다. 저장 공간과 폴더 권한을 확인하세요.\n"+e.Message,"설정 저장 실패");}
    }
    void LoadSettings()
    {
        loadingSettings=true;
        try
        {
            var path=Path.Combine(directory,"manager.json");if(!File.Exists(path))return;var values=JsonSerializer.Deserialize<Settings[]>(File.ReadAllText(path));
            foreach(var s in values??[]){var c=cards.FirstOrDefault(c=>c.Service==s.Service);if(c is null)continue;c.Data.Text=s.Data;c.Config.Text=s.Config;c.Mode.SelectedIndex=s.Mode is >=0 and <=2?s.Mode:s.Trial?0:1;c.Auto.IsChecked=s.Auto;
                c.School.Text=s.School;c.AdminUsername.Text=s.AdminUsername;if(s.SimpleData.Length>0)c.SimpleData.Text=s.SimpleData;if(s.Backup.Length>0)c.Backup.Text=s.Backup;c.Approval.Text=s.Approval;c.ApprovalSha256=s.ApprovalSha256;c.ReviewedAt=s.ReviewedAt;c.SchoolId=s.SchoolId;c.Activated=s.Activated;c.Ups.IsChecked=s.Ups;}
        }catch(Exception e)when(e is IOException or JsonException){MessageBox.Show(this,"서버 관리 설정을 읽지 못했습니다. 데이터·운영 설정 경로를 다시 확인하세요.");}
        finally{loadingSettings=false;}
    }
}
