using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.AspNetCore.SignalR.Client;

namespace SchoolMessenger.Desktop;

public sealed class RemoteClient(MainWindow main, Api? connectionApi = null) : IAsyncDisposable
{
    HubConnection? hub;
    RemoteWindow? window;
    CancellationTokenSource? session;
    PeerLink? link;
    readonly Queue<string> endedOffers = new();
    long authorityAt, inputWindow;
    int inputCount;
    bool connecting, disposed;
    public RemoteOffer? Offer { get; private set; }
    public bool IsHost => Offer?.HostId == (connectionApi ?? main.Api)?.Session?.User?.Id;
    internal RemoteWindow? SupportWindow => window;
    public bool IsTarget { get; private set; }
    public bool DirectConnected { get; private set; }
    internal string? LastFailure { get; private set; }
    internal int ReceivedInputCount => inputCount;
    internal string? LastAppliedInput { get; private set; }
    bool Authorized => hub?.State == HubConnectionState.Connected && authorityAt != 0 && Environment.TickCount64 - authorityAt < 6000;
    public bool CanControl => DirectConnected && Authorized && Offer is { State: "active", Control: true } && !IsHost;
    public async Task Connect()
    {
        var api = connectionApi ?? main.Api;
        if (disposed || api is null) return;
        if (hub is not null) { if (hub.State == HubConnectionState.Disconnected) await hub.StartAsync(); return; }
        hub = new HubConnectionBuilder().WithUrl(new Uri(api.Address, "remote"), o => { o.Cookies = api.Cookies; CertificatePins.Apply(o, api.Address); }).WithAutomaticReconnect().Build();
        hub.On<RemoteOffer>("Offer", offer => OnUi(() => { if (Offer is null) { IsTarget = true; Show(offer); } }));
        hub.On<RemoteOffer>("Started", offer => OnUi(() =>
        {
            if (Offer is not null && Offer.Id != offer.Id) return;
            Show(offer); _ = Begin(offer.Id);
        }));
        hub.On<RemotePeer>("PeerReady", peer => OnUi(() => { if (Offer?.Id == peer.Id && !connecting) { connecting = true; _ = Join(peer); } }));
        hub.On<RemoteOffer>("ControlChanged", offer => OnUi(() => UpdateAuthority(offer)));
        hub.On<string, string>("Ended", (id, reason) => OnUi(() =>
        {
            endedOffers.Enqueue(id); if (endedOffers.Count > 32) endedOffers.Dequeue();
            if (Offer?.Id == id) LocalStop(reason);
        }));
        hub.On<string>("OfferClosed", id => OnUi(() => { if (Offer?.Id == id && Offer.State == "pending" && IsTarget) LocalStop("다른 PC에서 요청에 응답했습니다."); }));
        hub.Reconnecting += _ => OnUi(() => LocalStop("서버 연결이 끊겨 원격 지원을 종료했습니다."));
        hub.Closed += _ => OnUi(() => LocalStop("원격 지원 연결이 끊겼습니다."));
        await hub.StartAsync();
    }
    Task OnUi(Action action) => main.Dispatcher.InvokeAsync(action).Task;
    void Show(RemoteOffer offer)
    {
        Offer = offer;
        if (window is null) { window = new RemoteWindow(main, this); window.Show(); }
        window.Update(offer); window.Activate();
    }
    void UpdateAuthority(RemoteOffer offer)
    {
        if (Offer?.Id != offer.Id || offer.ControlRevision < Offer.ControlRevision) return;
        if (Offer.ControlRevision == offer.ControlRevision && Offer.Control == offer.Control) return;
        RemoteDesktop.Release(); Offer = offer; window?.Update(offer);
    }
    public async Task Request(string target, bool shareMine)
    {
        if (Offer is not null) throw new InvalidOperationException("현재 원격 지원을 먼저 종료하세요.");
        if (shareMine && MessageBox.Show(main, "상대가 수락하면 내 주 모니터 화면과 마우스·키보드를 공유합니다. 요청하시겠습니까?", "원격 지원 받기", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Connect(); IsTarget = false; LastFailure = null;
        var offer = await hub!.InvokeAsync<RemoteOffer>("Request", target, shareMine, true);
        if (endedOffers.Contains(offer.Id)) throw new InvalidOperationException("상대방이 요청을 종료했습니다.");
        if (Offer?.Id != offer.Id || Offer.State != "active") Show(offer);
    }
    public Task Accept(bool control) => hub!.InvokeAsync("Accept", Offer!.Id, control);
    public Task SetControl(bool enabled)
    {
        if (!enabled) { RemoteDesktop.Release(); Offer = Offer! with { Control = false, ControlRevision = Offer.ControlRevision + 1 }; window?.Update(Offer); }
        return hub!.InvokeAsync("SetControl", Offer!.Id, enabled);
    }
    async Task Begin(string id)
    {
        if (session is not null) return;
        session = new CancellationTokenSource(); var token = session.Token;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(3000);
            var started = Environment.TickCount64;
            var approved = await hub!.InvokeAsync<RemoteOffer>("Check", id, timeout.Token);
            if (token.IsCancellationRequested || Offer?.Id != id) return;
            authorityAt = started; UpdateAuthority(approved);
            link = new PeerLink(id); if (IsHost) link.Listen((connectionApi ?? main.Api)!.Address.IsLoopback);
            _ = AuthorityLoop(id, link, token);
            await hub!.InvokeAsync("RegisterPeer", id, link.Pin, IsHost ? link.Port : 0, token);
        }
        catch (Exception error) { if (!token.IsCancellationRequested) await Fail(id, error); }
    }
    async Task Join(RemotePeer peer)
    {
        if (link is null || session is null) return;
        var transport = link; var token = session.Token; var host = IsHost;
        try
        {
            await transport.Connect(peer, host, token);
            if (token.IsCancellationRequested || Offer?.Id != peer.Id) return;
            DirectConnected = true; window?.Update(Offer);
            _ = ReceiveLoop(peer.Id, transport, host, token);
            if (host) _ = CaptureLoop(peer.Id, transport, token);
        }
        catch (Exception error) { if (!token.IsCancellationRequested) await Fail(peer.Id, error); }
    }
    async Task AuthorityLoop(string id, PeerLink transport, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var started = Environment.TickCount64;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(3000);
                var offer = await hub!.InvokeAsync<RemoteOffer>("Check", id, timeout.Token);
                if (token.IsCancellationRequested || Offer?.Id != id) return;
                authorityAt = started; UpdateAuthority(offer);
                if (transport.Connected) await transport.Send(3, [], token);
                await Task.Delay(2000, token);
            }
        }
        catch (Exception error) { if (!token.IsCancellationRequested) await Fail(id, error); }
    }
    async Task ReceiveLoop(string id, PeerLink transport, bool host, CancellationToken token)
    {
        try
        {
            await transport.Receive(host, (_, data) => OnUi(() =>
            {
                if (token.IsCancellationRequested || Offer?.Id != id) return;
                if (!Authorized) throw new InvalidOperationException("서버의 지원 권한을 확인할 수 없습니다.");
                if (!host) { window?.Frame(data); return; }
                var packet = JsonSerializer.Deserialize<PeerInput>(data) ?? throw new InvalidOperationException("잘못된 원격 입력입니다.");
                if (!SafeInput(packet.Input)) throw new InvalidOperationException("잘못된 원격 입력입니다.");
                var now = Environment.TickCount64;
                if (now - inputWindow >= 1000) { inputWindow = now; inputCount = 0; }
                if (++inputCount > 120) throw new InvalidOperationException("원격 입력이 너무 많습니다.");
                if (Offer.Control && packet.Revision == Offer.ControlRevision) { RemoteDesktop.Apply(packet.Input); LastAppliedInput = packet.Input.Kind; }
            }), token);
        }
        catch (Exception error) { if (!token.IsCancellationRequested) await Fail(id, error); }
    }
    internal static bool SafeInput(RemoteInput? input) => input is not null &&
        double.IsFinite(input.X) && double.IsFinite(input.Y) && input.X is >= 0 and <= 1 && input.Y is >= 0 and <= 1 &&
        input.Kind switch
        {
            "move" or "left" or "right" or "release" => input.Text is null,
            "key" => input.Value is >= 8 and <= 254 && input.Text is null,
            "wheel" => input.Value is >= -1200 and <= 1200 && input.Text is null,
            "text" => input.Text is { Length: > 0 and <= 128 },
            _ => false
        };
    async Task CaptureLoop(string id, PeerLink transport, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!Authorized) throw new InvalidOperationException("서버의 지원 권한을 확인할 수 없습니다.");
                var frame = await Task.Run(RemoteDesktop.Capture, token);
                if (token.IsCancellationRequested) return;
                await transport.Send(1, frame, token);
                await Task.Delay(500, token);
            }
        }
        catch (Exception error) { if (!token.IsCancellationRequested) await Fail(id, error); }
    }
    public async Task Input(RemoteInput input)
    {
        if (!CanControl || link is null || session is null || !SafeInput(input)) return;
        var id = Offer!.Id; var token = session.Token;
        try { await link.Send(2, JsonSerializer.SerializeToUtf8Bytes(new PeerInput(Offer.ControlRevision, input)), token); }
        catch (Exception error) { if (!token.IsCancellationRequested) await Fail(id, error); }
    }
    Task Fail(string id, Exception error) => OnUi(() => { if (Offer?.Id == id) { LastFailure = error.ToString(); _ = Stop("PC 직접 연결 중단: " + error.Message + " 학교 방화벽과 PC 간 접속을 확인하세요."); } });
    public async Task Stop(string reason = "원격 지원을 종료했습니다.")
    {
        var id = Offer?.Id; LocalStop(reason);
        if (id is not null && hub?.State == HubConnectionState.Connected) { try { await hub.InvokeAsync("End", id); } catch { } }
    }
    void LocalStop(string reason)
    {
        session?.Cancel(); session?.Dispose(); session = null;
        link?.Dispose(); link = null; DirectConnected = connecting = false; authorityAt = 0; inputWindow = 0; inputCount = 0;
        RemoteDesktop.Release(); Offer = null; var old = window; window = null; old?.Finish(reason); if (!disposed) main.StatusLabel.Text = reason;
    }
    public async ValueTask DisposeAsync()
    { disposed = true; await Stop(); if (hub is not null) { await hub.DisposeAsync(); hub = null; } }
}
