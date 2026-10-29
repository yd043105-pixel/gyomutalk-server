using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SchoolMessenger.Desktop;

public sealed partial class LocalServerWindow
{
    static IEnumerable<T> ViewControls<T>(DependencyObject parent)where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T found)yield return found;foreach(var descendant in ViewControls<T>(child))yield return descendant;}
    }
    async Task VerifyAdministration(string password,Action<bool,string> check,Action<string,Window?> capture)
    {
        async Task Wait(Func<bool> condition,string reason){for(var i=0;i<120;i++){UpdateLayout();if(condition())return;await Task.Delay(100);}throw new InvalidOperationException(reason+"\n"+string.Join("\n",ViewControls<TextBlock>(administrationView).Select(t=>t.Text)));}
        Button Action(string text)=>ViewControls<Button>(administrationView).Single(b=>b.Content?.ToString()==text);
        void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool HasText(string text)=>ViewControls<TextBlock>(administrationView).Any(t=>t.Text.Contains(text,StringComparison.Ordinal));
        await OpenAdministration("members","office");UpdateLayout();
        check(administrationController is null&&HasText("교내 업무 관리자 로그인")&&!OwnedWindows.Cast<Window>().Any(w=>w.IsVisible),"member administration requires in-place authentication without opening the teacher messenger");capture("administrator-signin.png",this);
        check(await SignInAdministrator("office","http://127.0.0.1:5080","admin",password,viewRevision),"office administrator signs in directly in the administrator application");
        var source=adminSessions["office"];var login="synthetic"+Guid.NewGuid().ToString("N")[..8];var name="가상교사 가";
        var teacher=await source.Send<JsonElement>(HttpMethod.Post,"api/admin/users",new{username=login,name,department="가상 교무부",password,isAdmin=false,canBroadcast=false});
        await OpenAdministration("members","office");await Wait(()=>HasText(name),"구성원 목록이 로드되지 않았습니다.");
        check(administrationController is OfficeWindow&&!administrationController.IsVisible&&!OwnedWindows.Cast<Window>().Any(w=>w.IsVisible),"existing member settings load inside the single administrator window");capture("administrator-members.png",this);
        Width=760;Height=560;UpdateLayout();
        check(ViewControls<Button>(administrationView).Where(b=>System.Windows.Automation.AutomationProperties.GetName(b).EndsWith(" 계정 설정",StringComparison.Ordinal)||b.Content?.ToString()=="교직원 등록 · 승인").All(b=>b.ActualWidth>0&&b.TransformToAncestor(this).Transform(new Point()).X+b.ActualWidth<=ActualWidth-8),"member table actions stay within the administrator window at its minimum width");capture("administrator-members-compact.png",this);Width=1060;Height=780;UpdateLayout();
        Click(ViewControls<Button>(administrationView).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)==name+" 계정 설정"));await Wait(()=>ViewControls<Button>(administrationView).Any(b=>b.Content?.ToString()=="계정 상태 저장 · 기존 세션 회수"),"교직원 설정을 열지 못했습니다.");
        var fields=ViewControls<TextBox>(administrationView).ToArray();fields[2].Text="수정된 가상 부서";fields[3].Text="가상부장";Click(Action("계정 상태 저장 · 기존 세션 회수"));
        await Wait(()=>HasText("수정된 가상 부서"),"교직원 설정 저장이 반영되지 않았습니다.");
        var saved=(await source.Get<JsonElement>("api/admin/users")).EnumerateArray().Single(u=>u.GetProperty("username").GetString()==login);
        check(saved.GetProperty("department").GetString()=="수정된 가상 부서"&&saved.GetProperty("position").GetString()=="가상부장","in-place member editing persists department and position through the authenticated API");
        var search=ViewControls<TextBox>(administrationView).Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="교직원 검색");search.Text="존재하지않는가상교사";UpdateLayout();check(HasText("일치하는 교직원이 없습니다.")&&!ViewControls<Button>(administrationView).Any(b=>b.Content?.ToString()=="설정"),"member search filters the actual loaded directory and reports an empty result");
        await OpenAdministration("rooms","office");await Wait(()=>ViewControls<Button>(administrationView).Any(b=>b.Content?.ToString()=="특별실 추가"),"특별실 설정이 로드되지 않았습니다.");
        var roomFields=ViewControls<TextBox>(administrationView).ToArray();var original=roomFields[0];var roomName="가상 특별실 "+Guid.NewGuid().ToString("N")[..6];roomFields[0].Text=roomName;roomFields[1].Text="가상 본관";Click(Action("특별실 추가"));
        await Wait(()=>!ViewControls<TextBox>(administrationView).Contains(original)&&ViewControls<TextBox>(administrationView).Any(t=>t.Text==roomName),"특별실 저장이 반영되지 않았습니다.");
        check((await source.Get<JsonElement>("api/office/setup")).GetProperty("rooms").EnumerateArray().Any(r=>r.GetProperty("name").GetString()==roomName),"special-room settings persist from the administrator's main content area");capture("administrator-rooms.png",this);
        await OpenAdministration("sections","office");await Wait(()=>HasText("선택과목 분반 · 수강 명부"),"분반 관리가 로드되지 않았습니다.");check(administrationController is OfficeWindow,"class and section administration loads through the connected school-news bridge");
        await OpenAdministration("calendar","office");await Wait(()=>ViewControls<DatePicker>(administrationView).Any()&&ViewControls<Button>(administrationView).Any(b=>b.Content?.ToString()=="학교 일정 등록"),"학교 일정 관리가 로드되지 않았습니다.");check(administrationController is OfficeWindow,"school calendar management is available in the administrator's main content area");
        await OpenAdministration("audit","office");await Wait(()=>HasText("최근 운영 변경 기록"),"운영 기록이 로드되지 않았습니다.");check(administrationController is OfficeWindow,"operating-change audit records load without opening another main window");
        await OpenAdministration("policy","office");await Wait(()=>ViewControls<Button>(administrationView).Any(b=>b.Content?.ToString()=="학교 검토용 초안 파일 만들기"&&b.IsEnabled),"학교 정책 초안 설정이 로드되지 않았습니다.");check(administrationController is SchoolPolicyWindow,"school privacy-policy drafting initializes inside the shared administrator host");
        await OpenAdministration("feedback","office");await Wait(()=>HasText("오류 메일:"),"오류 신고 관리가 로드되지 않았습니다.");check(administrationController is FeedbackWindow,"authenticated error reports and improvement feedback load in the same program");
        await OpenAdministration("resources","office");await Wait(()=>administrationController is ServerManagementWindow {Ready:true},"통합 운영 현황이 로드되지 않았습니다.");check(!administrationController!.IsVisible,"resource monitoring is embedded rather than spawning a separate main window");capture("administrator-resources.png",this);
        await OpenAdministration("portal","announcements");UpdateLayout();check(administrationController is null&&HasText("학생·학부모 소식 관리자 로그인")&&adminSessions.ContainsKey("office")&&!adminSessions.ContainsKey("announcements"),"office authentication does not authorize the separate student-parent service");
        check(await SignInAdministrator("announcements","http://127.0.0.1:5081","admin",password,viewRevision),"the school-news service verifies its own administrator session");await OpenAdministration("portal","announcements");await Wait(()=>HasText("가입 승인 · 계정 상태 · 고유번호"),"학생·학부모 관리가 로드되지 않았습니다.");
        check(administrationController is PortalAdministrationWindow&&!ReferenceEquals(adminSessions["office"].Cookies,adminSessions["announcements"].Cookies),"student-parent management is embedded and keeps its cookies isolated from office administration");capture("administrator-portal.png",this);
        await OpenAdministration("resources","announcements");await Wait(()=>administrationController is ServerManagementWindow {Ready:true},"학교 소식 운영 현황이 로드되지 않았습니다.");var resourceTabs=ViewControls<TabControl>(administrationView).Single();resourceTabs.SelectedIndex=2;UpdateLayout();check(ReferenceEquals(((ServerManagementWindow)administrationController!).InteractionOwner,this)&&!ViewControls<Button>(administrationView).Any(b=>b.Content?.ToString()=="교내 서버 자동검색 설정"),"school-news settings keep the shared dialog owner and omit unsupported office-only discovery controls");await OpenAdministration("portal","announcements");await Wait(()=>HasText("가입 승인 · 계정 상태 · 고유번호"),"학교 소식 명부로 돌아가지 못했습니다.");
        var oldCookies=adminSessions["office"].Cookies;await SignOutAdministrator("office");using(var copied=new HttpClient(new HttpClientHandler{CookieContainer=oldCookies})){check((await copied.GetAsync("http://127.0.0.1:5080/api/admin/users")).StatusCode==HttpStatusCode.Unauthorized,"logging out revokes the office session on the server, including a copied cookie");}
        check(adminSessions.ContainsKey("announcements")&&administrationController is PortalAdministrationWindow&&(await adminSessions["announcements"].Get<JsonElement>("api/admin/status")).ValueKind==JsonValueKind.Object,"logging out from office administration preserves the separate school-news session");
        await OpenAdministration("members","office");var denied=false;try{await SignInAdministrator("office","http://127.0.0.1:5080",login,password,viewRevision);}catch(ApiRequestException e)when(e.StatusCode==HttpStatusCode.Forbidden){denied=true;}check(denied&&!adminSessions.ContainsKey("office"),"ordinary teacher credentials cannot unlock the administrator workspace");
        await SignInAdministrator("office","http://127.0.0.1:5080","admin",password,viewRevision);await OpenAdministration("members","office");await Wait(()=>HasText(name),"재로그인 후 구성원 목록을 열지 못했습니다.");await adminSessions["office"].Send<JsonElement>(HttpMethod.Post,"api/logout");await CheckAdministratorSessions();UpdateLayout();
        check(!adminSessions.ContainsKey("office")&&administrationController is null&&HasText("교내 업무 관리자 로그인"),"server-side session revocation removes the active management view and requires authentication again");
        await SignOutAdministrators();check(adminSessions.Count==0&&!OwnedWindows.Cast<Window>().Any(w=>w.IsVisible),"locking the administrator program clears every administrative session and child view");
        await SignInAdministrator("announcements","http://127.0.0.1:5081","admin",password,viewRevision);var hiddenCookies=adminSessions["announcements"].Cookies;Close();
        using(var copied=new HttpClient(new HttpClientHandler{CookieContainer=hiddenCookies})){var revoked=false;for(var i=0;i<50;i++){if((await copied.GetAsync("http://127.0.0.1:5081/api/admin/status")).StatusCode==HttpStatusCode.Unauthorized){revoked=true;break;}await Task.Delay(100);}check(!IsVisible&&adminSessions.Count==0&&cards.All(c=>c.Host.Running)&&revoked,"closing the administrator hides to the tray, revokes its sessions and keeps both local servers running");}
        Show();Activate();ShowServerControls();
        var administratorOnly=new LocalServerWindow(System.IO.Path.Combine(directory,"empty-install"),System.IO.Path.Combine(directory,"administrator-only"),@"Software\YyManagerVerification\"+Guid.NewGuid().ToString("N")){Owner=this};
        try{administratorOnly.Show();await Wait(()=>administratorOnly.activePage=="members"&&ViewControls<TextBox>(administratorOnly.administrationView).Any(),"관리자 전용 구성이 로그인 화면으로 열리지 않았습니다.");check(administratorOnly.cards.All(c=>!c.Host.Installed&&!c.Start.IsEnabled)&&ViewControls<TextBox>(administratorOnly.administrationView).First().Text.Length==0,"administrator-only installation opens remote sign-in without requiring or starting a local server");}
        finally{administratorOnly.quitting=true;administratorOnly.Close();}
    }
}
