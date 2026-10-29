using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace SchoolMessenger.Desktop;

public sealed class PeerLink : IDisposable
{
    readonly X509Certificate2 certificate;
    readonly SemaphoreSlim writer = new(1, 1);
    TcpListener? listener;
    TcpClient? socket;
    SslStream? stream;
    public string Pin => certificate.GetCertHashString(HashAlgorithmName.SHA256);
    public int Port => ((IPEndPoint)listener!.LocalEndpoint).Port;
    public bool Connected => stream?.IsAuthenticated == true;
    bool disposed;
    public PeerLink(string id)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=school-peer-" + id, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Windows Schannel cannot use the ephemeral key from CreateSelfSigned.
        // Reimport without PersistKeySet; Windows releases the temporary key on disposal.
        var pfx = generated.Export(X509ContentType.Pfx);
        try { certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    public void Listen(bool localOnly = false)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            listener = new TcpListener(localOnly ? IPAddress.Loopback : Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, RandomNumberGenerator.GetInt32(49152, 65536));
            if (!localOnly && Socket.OSSupportsIPv6) listener.Server.DualMode = true;
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(4); return; }
            catch (SocketException) { listener.Stop(); }
        }
        throw new InvalidOperationException("원격 연결용 포트(49152~65535)를 열 수 없습니다. PC의 네트워크 설정을 확인하세요.");
    }
    bool Verify(X509Certificate? remote, string expected)
    {
        if (remote is null) return false;
        using var cert = X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
        return expected.Length == 64 && CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(expected)) &&
            cert.NotBefore.ToUniversalTime() <= DateTime.UtcNow && cert.NotAfter.ToUniversalTime() > DateTime.UtcNow;
    }
    public async Task Connect(RemotePeer peer, bool host, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        if (host)
        {
            Exception? lastError = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var candidate = await listener!.AcceptTcpClientAsync(ct); candidate.NoDelay = true;
                var tls = new SslStream(candidate.GetStream(), false, (_, remote, _, _) => Verify(remote, peer.ViewerPin));
                try
                {
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct); handshake.CancelAfter(TimeSpan.FromSeconds(3));
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        AllowRenegotiation = false, AllowTlsResume = false
                    }, handshake.Token);
                    if (!tls.IsMutuallyAuthenticated || !tls.IsEncrypted) throw new AuthenticationException("두 PC의 상호 인증을 확인하지 못했습니다.");
                    socket = candidate; stream = tls; listener.Stop(); return;
                }
                catch (Exception error) when (!ct.IsCancellationRequested) { lastError = error; tls.Dispose(); candidate.Dispose(); }
                catch { tls.Dispose(); candidate.Dispose(); throw; }
            }
            throw new AuthenticationException("승인된 PC의 인증서를 확인하지 못했습니다.", lastError);
        }
        socket = new TcpClient { NoDelay = true };
        var address = IPAddress.Parse(peer.HostAddress);
        await socket.ConnectAsync(IPAddress.IsLoopback(address) ? IPAddress.Loopback : address, peer.HostPort, ct);
        stream = new SslStream(socket.GetStream(), false, (_, remote, _, _) => Verify(remote, peer.HostPin));
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "school-peer-" + peer.Id, ClientCertificates = new X509CertificateCollection { certificate },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowRenegotiation = false, AllowTlsResume = false
        }, ct);
        if (!stream.IsMutuallyAuthenticated || !stream.IsEncrypted) throw new AuthenticationException("두 PC의 상호 인증을 확인하지 못했습니다.");
    }
    public async Task Send(byte kind, byte[] data, CancellationToken token)
    {
        if (kind is not (1 or 2 or 3) || data.Length > (kind == 1 ? 262144 : kind == 2 ? 4096 : 0)) throw new InvalidOperationException("잘못된 원격 데이터입니다.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await writer.WaitAsync(timeout.Token);
        try
        {
            var packet = new byte[data.Length + 5]; BinaryPrimitives.WriteInt32BigEndian(packet, data.Length + 1); packet[4] = kind; data.CopyTo(packet, 5);
            await stream!.WriteAsync(packet, timeout.Token);
        }
        finally { writer.Release(); }
    }
    public async Task Receive(bool host, Func<byte, byte[], Task> handler, CancellationToken token)
    {
        var header = new byte[4];
        while (!token.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await stream!.ReadExactlyAsync(header, timeout.Token);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length < 1 || length > (host ? 4097 : 262145)) throw new InvalidOperationException("원격 데이터 크기가 잘못되었습니다.");
            var packet = new byte[length]; await stream.ReadExactlyAsync(packet, timeout.Token);
            var kind = packet[0];
            if (kind == 3 && length == 1) continue;
            if (kind != (host ? 2 : 1)) throw new InvalidOperationException("원격 데이터 방향이 잘못되었습니다.");
            await handler(kind, packet[1..]);
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        listener?.Stop(); socket?.Dispose(); stream?.Dispose(); certificate.Dispose();
    }
}
