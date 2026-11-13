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
        StatusTone.Watch(result);if(owner is MainWindow main)main.RegisterWorkWindow(this);else Owner=owner;api=source;this.administration=administration;this.privacy=privacy;Title=privacy?"개인정보 권리 요청":"오류 신고 · 개선 의견";Width=620;Height=640;MinWidth=450;MinHeight=400;
        var view=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};AdminUi.Page(this,view);var initialized=false;view.Loaded+=async(_,_)=>{if(initialized)return;initialized=true;await Run(administration?()=>List(true):Home);};
    }
    void Text(string text)=>body.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,5)});
    void Button(string label,Func<Task> action){var button=AdminUi.Tone(new Button{Content=label,Margin=new Thickness(0,6,0,0),Padding=new Thickness(8)},label);button.Click+=async(_,_)=>await Run(action);body.Children.Add(button);}
    async Task Run(Func<Task> action){if(busy)return;busy=true;body.IsEnabled=false;result.Text="";try{await action();}catch(Exception error){result.Text=error.Message;}finally{busy=false;body.IsEnabled=true;}}
    // The status line is one control that moves between the form and the review page.
    void Fresh(){(result.Parent as Panel)?.Children.Remove(result);body.Children.Clear();}
    async Task Home()
    {
        Fresh();
        var head=new DockPanel{Margin=new Thickness(0,0,0,16)};
        if(administration||api.Session?.User?.IsAdmin==true){var all=new Button{Content="전체 접수 보기",VerticalAlignment=VerticalAlignment.Bottom};all.SetResourceReference(StyleProperty,"SubtleButton");all.Click+=async(_,_)=>await Run(()=>List(true));DockPanel.SetDock(all,Dock.Right);head.Children.Add(all);}
        head.Children.Add(AdminUi.PageTitle(privacy?"개인정보 요청":"오류 신고 · 개선 의견",privacy?"내 정보를 보거나 고치거나 지워 달라고 학교 정보관리자 선생님께 요청합니다.":"불편한 점이나 바라는 점을 학교에 알려 주세요."));body.Children.Add(head);
        var form=new StackPanel();
        T Field<T>(Panel panel,string label,T input)where T:FrameworkElement{var caption=AdminUi.Label(label,"Muted",12);caption.Margin=new Thickness(0,panel.Children.Count==0?0:14,0,6);panel.Children.Add(caption);panel.Children.Add(input);System.Windows.Automation.AutomationProperties.SetName(input,label);return input;}
        var kind=new ComboBox{ItemsSource=privacy?new[]{"개인정보 열람","개인정보 정정","개인정보 삭제·탈퇴","개인정보 처리정지"}:new[]{"오류 신고","개선 의견"},SelectedIndex=0};
        // Only what this program still has; the server keeps the older codes for the reports already filed.
        var areas=new[]{("로그인","login"),("메시지","messages"),("시간표","timetable"),("특별실","rooms"),("파일","files"),("기타","other")};
        var area=new ComboBox{ItemsSource=areas.Select(a=>a.Item1).ToArray(),SelectedIndex=areas.Length-1};
        if(privacy)Field(form,"요청 종류",kind);
        else
        {
            var pair=new Grid();pair.ColumnDefinitions.Add(new ColumnDefinition());pair.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});pair.ColumnDefinitions.Add(new ColumnDefinition());
            var left=new StackPanel();var right=new StackPanel();Grid.SetColumn(right,2);pair.Children.Add(left);pair.Children.Add(right);Field(left,"종류",kind);Field(right,"어디에서",area);form.Children.Add(pair);
        }
        var title=Field(form,"제목 (3~100자)",new TextBox{MaxLength=100});
        var details=Field(form,privacy?"어떤 정보를 어떻게 처리해 주기를 원하는지 (10자 이상)":"어떤 순서로 했고, 무엇이 기대와 달랐는지 (10자 이상)",new TextBox{MaxLength=4000,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=120,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var note=AdminUi.Label(privacy?"학교 정보관리자 선생님이 로그인 계정과 요청 내용을 확인해 처리하고 이 화면에 회신합니다. 요청만으로 계정이나 자료가 바로 지워지지는 않습니다. 비밀번호·주민등록번호·증명서 사진은 적지 마세요.":"이름·학번·연락처·비밀번호·메시지 본문은 넣지 마세요. 신고는 학교 서버에 보관되며 자동으로 GitHub에 공개되지 않습니다.","Faint",12);note.Margin=new Thickness(0,10,0,0);form.Children.Add(note);
        var actions=new DockPanel{Margin=new Thickness(0,14,0,0)};var send=new Button{Content="학교에 접수"};send.SetResourceReference(StyleProperty,"PrimaryButton");DockPanel.SetDock(send,Dock.Right);actions.Children.Add(send);
        result.Margin=new Thickness(0,0,12,0);result.VerticalAlignment=VerticalAlignment.Center;actions.Children.Add(result);form.Children.Add(actions);
        var sheet=AdminUi.Card(form);sheet.Margin=new Thickness(0,0,0,12);body.Children.Add(sheet);
        FeedbackRequest? pending=null;
        send.Click+=async(_,_)=>await Run(async()=>
        {
            if(pending is null&&(title.Text.Trim().Length<3||details.Text.Trim().Length<10))throw new InvalidOperationException("제목 3자, 설명 10자 이상 적어 주세요.");
            pending??=new(Guid.NewGuid().ToString("N"),privacy?"privacy":kind.SelectedIndex==0?"bug":"suggestion","windows",privacy?new[]{"privacy-access","privacy-rectify","privacy-erase","privacy-restrict"}[kind.SelectedIndex]:areas[area.SelectedIndex].Item2,title.Text,details.Text,typeof(App).Assembly.GetName().Version?.ToString(3)??"1.0.0");
            try{var reply=await api.Send<JsonElement>(HttpMethod.Post,"api/feedback",pending);pending=null;await Home();result.Text="접수 완료 · "+reply.GetProperty("id").GetString();}
            catch{kind.IsEnabled=area.IsEnabled=title.IsEnabled=details.IsEnabled=false;throw;}
        });
        var mine=(await api.Get<JsonElement[]>("api/feedback")).Where(row=>(row.GetProperty("report").GetProperty("kind").GetString()=="privacy")==privacy).ToArray();
        var list=new StackPanel();list.Children.Add(AdminUi.Group("내 접수 현황 · "+mine.Length+"건"));
        if(mine.Length==0)list.Children.Add(AdminUi.Label("아직 접수한 내용이 없습니다.","Muted",13));
        foreach(var row in mine)
        {
            var line=new DockPanel{Margin=new Thickness(0,6,0,6)};var state=row.GetProperty("status").GetString();
            var badge=AdminUi.Badge(Status(state),state=="resolved"?"Success":state=="received"?"Accent":"Warning");badge.VerticalAlignment=VerticalAlignment.Top;DockPanel.SetDock(badge,Dock.Right);line.Children.Add(badge);
            var text=new StackPanel{Margin=new Thickness(0,0,12,0)};var name=AdminUi.Label(row.GetProperty("report").GetProperty("title").GetString()??"","Ink",13);name.TextWrapping=TextWrapping.NoWrap;name.TextTrimming=TextTrimming.CharacterEllipsis;text.Children.Add(name);
            if(privacy&&row.GetProperty("developmentSummary").GetString() is{Length:>0} reply)text.Children.Add(AdminUi.Label("학교 회신: "+reply,"Muted",12));
            line.Children.Add(text);list.Children.Add(line);
        }
        body.Children.Add(AdminUi.Card(list));
    }
    // Reports store short codes; the review page shows them in words.
    static string Where(string? code)=>code switch{"windows"=>"Windows 프로그램","mac"=>"Mac 프로그램","login"=>"로그인","messages"=>"메시지","notices"=>"학교 공지","timetable"=>"시간표","counseling"=>"상담","rooms"=>"특별실","files"=>"파일","other"=>"기타",
        "privacy-access"=>"열람","privacy-rectify"=>"정정","privacy-erase"=>"삭제·탈퇴","privacy-restrict"=>"처리정지",_=>code??""};
    static string Status(string? state)=>state switch{"received"=>"접수","investigating"=>"확인 중","verified"=>"수정 대기","deferred"=>"보류","resolved"=>"처리 완료",_=>state??""};
    // The administrator's review page: one card per report, newest first, each with its own state and reply.
    async Task List(bool admin)
    {
        var embedded=InteractionOwner is LocalServerWindow;
        var mail=await api.Get<JsonElement>("api/admin/feedback/mail-status");var rows=await api.Get<JsonElement[]>("api/admin/feedback");
        Fresh();body.Margin=embedded?new Thickness(0,0,6,0):new Thickness(20);
        if(!embedded){var back=new Button{Content="‹  신고 화면으로",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(-8,0,0,6)};back.SetResourceReference(StyleProperty,"SubtleButton");back.Click+=async(_,_)=>await Run(Home);body.Children.Add(back);}
        var head=new DockPanel{Margin=new Thickness(0,0,0,16)};
        var export=new Button{Content="개발용 자료 저장",VerticalAlignment=VerticalAlignment.Bottom,ToolTip="개발용으로 승인한 설명만 JSON 파일로 저장합니다."};
        export.Click+=async(_,_)=>await Run(async()=>
        {
            var data=await api.Get<JsonElement>("api/admin/feedback/development-export");
            var dialog=new Microsoft.Win32.SaveFileDialog{FileName="feedback-office-"+DateTime.Now.ToString("yyyyMMdd")+".json",Filter="JSON|*.json"};
            if(dialog.ShowDialog(InteractionOwner)==true){await File.WriteAllTextAsync(dialog.FileName,JsonSerializer.Serialize(data,new JsonSerializerOptions{WriteIndented=true}));result.Text="저장 완료. 개인정보를 확인한 개발 자료만 유지보수 PC의 .maintenance/inbox에 전달하세요.";}
        });
        DockPanel.SetDock(export,Dock.Right);head.Children.Add(export);
        head.Children.Add(AdminUi.PageTitle("오류·의견","교직원이 보낸 오류 신고와 개선 의견을 확인하고 처리 상태를 남깁니다."));body.Children.Add(head);
        var mailRow=new DockPanel();
        var mailBadge=AdminUi.Badge(mail.GetProperty("enabled").GetBoolean()?"메일 알림 켜짐":"메일 알림 꺼짐",mail.GetProperty("enabled").GetBoolean()?"Success":"Neutral");mailBadge.Margin=new Thickness(0,0,12,0);mailRow.Children.Add(mailBadge);
        var mailText=AdminUi.Label("오류 메일: "+(mail.GetProperty("configured").GetBoolean()?"발신 정보 입력됨":"발신 인증 설정 필요")+" · 대기 "+mail.GetProperty("pending").GetInt32()+"건 · 발송 "+mail.GetProperty("sent").GetInt32()+"건 · 실패 "+mail.GetProperty("failed").GetInt32()+"건","Muted",13);mailText.VerticalAlignment=VerticalAlignment.Center;mailRow.Children.Add(mailText);
        var mailCard=AdminUi.Card(mailRow,16);mailCard.Margin=new Thickness(0,0,0,12);body.Children.Add(mailCard);
        result.Margin=new Thickness(0,0,0,8);body.Children.Add(result);
        if(rows.Length==0){body.Children.Add(AdminUi.EmptyCard("접수된 신고가 없습니다","교직원이 메신저의 설정 → ‘오류 신고 · 개선 의견’으로 보내면 여기에 표시됩니다."));return;}
        var states=new[]{"received","investigating","verified","deferred","resolved"};
        foreach(var row in rows)
        {
            var report=row.GetProperty("report");var kind=report.GetProperty("kind").GetString();var rights=kind=="privacy";var panel=new StackPanel();
            var top=new DockPanel{Margin=new Thickness(0,0,0,8)};
            var state=new ComboBox{ItemsSource=states.Select(Status).ToArray(),SelectedIndex=Array.IndexOf(states,row.GetProperty("status").GetString()),Width=120,VerticalAlignment=VerticalAlignment.Top};
            System.Windows.Automation.AutomationProperties.SetName(state,"처리 상태");DockPanel.SetDock(state,Dock.Right);top.Children.Add(state);
            var badge=AdminUi.Badge(rights?"개인정보 요청":kind=="bug"?"오류 신고":"개선 의견",rights?"Warning":kind=="bug"?"Danger":"Accent",false);badge.Margin=new Thickness(0,0,10,0);badge.VerticalAlignment=VerticalAlignment.Top;top.Children.Add(badge);
            var words=new StackPanel{Margin=new Thickness(0,0,12,0)};words.Children.Add(AdminUi.Label(report.GetProperty("title").GetString()??"","Ink",14,true));
            words.Children.Add(AdminUi.Label(Where(report.GetProperty("product").GetString())+" · "+Where(report.GetProperty("area").GetString())+(rights?" · 학교 계정 식별자 "+row.GetProperty("ownerId").GetString():""),"Faint",12));top.Children.Add(words);panel.Children.Add(top);
            var details=AdminUi.Label(report.GetProperty("details").GetString()??"","Ink",13);details.Margin=new Thickness(0,0,0,12);panel.Children.Add(details);
            var caption=AdminUi.Label(rights?"학교 회신 · 신청자에게 표시됩니다. 본인 확인과 법령상 처리 가능 여부를 확인한 뒤 결과나 제한 사유를 적으세요.":"개발용 설명 · 가상의 재현 절차만 적으세요. 원문을 그대로 옮기지 마세요.","Muted",12);caption.Margin=new Thickness(0,0,0,6);panel.Children.Add(caption);
            var summary=new TextBox{Text=row.GetProperty("developmentSummary").GetString(),MaxLength=2000,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=56};System.Windows.Automation.AutomationProperties.SetName(summary,rights?"학교 회신":"개발용 설명");panel.Children.Add(summary);
            var bottom=new DockPanel{Margin=new Thickness(0,10,0,0)};
            var save=new Button{Content="저장"};save.SetResourceReference(StyleProperty,"PrimaryButton");DockPanel.SetDock(save,Dock.Right);bottom.Children.Add(save);
            var approved=new CheckBox{Content="개인정보·비밀값이 없는 개발용 설명임을 확인했습니다",IsChecked=!rights&&row.GetProperty("approvedForDevelopment").GetBoolean(),VerticalAlignment=VerticalAlignment.Center};
            if(row.GetProperty("mailStatus").GetString()=="failed"){var retry=new Button{Content="메일 다시 보내기",Margin=new Thickness(0,0,8,0)};DockPanel.SetDock(retry,Dock.Right);bottom.Children.Add(retry);retry.Click+=async(_,_)=>await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/feedback/"+row.GetProperty("id").GetString()+"/retry-mail");await List(true);});}
            if(rights)bottom.Children.Add(AdminUi.Label("이 접수는 오류 메일과 개발용 내보내기에서 제외됩니다.","Faint",12));else bottom.Children.Add(approved);
            panel.Children.Add(bottom);
            save.Click+=async(_,_)=>await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/feedback/"+row.GetProperty("id").GetString()+"/review",new FeedbackReview(row.GetProperty("revision").GetInt32(),states[state.SelectedIndex],summary.Text??"",!rights&&approved.IsChecked==true));await List(true);result.Text="저장했습니다.";});
            var card=AdminUi.Card(panel);card.Margin=new Thickness(0,0,0,12);body.Children.Add(card);
        }
    }
}
