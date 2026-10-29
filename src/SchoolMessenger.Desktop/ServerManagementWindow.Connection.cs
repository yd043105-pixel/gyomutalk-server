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
    async Task ExportSchoolRegistration()
    {
        try
        {
            var review=await api.Get<JsonElement>("api/admin/deployment-review");
            if(api.Address.Scheme!="https"||review.GetProperty("state").GetString()!="school-record-checked")throw new InvalidOperationException("학교별 검토 기록을 적용한 운영 HTTPS 서버에서 등록자료를 저장하세요. 시험 서버는 검색 목록에 등록하지 않습니다.");
            var identity=await api.Get<JsonElement>("server-info");var id=identity.GetProperty("schoolId").GetString();var name=identity.GetProperty("schoolName").GetString();var service=identity.GetProperty("service").GetString();
            if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(name)||service is not("office" or "announcements"))throw new InvalidOperationException("학교 식별자와 서버 역할을 확인하세요.");
            var save=new SaveFileDialog{Title="개발자에게 전달할 학교 검색 등록자료",FileName="school-registration-"+service+".json",Filter="학교 등록자료 (*.json)|*.json"};
            if(save.ShowDialog(InteractionOwner)!=true)return;
            File.WriteAllText(save.FileName,JsonSerializer.Serialize(new{schema=1,schoolId=id,schoolName=name,service,address=api.Address.AbsoluteUri},new JsonSerializerOptions{WriteIndented=true}));
            feedback.Text="등록자료를 저장했습니다. 학교명·학교 식별자·현재 접속 주소만 포함하며 비밀번호·계정 명부는 포함하지 않습니다. 개발자에게 지역과 함께 전달해 등록을 요청하세요. 주소가 다른 교사 PC/앱에서도 연결되는지 확인해야 합니다.";
        }catch(Exception e){feedback.Text=e.Message;}
    }
    Window Dialog(string title,StackPanel panel)=>new(){Owner=InteractionOwner,Title=title,Width=580,Height=550,Resources=Resources,Style=(Style)FindResource("SchoolWindow"),WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new ScrollViewer{Content=panel}};
    async Task OpenDeploymentReview()
    {
        try
        {
            var record=await api.Get<JsonElement>("api/admin/deployment-review");var panel=new StackPanel{Margin=new Thickness(22)};
            var state=record.GetProperty("state").GetString();
            panel.Children.Add(Label(state=="trial"?"시험 모드 · 운영 검토 기록 미적용":state=="expired"?"학교 검토 기록 유효기간 만료":"학교 검토 기록 형식·증빙 해시 확인",20));
            if(state!="trial")
            {
                panel.Children.Add(Label("학교: "+record.GetProperty("school").GetString()));
                panel.Children.Add(Label("검토 근거: "+(record.GetProperty("basis").GetString() switch{"on-premises"=>"학교 자체 서버","school-education-41-7"=>"교육현장 적용 판단 기록","private-cloud-41-2"=>"민간 클라우드 기준 검토",_=>"확인 필요"})));
                foreach(var item in new[]{("reviewedAt","검토일"),("expiresAt","유효기간")})panel.Children.Add(Label(item.Item2+": "+record.GetProperty(item.Item1).GetDateTimeOffset().ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm")));
                panel.Children.Add(Label("기록된 외부 연동: "+record.GetProperty("externalServiceCount").GetInt32()+"개"));
            }
            panel.Children.Add(Label("이 화면은 학교가 등록한 기록을 확인한 결과입니다. CSAP 인증·교육청 보안성 검토 승인·법령 전체 준수 여부를 프로그램이 판정한 결과가 아닙니다.",12));
            panel.Children.Add(Label("교육현장 경로는 학교장 승인·적용 범위 판단·자체 보안대책을 학교에 보관합니다. 해당 사업의 검토 생략 규정을 적용하면 일반기관 제출서류를 일괄 요구하지 않습니다. 확정 문서의 해시와 운영 설정의 securityReviewFile을 등록하고 실제 저장 위치·외부 연동·접속 범위를 확인하세요.",12));
            Dialog("보안 도입 검토 기록",panel).ShowDialog();
        }catch(Exception e){feedback.Text=e.Message;}
    }
    async Task OpenDiscovery()
    {
        try
        {
            var state=await api.Get<JsonElement>("api/admin/server-discovery");var saved=state.GetProperty("settings");var panel=new StackPanel{Margin=new Thickness(22)};var dialog=Dialog("교내 서버 자동검색",panel);
            panel.Children.Add(Label("교사 PC에서 ‘자동검색’으로 찾을 수 있게 합니다. 검색 응답에는 서버 주소만 포함하며 이름·계정·메시지는 보내지 않습니다."));
            var enabled=new CheckBox{Content="교내 서버 자동검색 허용",IsChecked=saved.GetProperty("enabled").GetBoolean(),Margin=new Thickness(0,12,0,12)};panel.Children.Add(enabled);
            panel.Children.Add(Label("다른 교사 PC에서 연결할 교내 HTTPS 주소 · 인증서 이름과 일치"));var address=new TextBox{Text=saved.GetProperty("address").GetString()??""};if(address.Text.Length==0&&!api.Address.IsLoopback)address.Text=api.Address.AbsoluteUri;panel.Children.Add(address);
            panel.Children.Add(Label("localhost·127.0.0.1은 사용할 수 없습니다. 같은 교직원망에서 UDP 45983이 허용되어야 하며, 방화벽은 자동으로 변경하지 않습니다. 다른 VLAN에서는 직접 주소를 설정해야 할 수 있습니다.",12));
            var status=Label(state.GetProperty("listening").GetBoolean()?"검색 응답 대기 중":"현재 검색 응답 중지");panel.Children.Add(status);
            if(state.GetProperty("lastError").ValueKind==JsonValueKind.String)panel.Children.Add(Label(state.GetProperty("lastError").GetString()!,12));
            var save=new Button{Content="자동검색 설정 저장",Margin=new Thickness(0,15,0,0)};panel.Children.Add(save);
            save.Click+=async(_,_)=>{save.IsEnabled=false;try{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/server-discovery",new{enabled=enabled.IsChecked==true,address=address.Text.Trim()});status.Text="저장했습니다. 약 1초 후 새 설정을 적용합니다. 다른 교사 PC에서 검색해 확인하세요.";}catch(Exception e){status.Text=e.Message;}finally{save.IsEnabled=true;}};
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
                manager.Show();manager.Activate();feedback.Text="현재 서버는 서버 관리 프로그램에서 가동합니다. 상단의 Windows 로그인 시 자동 실행과 각 서버의 자동 시작을 설정하세요. 로그인 전 운영 작업으로 바꾸려면 현재 가동을 정상 종료한 뒤 운영 담당자가 전환해야 합니다.";return;
            }
            var panel=new StackPanel{Margin=new Thickness(22)};var dialog=Dialog("서버 PC 자동 구동",panel);
            panel.Children.Add(Label("Windows가 재시작되면 로그인 전에도 서버를 시작하고, 비정상 종료 시 1분 간격으로 최대 3회 다시 시작합니다."));
            panel.Children.Add(Label("기존 서버 운영 설정 JSON을 선택하세요. 업무 서버와 학교 소식 서버는 각각 등록합니다. 비밀 설정을 만든 전용 표준 Windows 운영 계정으로 실행해야 합니다.",12));
            var path=new TextBox{IsReadOnly=true};panel.Children.Add(path);var choose=new Button{Content="운영 설정 파일 선택",Margin=new Thickness(0,10,0,5)};panel.Children.Add(choose);choose.Click+=(_,_)=>{var picker=new OpenFileDialog{Filter="서버 운영 설정 (*.json)|*.json",CheckFileExists=true};if(picker.ShowDialog(dialog)==true)path.Text=picker.FileName;};
            var status=Label("");panel.Children.Add(status);var actions=new WrapPanel();panel.Children.Add(actions);bool working=false;
            foreach(var (name,mode) in new[]{("설정 상태 확인","Status"),("자동 구동 켜기","Enable"),("자동 구동 끄기","Disable")})
            {
                var button=new Button{Content=name,Margin=new Thickness(0,8,8,0)};actions.Children.Add(button);
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
            panel.Children.Add(Label("기존 서버를 지금 종료하거나 PC를 재시작하지 않습니다. 최초 등록 시 Windows 운영 계정 인증 창이 뜹니다. 서버를 하나 더 실행하지 말고, 계획된 재부팅 후 접속을 확인하세요.",12));
            dialog.Closing+=(_,e)=>{if(working)e.Cancel=true;};dialog.ShowDialog();
        }
        catch(Exception e){feedback.Text=e.Message;}
    }
}
