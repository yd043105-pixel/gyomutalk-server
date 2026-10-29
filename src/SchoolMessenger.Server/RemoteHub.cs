using SchoolMessenger.Shared;
using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SchoolMessenger.Server;

public record RemoteOffer(string Id, string HostId, string HostName, string ViewerId, string ViewerName, string InitiatorName, bool Control, string State, long ExpiresAt, int ControlRevision = 0);
public record RemotePeer(string Id, string HostAddress, int HostPort, string HostPin, string ViewerPin);
public sealed class RemoteSession
{
    public required RemoteOffer Offer;
    public required string InitiatorConnection;
    public required string TargetUser;
    public string? HostConnection, ViewerConnection;
    public required int HostVersion, ViewerVersion;
    public string? HostPin, ViewerPin;
    public int HostPort;
    public bool PeerPublished;
}

public sealed class RemoteSessions(IServiceProvider services) : BackgroundService
{
    public object Gate { get; } = new();
    public Dictionary<string, RemoteSession> Sessions { get; } = new();
    public ConcurrentDictionary<string, string> Connections { get; } = new();
    public ConcurrentDictionary<string, string> Addresses { get; } = new();
    public ConcurrentDictionary<string, LiveConnection> Live { get; } = new();
    public static string Group(string user) => "remote-user:" + user;
    public async Task End(string id, string reason)
    {
        RemoteSession? session;
        lock (Gate) { if (!Sessions.Remove(id, out session)) return; }
        var hub = services.GetRequiredService<IHubContext<RemoteHub>>();
        await hub.Clients.Groups(Group(session.Offer.HostId), Group(session.Offer.ViewerId)).SendAsync("Ended", id, reason);
    }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var store = services.GetRequiredService<Store>();
        var presence = services.GetRequiredService<Presence>();
        while (!token.IsCancellationRequested)
        {
            foreach (var identity in Live.Values.Concat(presence.Live.Values))
                if (!identity.Valid(store)) identity.Context.Abort();
            string[] expired;
            lock (Gate) expired = Sessions.Values.Where(s => s.Offer.ExpiresAt <= Maintenance.Now || !Valid(store, s)).Select(s => s.Offer.Id).ToArray();
            foreach (var id in expired) { try { await End(id, "지원 시간이 끝났거나 계정이 변경되었습니다."); } catch { } }
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
        }
    }
    public static bool Valid(Store store, RemoteSession session) => store.GetUser(session.Offer.HostId) is { Active: true } host && host.SessionVersion == session.HostVersion &&
        store.GetUser(session.Offer.ViewerId) is { Active: true } viewer && viewer.SessionVersion == session.ViewerVersion;
}

