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
        async Task Click(Button button){await Wait(()=>button.IsEnabled,"Action is still loading.");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Yield();}
        bool HasText(string text)=>ViewControls<TextBlock>(administrationView).Any(t=>t.Text.Contains(text,StringComparison.Ordinal));
        await OpenAdministration("members","office");UpdateLayout();
        check(administrationController is null&&HasText("교내 업무 관리자 로그인")&&!OwnedWindows.Cast<Window>().Any(w=>w.IsVisible),"member administration requires in-place authentication without opening the teacher messenger");capture("administrator-signin.png",this);
        check(await SignInAdministrator("office","http://127.0.0.1:5080","admin",password,viewRevision),"office administrator signs in directly in the administrator application");
        var source=adminSessions["office"];var login="synthetic"+Guid.NewGuid().ToString("N")[..8];var name="가상교사 가";
        await OpenAdministration("features","office");await Wait(()=>ViewControls<CheckBox>(administrationView).Count()>=7,"기능 설정이 열리지 않았습니다.");
        async Task Toggle(string title,bool on)
        {
            var box=ViewControls<CheckBox>(administrationView).Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)==title);
            box.IsChecked=on;box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Wait(()=>HasText(on?$"‘{title}’ 메뉴가 표시됩니다":$"‘{title}’ 메뉴를 숨깁니다"),title+" 설정이 저장되지 않았습니다.");
        }
        await Toggle("시간표",false);await Toggle("설문",false);
        var features=await source.Get<Dictionary<string,bool>>("api/features");
        check(!features["timetable"]&&!features["survey"]&&features["chat"]&&!(await source.Get<JsonElement>("api/timetable/status")).GetProperty("enabled").GetBoolean(),
            "the administrator switches optional menus off one by one from the native settings page; the timetable is also closed on the server");
        capture("administrator-features.png",this);
        await Toggle("시간표",true);await Toggle("설문",true);
        check((await source.Get<Dictionary<string,bool>>("api/features")).Values.All(on=>on),"switching the menus back on restores every feature");
        await OpenAdministration("attachment-limit","office");await Wait(()=>ViewControls<ComboBox>(administrationView).Any(),"첨부 용량 화면이 열리지 않았습니다.");
        var capacity=ViewControls<ComboBox>(administrationView).Single();capacity.SelectedItem=3;
        await Click(Action("설정 저장"));await Wait(()=>HasText("3GiB로 저장했습니다"),"첨부 용량 설정이 저장되지 않았습니다.");
        await source.RefreshAttachmentLimit();check(source.AttachmentLimit==3L*1024*1024*1024,"the native administrator can change the school attachment limit");capture("administrator-attachment.png",this);
        var teacher=await source.Send<JsonElement>(HttpMethod.Post,"api/admin/users",new{username=login,name,department="가상 교무부",password,isAdmin=false,canBroadcast=false});
        await OpenAdministration("members","office");await Wait(()=>HasText(name),"구성원 목록이 로드되지 않았습니다.");
        check(administrationController is OfficeWindow&&!administrationController.IsVisible&&!OwnedWindows.Cast<Window>().Any(w=>w.IsVisible),"existing member settings load inside the single administrator window");capture("administrator-members.png",this);
        Width=760;Height=560;UpdateLayout();
        var memberHeading=ViewControls<TextBlock>(administrationView).Single(text=>text.Text=="교직원·권한");
        var registrationButton=ViewControls<Button>(administrationView).Single(button=>System.Windows.Automation.AutomationProperties.GetName(button)=="교직원 등록 · 승인");
        FrameworkElement[] memberHeader=[memberHeading,registrationButton,Action("조직도 폴더 관리")];
        check(memberHeading.ActualHeight<memberHeading.FontSize*1.6&&memberHeader.All(control=>
        {
            var start=control.TransformToAncestor(this).Transform(new Point());
            return control.IsVisible&&control.ActualWidth>0&&start.X>=0&&start.X+control.ActualWidth<=ActualWidth-8&&
                start.Y>=0&&start.Y+control.ActualHeight<=ActualHeight-42;
        }),"the member heading stays on one line and both toolbar buttons are within the viewport at minimum size");
        var memberRows=ViewControls<Button>(administrationView).Where(button=>System.Windows.Automation.AutomationProperties.GetName(button).EndsWith(" 계정 설정",StringComparison.Ordinal)).ToArray();
        check(memberRows.Length>=2&&memberRows.All(button=>button.ActualWidth>0&&button.TransformToAncestor(this).Transform(new Point()).X+button.ActualWidth<=ActualWidth-8),
            "the member table stays within the administrator window at its minimum width");
        capture("administrator-members-compact.png",this);Width=1060;Height=780;UpdateLayout();
        await Click(ViewControls<Button>(administrationView).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)==name+" 계정 설정"));await Wait(()=>ViewControls<TextBlock>(administrationView).Any(t=>t.Text.StartsWith("로그인 이름 "+login,StringComparison.Ordinal)),"교직원 설정을 열지 못했습니다.");
        TextBox Input(string label)=>ViewControls<TextBox>(administrationView).Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)==label);
        Input("부서").Text="수정된 가상 부서";Input("직책").Text="가상부장";foreach(var role in new[]{"학사일정 담당","시간표 담당"})ViewControls<CheckBox>(administrationView).Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)==role).IsChecked=true;await Click(Action("변경 저장"));
        await Wait(()=>HasText("수정된 가상 부서")&&HasText("학사일정"),"교직원 설정 저장이 반영되지 않았습니다.");
        var saved=(await source.Get<JsonElement>("api/admin/users")).EnumerateArray().Single(u=>u.GetProperty("username").GetString()==login);
        var teacherId=saved.GetProperty("id").GetString();var duty=(await source.Get<JsonElement>("api/admin/office/permissions")).EnumerateArray().Single(p=>p.GetProperty("userId").GetString()==teacherId);
        check(saved.GetProperty("department").GetString()=="수정된 가상 부서"&&saved.GetProperty("position").GetString()=="가상부장"&&duty.GetProperty("calendarManager").GetBoolean()&&(await source.Get<JsonElement>("api/admin/timetable-managers")).EnumerateArray().Single(e=>e.GetProperty("id").GetString()==teacherId).GetProperty("enabled").GetBoolean(),"the member panel saves department, position and school duties, including the timetable, together through the authenticated API");
        await Click(Action("조직도 폴더 관리"));await Wait(()=>HasText("학교 아래 두 단계"),"조직도 폴더 관리가 열리지 않았습니다.");
        Input("폴더 이름").Text="가상 프로젝트";await Click(Action("폴더 추가"));
        Input("폴더 이름").Text="가상 준비팀";await Click(Action("하위 폴더 추가"));
        await Wait(()=>HasText("가상 준비팀 · 교직원"),"하위 폴더가 선택되지 않았습니다.");
        check(!Action("하위 폴더 추가").IsEnabled,"the folder editor allows exactly two folder levels below the school");
        ViewControls<CheckBox>(administrationView).Single(box=>box.Content?.ToString()?.StartsWith(name+" · ",StringComparison.Ordinal)==true).IsChecked=true;
        await Click(Action("변경 저장"));await Wait(()=>HasText("조직도 폴더를 저장했습니다."),"조직도 저장에 실패했습니다.");
        var folders=(await source.Get<SchoolMessenger.Contracts.RosterFolders>("api/admin/roster-folders")).Folders;
        var rootFolder=folders.Single(folder=>folder.Name=="가상 프로젝트");
        check(folders.Any(folder=>folder.Name=="가상 준비팀"&&folder.ParentId==rootFolder.Id&&folder.MemberIds.SequenceEqual([teacherId])),
            "the administrator creates two nested folders and places a teacher in the selected folder");
        capture("administrator-roster-folders.png",this);
        await Click(Action("← 교직원 목록"));await Wait(()=>ViewControls<TextBox>(administrationView).Any(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="교직원 검색"),"교직원 목록으로 돌아오지 못했습니다.");
        var search=ViewControls<TextBox>(administrationView).Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="교직원 검색");search.Text="존재하지않는가상교사";UpdateLayout();check(HasText("일치하는 교직원이 없습니다.")&&!ViewControls<Button>(administrationView).Any(b=>System.Windows.Automation.AutomationProperties.GetName(b).EndsWith(" 계정 설정",StringComparison.Ordinal)),"member search filters the actual loaded directory and reports an empty result");
        await Click(ViewControls<Button>(administrationView).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="교직원 등록 · 승인"));await Wait(()=>HasText("새 교직원 등록")&&HasText("대기 중인 가입 신청이 없습니다."),"교직원 등록 · 승인 화면이 열리지 않았습니다.");
        check(!ViewControls<Button>(administrationView).Any(b=>b.Content?.ToString()=="특별실 목록"),"the registration page leaves navigation to the rail instead of repeating shortcut buttons");capture("administrator-registration.png",this);
        await OpenAdministration("rooms","office");await Wait(()=>ViewControls<Button>(administrationView).Any(b=>b.Content?.ToString()=="특별실 추가"),"특별실 설정이 로드되지 않았습니다.");
        var roomFields=ViewControls<TextBox>(administrationView).ToArray();var original=roomFields[0];var roomName="가상 특별실 "+Guid.NewGuid().ToString("N")[..6];roomFields[0].Text=roomName;roomFields[1].Text="가상 본관";await Click(Action("특별실 추가"));
        await Wait(()=>!ViewControls<TextBox>(administrationView).Contains(original)&&ViewControls<TextBox>(administrationView).Any(t=>t.Text==roomName),"특별실 저장이 반영되지 않았습니다.");
        check((await source.Get<JsonElement>("api/office/setup")).GetProperty("rooms").EnumerateArray().Any(r=>r.GetProperty("name").GetString()==roomName),"special-room settings persist from the administrator's main content area");capture("administrator-rooms.png",this);
        await OpenAdministration("calendar","office");await Wait(()=>ViewControls<Button>(administrationView).Any(b=>b.Content?.ToString()=="학교 일정 등록"),"학교 일정 관리가 로드되지 않았습니다.");check(administrationController is OfficeWindow,"school calendar management is available in the administrator's main content area");capture("administrator-calendar.png",this);
        await OpenAdministration("audit","office");await Wait(()=>HasText("최근 운영 변경 기록"),"운영 기록이 로드되지 않았습니다.");check(administrationController is OfficeWindow,"operating-change audit records load without opening another main window");capture("administrator-audit.png",this);
        await OpenAdministration("policy","office");await Wait(()=>ViewControls<Button>(administrationView).Any(b=>b.Content?.ToString()=="학교 검토용 초안 파일 만들기"&&b.IsEnabled),"학교 정책 초안 설정이 로드되지 않았습니다.");check(administrationController is SchoolPolicyWindow,"school privacy-policy drafting initializes inside the shared administrator host");capture("administrator-policy.png",this);
        await source.Send<JsonElement>(HttpMethod.Post,"api/feedback",new SchoolMessenger.Contracts.FeedbackRequest(Guid.NewGuid().ToString("N"),"bug","windows","messages","가상 오류 신고","가상 재현 절차: 메시지를 보낸 뒤 받은함 목록이 바로 갱신되지 않았습니다.","0.1.1"));
        await OpenAdministration("feedback","office");await Wait(()=>HasText("오류 메일:")&&HasText("가상 오류 신고"),"오류 신고 관리가 로드되지 않았습니다.");check(administrationController is FeedbackWindow,"authenticated error reports and improvement feedback load in the same program, one card per report");capture("administrator-feedback.png",this);
        // No page may leave a white sheet on the grey canvas: the embedded page itself has no background of its own.
        check(administrationView.Children.OfType<Border>().All(sheet=>sheet.Background is null),"embedded pages sit on the canvas with their own cards");
        await OpenAdministration("resources","office");await Wait(()=>administrationController is ServerManagementWindow {Ready:true},"통합 운영 현황이 로드되지 않았습니다.");check(!administrationController!.IsVisible,"resource monitoring is embedded rather than spawning a separate main window");capture("administrator-resources.png",this);
        await SignOutAdministrators();check(adminSessions.Count==0&&administrationController is null&&!OwnedWindows.Cast<Window>().Any(w=>w.IsVisible),"locking the administrator clears every session and child view");
        var administratorOnly=new LocalServerWindow(System.IO.Path.Combine(directory,"empty-install"),System.IO.Path.Combine(directory,"administrator-only"),@"Software\YyManagerVerification\"+Guid.NewGuid().ToString("N")){Owner=this};
        try{administratorOnly.Show();await Wait(()=>administratorOnly.activePage=="members"&&ViewControls<TextBox>(administratorOnly.administrationView).Any(),"관리자 전용 구성이 로그인 화면으로 열리지 않았습니다.");check(administratorOnly.cards.All(c=>!c.Host.Installed&&!c.Start.IsEnabled)&&ViewControls<TextBox>(administratorOnly.administrationView).First().Text.Length==0,"administrator-only installation opens remote sign-in without requiring or starting a local server");}
        finally{administratorOnly.quitting=true;administratorOnly.Close();}
    }
}
