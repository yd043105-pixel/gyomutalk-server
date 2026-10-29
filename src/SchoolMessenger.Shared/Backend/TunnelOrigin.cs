using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace SchoolMessenger.Shared;

public static class TunnelOrigin
{
    public static void Validate(IConfiguration config, bool development)
    {
        if (!config.GetValue("Portal:TunnelOnly", false)) return;
        var addresses = (config["urls"] ?? config["ASPNETCORE_URLS"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Length == 0 || addresses.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsLoopback || (!development && uri.Scheme != "https")))
            throw new InvalidOperationException("터널 전용 서버는 HTTPS 루프백 주소에서만 실행하세요.");
        if (config.GetValue("ForwardedHeaders_Enabled", false)) throw new InvalidOperationException("모든 프록시의 전달 헤더를 신뢰하는 설정을 사용하지 마세요.");
    }
    public static void Use(WebApplication app)
    {
        if (!app.Configuration.GetValue("Portal:TunnelOnly", false)) return;
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardedForHeaderName = "CF-Connecting-IP", ForwardLimit = 1 };
        options.KnownProxies.Clear(); options.KnownIPNetworks.Clear();
        options.KnownProxies.Add(IPAddress.Loopback); options.KnownProxies.Add(IPAddress.IPv6Loopback);
        // Only the connector on this host is trusted, never arbitrary public forwarding headers.
        app.UseForwardedHeaders(options);
    }
}