[Authorize]
public sealed class RemoteHub(Store store, RemoteSessions sessions) : Hub
{
    string UserId => Context.UserIdentifier!;
    void Authenticate()
    {
        var user = store.GetUser(UserId);
        using var sessionDb=store.Open();
        if (user is not { Active: true } || user.SessionVersion.ToString() != Context.User!.FindFirstValue("version") || !DeviceSessions.Valid(sessionDb,UserId,Context.User!.FindFirstValue("version"),Context.User!.FindFirstValue("sid"))) { Context.Abort(); throw new HubException("로그인이 만료되었습니다."); }
    }
    public override async Task OnConnectedAsync()
    {
        Authenticate(); sessions.Connections[Context.ConnectionId] = UserId;
        sessions.Live[Context.ConnectionId] = new LiveConnection(Context, UserId, Context.User!.FindFirstValue("version"),Context.User!.FindFirstValue("sid"));
        var address = Context.GetHttpContext()!.Connection.RemoteIpAddress!;
        sessions.Addresses[Context.ConnectionId] = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        await Groups.AddToGroupAsync(Context.ConnectionId, RemoteSessions.Group(UserId)); await base.OnConnectedAsync();
    }
    public override async Task OnDisconnectedAsync(Exception? error)
    {
        sessions.Connections.TryRemove(Context.ConnectionId, out _);
        sessions.Addresses.TryRemove(Context.ConnectionId, out _);
        sessions.Live.TryRemove(Context.ConnectionId, out _);
        string[] ended;
        lock (sessions.Gate) ended = sessions.Sessions.Values.Where(s => s.InitiatorConnection == Context.ConnectionId || s.HostConnection == Context.ConnectionId || s.ViewerConnection == Context.ConnectionId).Select(s => s.Offer.Id).ToArray();
        foreach (var id in ended) await sessions.End(id, "상대방 연결이 끊겼습니다.");
        await base.OnDisconnectedAsync(error);
    }
    public async Task<RemoteOffer> Request(string targetId, bool shareMine, bool control)
    {
        Authenticate(); var me = store.GetUser(UserId)!; var target = store.GetUser(targetId);
        if (target is not { Active: true } || targetId == UserId || !sessions.Connections.Values.Contains(targetId)) throw new HubException("접속 중인 다른 교직원을 선택하세요.");
        var host = shareMine ? me : target; var viewer = shareMine ? target : me;
        var offer = new RemoteOffer(Guid.NewGuid().ToString("N"), host.Id, host.Name, viewer.Id, viewer.Name, me.Name, control, "pending", Maintenance.Now + 60_000);
        lock (sessions.Gate)
        {
            if (sessions.Sessions.Values.Any(s => s.Offer.HostId == UserId || s.Offer.ViewerId == UserId || s.Offer.HostId == targetId || s.Offer.ViewerId == targetId)) throw new HubException("이미 대기 중이거나 연결된 원격 지원이 있습니다.");
            sessions.Sessions.Add(offer.Id, new RemoteSession { Offer = offer, InitiatorConnection = Context.ConnectionId, TargetUser = targetId, HostVersion = host.SessionVersion, ViewerVersion = viewer.SessionVersion });
        }
        try { await Clients.Group(RemoteSessions.Group(targetId)).SendAsync("Offer", offer); }
        catch { await sessions.End(offer.Id, "요청을 전달하지 못했습니다."); throw; }
        return offer;
    }
    public async Task Accept(string id, bool control)
    {
        Authenticate(); RemoteSession session;
        lock (sessions.Gate)
        {
            session = Get(id);
            if (session.TargetUser != UserId || session.Offer.State != "pending") throw new HubException("수락할 요청이 없습니다.");
            var hostConnection = session.Offer.HostId == UserId ? Context.ConnectionId : session.InitiatorConnection;
            var viewerConnection = session.Offer.ViewerId == UserId ? Context.ConnectionId : session.InitiatorConnection;
            session.HostConnection = hostConnection; session.ViewerConnection = viewerConnection;
            // The accepting viewer cannot upgrade permission granted by the sharing initiator.
            session.Offer = session.Offer with { State = "active", Control = session.Offer.Control && (session.Offer.HostId != UserId || control), ExpiresAt = Maintenance.Now + 1_800_000 };
        }
        await Clients.Clients(session.HostConnection!, session.ViewerConnection!).SendAsync("Started", session.Offer);
        await Clients.Group(RemoteSessions.Group(session.TargetUser)).SendAsync("OfferClosed", id);
    }
    RemoteSession Get(string id)
    {
        if (!sessions.Sessions.TryGetValue(id, out var session) || session.Offer.ExpiresAt <= Maintenance.Now || !RemoteSessions.Valid(store, session)) throw new HubException("종료되었거나 사용할 수 없는 지원입니다.");
        return session;
    }
    public Task SetControl(string id, bool enabled)
    {
        Authenticate(); RemoteSession session;
        lock (sessions.Gate)
        {
            session = Get(id);
            if (session.HostConnection != Context.ConnectionId && !(session.Offer.State == "pending" && session.Offer.HostId == UserId && session.InitiatorConnection == Context.ConnectionId)) throw new HubException("화면 공유자만 제어 허용을 바꿀 수 있습니다.");
            session.Offer = session.Offer with { Control = enabled, ControlRevision = session.Offer.ControlRevision + 1 };
        }
        return session.Offer.State == "pending"
            ? Clients.Groups(RemoteSessions.Group(session.Offer.HostId), RemoteSessions.Group(session.Offer.ViewerId)).SendAsync("ControlChanged", session.Offer)
            : Clients.Clients(session.HostConnection!, session.ViewerConnection!).SendAsync("ControlChanged", session.Offer);
    }
    public Task RegisterPeer(string id, string pin, int port)
    {
        Authenticate(); RemoteSession session; RemotePeer? peer = null;
        lock (sessions.Gate)
        {
            session = Get(id);
            if (session.Offer.State != "active" || (session.HostConnection != Context.ConnectionId && session.ViewerConnection != Context.ConnectionId)) throw new HubException("수락된 두 PC만 연결 정보를 등록할 수 있습니다.");
            if (pin is not { Length: 64 } || !pin.All(Uri.IsHexDigit)) throw new HubException("인증서 지문을 확인하세요.");
            pin = pin.ToUpperInvariant();
            if (session.HostConnection == Context.ConnectionId)
            {
                if (port is < 49152 or > 65535 || (session.HostPin is not null && (session.HostPin != pin || session.HostPort != port))) throw new HubException("직접 연결 포트 또는 인증서가 잘못되었습니다.");
                session.HostPin = pin; session.HostPort = port;
            }
            else
            {
                if (port != 0 || (session.ViewerPin is not null && session.ViewerPin != pin)) throw new HubException("직접 연결 정보를 변경할 수 없습니다.");
                session.ViewerPin = pin;
            }
            if (!session.PeerPublished && session.HostPin is not null && session.ViewerPin is not null)
            {
                peer = new RemotePeer(id, sessions.Addresses[session.HostConnection!], session.HostPort, session.HostPin, session.ViewerPin);
                session.PeerPublished = true;
            }
        }
        return peer is null ? Task.CompletedTask : Clients.Clients(session.HostConnection!, session.ViewerConnection!).SendAsync("PeerReady", peer);
    }
    public RemoteOffer Check(string id)
    {
        Authenticate();
        lock (sessions.Gate)
        {
            var session = Get(id);
            if (session.Offer.State != "active" || (session.HostConnection != Context.ConnectionId && session.ViewerConnection != Context.ConnectionId)) throw new HubException("연결 확인 권한이 없습니다.");
            return session.Offer;
        }
    }
    public async Task End(string id)
    {
        Authenticate();
        lock (sessions.Gate)
        {
            var session = Get(id);
            if (session.InitiatorConnection != Context.ConnectionId && session.HostConnection != Context.ConnectionId && session.ViewerConnection != Context.ConnectionId && !(session.Offer.State == "pending" && session.TargetUser == UserId)) throw new HubException("종료할 권한이 없습니다.");
        }
        await sessions.End(id, "원격 지원을 종료했습니다.");
    }
}
