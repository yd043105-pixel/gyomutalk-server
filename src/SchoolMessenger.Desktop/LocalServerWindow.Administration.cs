using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SchoolMessenger.Desktop;

public sealed partial class LocalServerWindow
{
    readonly Dictionary<string,Api> adminSessions=new();
    readonly Grid administrationView=new();
    readonly TextBlock adminIdentity=Note("관리자 로그인이 필요합니다."),adminRole=AdminUi.Label("","Faint",12);
    readonly Border adminAvatar=AdminUi.Avatar("관");
    readonly Button signOut=new(){Content="이 서버 로그아웃",Visibility=Visibility.Collapsed};
    readonly List<Button> administrationNavigation=new();
    readonly DispatcherTimer sessionTimer=new(){Interval=TimeSpan.FromSeconds(15)};
    Window? administrationController;
    string activeService="office",activePage="server";
    int viewRevision;
    bool closingPage,checkingSessions,signingIn;
    static string RoleName(string role)=>"교내 업무";

    void CloseAdministrationPage()
    {
        viewRevision++;closingPage=true;
        try
        {
            administrationView.Children.Clear();
            foreach(var window in OwnedWindows.Cast<Window>().ToArray())window.Close();
            administrationController=null;
        }finally{closingPage=false;}
    }
    void UpdateAdminIdentity()
    {
        var authenticated=adminSessions.TryGetValue(activeService,out var source);
        var name=authenticated?source!.Session?.User?.Name??"관리자":"로그인 필요";
        adminIdentity.Text=name;adminRole.Text=RoleName(activeService)+" 관리자";((TextBlock)adminAvatar.Child).Text=authenticated&&name.Length>0?name[..1]:"관";
        signOut.Visibility=authenticated?Visibility.Visible:Visibility.Collapsed;
    }
    internal void ShowServerControls()=>SelectCard(cards.First(c=>c.Service==activeService));
    internal void ShowServer(string service)=>SelectCard(cards.First(c=>c.Service==service));
    async Task<bool> SignInAdministrator(string service,string address,string username,string password,int expectedRevision)
    {
        if(signingIn)return false;signingIn=true;Api? candidate=null;
        try
        {
            candidate=new Api(address);await candidate.Login(username,password,false);
            // This endpoint verifies administrator authority on both distinct services.
            // Announcement accounts use a different role model from office accounts.
            await candidate.Get<JsonElement>("api/admin/server-resources");
            if(expectedRevision!=viewRevision||!IsVisible)return false;
            if(adminSessions.Remove(service,out var previous)){try{await previous.Send<JsonElement>(HttpMethod.Post,"api/logout");}finally{previous.Dispose();}}
            if(expectedRevision!=viewRevision||!IsVisible)return false;
            adminSessions[service]=candidate;candidate=null;UpdateAdminIdentity();return true;
        }
        finally
        {
            if(candidate is not null){try{if(candidate.Session?.User is not null)await candidate.Send<JsonElement>(HttpMethod.Post,"api/logout");}catch{}candidate.Dispose();}
            signingIn=false;
        }
    }
    async Task SignOutAdministrator(string service)
    {
        var visible=activeService==service;
        if(visible)CloseAdministrationPage();
        var revision=viewRevision;var page=activePage;
        if(adminSessions.Remove(service,out var source))
        {
            try{await source.Send<JsonElement>(HttpMethod.Post,"api/logout");}catch{}finally{source.Dispose();}
        }
        UpdateAdminIdentity();
        if(visible&&revision==viewRevision&&activeService==service&&page!="server")ShowAdministratorLogin(service,page,"로그아웃했습니다.");
    }
    async Task SignOutAdministrators()
    {
        CloseAdministrationPage();var sessions=adminSessions.Values.ToArray();adminSessions.Clear();UpdateAdminIdentity();
        if(activePage!="server")ShowAdministratorLogin(activeService,activePage,"관리자 계정이 로그아웃되었습니다.");
        foreach(var source in sessions){try{await source.Send<JsonElement>(HttpMethod.Post,"api/logout");}catch{}finally{source.Dispose();}}
    }
    async Task CheckAdministratorSessions()
    {
        if(checkingSessions||!IsVisible)return;checkingSessions=true;
        try
        {
            foreach(var pair in adminSessions.ToArray())
            {
                try{await pair.Value.Get<JsonElement>("api/admin/server-resources");}
                catch(ApiRequestException e)when(e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden){if(adminSessions.TryGetValue(pair.Key,out var current)&&ReferenceEquals(current,pair.Value))await SignOutAdministrator(pair.Key);}
                catch{} // A temporary outage does not grant authority or change server settings.
            }
        }finally{checkingSessions=false;}
    }
    internal async Task OpenAdministration(string page,string? service=null)
    {
        var role=service??activeService;CloseAdministrationPage();activeService=role;activePage=page;
        serverViews.Visibility=Visibility.Collapsed;administrationView.Visibility=Visibility.Visible;UpdateAdminIdentity();
        foreach(var card in cards)NavigationState(card.Navigation,false);
        foreach(var button in administrationNavigation)NavigationState(button,button.Uid==page);
        if(!adminSessions.TryGetValue(role,out var source)){ShowAdministratorLogin(role,page);return;}
        var revision=viewRevision;
        administrationView.Children.Add(Surface(Note("관리자 권한을 확인하고 있습니다…")));
        try
        {
            await source.Get<JsonElement>("api/admin/server-resources");
            if(revision!=viewRevision||!adminSessions.TryGetValue(role,out var current)||!ReferenceEquals(source,current))return;
            if(page=="timetable-feature") { await ShowTimetableFeature(source,revision); return; }
            if(page=="attachment-limit") { await ShowAttachmentLimit(source,revision); return; }
            administrationView.Children.Clear();
            Window controller=page switch
            {
                "members"=>new OfficeWindow(this,source,true,"staff"),
                "rooms"=>new OfficeWindow(this,source,true,"rooms"),
                "calendar"=>new OfficeWindow(this,source,true,"calendar"),
                "audit"=>new OfficeWindow(this,source,true,"audit"),
                "policy"=>new SchoolPolicyWindow(this,source),
                "feedback"=>new FeedbackWindow(this,source,true),
                _=>new ServerManagementWindow(this,source,role=="office")
            };
            var view=(FrameworkElement)controller.Content;controller.Content=null;view.Resources=controller.Resources;
            administrationController=controller;
            controller.Closed+=(_,_)=>{if(!closingPage&&ReferenceEquals(administrationController,controller))_ = SignOutAdministrator(role);};
            // Office and student-parent pages draw their own cards; overview pages sit directly on the canvas.
            administrationView.Children.Add(controller is ServerManagementWindow or OfficeWindow?view:Surface(view,new Thickness(0)));
        }
        catch(ApiRequestException e)when(e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden){await SignOutAdministrator(role);}
        catch(Exception e){if(revision==viewRevision){var panel=new StackPanel();panel.Children.Add(Note(e.Message));var retry=new Button{Content="다시 연결",HorizontalAlignment=HorizontalAlignment.Left};retry.Click+=async(_,_)=>await OpenAdministration(page,role);panel.Children.Add(retry);administrationView.Children.Add(Surface(panel));}}
    }
    async Task ShowAttachmentLimit(Api source,int revision)
    {
        await source.RefreshAttachmentLimit();
        if(revision!=viewRevision)return;
        var panel=new StackPanel();panel.Children.Add(AdminUi.Heading("첨부 용량",22));
        panel.Children.Add(Note("일반 메시지·채팅의 파일 한 개와 메시지 전체에 같은 상한을 적용합니다. 폴더는 ZIP 크기를 기준으로 계산합니다."));
        var limit=new ComboBox{ItemsSource=Enumerable.Range(2,9).ToArray(),SelectedItem=(int)(source.AttachmentLimit/SchoolMessenger.Contracts.AttachmentLimits.GiB),Width=140,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,12,0,12)};
        System.Windows.Automation.AutomationProperties.SetName(limit,"메시지당 최대 첨부 용량 (GiB)");
        panel.Children.Add(Note("최대 첨부 용량 (GiB)"));panel.Children.Add(limit);
        var status=Note("기본값 2GiB · 설정 범위 2~10GiB");
        var save=new Button{Content="설정 저장",HorizontalAlignment=HorizontalAlignment.Left};save.SetResourceReference(StyleProperty,"PrimaryButton");panel.Children.Add(save);panel.Children.Add(status);
        save.Click+=async(_,_)=>
        {
            save.IsEnabled=limit.IsEnabled=false;
            try
            {
                var result=await source.Send<JsonElement>(HttpMethod.Post,"api/admin/attachment-policy",new { limitGiB=(int)limit.SelectedItem });
                status.Text=$"{SchoolMessenger.Contracts.AttachmentLimits.Label(result.GetProperty("maxBytes").GetInt64())}로 저장했습니다.";
            }
            catch(Exception error){status.Text=error.Message;}
            finally{save.IsEnabled=limit.IsEnabled=true;}
        };
        administrationView.Children.Clear();administrationView.Children.Add(Surface(panel));
    }
    async Task ShowTimetableFeature(Api source,int revision)
    {
        var current=(await source.Get<JsonElement>("api/timetable/status")).GetProperty("enabled").GetBoolean();
        if(revision!=viewRevision)return;
        var panel=new StackPanel();
        panel.Children.Add(AdminUi.Heading("시간표 기능",22));
        panel.Children.Add(Note("별도 시간표 서비스를 사용하는 학교는 교무톡의 시간표를 끌 수 있습니다. 다시 켜면 기존 시간표와 교체 신청을 볼 수 있습니다."));
        var enabled=new CheckBox{Content="교무톡 시간표 사용",IsChecked=current,Margin=new Thickness(0,16,0,12)};panel.Children.Add(enabled);
        var status=Note(current?"현재 켜짐":"현재 꺼짐");
        var save=new Button{Content="설정 저장",HorizontalAlignment=HorizontalAlignment.Left};save.SetResourceReference(StyleProperty,"PrimaryButton");
        panel.Children.Add(save);panel.Children.Add(status);
        save.Click+=async(_,_)=>
        {
            save.IsEnabled=enabled.IsEnabled=false;
            try
            {
                var result=await source.Send<JsonElement>(HttpMethod.Post,"api/admin/timetable-feature",new { enabled=enabled.IsChecked==true });
                status.Text=result.GetProperty("enabled").GetBoolean()?"저장했습니다. 교직원 메신저에 시간표가 표시됩니다.":"저장했습니다. 교직원 메신저에서 시간표를 숨깁니다.";
            }
            catch(Exception error){status.Text=error.Message;}
            finally{save.IsEnabled=enabled.IsEnabled=true;}
        };
        administrationView.Children.Clear();administrationView.Children.Add(Surface(panel));
    }
    void ShowAdministratorLogin(string service,string page,string? message=null)
    {
        administrationView.Children.Clear();var revision=viewRevision;
        var content=new StackPanel{MaxWidth=560,HorizontalAlignment=HorizontalAlignment.Left};
        var title=Text(RoleName(service)+" 관리자 로그인",22);title.FontWeight=FontWeights.Bold;content.Children.Add(title);
        var intro=Note("구성원과 학교 설정을 바꾸려면 이 서버의 관리자 계정으로 로그인하세요.");intro.Margin=new Thickness(0,0,0,18);content.Children.Add(intro);
        StackPanel Field(string label,FrameworkElement input){var field=new StackPanel{Margin=new Thickness(0,0,0,14)};var caption=AdminUi.Label(label,"Muted",13,true);caption.Margin=new Thickness(0,0,0,6);field.Children.Add(caption);field.Children.Add(input);content.Children.Add(field);return field;}
        var card=cards.First(c=>c.Service==service);var local=card.Host.Installed&&card.Activated;
        var address=new TextBox{Text=local&&card.Mode.SelectedIndex==0?(service=="office"?"http://localhost:5080":"http://localhost:5081"):local&&Simple(card)?"https://localhost:"+SimpleOperation.Port(service):"",MinWidth=360,ToolTip="학교에서 안내한 해당 서버의 HTTPS 주소"};
        var addressField=Field("서버 주소",address);
        // The server on this PC needs no address typing; another server's address stays one click away.
        if(local&&address.Text.Length>0)
        {
            addressField.Visibility=Visibility.Collapsed;
            var here=new DockPanel{Margin=new Thickness(0,0,0,14)};var other=new Button{Content="다른 서버 주소 입력",FontSize=13};other.SetResourceReference(StyleProperty,"SubtleButton");DockPanel.SetDock(other,Dock.Right);here.Children.Add(other);
            var where=AdminUi.Label("이 PC에서 실행 중인 "+RoleName(service)+" 서버","Ink",14,true);where.VerticalAlignment=VerticalAlignment.Center;here.Children.Add(where);content.Children.Add(here);
            other.Click+=(_,_)=>{here.Visibility=Visibility.Collapsed;addressField.Visibility=Visibility.Visible;address.Focus();};
        }
        var username=new TextBox{Text=local&&Simple(card)&&card.AdminUsername.Text.Trim().Length>0?card.AdminUsername.Text.Trim():"admin"};Field("관리자 아이디",username);
        var password=new PasswordBox{MinHeight=38};Field("관리자 비밀번호",password);
        var status=Note(message??"비밀번호와 로그인 정보는 파일에 저장하지 않습니다.");content.Children.Add(status);
        var login=new Button{Content="관리자 로그인",Margin=new Thickness(0,16,0,12),HorizontalAlignment=HorizontalAlignment.Left,MinWidth=140,IsDefault=true};login.SetResourceReference(StyleProperty,"PrimaryButton");content.Children.Add(login);
        password.Loaded+=(_,_)=>password.Focus();
        login.Click+=async(_,_)=>
        {
            login.IsEnabled=false;
            try{if(await SignInAdministrator(service,address.Text,username.Text,password.Password,revision))await OpenAdministration(page,service);}
            catch(Exception e){status.Text=e.Message;}
            finally{password.Clear();login.IsEnabled=true;}
        };
        content.Children.Add(Note("서버가 꺼져 있다면 왼쪽 서버 메뉴에서 먼저 시작하세요. 최초 서버 비밀번호는 교사의 초기 비밀번호와 다릅니다."));
        administrationView.Children.Add(Surface(new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}));
    }
}
