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
        Owner=owner;Title="학교별 약관 · 개인정보 설정 초안";Width=640;Height=720;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var embedded=owner is LocalServerWindow;
        var body=new StackPanel{Margin=embedded?new Thickness(0,0,6,0):new Thickness(24)};AdminUi.Page(this,new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var title=AdminUi.PageTitle("약관·개인정보","학교 이름과 문의처를 넣어, 학교가 검토할 약관·개인정보 처리방침 초안 파일을 만듭니다.");title.Margin=new Thickness(0,0,0,16);body.Children.Add(title);
        // Fields two to a row, like the other administrator forms.
        var form=new StackPanel();form.Children.Add(AdminUi.Group("학교 정보"));
        var fields=new Grid();fields.ColumnDefinitions.Add(new ColumnDefinition());fields.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});fields.ColumnDefinitions.Add(new ColumnDefinition());form.Children.Add(fields);
        var index=0;
        TextBox Field(string label,bool wide=false)
        {
            if(wide&&index%2==1)index++;
            if(index%2==0)fields.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var cell=new StackPanel{Margin=new Thickness(0,0,0,12)};var caption=AdminUi.Label(label,"Muted",13);caption.Margin=new Thickness(0,0,0,6);cell.Children.Add(caption);
            var field=new TextBox{MaxLength=150};System.Windows.Automation.AutomationProperties.SetName(field,label);cell.Children.Add(field);
            Grid.SetRow(cell,index/2);Grid.SetColumn(cell,index%2==0?0:2);if(wide){Grid.SetColumnSpan(cell,3);index++;}index++;fields.Children.Add(cell);return field;
        }
        TextBox school=Field("운영 학교명"),phone=Field("학교 문의 전화 (예: 061-000-0000)"),office=Field("권리 요청 창구 (예: ○○고등학교 교무실)",true),responsible=Field("개인정보 보호책임자 성명·직위 (모르면 비워 두기)"),contact=Field("보호책임자 연락처 (모르면 비워 두기)");
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,0)};
        var save=new Button{Content="학교 검토용 초안 파일 만들기",IsEnabled=false};save.SetResourceReference(StyleProperty,"PrimaryButton");actions.Children.Add(save);form.Children.Add(actions);
        var result=AdminUi.Label("","Muted",13);result.Margin=new Thickness(0,10,0,0);StatusTone.Watch(result);form.Children.Add(result);
        var formCard=AdminUi.Card(form);formCard.Margin=new Thickness(0,0,0,12);body.Children.Add(formCard);
        var notes=new StackPanel();notes.Children.Add(AdminUi.Group("알아 둘 점"));
        foreach(var line in new[]{
            "학교별 서버 운영 관리자가 작성합니다. 개발자 김동욱은 각 학교의 개인정보 보호책임자로 자동 지정되지 않으며, 일반 회원은 이 설정을 바꿀 수 없습니다.",
            "검토용 JSON 파일만 만듭니다. 운영 중인 서버에는 아무것도 적용하지 않습니다.",
            "전문·처리 근거·보유 기간·위탁·만 14세 미만 절차와 학교 확정 사항을 검토한 뒤, 서버 운영 설정의 registrationPolicyFile에 파일의 절대 경로를 지정하고 계획된 재시작 때 적용하세요."})
        {var note=AdminUi.Label("· "+line,"Muted",13);note.Margin=new Thickness(0,0,0,6);notes.Children.Add(note);}
        var noteCard=AdminUi.Card(notes);noteCard.SetResourceReference(Border.BackgroundProperty,"Subtle");body.Children.Add(noteCard);
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
