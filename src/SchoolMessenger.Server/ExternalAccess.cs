using System.Net;

namespace SchoolMessenger.Server;

// The entrance for phones outside the school. A tunnel connector on this PC forwards to a loopback-only port, and
// everything arriving there is limited to the read-only list below, whatever the account may do inside the school.
public static class ExternalAccess
{
    const string Marker="school.external";
    static readonly HashSet<string> Reads=new(StringComparer.Ordinal){"/health","/server-info","/api/session","/api/registration/policy","/api/timetable/setup","/api/timetable/day","/api/todos","/api/submission-requests","/api/office/setup","/api/office/events"};
    static readonly HashSet<string> Posts=new(StringComparer.Ordinal){"/api/login","/api/logout"};
    public static bool Is(HttpContext context)=>context.Items.ContainsKey(Marker);
    static bool Allowed(HttpRequest request)=>request.Method switch{"GET"=>Reads.Contains(request.Path.Value??""),"POST"=>Posts.Contains(request.Path.Value??""),_=>false};
    static int Port(IConfiguration config)=>config.GetValue("School:ExternalPort",0);
    public static void Validate(IConfiguration config)
    {
        var port=Port(config);var host=config["School:ExternalHost"]??"";
        if(port==0&&host.Length==0)return;
        if(port is <1 or >65535||Uri.CheckHostName(host)!=UriHostNameType.Dns||!host.Contains('.'))throw new InvalidOperationException("학교 밖 접속에는 도메인 주소와 내부 포트가 모두 필요합니다.");
        // A wider listener on this port would let anyone claim to be the connector and set their own address.
        var listeners=(config["urls"]??config["ASPNETCORE_URLS"]??"").Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(u=>new Uri(u)).Where(u=>u.Port==port).ToArray();
        if(listeners.Length!=1||listeners[0].Host!="127.0.0.1")throw new InvalidOperationException("학교 밖 접속 입구는 이 PC 내부 주소(127.0.0.1)에서만 열 수 있습니다.");
    }
    public static void Use(WebApplication app)
    {
        var port=Port(app.Configuration);if(port==0)return;
        var host=app.Configuration["School:ExternalHost"]!;
        app.Use(async(context,next)=>
        {
            if(context.Connection.LocalPort!=port){await next();return;}
            if(context.Connection.RemoteIpAddress is not {} peer||!IPAddress.IsLoopback(peer)||!context.Request.Host.Host.Equals(host,StringComparison.OrdinalIgnoreCase)){context.Response.StatusCode=404;return;}
            if(!Allowed(context.Request))
            {
                context.Response.StatusCode=403;context.Response.Headers.CacheControl="no-store";
                await context.Response.WriteAsJsonAsync(new{error="학교 밖에서는 시간표·할 일·제출 현황·학교 일정만 조회할 수 있습니다. 다른 기능은 학교 안 PC에서 사용하세요.",external=true});return;
            }
            // The phone reached the tunnel over HTTPS, and only the connector can reach this port, so its address header is trusted here alone.
            context.Items[Marker]=true;context.Request.Scheme="https";
            if(IPAddress.TryParse(context.Request.Headers["CF-Connecting-IP"].ToString(),out var visitor))context.Connection.RemoteIpAddress=visitor;
            await next();
        });
    }
}
