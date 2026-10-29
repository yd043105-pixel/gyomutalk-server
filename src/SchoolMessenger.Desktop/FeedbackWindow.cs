using System.Text.Json;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed class FeedbackWindow:Window
{
    readonly Api api;
    readonly StackPanel body=new(){Margin=new Thickness(20)};
    readonly TextBlock result=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)};
    bool busy;
    readonly bool administration;
    bool privacy;
    Window InteractionOwner=>Window.GetWindow(body)??this;
    public FeedbackWindow(MainWindow main):this(main,main.Api!,false){}
    public FeedbackWindow(Window owner,Api source,bool administration,bool privacy=false)
    {
        Owner=owner;api=source;this.administration=administration;this.privacy=privacy;Title=privacy?"개인정보 권리 요청":"오류 신고 · 개선 의견";Width=620;Height=640;MinWidth=450;MinHeight=400;
        var view=new ScrollViewer{Content=body};Content=view;var initialized=false;view.Loaded+=async(_,_)=>{if(initialized)return;initialized=true;await Run(administration?()=>List(true):Home);};
    }
    void Text(string text)=>body.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,5)});
    void Button(string label,Func<Task> action){var button=new Button{Content=label,Margin=new Thickness(0,6,0,0),Padding=new Thickness(8)};button.Click+=async(_,_)=>await Run(action);body.Children.Add(button);}
    async Task Run(Func<Task> action){if(busy)return;busy=true;body.IsEnabled=false;result.Text="";try{await action();}catch(Exception error){result.Text=error.Message;}finally{busy=false;body.IsEnabled=true;}}
    async Task Home()
    {
        body.Children.Clear();Text(privacy?"개인정보 열람·정정·삭제·처리정지 요청":"오류 신고 · 개선 의견");body.Children.Add(result);
        if(privacy){var policy=await api.Get<RegistrationPolicySet>("api/registration/policy");Text("처리 주체: "+policy.Operator+"\n문의: "+policy.Contact);}
        Text(privacy?"학교가 로그인 계정과 요청 내용을 확인하여 처리하고 이 화면에 회신합니다. 요청만으로 계정이나 자료가 즉시 삭제되지는 않습니다. 비밀번호·주민등록번호·증명서 사진을 적거나 첨부하지 마세요. 로그인할 수 없거나 다른 방식의 도움이 필요하면 위 학교 문의처로 연락하세요.":"이름·학번·연락처·비밀번호·메시지 본문은 넣지 마세요. 신고는 학교 서버에 보관되며 자동으로 GitHub에 공개되지 않습니다.");
        var kind=new ComboBox{ItemsSource=privacy?new[]{"개인정보 열람","개인정보 정정","개인정보 삭제·탈퇴·동의 철회","개인정보 처리정지"}:new[]{"오류 신고","개선 의견"},SelectedIndex=0};body.Children.Add(kind);
        var area=new ComboBox{ItemsSource=new[]{"로그인","메시지","학교 공지","시간표","상담","특별실","파일","기타"},SelectedIndex=7};if(!privacy)body.Children.Add(area);
        var title=new TextBox{MaxLength=100,Margin=new Thickness(0,8,0,0)};Text("제목 (3~100자)");body.Children.Add(title);
        var details=new TextBox{MaxLength=4000,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=110,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Text(privacy?"어떤 정보에 대해 어떻게 처리해 주기를 원하는지 적어 주세요. (10자 이상)":"어떤 순서로 실행했나요? 예상한 결과와 실제 결과를 적어 주세요. (10자 이상)");body.Children.Add(details);
        FeedbackRequest? pending=null;
        Button("학교에 접수",async()=>
        {
            if(pending is null&&(title.Text.Trim().Length<3||details.Text.Trim().Length<10))throw new InvalidOperationException("제목 3자, 재현 설명 10자 이상 적어 주세요.");
            pending??=new(Guid.NewGuid().ToString("N"),privacy?"privacy":kind.SelectedIndex==0?"bug":"suggestion","windows",privacy?new[]{"privacy-access","privacy-rectify","privacy-erase","privacy-restrict"}[kind.SelectedIndex]:new[]{"login","messages","notices","timetable","counseling","rooms","files","other"}[area.SelectedIndex],title.Text,details.Text,typeof(App).Assembly.GetName().Version?.ToString(3)??"1.0.0");
            try{var reply=await api.Send<JsonElement>(HttpMethod.Post,"api/feedback",pending);pending=null;await Home();result.Text="접수 완료 · "+reply.GetProperty("id").GetString();}
            catch{kind.IsEnabled=area.IsEnabled=title.IsEnabled=details.IsEnabled=false;throw;}
        });
        Button("내 접수 현황 새로고침",Home);
        if(administration||api.Session?.User?.IsAdmin==true)Button("관리자 · 전체 접수와 개발용 내보내기",()=>List(true));
        foreach(var row in await api.Get<JsonElement[]>("api/feedback"))
        {
            if((row.GetProperty("report").GetProperty("kind").GetString()=="privacy")!=privacy)continue;
            Text(row.GetProperty("report").GetProperty("title").GetString()+" · "+Status(row.GetProperty("status").GetString()));
            if(privacy&&row.GetProperty("developmentSummary").GetString() is {Length:>0} reply)Text("학교 회신: "+reply);
        }
    }
    static string Status(string? state)=>state switch{"received"=>"접수","investigating"=>"확인 중","verified"=>"수정 대기","deferred"=>"보류","resolved"=>"처리 완료",_=>state??""};
    async Task List(bool admin)
    {
        body.Children.Clear();body.Children.Add(result);Button("신고 화면으로",Home);
        var mail=await api.Get<JsonElement>("api/admin/feedback/mail-status");Text("오류 메일: "+(mail.GetProperty("enabled").GetBoolean()?"활성":"비활성")+" · "+(mail.GetProperty("configured").GetBoolean()?"발신 정보 입력됨":"발신 인증 설정 필요")+" · 대기 "+mail.GetProperty("pending").GetInt32()+"건 · 발송 "+mail.GetProperty("sent").GetInt32()+"건 · 실패 "+mail.GetProperty("failed").GetInt32()+"건");
        Text("개발용 설명에는 가상의 재현 절차만 적으세요. 원문을 그대로 복사하지 마세요. 승인된 설명만 개발 도구에 내보냅니다.");
        Button("승인한 개발용 자료를 JSON으로 저장",async()=>
        {
            var export=await api.Get<JsonElement>("api/admin/feedback/development-export");
            var dialog=new Microsoft.Win32.SaveFileDialog{FileName="feedback-office-"+DateTime.Now.ToString("yyyyMMdd")+".json",Filter="JSON|*.json"};
            if(dialog.ShowDialog(InteractionOwner)==true){await File.WriteAllTextAsync(dialog.FileName,JsonSerializer.Serialize(export,new JsonSerializerOptions{WriteIndented=true}));result.Text="저장 완료. 개인정보를 확인한 개발 자료만 유지보수 PC의 .maintenance/inbox에 전달하세요.";}
        });
        foreach(var row in await api.Get<JsonElement[]>("api/admin/feedback"))
        {
            var report=row.GetProperty("report");var rights=report.GetProperty("kind").GetString()=="privacy";Text(report.GetProperty("title").GetString()+" · "+report.GetProperty("product").GetString());
            if(rights)Text("개인정보 권리 요청 · "+report.GetProperty("area").GetString()+" · 학교 계정 식별자: "+row.GetProperty("ownerId").GetString()+"\n본인·대리인 자격 및 법령상 처리 가능 여부를 확인한 뒤 처리하고 결과·제한 사유를 회신하세요. 이 접수는 오류 메일과 개발용 내보내기에서 제외됩니다.");
            Text(report.GetProperty("details").GetString()!);if(rights)Text("학교 회신 (신청자에게 표시됨)");
            var states=new[]{"received","investigating","verified","deferred","resolved"};var state=new ComboBox{ItemsSource=states.Select(Status).ToArray(),SelectedIndex=Array.IndexOf(states,row.GetProperty("status").GetString())};body.Children.Add(state);
            var summary=new TextBox{Text=row.GetProperty("developmentSummary").GetString(),MaxLength=2000,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=65};body.Children.Add(summary);
            var approved=new CheckBox{Content="개인정보·비밀값이 없는 개발용 설명임을 확인했습니다",IsChecked=!rights&&row.GetProperty("approvedForDevelopment").GetBoolean()};if(!rights)body.Children.Add(approved);
            Button(rights?"처리 상태·학교 회신 저장":"상태·개발용 설명 저장",async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/feedback/"+row.GetProperty("id").GetString()+"/review",new FeedbackReview(row.GetProperty("revision").GetInt32(),states[state.SelectedIndex],summary.Text??"",!rights&&approved.IsChecked==true));await List(true);});
            if(row.GetProperty("mailStatus").GetString()=="failed")Button("실패한 메일 다시 보내기",async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/feedback/"+row.GetProperty("id").GetString()+"/retry-mail");await List(true);});
        }
    }
}
