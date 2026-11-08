using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

// Sign-ups open once the school confirms its own terms and privacy notice: the bundled school documents get the
// school's name, contact, privacy officer, effective date and external access service, and are saved beside the
// server data as an approved policy the server loads at start.
public sealed partial class LocalServerWindow
{
    internal sealed record PolicyFields(string School,string Contact,string Officer,string Effective,string External);
    internal static string PolicyPath(string data)=>Path.Combine(data,"registration-policy.json");
    internal static string RenderPolicy(PolicyFields fields)
    {
        string? Problem(string label,string value)=>value.Trim().Length is <1 or >100||value.Any(char.IsControl)||value.Contains("{{")?label+"을(를) 1~100자로 입력하세요.":null;
        if(new[]{Problem("학교 이름",fields.School),Problem("문의처",fields.Contact),Problem("개인정보 보호책임자",fields.Officer),Problem("외부 접속 서비스",fields.External)}.FirstOrDefault(p=>p is not null) is{} problem)throw new InvalidOperationException(problem);
        if(!DateOnly.TryParseExact(fields.Effective.Trim(),"yyyy-MM-dd",out _))throw new InvalidOperationException("시행일은 2026-11-03처럼 입력하세요.");
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("SchoolMessenger.RegistrationSchool.json")!;using var reader=new StreamReader(stream);
        string Json(string value)=>JsonSerializer.Serialize(value.Trim())[1..^1];
        var text=reader.ReadToEnd().Replace("{{학교}}",Json(fields.School)).Replace("{{문의처}}",Json(fields.Contact)).Replace("{{책임자}}",Json(fields.Officer)).Replace("{{시행일}}",Json(fields.Effective)).Replace("{{외부접속}}",Json(fields.External));
        var policy=JsonNode.Parse(text)!;policy["approvedForProduction"]=true;
        return policy.ToJsonString(new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
    }
    internal static void ConfirmPolicy(string data,PolicyFields fields)
    {
        var json=RenderPolicy(fields);Directory.CreateDirectory(data);var path=PolicyPath(data);var temp=path+".tmp";
        File.WriteAllText(temp,json);File.Move(temp,path,true);
    }
    static PolicyFields? ConfirmedPolicy(string data)
    {
        try
        {
            if(!File.Exists(PolicyPath(data)))return null;
            var policy=JsonNode.Parse(File.ReadAllText(PolicyPath(data)))!;var privacy=policy["documents"]!.AsArray().First(d=>(string?)d!["id"]=="privacy")!;
            return new((string?)policy["operator"]??"",(string?)policy["contact"]??"","",(string?)privacy["version"]??"","");
        }
        catch(Exception e)when(e is IOException or JsonException or InvalidOperationException or NullReferenceException){return null;}
    }
    void ShowPolicy(ServerCard c)
    {
        var data=DataPath(c).Trim();
        var window=new Window{Owner=this,Title="약관·개인정보 확정",Width=640,Height=780,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        window.SetResourceReference(BackgroundProperty,"Surface");
        var stack=new StackPanel{Margin=new Thickness(24)};window.Content=new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        var intro=AdminUi.Label("학교 정보를 넣고 전문을 확인한 뒤 확정하면, 이 약관과 개인정보 안내로 교직원 계정 신청과 활성화를 받습니다. 실행 중인 서버는 바로 다시 시작해 적용하며, 기존 계정과 신청은 그대로 남습니다.","Muted",13);intro.TextWrapping=TextWrapping.Wrap;stack.Children.Add(intro);
        TextBox Field(string label,string value,string note)
        {
            var title=AdminUi.Label(label,"Ink",13,true);title.Margin=new Thickness(0,14,0,6);stack.Children.Add(title);
            var box=new TextBox{Text=value};System.Windows.Automation.AutomationProperties.SetName(box,label);stack.Children.Add(box);
            if(note.Length>0){var hint=AdminUi.Label(note,"Muted",12);hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,4,0,0);stack.Children.Add(hint);}
            return box;
        }
        var saved=ConfirmedPolicy(data);
        var school=Field("학교 이름",saved?.School is{Length:>0} s?s:c.School.Text.Trim(),"");
        var contact=Field("문의처",saved?.Contact??"","탈퇴·개인정보 권리 행사를 받을 창구와 전화번호 (예: 교무실 031-000-0000)");
        var officer=Field("개인정보 보호책임자","","직위와 이름 (예: 교감 홍길동)");
        var effective=Field("시행일",DateTime.Today.ToString("yyyy-MM-dd"),"");
        var external=Field("학교가 설정한 외부 처리 서비스","사용하지 않음","오류 메일이나 업데이트 외에 외부 처리를 별도로 구성했다면 서비스와 운영 회사를 확인하여 적으세요.");
        var preview=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=260,Margin=new Thickness(0,16,0,0)};System.Windows.Automation.AutomationProperties.SetName(preview,"약관 전문 미리보기");stack.Children.Add(preview);
        var error=AdminUi.Label("","Danger",12);error.TextWrapping=TextWrapping.Wrap;
        PolicyFields Fields()=>new(school.Text,contact.Text,officer.Text,effective.Text,external.Text);
        void Preview()
        {
            try{var policy=JsonNode.Parse(RenderPolicy(Fields()))!;preview.Text=string.Join("\n\n────────\n\n",policy["documents"]!.AsArray().Select(d=>(string?)d!["body"]));error.Text="";}
            catch(InvalidOperationException e){preview.Text="";error.Text=e.Message;}
        }
        foreach(var box in new[]{school,contact,officer,effective,external})box.TextChanged+=(_,_)=>Preview();
        var reviewed=new CheckBox{Content="학교 개인정보 담당자 등 학교 내부 검토를 마쳤고, 이 내용으로 운영합니다.",Margin=new Thickness(0,14,0,0)};stack.Children.Add(reviewed);
        var confirm=new Button{Content="약관 확정",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,12,0,0),IsEnabled=false};confirm.SetResourceReference(StyleProperty,"PrimaryButton");stack.Children.Add(confirm);stack.Children.Add(error);
        reviewed.Checked+=(_,_)=>confirm.IsEnabled=true;reviewed.Unchecked+=(_,_)=>confirm.IsEnabled=false;
        // The server reads the documents when it starts, so a running server is restarted right away. Accounts,
        // applications and activations live in the server data and are kept.
        confirm.Click+=async(_,_)=>
        {
            try
            {
                if(data.Length==0)throw new InvalidOperationException("서버 데이터 위치를 먼저 정하세요.");
                ConfirmPolicy(data,Fields());c.ChecklistKey="";confirm.IsEnabled=false;
                if(c.Host.Running){error.Text="서버를 다시 시작해 적용하는 중…";await c.Host.Stop();await Start(c);}
                Refresh();
                Notice.Show(window,c.Host.Running?"약관을 확정하고 서버에 적용했습니다. 이제 교직원 계정 신청과 활성화를 받습니다. 기존 계정과 신청은 그대로입니다.":"약관을 확정했습니다. 서버를 시작하면 회원가입을 받습니다.","약관·개인정보 확정");window.Close();
            }
            catch(Exception e)when(e is InvalidOperationException or IOException or UnauthorizedAccessException){error.Text=e.Message;confirm.IsEnabled=reviewed.IsChecked==true;}
        };
        Preview();window.ShowDialog();
    }
}
