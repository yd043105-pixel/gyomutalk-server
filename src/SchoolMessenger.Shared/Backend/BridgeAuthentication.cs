using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace SchoolMessenger.Shared;

// The bridge authenticates services, never a mobile user. TLS is still mandatory.
public static class BridgeAuthentication
{
    public const long MaximumBodyBytes = 110L * 1024 * 1024;
    public static string Canonical(string method, string path, string time, string nonce, string service, string keyId, string hash)
        => string.Join('\n', "YY-BRIDGE-1", method.ToUpperInvariant(), path, time, nonce, service, keyId, hash);
    public static string Sign(string key, string value)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value)));
    public static bool Identifier(string value) => value.Length is >= 1 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static async Task<bool> Verify(HttpContext context, IConfiguration config, Func<string, string, long, bool> consume)
    {
        var request = context.Request;
        string Header(string name) => request.Headers[name].Count == 1 ? request.Headers[name].ToString() : "";
        var service = Header("X-YY-Service"); var keyId = Header("X-YY-Key");
        var time = Header("X-YY-Time"); var nonce = Header("X-YY-Nonce");
        var signature = Header("X-YY-Signature");
        var digest = Header("X-YY-Content-SHA256");
        if (service != (config["Portal:BridgeServiceId"] ?? "office") || !Identifier(service) || !Identifier(keyId) ||
            nonce.Length != 64 || !nonce.All(Uri.IsHexDigit) || signature.Length != 64 || !signature.All(Uri.IsHexDigit) ||
            digest.Length != 64 || !digest.All(Uri.IsHexDigit) ||
            !long.TryParse(time, out var timestamp)) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var skew = Math.Clamp(config.GetValue("Portal:BridgeClockSkewSeconds", 120), 10, 300);
        if (timestamp < now - skew || timestamp > now + skew) return false;
        var key = config[$"Portal:BridgeKeys:{keyId}"];
        if (keyId == "primary") key ??= config["Portal:BridgeKey"];
        if (key is not { Length: >= 32 }) return false;
        var notBefore=config.GetValue<long?>($"Portal:BridgeKeyValidity:{keyId}:NotBefore");
        var notAfter=config.GetValue<long?>($"Portal:BridgeKeyValidity:{keyId}:NotAfter");
        if(notBefore is {} begins&&now<begins || notAfter is {} ends&&now>=ends)return false;
        // Authenticate the signed digest before reading/spooling an untrusted upload body.
        var path = request.PathBase.ToUriComponent() + request.Path.ToUriComponent() + request.QueryString.ToUriComponent();
        var expected = Sign(key, Canonical(request.Method, path, time, nonce, service, keyId, digest.ToUpperInvariant()));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(signature))) return false;
        if (request.ContentLength > MaximumBodyBytes) return false;
        request.EnableBuffering(64 * 1024, MaximumBodyBytes);
        byte[] hash;
        try { hash = await SHA256.HashDataAsync(request.Body, context.RequestAborted); }
        catch (IOException) { return false; }
        finally { request.Body.Position = 0; }
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(digest))) return false;
        // Store through a unique constraint before dispatch. Retain beyond the full allowed time window.
        return consume(service, nonce, (now + skew * 2 + 60) * 1000);
    }
}

public sealed class BridgeSigningHandler : DelegatingHandler
{
    readonly string key, keyId, service;
    readonly DeploymentReview review;
    public BridgeSigningHandler(IConfiguration config,DeploymentReview review) : base(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        this.review=review;
        key = config["Announcements:BridgeKey"] ?? "";
        keyId = config["Announcements:BridgeKeyId"] ?? "primary";
        service = config["Announcements:BridgeServiceId"] ?? "office";
        if (key.Length < 32 || !BridgeAuthentication.Identifier(keyId) || !BridgeAuthentication.Identifier(service)) throw new InvalidOperationException("서버 연결 키와 식별자 설정을 확인하세요.");
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if(request.RequestUri is not {} destination)throw new InvalidOperationException("학교 소식 서버의 목적지를 확인하세요.");
        if(!review.Trial&&destination.Scheme!="https")throw new InvalidOperationException("운영 서버 연결은 HTTPS여야 합니다.");
        review.Demand("school-bridge",destination.Host,destination.Port);
        // Disk spool avoids holding up to 100 MB of multipart content per connection in RAM.
        FileStream? spool = null; HttpContent? replacement = null;
        try
        {
            byte[] hash;
            if (request.Content is { } original)
            {
                spool = new FileStream(Path.Combine(Path.GetTempPath(), "yy-bridge-" + Guid.NewGuid().ToString("N")),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await original.CopyToAsync(new BoundedWriteStream(spool), cancellationToken);
                spool.Position = 0; hash = await SHA256.HashDataAsync(spool, cancellationToken); spool.Position = 0;
                replacement = new StreamContent(spool);
                foreach (var header in original.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                replacement.Headers.ContentLength = spool.Length;
                request.Content = replacement; original.Dispose();
            }
            else hash = SHA256.HashData([]);
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            request.Headers.Remove("Authorization");
            request.Headers.Add("X-YY-Service", service); request.Headers.Add("X-YY-Key", keyId);
            request.Headers.Add("X-YY-Time", time); request.Headers.Add("X-YY-Nonce", nonce);
            request.Headers.Add("X-YY-Content-SHA256", Convert.ToHexString(hash));
            request.Headers.Add("X-YY-Signature", BridgeAuthentication.Sign(key,
                BridgeAuthentication.Canonical(request.Method.Method, request.RequestUri!.PathAndQuery, time, nonce, service, keyId, Convert.ToHexString(hash))));
            return await base.SendAsync(request, cancellationToken);
        }
        finally { replacement?.Dispose(); if (spool is not null) await spool.DisposeAsync(); }
    }
    sealed class BoundedWriteStream(Stream target) : Stream
    {
        void Check(int count) { if (target.Position + count > BridgeAuthentication.MaximumBodyBytes) throw new IOException("서버 연결 요청 크기 제한을 초과했습니다."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); target.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Check(buffer.Length); await target.WriteAsync(buffer, cancellationToken); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => target.Flush();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => target.Length;
        public override long Position { get => target.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
