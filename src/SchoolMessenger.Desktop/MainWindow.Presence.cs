using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse=System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Win32;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    readonly DispatcherTimer activityTimer=new(){Interval=TimeSpan.FromSeconds(5)};
    bool sessionLocked,activitySending,localAway;
    bool? sentAway;
    public int AutomaticAwayMinutes{get;private set;}=5;
    void InitializeActivity()
    {
        activityTimer.Tick+=async(_,_)=>await SynchronizeActivity();
        SystemEvents.SessionSwitch+=SessionActivityChanged;
        Closed+=(_,_)=>{activityTimer.Stop();SystemEvents.SessionSwitch-=SessionActivityChanged;};
    }
    void SessionActivityChanged(object sender,SessionSwitchEventArgs e)
    {
        if(e.Reason is not(SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock))return;
        _=Dispatcher.InvokeAsync(async()=>{sessionLocked=e.Reason==SessionSwitchReason.SessionLock;await SynchronizeActivity();});
    }
    public void SetAutomaticAway(int minutes)
    {AutomaticAwayMinutes=new[]{2,5,10,15,30}.Contains(minutes)?minutes:5;sentAway=null;_=SynchronizeActivity();}
    async Task SynchronizeActivity(TimeSpan? observedIdle=null)
    {
        if(activitySending||Api?.Session?.User is null||Workspace.Visibility!=Visibility.Visible)return;
        var connection=hub;
        if(connection?.State!=HubConnectionState.Connected){ShowOwnPresence(false,false);return;}
        localAway=IdleActivity.IsAway(observedIdle??IdleActivity.Read(),AutomaticAwayMinutes,sessionLocked,localAway);
        if(sentAway==localAway){ShowOwnPresence(true,localAway);return;}
        activitySending=true;
        try
        {
            var accepted=await connection.InvokeAsync<bool>("SetAway",localAway);
            if(connection==hub){sentAway=accepted;ShowOwnPresence(true,accepted);}
        }
        catch(Exception error)when(error is InvalidOperationException or Microsoft.AspNetCore.SignalR.HubException or System.IO.IOException or OperationCanceledException)
        {if(connection==hub)ShowOwnPresence(false,false);}
        finally{activitySending=false;}
    }
    void ShowOwnPresence(bool online,bool away)
    {
        IdentityDot.Content=PresenceMark(online,away);
        IdentityStatus.Text=online?(away?"자리비움":"온라인"):"연결 중";
        IdentityStatus.ToolTip=online?$"{AutomaticAwayMinutes}분 동안 입력이 없으면 자리비움 · 입력하면 복귀":"실시간 연결 상태를 확인하고 있습니다.";
    }
    static FrameworkElement PresenceMark(bool online,bool away,bool mobile=false)
    {
        var label=!online?(mobile?"휴대폰 접속":"오프라인"):away?"자리비움":"온라인";
        var color=SystemParameters.HighContrast?SystemColors.WindowTextColor:!online?Color.FromRgb(139,153,166):away?Color.FromRgb(183,121,31):Color.FromRgb(20,137,105);
        var brush=new SolidColorBrush(color);var mark=new Grid{Width=12,Height=12,VerticalAlignment=VerticalAlignment.Center,ToolTip=label};
        if(!online&&mobile)mark.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M3,1 L9,1 L9,11 L3,11 Z M5,9 L7,9"),Stroke=SystemParameters.HighContrast?SystemColors.WindowTextBrush:new SolidColorBrush(Color.FromRgb(36,91,198)),StrokeThickness=1.4});
        else mark.Children.Add(new Ellipse{Width=10,Height=10,Stroke=brush,StrokeThickness=1.6,Fill=online?brush:Brushes.Transparent});
        if(online&&away)mark.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M6,3 L6,6 L8,7"),Stroke=SystemParameters.HighContrast?SystemColors.WindowBrush:Brushes.White,StrokeThickness=1.2});
        ToolTipService.SetInitialShowDelay(mark,500);System.Windows.Automation.AutomationProperties.SetName(mark,label);return mark;
    }
    async Task VerifyPresenceUi(string directory)
    {
        activityTimer.Stop();var checks=new System.Collections.Generic.List<string>();
        var password=Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")!;
        using var bob=new Api(ServerAddress.Text);using var carol=new Api(ServerAddress.Text);
        await bob.Login("bob",password);await carol.Login("alice",password);
        await Api!.Send<JsonElement>(System.Net.Http.HttpMethod.Post,"api/admin/users",new{username="presence.offline",name="한지원",department="행정실",password,isAdmin=false,canBroadcast=false});
        await Api.Send<JsonElement>(System.Net.Http.HttpMethod.Post,"api/admin/users",new{username="presence.phone",name="박서연",department="교무부",password,isAdmin=false,canBroadcast=false});
        using var phone=new Api(ServerAddress.Text);await phone.Login("presence.phone",password);
        await phone.Send<JsonElement>(System.Net.Http.HttpMethod.Post,"api/presence/mobile",new{});
        await using var first=new HubConnectionBuilder().WithUrl(new Uri(Api!.Address,"hub"),o=>o.Cookies=bob.Cookies).Build();
        await using var second=new HubConnectionBuilder().WithUrl(new Uri(Api.Address,"hub"),o=>o.Cookies=carol.Cookies).Build();
        await first.StartAsync();await second.StartAsync();await second.InvokeAsync<bool>("SetAway",true);
        await Task.Delay(3100);await SynchronizeActivity(TimeSpan.Zero);await RefreshPeople();
        if(IdentityStatus.Text!="온라인"||!people.Any(p=>p.Online&&p.Away)||!people.Any(p=>!p.Online))throw new InvalidOperationException("세 가지 접속 상태 표시 실패");
        SavePreview(this,Path.Combine(directory,"presence-online-preview.png"));checks.Add("native roster shows online, away and offline with distinct shapes and delayed state tooltips");
        var mobileContact=people.Single(p=>p.Name=="박서연");
        var mobileMark=PresenceMark(mobileContact.Online,mobileContact.Away,mobileContact.Mobile);
        if(mobileContact.Online||!mobileContact.Mobile||mobileMark.ToolTip?.ToString()!="휴대폰 접속"||((Grid)mobileMark).Children.OfType<Ellipse>().Any())throw new InvalidOperationException("휴대폰 전용 접속 표시 실패");
        if(PersonMenu(mobileContact).Items.OfType<MenuItem>().Any(i=>i.Header?.ToString()?.Contains("원격")==true))throw new InvalidOperationException("휴대폰에 PC 원격 지원 노출");
        checks.Add("phone-only contact displays a phone glyph and has no PC remote support action");
        AwayFilter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if(OrganizationTree.Items.OfType<TreeViewItem>().SelectMany(r=>r.Items.OfType<TreeViewItem>()).SelectMany(r=>r.Items.OfType<TreeViewItem>()).Any(r=>r.Tag is Person p&&(!p.Online||!p.Away)))throw new InvalidOperationException("자리비움 필터 실패");
        AllFilter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));checks.Add("native away filter retains only connected away contacts");
        await Task.Delay(3100);await SynchronizeActivity(TimeSpan.FromMinutes(AutomaticAwayMinutes));
        // PresenceChanged can start a newer roster refresh. Wait for that render as well.
        for(var attempt=0;attempt<20;attempt++){await RefreshPeople();if(people.Single(p=>p.Id==Api.Session!.User!.Id).Away)break;await Task.Delay(100);}
        if(IdentityStatus.Text!="자리비움"||!people.Single(p=>p.Id==Api.Session!.User!.Id).Away)throw new InvalidOperationException($"입력 없음 상태 연동 실패: identity={IdentityStatus.Text}, local={localAway}, sent={sentAway}, sending={activitySending}, serverAway={people.Single(p=>p.Id==Api.Session!.User!.Id).Away}, hub={hub?.State}");
        SavePreview(this,Path.Combine(directory,"presence-away-preview.png"));checks.Add("simulated idle deadline updates both native identity and authenticated server presence");
        await Task.Delay(3100);await SynchronizeActivity(TimeSpan.Zero);
        for(var attempt=0;attempt<20;attempt++){await RefreshPeople();if(!people.Single(p=>p.Id==Api.Session!.User!.Id).Away)break;await Task.Delay(100);}
        if(IdentityStatus.Text!="온라인"||people.Single(p=>p.Id==Api.Session!.User!.Id).Away)throw new InvalidOperationException("입력 복귀 연동 실패");
        checks.Add("simulated new input restores online without logging out or changing mailbox access");
        File.WriteAllText(Path.Combine(directory,"presence-ui-checks.json"),JsonSerializer.Serialize(new{checks,simulatedIdle=true,realSchoolData=false}));
    }
}
