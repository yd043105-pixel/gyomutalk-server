using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

// Creates a local review draft only. It never edits a running server or approves legal documents.
public sealed class SchoolPolicyWindow:Window
{
    public SchoolPolicyWindow(Window owner,Api api)
    {
        Owner=owner;Title="학교별 약관 · 개인정보 설정 초안";Width=600;Height=700;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(24)};Content=new ScrollViewer{Content=body};
        void Text(string value)=>body.Children.Add(new TextBlock{Text=value,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,6)});
        TextBox Field(string label,string initial=""){Text(label);var field=new TextBox{Text=initial,MaxLength=150};body.Children.Add(field);return field;}
        Text("학교별 서버 운영 관리자가 작성합니다. 개발자 김동욱은 각 학교의 개인정보 보호책임자로 자동 지정되지 않습니다. 일반 회원은 이 설정을 변경할 수 없습니다.");
        TextBox school=Field("운영 학교명"),office=Field("권리 요청 창구 (예: ○○고등학교 교무실)"),phone=Field("학교 문의 전화"),responsible=Field("학교 개인정보 보호책임자 성명·직위 (모르면 비워 두기)"),contact=Field("보호책임자 연락처 (모르면 비워 두기)");
        Text("검토용 JSON 파일만 만듭니다. 전문·처리 근거·보유 기간·위탁·만 14세 미만 절차와 학교 확정 사항을 검토한 뒤, 서버 운영 설정의 registrationPolicyFile에 절대 경로를 지정하고 계획된 재시작 때 적용하세요. 업무·학교 소식 서버는 각각 설정합니다.");
        var result=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};var save=new Button{Content="학교 검토용 초안 파일 만들기",IsEnabled=false,Margin=new Thickness(0,12,0,0)};body.Children.Add(save);body.Children.Add(result);
        RegistrationPolicySet? policy=null;
        var initialized=false;body.Loaded+=async(_,_)=>
        {
            if(initialized)return;initialized=true;
            try
            {
                await api.Get<JsonElement>("api/admin/server-resources");
                policy=await api.Get<RegistrationPolicySet>("api/registration/policy");school.Text=policy.Operator;office.Text=Regex.Replace(policy.Contact,@"\s*\(?\d{2,4}-\d{3,4}-\d{4}\)?","").Trim();
                phone.Text=Regex.Match(policy.Contact,@"\d{2,4}-\d{3,4}-\d{4}").Value;
                // Start with the unapproved generic development draft, never another school's signed policy or officer.
                using var stream=typeof(SchoolPolicyWindow).Assembly.GetManifestResourceStream("SchoolMessenger.RegistrationDraft.json")!;
                using var draft=JsonDocument.Parse(stream);var json=draft.RootElement;
                policy=policy with{Operator=json.GetProperty("operator").GetString()!,Contact=json.GetProperty("contact").GetString()!,Documents=json.GetProperty("documents").Deserialize<RegistrationDocument[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!};save.IsEnabled=true;
            }
            catch(Exception error){result.Text=error.Message;}
        };
        save.Click+=(_,_)=>
        {
            try
            {
                if(policy is null)return;
                if(school.Text.Trim().Length<2||office.Text.Trim().Length<2||!Regex.IsMatch(phone.Text,@"^\d{2,4}-\d{3,4}-\d{4}$"))throw new InvalidOperationException("학교명·문의 창구와 하이픈을 포함한 전화번호를 확인하세요.");
                var newContact=office.Text.Trim()+" ("+phone.Text+")";var oldPhone=Regex.Match(policy.Contact,@"\d{2,4}-\d{3,4}-\d{4}").Value;
                string Replace(string value)
                {
                    var text=value.Replace(policy.Contact,newContact,StringComparison.Ordinal).Replace(policy.Operator,school.Text.Trim(),StringComparison.Ordinal);
                    if(oldPhone.Length>0)text=text.Replace(oldPhone,phone.Text,StringComparison.Ordinal);return text;
                }
                var documents=policy.Documents.Select(d=>d with{Version=DateTime.Now.ToString("yyyy-MM-dd")+"-school-draft-"+Guid.NewGuid().ToString("N")[..8],Body=Replace(d.Body)+(d.Id=="privacy"?"\n\n학교별 운영 설정 (확정 전 초안)\n처리 주체: "+school.Text.Trim()+"\n권리 행사 창구: "+newContact+"\n개인정보 보호책임자: "+(string.IsNullOrWhiteSpace(responsible.Text)?"[학교 확정 필요]":responsible.Text.Trim())+"\n보호책임자 연락처: "+(string.IsNullOrWhiteSpace(contact.Text)?"[학교 확정 필요]":contact.Text.Trim())+"\n앱 설정의 개인정보 권리 요청에서도 접수·회신을 확인할 수 있습니다. 아직 학교가 승인한 운영 문서가 아닙니다.":"")}).ToArray();
                var file=new Microsoft.Win32.SaveFileDialog{FileName="registration-policy.local.json",Filter="학교별 약관 설정 (*.json)|*.json"};
                if(file.ShowDialog(Window.GetWindow(body)??this)!=true)return;
                File.WriteAllText(file.FileName,JsonSerializer.Serialize(new{approvedForProduction=false,@operator=school.Text.Trim(),contact=newContact,documents},new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
                result.Text="검토용 초안을 저장했습니다. 운영 가입 승인·서버 적용은 수행하지 않았습니다. 학교 책임자의 검토 후 운영 안내에 따라 설정하세요.";
            }
            catch(Exception error){result.Text=error.Message;}
        };
    }
}
