using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SchoolMessenger.Desktop;

public sealed partial class ServerManagementWindow
{
    // The settings windows look like the pages: a title with one line under it, then the content on a card.
    Window Dialog(string title,string description,StackPanel content)
    {
        var page=new StackPanel{Margin=new Thickness(24)};var heading=AdminUi.PageTitle(title,description);heading.Margin=new Thickness(0,0,0,16);page.Children.Add(heading);page.Children.Add(AdminUi.Card(content));
        var window=new Window{Owner=InteractionOwner,Title=title,Width=560,SizeToContent=SizeToContent.Height,MaxHeight=SystemParameters.WorkArea.Height-40,ResizeMode=ResizeMode.NoResize,Resources=Resources,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Content=new ScrollViewer{Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        window.Style=(Style)FindResource("SchoolWindow");window.SetResourceReference(BackgroundProperty,"Soft");return window;
    }
    static TextBlock Caption(string text,bool first=false){var caption=AdminUi.Label(text,"Muted",12);caption.Margin=new Thickness(0,first?0:14,0,6);return caption;}
    static StackPanel Fact(string label,string value)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,8)};
        var name=AdminUi.Label(label,"Muted",13);name.Width=110;row.Children.Add(name);row.Children.Add(AdminUi.Label(value,"Ink",13));return row;
    }
    async Task OpenDeploymentReview()
    {
        try
        {
            var record=await api.Get<JsonElement>("api/admin/deployment-review");var panel=new StackPanel();
            var state=record.GetProperty("state").GetString();
            var badge=AdminUi.Badge(state=="trial"?"시험 모드 · 검토 기록 없음":state=="expired"?"검토 기록 유효기간 만료":"검토 기록 확인됨",state=="trial"?"Warning":state=="expired"?"Danger":"Success");
            badge.HorizontalAlignment=HorizontalAlignment.Left;badge.Margin=new Thickness(0,0,0,14);panel.Children.Add(badge);
            if(state!="trial")
            {
                panel.Children.Add(Fact("학교",record.GetProperty("school").GetString()??""));
                panel.Children.Add(Fact("검토 근거",record.GetProperty("basis").GetString() switch{"on-premises"=>"학교 자체 서버","school-education-41-7"=>"교육현장 적용 판단 기록","private-cloud-41-2"=>"민간 클라우드 기준 검토",_=>"확인 필요"}));
                foreach(var item in new[]{("reviewedAt","검토일"),("expiresAt","유효기간")})panel.Children.Add(Fact(item.Item2,record.GetProperty(item.Item1).GetDateTimeOffset().ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm")));
                panel.Children.Add(Fact("외부 연동",record.GetProperty("externalServiceCount").GetInt32()+"개"));
            }
            foreach(var line in new[]{"학교가 등록한 기록을 확인한 결과입니다. CSAP 인증·교육청 보안성 검토 승인·법령 전체 준수 여부를 프로그램이 판정한 결과가 아닙니다.",
                "교육현장 경로는 학교장 승인·적용 범위 판단·자체 보안대책을 학교에 보관합니다. 확정 문서의 해시와 운영 설정의 securityReviewFile을 등록하고 실제 저장 위치·외부 연동·접속 범위를 확인하세요."})
            {var note=AdminUi.Label(line,"Muted",12);note.Margin=new Thickness(0,8,0,0);panel.Children.Add(note);}
            Dialog("보안 도입 검토 기록","학교가 등록한 보안성 검토 기록입니다.",panel).ShowDialog();
        }catch(Exception e){feedback.Text=e.Message;}
    }
    async Task OpenDiscovery()
    {
        try
        {
            var state=await api.Get<JsonElement>("api/admin/server-discovery");var saved=state.GetProperty("settings");var panel=new StackPanel();
            var dialog=Dialog("교내 서버 자동검색","교사 PC의 ‘자동검색’이 이 서버를 찾게 합니다. 응답에는 서버 주소만 담깁니다.",panel);
            var enabled=new CheckBox{Content="교내 서버 자동검색 허용",IsChecked=saved.GetProperty("enabled").GetBoolean()};panel.Children.Add(enabled);
            panel.Children.Add(Caption("교사 PC가 연결할 교내 HTTPS 주소 (인증서 이름과 같아야 합니다)"));
            var address=new TextBox{Text=saved.GetProperty("address").GetString()??""};if(address.Text.Length==0&&!api.Address.IsLoopback)address.Text=api.Address.AbsoluteUri;System.Windows.Automation.AutomationProperties.SetName(address,"교내 HTTPS 주소");panel.Children.Add(address);
            var note=AdminUi.Label("localhost·127.0.0.1은 쓸 수 없습니다. 같은 교직원망에서 UDP 45983이 허용되어야 하며 방화벽은 자동으로 바꾸지 않습니다. 다른 VLAN에서는 주소를 직접 넣어야 할 수 있습니다.","Muted",12);note.Margin=new Thickness(0,8,0,0);panel.Children.Add(note);
            var actions=new DockPanel{Margin=new Thickness(0,16,0,0)};var save=new Button{Content="저장"};save.SetResourceReference(StyleProperty,"PrimaryButton");DockPanel.SetDock(save,Dock.Right);actions.Children.Add(save);
            var status=AdminUi.Label(state.GetProperty("listening").GetBoolean()?"검색 응답 대기 중":"지금은 검색에 응답하지 않습니다.","Muted",12);status.VerticalAlignment=VerticalAlignment.Center;status.Margin=new Thickness(0,0,12,0);StatusTone.Watch(status);actions.Children.Add(status);panel.Children.Add(actions);
            if(state.GetProperty("lastError").ValueKind==JsonValueKind.String){var error=AdminUi.Label(state.GetProperty("lastError").GetString()!,"Danger",12);error.Margin=new Thickness(0,8,0,0);panel.Children.Add(error);}
            save.Click+=async(_,_)=>{save.IsEnabled=false;try{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/server-discovery",new{enabled=enabled.IsChecked==true,address=address.Text.Trim()});status.Text="저장했습니다. 약 1초 뒤 적용됩니다. 다른 교사 PC에서 검색해 확인하세요.";}catch(Exception e){status.Text=e.Message;}finally{save.IsEnabled=true;}};
            dialog.ShowDialog();
        }
        catch(Exception e){feedback.Text=e.Message;}
    }
    void OpenServerStartup()
    {
        // Only the server's local Windows operator may manage scheduled tasks; no remote shell API exists.
        try
        {
            var local=api.Address.IsLoopback||Dns.GetHostAddresses(api.Address.DnsSafeHost).Any(a=>Dns.GetHostAddresses(Dns.GetHostName()).Contains(a));
            if(!local){feedback.Text="자동 구동은 실제 서버 PC에서 관리자 프로그램을 열어 설정하세요.";return;}
            if(Application.Current.MainWindow is LocalServerWindow manager)
            {
                manager.Show();manager.Activate();feedback.Text="현재 서버는 이 관리자 프로그램이 가동합니다. 왼쪽 아래 ‘Windows 로그인 시 자동 실행’과 서버 화면의 ‘자동 시작’을 켜 두세요. 로그인 전에 서버를 시작하는 방식으로 바꾸려면 지금 가동을 정상 종료한 뒤 운영 담당자가 전환해야 합니다.";return;
            }
            var panel=new StackPanel();var dialog=Dialog("서버 PC 자동 구동","Windows가 재시작되면 로그인 전에도 서버를 시작하고, 멈추면 1분 간격으로 세 번까지 다시 시작합니다.",panel);
            panel.Children.Add(Caption("서버 운영 설정 파일 (JSON)",true));
            var pick=new DockPanel();var choose=new Button{Content="파일 선택",Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(choose,Dock.Right);pick.Children.Add(choose);
            var path=new TextBox{IsReadOnly=true};System.Windows.Automation.AutomationProperties.SetName(path,"서버 운영 설정 파일");pick.Children.Add(path);panel.Children.Add(pick);
            choose.Click+=(_,_)=>{var picker=new OpenFileDialog{Filter="서버 운영 설정 (*.json)|*.json",CheckFileExists=true};if(picker.ShowDialog(dialog)==true)path.Text=picker.FileName;};
            var hint=AdminUi.Label("비밀 설정을 만든 전용 Windows 운영 계정으로 실행해야 합니다. 처음 등록할 때 Windows 계정 확인 창이 뜹니다.","Muted",12);hint.Margin=new Thickness(0,8,0,0);panel.Children.Add(hint);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};panel.Children.Add(actions);
            var status=AdminUi.Label("","Muted",12);status.Margin=new Thickness(0,10,0,0);StatusTone.Watch(status);panel.Children.Add(status);bool working=false;
            foreach(var (name,mode,style) in new[]{("상태 확인","Status",(string?)null),("자동 구동 끄기","Disable","DangerButton"),("자동 구동 켜기","Enable","PrimaryButton")})
            {
                var button=new Button{Content=name,Margin=new Thickness(8,0,0,0)};if(style is not null)button.SetResourceReference(StyleProperty,style);actions.Children.Add(button);
                button.Click+=async(_,_)=>
                {
                    if(working)return;if(!File.Exists(path.Text)){status.Text="운영 설정 파일을 먼저 선택하세요.";return;}working=true;actions.IsEnabled=choose.IsEnabled=false;status.Text="운영 설정 확인 중…";
                    try
                    {
                        using var config=JsonDocument.Parse(File.ReadAllText(path.Text));var server=Path.GetDirectoryName(config.RootElement.GetProperty("executable").GetString())??throw new InvalidOperationException("서버 실행 파일 경로를 확인하세요.");
                        var candidates=new[]{Path.Combine(server,"..","scripts","Configure-ServerOperations.ps1"),Path.Combine(AppContext.BaseDirectory,"..","scripts","Configure-ServerOperations.ps1"),Path.Combine(Environment.CurrentDirectory,"scripts","Configure-ServerOperations.ps1")};
                        var script=candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)??throw new InvalidOperationException("서버 배포 폴더의 scripts가 필요합니다. 최신 배포본의 관리자 프로그램을 사용하세요.");
                        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                        start.Environment["PSModulePath"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","Modules")+";"+Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsPowerShell","Modules");
                        foreach(var argument in new[]{"-NoProfile","-File",script,"-ConfigurationPath",path.Text,"-StartupAction",mode})start.ArgumentList.Add(argument);
                        using var child=Process.Start(start)??throw new InvalidOperationException("작업 스케줄러 설정 도구를 시작하지 못했습니다.");var output=child.StandardOutput.ReadToEndAsync();var errors=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();var text=(await output)+"\n"+(await errors);status.Text=(child.ExitCode==0?"완료\n":"설정 실패\n")+text;
                    }
                    catch(Exception e){status.Text=e.Message;}
                    finally{working=false;actions.IsEnabled=choose.IsEnabled=true;}
                };
            }
            var note=AdminUi.Label("지금 돌고 있는 서버를 끄거나 PC를 재시작하지 않습니다. 서버를 하나 더 실행하지 말고, 계획된 재부팅 뒤 접속을 확인하세요.","Faint",12);note.Margin=new Thickness(0,12,0,0);panel.Children.Add(note);
            dialog.Closing+=(_,e)=>{if(working)e.Cancel=true;};dialog.ShowDialog();
        }
        catch(Exception e){feedback.Text=e.Message;}
    }
}
