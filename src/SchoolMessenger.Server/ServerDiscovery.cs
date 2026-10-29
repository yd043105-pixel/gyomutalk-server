using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Server;

public sealed class ServerDiscovery(Store store,IConfiguration configuration,ILogger<ServerDiscovery> log):BackgroundService
{
    public record Settings(bool Enabled,string Address);
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    readonly object gate=new();
    Settings current=new(false,"");
    public string? LastError{get;private set;}
    public bool Listening{get;private set;}
    public int Port=>configuration.GetValue("School:DiscoveryPort",SchoolDiscovery.Port);
    string FilePath=>Path.Combine(store.Root,"discovery.json");
    public Settings Current{get{lock(gate)return current;}}
    public void Load()
    {
        if(!File.Exists(FilePath))return;
        try{var settings=JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath),Json);if(settings is not null&&(!settings.Enabled||SchoolDiscovery.Address(settings.Address,out _)))current=settings;}
        catch(Exception e)when(e is IOException or JsonException){LastError="자동검색 설정 파일을 확인하세요.";}
    }
    public void Save(Settings settings)
    {
        if(settings.Address is null||settings.Address.Length>240||settings.Address.Any(char.IsControl)||settings.Address!=settings.Address.Trim())throw new InvalidOperationException("서버 주소는 제어 문자 없이 240자 이내로 입력하세요.");
        if(settings.Enabled&&(!SchoolDiscovery.Address(settings.Address,out var address)||address!.IsLoopback))throw new InvalidOperationException("서버 PC에 연결되는 교내 HTTPS 주소를 입력하세요. localhost는 다른 교사 PC에서 사용할 수 없습니다.");
        lock(gate){var temp=FilePath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(settings,Json));File.Move(temp,FilePath,true);current=settings;LastError=null;}
    }
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        Load();UdpClient? socket=null;var seen=new Dictionary<IPAddress,long>();
        try
        {
            while(!stop.IsCancellationRequested)
            {
                if(!Current.Enabled){socket?.Dispose();socket=null;Listening=false;await Task.Delay(1000,stop);continue;}
                if(socket is null)
                {
                    try{socket=new UdpClient(new IPEndPoint(IPAddress.Any,Port));socket.Client.ReceiveBufferSize=8192;Listening=true;LastError=null;}
                    catch(SocketException){LastError="자동검색 UDP 포트를 사용할 수 없습니다. 포트 중복·방화벽을 확인하세요.";await Task.Delay(5000,stop);continue;}
                }
                using var wait=CancellationTokenSource.CreateLinkedTokenSource(stop);wait.CancelAfter(1000);
                UdpReceiveResult message;try{message=await socket.ReceiveAsync(wait.Token);}catch(OperationCanceledException)when(!stop.IsCancellationRequested){continue;}
                var ip=message.RemoteEndPoint.Address;if(!SchoolDiscovery.Local(ip))continue;
                var packet=SchoolDiscovery.Decode(message.Buffer);if(packet is null||packet.Address is not null)continue;
                var now=Environment.TickCount64;if(seen.TryGetValue(ip,out var previous)&&now-previous<1000)continue;
                if(seen.Count>=512){foreach(var old in seen.Where(p=>now-p.Value>60_000).Select(p=>p.Key).ToArray())seen.Remove(old);if(seen.Count>=512&&!seen.ContainsKey(ip))continue;}
                seen[ip]=now;var active=Current;if(!active.Enabled)continue;
                await socket.SendAsync(SchoolDiscovery.Encode(new(SchoolDiscovery.Product,packet.Nonce,active.Address)),message.RemoteEndPoint,stop);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(SocketException e){LastError="자동검색 수신 오류입니다. 설정을 확인하고 서버를 재시작하세요.";log.LogWarning("Server discovery stopped with socket error {Code}",e.SocketErrorCode);}
        finally{Listening=false;socket?.Dispose();}
    }
    public static void Map(WebApplication app)
    {
        app.MapGet("/server-info",(IConfiguration c,DeploymentReview review)=>new{product=SchoolDiscovery.Product,schoolName=review.SchoolName??c["School:Name"]??"학교",directoryProduct="SchoolMessenger.School.1",schoolId=review.SchoolId??c["Operations:SchoolId"]??"",service="office"});
        app.MapGet("/api/admin/server-discovery",(ServerDiscovery service)=>new{settings=service.Current,service.Listening,service.LastError,port=service.Port}).RequireAuthorization("Admin");
        app.MapPost("/api/admin/server-discovery",IResult(Settings settings,ServerDiscovery service)=>
        {try{service.Save(settings);return Results.Ok();}catch(InvalidOperationException e){return Results.Json(new{error=e.Message},statusCode:400);}}).RequireAuthorization("Admin");
    }
}
