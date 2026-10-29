using System;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SchoolMessenger.Desktop;

public sealed partial class ServerManagementWindow:Window
{
    readonly Api api;readonly StackPanel body=new(){Margin=new Thickness(22)};
    readonly DockPanel hostView=new();
    readonly TextBlock hardware=Label("서버 정보 확인 중"),traffic=Label(""),storage=Label(""),settings=Label(""),feedback=Label("");
    readonly TextBlock cpu=Label("샘플 수집 중",24),memory=Label("샘플 수집 중",24),process=Label("확인 중",24);
    readonly TextBlock memoryNote=Label("",12),processNote=Label("",12);
    readonly ProgressBar cpuBar=new(){Maximum=100,Height=8},memoryBar=new(){Maximum=100,Height=8};
    readonly ComboBox profile=new(){ItemsSource=new[]{"저부하 · 8GB 서버 권장","일반 · 부하 검증 후 사용"},SelectedIndex=0};
    readonly Button apply=new(){Content="부하 설정 적용",Margin=new Thickness(0,12,0,0)};
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(10)};bool busy,loaded;
    public bool Ready{get;private set;}
    internal Window InteractionOwner=>Window.GetWindow(hostView)??this;
    public ServerManagementWindow(Window owner,Api api,bool officeService=true)
    {
        this.api=api;Owner=owner;Resources=owner.Resources;Title="관리자 시스템 · 서버 운영";Width=940;Height=760;MinWidth=760;MinHeight=560;Style=(Style)FindResource("SchoolWindow");
        MaxHeight=SystemParameters.WorkArea.Height-30;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var dock=hostView;dock.Margin=new Thickness(24,18,24,12);Content=dock;
        var footer=new DockPanel{Margin=new Thickness(0,12,0,0)};var refresh=new Button{Content="상태 새로고침",MinHeight=38,Margin=new Thickness(0,0,16,0)};refresh.Click+=async(_,_)=>await Refresh(true);DockPanel.SetDock(refresh,Dock.Left);footer.Children.Add(refresh);footer.Children.Add(feedback);DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);
        var header=new DockPanel{Margin=new Thickness(0,0,0,18)};
        var localControl=new Button{Content="이 PC 서버 시작 · 중지 관리",Margin=new Thickness(0,5,0,10)};
        var localRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
        localControl.IsEnabled=Application.Current.MainWindow is LocalServerWindow||File.Exists(Path.Combine(localRoot,"server","SchoolMessenger.Server.exe"))||File.Exists(Path.Combine(localRoot,"announcement-server","SchoolMessenger.AnnouncementServer.exe"));
        localControl.Click+=(_,_)=>
        {
            if(Application.Current.MainWindow is LocalServerWindow manager){manager.Show();manager.ShowServerControls();manager.Activate();return;}
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--server-manager");Process.Start(start);
        };localControl.Content="이 PC 서버 시작·중지";localControl.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(localControl,Dock.Right);header.Children.Add(localControl);
        var heading=new StackPanel();var title=Label("서버 운영",22);title.FontWeight=FontWeights.Bold;heading.Children.Add(title);heading.Children.Add(Label("접속 서버  "+api.Address,12));header.Children.Add(heading);DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        var tabs=new TabControl{BorderThickness=new Thickness(0)};tabs.SetResourceReference(Control.BackgroundProperty,"Soft");dock.Children.Add(tabs);body.Margin=new Thickness(0,12,0,0);
        tabs.Items.Add(new TabItem{Header="운영 현황",Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        var cards=new Grid();for(var i=0;i<3;i++)cards.ColumnDefinitions.Add(new ColumnDefinition());
        AddCard(cards,0,"PC 전체 CPU",cpu,cpuBar);AddCard(cards,1,"PC 전체 RAM",memory,memoryBar,memoryNote);AddCard(cards,2,"이 서버 프로그램 RAM",process,null,processNote);body.Children.Add(cards);
        var information=new StackPanel();var infoGrid=new Grid();infoGrid.ColumnDefinitions.Add(new ColumnDefinition());infoGrid.ColumnDefinitions.Add(new ColumnDefinition());var machine=new StackPanel{Margin=new Thickness(0,0,20,0)};machine.Children.Add(hardware);machine.Children.Add(traffic);infoGrid.Children.Add(machine);Grid.SetColumn(storage,1);infoGrid.Children.Add(storage);foreach(var label in new[]{hardware,traffic,storage})label.FontSize=12;information.Children.Add(infoGrid);information.Children.Add(Label("네트워크 수치는 같은 PC의 다른 프로그램도 포함합니다.",12));body.Children.Add(Section("하드웨어 · 네트워크 · 저장 공간",information));
        var load=new StackPanel();var loadActions=new DockPanel();apply.MinHeight=38;apply.MinWidth=160;apply.Margin=new Thickness(16,0,0,0);apply.SetResourceReference(StyleProperty,"PrimaryButton");DockPanel.SetDock(apply,Dock.Right);loadActions.Children.Add(apply);loadActions.Children.Add(profile);load.Children.Add(loadActions);load.Children.Add(Label("새 파일 작업의 동시 실행과 전송 속도를 조정합니다. 서버 RAM 전체의 강제 상한은 아닙니다.",12));load.Children.Add(new Expander{Header="전송 제한과 현재 작업",Content=settings,Margin=new Thickness(0,8,0,0)});body.Children.Add(Section("부하 설정",load));
        var school=new StackPanel{Margin=new Thickness(0,16,0,0)};tabs.Items.Add(new TabItem{Header="계정·학교",Content=new ScrollViewer{Content=school,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        var connection=new StackPanel{Margin=new Thickness(0,16,0,0)};tabs.Items.Add(new TabItem{Header="시작·연결",Content=new ScrollViewer{Content=connection,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        if(officeService){var discovery=new Button{Content="교내 서버 자동검색 설정",HorizontalAlignment=HorizontalAlignment.Left};discovery.Click+=async(_,_)=>await OpenDiscovery();connection.Children.Add(Section("교사용 PC 연결",discovery));}
        var boot=new Button{Content="서버 PC 재시작 후 자동 구동 설정",HorizontalAlignment=HorizontalAlignment.Left};boot.Click+=(_,_)=>OpenServerStartup();connection.Children.Add(Section("서버 자동 시작",boot));
        var policy=new Button{Content="학교별 약관 · 개인정보 설정 초안",HorizontalAlignment=HorizontalAlignment.Left};policy.Click+=async(_,_)=>{if(InteractionOwner is LocalServerWindow manager)await manager.OpenAdministration("policy");else new SchoolPolicyWindow(InteractionOwner,api).ShowDialog();};school.Children.Add(Section("학교 운영 정책",policy));
        var securityReview=new Button{Content="보안 도입 검토 기록 확인",HorizontalAlignment=HorizontalAlignment.Left};securityReview.Click+=async(_,_)=>await OpenDeploymentReview();school.Children.Add(Section("보안성 검토 · 외부 서비스",securityReview));
        var directory=new Button{Content="학교 검색 등록자료 저장",HorizontalAlignment=HorizontalAlignment.Left};directory.Click+=async(_,_)=>await ExportSchoolRegistration();school.Children.Add(Section("여러 학교에서 사용 · 학교 검색",directory));
        var requestSchool=new Button{Content="학교 등록 신청 · 개발자 승인",HorizontalAlignment=HorizontalAlignment.Left};requestSchool.Click+=async(_,_)=>await RequestSchoolRegistration();school.Children.Add(Section("학교 등록 신청",requestSchool));
        if(owner is MainWindow main)
        {
            var actions=new WrapPanel();var accounts=new Button{Content="교직원 계정 · 승인",Margin=new Thickness(0,4,8,8)};accounts.Click+=(_,_)=>new OfficeWindow(main,true).Show();actions.Children.Add(accounts);
            var portal=new Button{Content="학생·학부모 관리",Margin=new Thickness(0,4,0,8)};portal.Click+=(_,_)=>new PortalAdministrationWindow(this).Show();actions.Children.Add(portal);school.Children.Insert(0,Section("계정 관리",actions));
        }
        apply.Click+=async(_,_)=>
        {
            if(busy)return;busy=true;Ready=false;apply.IsEnabled=false;
            try{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/server-resources",new{profile=profile.SelectedIndex==0?"low-memory":"balanced"});loaded=false;feedback.Text="설정을 저장했습니다. 새 파일 작업부터 적용됩니다.";}
            catch(ApiRequestException e)when(e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden){Close();return;}
            catch(Exception e){feedback.Text=e.Message;}
            finally{busy=false;}await Refresh(false);
        };
        dock.Loaded+=async(_,_)=>{await Refresh(true);timer.Start();};dock.Unloaded+=(_,_)=>timer.Stop();timer.Tick+=async(_,_)=>{if(tabs.SelectedIndex==0&&InteractionOwner.IsActive)await Refresh(false);};
        Closed+=(_,_)=>timer.Stop();
    }
    static TextBlock Label(string value,int size=14)=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,7)};
    static Border Section(string title,UIElement content)
    {
        var stack=new StackPanel();var heading=Label(title,16);heading.FontWeight=FontWeights.Bold;heading.Margin=new Thickness(0,0,0,14);stack.Children.Add(heading);stack.Children.Add(content);
        var card=new Border{Child=stack,Padding=new Thickness(20),CornerRadius=new CornerRadius(14),Margin=new Thickness(0,0,0,16)};card.SetResourceReference(Border.BackgroundProperty,"Surface");return card;
    }
    static void AddCard(Grid grid,int column,string title,TextBlock value,ProgressBar? bar,TextBlock? note=null)
    {
        var stack=new StackPanel();stack.Children.Add(Label(title,12));stack.Children.Add(value);if(bar is not null)stack.Children.Add(bar);if(note is not null)stack.Children.Add(note);
        var card=new Border{CornerRadius=new CornerRadius(12),Padding=new Thickness(14),Margin=new Thickness(0,8,10,12),BorderThickness=new Thickness(1),Child=stack};
        card.SetResourceReference(Border.BackgroundProperty,"Surface");card.SetResourceReference(Border.BorderBrushProperty,"Line");value.FontWeight=FontWeights.Bold;Grid.SetColumn(card,column);grid.Children.Add(card);
    }
    static string Bytes(double value)=>value>=1073741824?(value/1073741824).ToString("0.0")+" GB":(value/1048576).ToString("0.0")+" MB";
    static string Rate(JsonElement sample,string name)=>sample.GetProperty(name).ValueKind==JsonValueKind.Number?(sample.GetProperty(name).GetDouble()/1024).ToString("0.0")+" KB/s":"수집 중";
    public async Task Refresh(bool manual)
    {
        if(busy)return;busy=true;apply.IsEnabled=false;
        try
        {
            var data=await api.Get<JsonElement>("api/admin/server-resources");var sample=data.GetProperty("sample");var limits=data.GetProperty("limits");var active=data.GetProperty("active");
            var total=sample.GetProperty("totalMemoryBytes").GetDouble();var available=sample.GetProperty("availableMemoryBytes").GetDouble();
            var systemCpu=sample.GetProperty("systemCpuPercent");cpu.Text=systemCpu.ValueKind==JsonValueKind.Number?systemCpu.GetDouble().ToString("0.0")+"%":"수집 중";cpuBar.Value=systemCpu.ValueKind==JsonValueKind.Number?systemCpu.GetDouble():0;
            memoryBar.Value=total>0?100*(total-available)/total:0;memory.Text=total>0?memoryBar.Value.ToString("0.0")+"%":"확인 불가";memoryNote.Text=total>0?Bytes(total-available)+" / "+Bytes(total):"";process.Text=Bytes(sample.GetProperty("processMemoryBytes").GetDouble());processNote.Text="CPU "+(sample.GetProperty("processCpuPercent").ValueKind==JsonValueKind.Number?sample.GetProperty("processCpuPercent").GetDouble().ToString("0.0")+"%":"수집 중");
            hardware.Text=sample.GetProperty("os").GetString()+"\n"+sample.GetProperty("processor").GetString()+" · 논리 프로세서 "+sample.GetProperty("logicalProcessors")+"개\n서버 IPv4: "+string.Join(", ",sample.GetProperty("addresses").EnumerateArray().Select(a=>a.GetString()));
            traffic.Text="PC 네트워크 송신 "+Rate(sample,"networkSendBytesPerSecond")+" · 수신 "+Rate(sample,"networkReceiveBytesPerSecond")+"\n상태 수집: "+data.GetProperty("sampledAt").GetDateTimeOffset().ToLocalTime().ToString("HH:mm:ss")+" · 10초 간격 · 관리자 창 활성 시 갱신";
            storage.Text="데이터 저장 위치: "+sample.GetProperty("dataDirectory").GetString()+"\n첨부 사용량 "+Bytes(sample.GetProperty("filesBytes").GetDouble())+" · 디스크 여유 "+Bytes(sample.GetProperty("freeBytes").GetDouble());
            settings.Text="동시 업로드 "+limits.GetProperty("uploads")+"건 · 다운로드 "+limits.GetProperty("downloads")+"건 · ZIP "+limits.GetProperty("zip")+"건\n진행 중: 업로드 "+active.GetProperty("uploads")+" · 다운로드 "+active.GetProperty("downloads")+" · ZIP "+active.GetProperty("zips")+"\n파일당 전송 속도: "+(limits.GetProperty("downloadBytesPerSecond").GetInt32()>0?Bytes(limits.GetProperty("downloadBytesPerSecond").GetDouble())+"/초":"별도 제한 없음")+"\n공지 이미지 200KB · 최대 5장 · 파일 보관 30일\n저부하 모드: 여유 RAM 512MB 미만, 서버 RAM 768MB 초과 또는 CPU 평균 85% 이상이면 새 파일 작업은 잠시 대기합니다.";
            if(!loaded){profile.SelectedIndex=data.GetProperty("profile").GetString()=="low-memory"?0:1;loaded=true;}
            Ready=true;apply.IsEnabled=true;if(manual)feedback.Text="최신 상태를 확인했습니다.";
        }
        catch(ApiRequestException e)when(e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden){Close();}
        catch(Exception e){Ready=false;cpu.Text=memory.Text=process.Text="확인 불가";hardware.Text=traffic.Text=storage.Text=settings.Text="";feedback.Text=e.Message;}
        finally{busy=false;}
    }
}
