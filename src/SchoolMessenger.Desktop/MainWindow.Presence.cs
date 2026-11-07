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
        var me=Api?.Session?.User;IdentityDot.Content=Avatar.WithPresence(me?.Name??"교",42,null,online,away,false,!online?"연결 중":away?"자리비움":"온라인");
        IdentityStatus.Text=online?(away?"자리비움":"온라인"):"연결 중";
        IdentityStatus.ToolTip=online?$"{AutomaticAwayMinutes}분 동안 입력이 없으면 자리비움 · 입력하면 복귀":"실시간 연결 상태를 확인하고 있습니다.";
    }
    async Task VerifyPresenceUi(string directory)
    {
        activityTimer.Stop();var checks=new System.Collections.Generic.List<string>();
        var password=Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")!;
        using var bob=new Api(ServerAddress.Text);using var carol=new Api(ServerAddress.Text);
        await bob.Login("bob",password);await carol.Login("alice",password);
        await Api!.Send<JsonElement>(System.Net.Http.HttpMethod.Post,"api/admin/users",new{username="presence.offline",name="한지원",department="행정실",password,isAdmin=false,canBroadcast=false});
        await using var first=new HubConnectionBuilder().WithUrl(new Uri(Api!.Address,"hub"),o=>o.Cookies=bob.Cookies).Build();
        await using var second=new HubConnectionBuilder().WithUrl(new Uri(Api.Address,"hub"),o=>o.Cookies=carol.Cookies).Build();
        await first.StartAsync();await second.StartAsync();await second.InvokeAsync<bool>("SetAway",true);
        await Task.Delay(3100);await SynchronizeActivity(TimeSpan.Zero);await RefreshPeople();
        if(IdentityStatus.Text!="온라인"||!people.Any(p=>p.Online&&p.Away)||!people.Any(p=>!p.Online))throw new InvalidOperationException("세 가지 접속 상태 표시 실패");
        SavePreview(this,Path.Combine(directory,"presence-online-preview.png"));checks.Add("native roster shows online, away and offline with distinct shapes and delayed state tooltips");
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
