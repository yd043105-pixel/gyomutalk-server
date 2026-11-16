using System;
using System.IO;
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
    readonly TextBlock cpu=Label("측정 중",22),memory=Label("측정 중",22),process=Label("확인 중",22);
    readonly TextBlock memoryNote=Label("",12),processNote=Label("",12);
    readonly ProgressBar cpuBar=new(){Maximum=100,Height=8},memoryBar=new(){Maximum=100,Height=8};
    readonly ComboBox profile=new(){ItemsSource=new[]{"저부하 · 8GB 서버 권장","일반 · 부하 검증 후 사용"},SelectedIndex=0};
    readonly Button apply=new(){Content="적용"};
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(10)};bool busy,loaded;
    public bool Ready{get;private set;}
    internal Window InteractionOwner=>Window.GetWindow(hostView)??this;
    public ServerManagementWindow(Window owner,Api api,bool officeService=true)
    {
        this.api=api;if(owner is MainWindow workOwner)workOwner.RegisterWorkWindow(this);else Owner=owner;Resources=owner.Resources;Title="관리자 시스템 · 서버 운영";Width=940;Height=760;MinWidth=760;MinHeight=560;Style=(Style)FindResource("SchoolWindow");
        MaxHeight=SystemParameters.WorkArea.Height-30;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        // One page like every other administrator page: title with refresh on the right, then cards. The settings that
        // used to hide behind tabs are rows of their own at the end.
        var embedded=owner is LocalServerWindow;var dock=hostView;dock.Margin=embedded?new Thickness(0):new Thickness(24,18,24,12);Content=dock;
        var header=new DockPanel{Margin=new Thickness(0,0,0,4)};var tools=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Bottom};DockPanel.SetDock(tools,Dock.Right);header.Children.Add(tools);
        if(!embedded)
        {
            var localControl=new Button{Content="이 PC 서버 시작·중지",Margin=new Thickness(0,0,8,0)};
            var localRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
            localControl.IsEnabled=Application.Current.MainWindow is LocalServerWindow||File.Exists(Path.Combine(localRoot,"server","SchoolMessenger.Server.exe"));
            localControl.Click+=(_,_)=>
            {
                if(Application.Current.MainWindow is LocalServerWindow manager){manager.Show();manager.ShowServerControls();manager.Activate();return;}
                var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--server-manager");Process.Start(start);
            };tools.Children.Add(localControl);
        }
        var refresh=new Button{Content="새로고침"};refresh.Click+=async(_,_)=>await Refresh(true);tools.Children.Add(refresh);
        header.Children.Add(AdminUi.PageTitle(embedded?"운영 현황":"서버 운영","접속 서버  "+api.Address));DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        feedback.FontSize=12;feedback.Margin=new Thickness(0,0,0,8);feedback.SetResourceReference(TextBlock.ForegroundProperty,"Muted");StatusTone.Watch(feedback);DockPanel.SetDock(feedback,Dock.Top);dock.Children.Add(feedback);
        body.Margin=new Thickness(0,4,embedded?6:0,0);dock.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var cards=new Grid{Margin=new Thickness(0,0,-12,0)};for(var i=0;i<3;i++)cards.ColumnDefinitions.Add(new ColumnDefinition());
        AddCard(cards,0,"PC 전체 CPU",cpu,cpuBar);AddCard(cards,1,"PC 전체 RAM",memory,memoryBar,memoryNote);AddCard(cards,2,"이 서버 프로그램 RAM",process,null,processNote);body.Children.Add(cards);
        var information=new StackPanel();var infoGrid=new Grid();infoGrid.ColumnDefinitions.Add(new ColumnDefinition());infoGrid.ColumnDefinitions.Add(new ColumnDefinition());var machine=new StackPanel{Margin=new Thickness(0,0,20,0)};machine.Children.Add(hardware);machine.Children.Add(traffic);infoGrid.Children.Add(machine);Grid.SetColumn(storage,1);infoGrid.Children.Add(storage);information.Children.Add(infoGrid);information.Children.Add(Note("네트워크 수치는 같은 PC의 다른 프로그램도 포함합니다."));body.Children.Add(Section("하드웨어 · 네트워크 · 저장 공간",information));
        var load=new StackPanel();var loadActions=new DockPanel();apply.Margin=new Thickness(12,0,0,0);apply.SetResourceReference(StyleProperty,"PrimaryButton");DockPanel.SetDock(apply,Dock.Right);loadActions.Children.Add(apply);loadActions.Children.Add(profile);load.Children.Add(loadActions);load.Children.Add(Note("새 파일 작업의 동시 실행과 전송 속도를 조정합니다. 서버 RAM 전체의 강제 상한은 아닙니다."));load.Children.Add(settings);body.Children.Add(Section("부하 설정",load));
        // The settings that open a window of their own: one line each, the button on the right.
        var more=new StackPanel();
        void Row(string title,string detail,string action,Func<Task> open)
        {
            if(more.Children.Count>0){var line=new Border{Height=1,Margin=new Thickness(0,14,0,14)};line.SetResourceReference(Border.BackgroundProperty,"Line");more.Children.Add(line);}
            var row=new DockPanel();var button=new Button{Content=action,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)};button.Click+=async(_,_)=>await open();DockPanel.SetDock(button,Dock.Right);row.Children.Add(button);
            var text=new StackPanel();text.Children.Add(AdminUi.Label(title,"Ink",13,true));var note=AdminUi.Label(detail,"Muted",12);note.Margin=new Thickness(0,2,0,0);text.Children.Add(note);row.Children.Add(text);
            more.Children.Add(row);
        }
        if(owner is MainWindow main)Row("교직원 계정 · 승인","교직원을 등록하고 계정 신청을 승인합니다.","열기",()=>{new OfficeWindow(main,true).Show();return Task.CompletedTask;});
        if(officeService)Row("교내 서버 자동검색","교사 PC가 주소를 몰라도 이 서버를 찾게 합니다.","설정",OpenDiscovery);
        Row("서버 PC 자동 구동","Windows가 재시작되면 로그인 전에도 서버를 시작합니다.","설정",()=>{OpenServerStartup();return Task.CompletedTask;});
        Row("보안 도입 검토 기록","학교가 등록한 보안성 검토 기록을 확인합니다.","확인",OpenDeploymentReview);
        body.Children.Add(Section("운영 설정",more));
        apply.Click+=async(_,_)=>
        {
            if(busy)return;busy=true;Ready=false;apply.IsEnabled=false;
            try{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/server-resources",new{profile=profile.SelectedIndex==0?"low-memory":"balanced"});loaded=false;feedback.Text="설정을 저장했습니다. 새 파일 작업부터 적용됩니다.";}
            catch(ApiRequestException e)when(e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden){Close();return;}
            catch(Exception e){feedback.Text=e.Message;}
            finally{busy=false;}await Refresh(false);
        };
        dock.Loaded+=async(_,_)=>{await Refresh(true);timer.Start();};dock.Unloaded+=(_,_)=>timer.Stop();timer.Tick+=async(_,_)=>{if(InteractionOwner.IsActive)await Refresh(false);};
        Closed+=(_,_)=>timer.Stop();
    }
    static TextBlock Label(string value,int size=13){var label=AdminUi.Label(value,"Ink",size);label.Margin=new Thickness(0,3,0,3);return label;}
    static TextBlock Note(string value){var note=AdminUi.Label(value,"Muted",12);note.Margin=new Thickness(0,8,0,0);return note;}
    static Border Section(string title,UIElement content)
    {
        var stack=new StackPanel();var heading=AdminUi.Heading(title);heading.Margin=new Thickness(0,0,0,12);stack.Children.Add(heading);stack.Children.Add(content);
        var card=AdminUi.Card(stack);card.Margin=new Thickness(0,0,0,12);return card;
    }
    static void AddCard(Grid grid,int column,string title,TextBlock value,ProgressBar? bar,TextBlock? note=null)
    {
        var stack=new StackPanel();var caption=AdminUi.Label(title,"Muted",12);caption.Margin=new Thickness(0,0,0,6);stack.Children.Add(caption);stack.Children.Add(value);if(bar is not null){bar.Margin=new Thickness(0,8,0,0);stack.Children.Add(bar);}if(note is not null){note.SetResourceReference(TextBlock.ForegroundProperty,"Muted");stack.Children.Add(note);}
        var card=AdminUi.Card(stack,16);card.Margin=new Thickness(0,0,12,12);value.FontWeight=FontWeights.Bold;Grid.SetColumn(card,column);grid.Children.Add(card);
    }
    static string Bytes(double value)=>value>=1073741824?(value/1073741824).ToString("0.0")+" GB":(value/1048576).ToString("0.0")+" MB";
    static string Rate(JsonElement sample,string name)=>sample.GetProperty(name).ValueKind==JsonValueKind.Number?(sample.GetProperty(name).GetDouble()/1024).ToString("0.0")+" KB/s":"측정 중";
    public async Task Refresh(bool manual)
    {
        if(busy)return;busy=true;apply.IsEnabled=false;
        try
        {
            var data=await api.Get<JsonElement>("api/admin/server-resources");var sample=data.GetProperty("sample");var limits=data.GetProperty("limits");var active=data.GetProperty("active");
            var total=sample.GetProperty("totalMemoryBytes").GetDouble();var available=sample.GetProperty("availableMemoryBytes").GetDouble();
            var systemCpu=sample.GetProperty("systemCpuPercent");cpu.Text=systemCpu.ValueKind==JsonValueKind.Number?systemCpu.GetDouble().ToString("0.0")+"%":"측정 중";cpu.ToolTip=systemCpu.ValueKind==JsonValueKind.Number?null:"서버가 10초마다 측정합니다. 잠시 후 표시됩니다.";cpuBar.Value=systemCpu.ValueKind==JsonValueKind.Number?systemCpu.GetDouble():0;
            memoryBar.Value=total>0?100*(total-available)/total:0;memory.Text=total>0?memoryBar.Value.ToString("0.0")+"%":"확인 불가";memoryNote.Text=total>0?Bytes(total-available)+" / "+Bytes(total):"";process.Text=Bytes(sample.GetProperty("processMemoryBytes").GetDouble());processNote.Text="CPU "+(sample.GetProperty("processCpuPercent").ValueKind==JsonValueKind.Number?sample.GetProperty("processCpuPercent").GetDouble().ToString("0.0")+"%":"측정 중");
            hardware.Text=sample.GetProperty("os").GetString()+"\n"+sample.GetProperty("processor").GetString()+" · 논리 프로세서 "+sample.GetProperty("logicalProcessors")+"개\n서버 IPv4: "+string.Join(", ",sample.GetProperty("addresses").EnumerateArray().Select(a=>a.GetString()));
            traffic.Text="PC 네트워크 송신 "+Rate(sample,"networkSendBytesPerSecond")+" · 수신 "+Rate(sample,"networkReceiveBytesPerSecond")+"\n상태 수집: "+data.GetProperty("sampledAt").GetDateTimeOffset().ToLocalTime().ToString("HH:mm:ss")+" · 10초 간격 · 관리자 창 활성 시 갱신";
            var folder=sample.GetProperty("dataDirectory").GetString()??"";storage.ToolTip=folder;
            // The full path is long and personal to this PC; the last folders say enough, the tooltip has the rest.
            var parts=folder.Split(System.IO.Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries);var shown=parts.Length>3?"…"+System.IO.Path.DirectorySeparatorChar+string.Join(System.IO.Path.DirectorySeparatorChar,parts[^2..]):folder;
            storage.Text="데이터 저장 위치: "+shown+"\n첨부 사용량 "+Bytes(sample.GetProperty("filesBytes").GetDouble())+" · 디스크 여유 "+Bytes(sample.GetProperty("freeBytes").GetDouble());
            settings.Text="동시 업로드 "+limits.GetProperty("uploads")+"건 · 다운로드 "+limits.GetProperty("downloads")+"건 · ZIP "+limits.GetProperty("zip")+"건\n진행 중: 업로드 "+active.GetProperty("uploads")+" · 다운로드 "+active.GetProperty("downloads")+" · ZIP "+active.GetProperty("zips")+"\n파일당 전송 속도: "+(limits.GetProperty("downloadBytesPerSecond").GetInt32()>0?Bytes(limits.GetProperty("downloadBytesPerSecond").GetDouble())+"/초":"별도 제한 없음")+"\n공지 이미지 200KB · 최대 5장 · 파일 보관 30일\n저부하 모드: 여유 RAM 512MB 미만, 서버 RAM 768MB 초과 또는 CPU 평균 85% 이상이면 새 파일 작업은 잠시 대기합니다.";
            if(!loaded){profile.SelectedIndex=data.GetProperty("profile").GetString()=="low-memory"?0:1;loaded=true;}
            Ready=true;apply.IsEnabled=true;if(manual)feedback.Text="최신 상태를 확인했습니다.";
        }
        catch(ApiRequestException e)when(e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden){Close();}
        catch(Exception e){Ready=false;cpu.Text=memory.Text=process.Text="확인 불가";hardware.Text=traffic.Text=storage.Text=settings.Text="";feedback.Text=e.Message;}
        finally{busy=false;}
    }
}
