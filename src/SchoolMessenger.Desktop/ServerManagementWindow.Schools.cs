using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Desktop;

public sealed partial class ServerManagementWindow
{
    async Task RequestSchoolRegistration()
    {
        try
        {
            var review=await api.Get<JsonElement>("api/admin/deployment-review");
            if(api.Address.Scheme!="https"||review.GetProperty("state").GetString()!="school-record-checked")throw new InvalidOperationException("학교별 검토 기록을 적용한 운영 HTTPS 서버에서 신청하세요. 시험 서버는 등록하지 않습니다.");
            var identity=await api.Get<JsonElement>("server-info");var id=identity.GetProperty("schoolId").GetString()??"";var name=identity.GetProperty("schoolName").GetString()??"";var service=identity.GetProperty("service").GetString();
            if(service is not("office" or "announcements"))throw new InvalidOperationException("학교 서버 역할을 확인하세요.");
            var panel=new StackPanel{Margin=new Thickness(22)};var dialog=Dialog("학교 등록 신청 · 개발자 승인",panel);
            panel.Children.Add(Label(name+" · "+id,18));
            TextBox Field(string title,string value,int length){panel.Children.Add(Label(title,12));var field=new TextBox{Text=value,MaxLength=length,Margin=new Thickness(0,4,0,10)};panel.Children.Add(field);return field;}
            var region=Field("지역 · 시도·시군구","",80);var phone=Field("학교 홈페이지에 공개된 대표전화 · 개인 휴대전화 제외","",24);
            var office=Field("교직원용 HTTPS 주소 · 미도입 시 비워 둠",service=="office"&&!api.Address.IsLoopback?api.Address.AbsoluteUri:"",240);
            var portal=Field("학생·학부모용 HTTPS 주소 · 미도입 시 비워 둠",service=="announcements"&&!api.Address.IsLoopback?api.Address.AbsoluteUri:"",240);
            panel.Children.Add(Label("학교명·지역·학교 식별자·역할별 주소는 승인 후 공개 검색 목록에 표시됩니다. 대표전화는 학교 확인에만 사용하며 공개 목록에 포함하지 않습니다. 비밀번호·명부·보안 검토 증빙을 첨부하지 마세요.",12));
            panel.Children.Add(Label("학교 자체 서버로 운영하며 학교 검색은 승인된 접속 주소를 안내합니다. 도입·운영 내부결재 승인 문서(문서번호·결재일·승인 범위)를 학교에 보관하세요. 학교장 결재가 필요한 학운위 심의·보안 검토를 대신하지 않습니다. 원본 승인 문서는 신청 메일이나 공개 저장소에 첨부하지 마세요.",12));
            var agree=new CheckBox{Content="학교의 내부결재 승인 문서 보관 및 필요한 심의·검토와 위 공개 범위를 확인했습니다.",Margin=new Thickness(0,12,0,12)};panel.Children.Add(agree);
            var save=new Button{Content="신청서 저장 후 메일 작성",HorizontalAlignment=HorizontalAlignment.Left};panel.Children.Add(save);
            var state=Label("신청서 작성 → 학교에서 메일 발송 → 개발자 확인·승인 → 학교 검색에 공개",12);panel.Children.Add(state);
            save.Click+=async(_,_)=>
            {
                if(agree.IsChecked!=true){state.Text="내부결재 승인 문서 보관·필요한 심의와 검토·공개 범위를 먼저 확인해 주세요.";return;}
                save.IsEnabled=false;
                try
                {
                    var request=SchoolRegistration.Create(id,name,region.Text,phone.Text,office.Text,portal.Text);
                    var school=new SchoolDirectory.Entry(id,name,request.Region,request.OfficeAddress,request.AnnouncementsAddress);
                    foreach(var role in new[]{"office","announcements"})if(school.Address(role).Length>0)await SchoolDirectory.Verify(school,role);
                    var file=new SaveFileDialog{Title="개발자에게 보낼 학교 등록 신청서",FileName="school-request-"+request.RequestId+".json",Filter="학교 등록 신청서 (*.json)|*.json"};
                    if(file.ShowDialog(dialog)!=true)return;
                    File.WriteAllText(file.FileName,request.ToJson());
                    state.Text="신청서를 저장했습니다. 메일에 저장한 JSON 파일을 직접 첨부하고 보내주세요. 메일 발송·접수·승인은 아직 확인되지 않았습니다. 승인 전에는 학교 검색에 공개되지 않습니다.";
                    try{Process.Start(new ProcessStartInfo(request.Mailto(file.FileName)){UseShellExecute=true});}
                    catch{state.Text="신청서를 저장했지만 메일 앱을 열지 못했습니다. 저장한 JSON 파일을 "+SchoolRegistration.Recipient+"으로 첨부해 보내주세요. 아직 접수·승인된 상태는 아닙니다.";}
                }
                catch(Exception error){state.Text=error is HttpRequestException?"학교에 연결하지 못했습니다. 주소·인증서·교내망/VPN과 서버 가동 여부를 확인하세요.":error.Message;}
                finally{save.IsEnabled=true;}
            };
            dialog.ShowDialog();
        }catch(Exception error){feedback.Text=error.Message;}
    }
}
