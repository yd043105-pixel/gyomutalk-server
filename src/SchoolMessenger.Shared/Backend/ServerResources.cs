using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace SchoolMessenger.Shared;

public sealed class ServerResources
{
    readonly object gate=new(); readonly string root,prefix,policyPath; readonly Func<SqliteConnection> open;
    string profile; int uploads,downloads,zips; long sampled;DateTimeOffset sampledAt; object? sample;
    long previousTicks,previousProcess,previousSent,previousReceived; ulong previousIdle,previousKernel,previousUser;double? measuredSystemCpu;long measuredProcessMemory;
    public sealed record PolicyRequest(string Profile);
    public ServerResources(string root,string prefix,Func<SqliteConnection> open,IConfiguration config,bool development)
    {
        this.root=root;this.prefix=prefix;this.open=open;policyPath=Path.Combine(root,"server-resource-policy.json");
        profile=config["Operations:ResourceProfile"]??(development?"balanced":"low-memory");
        if(File.Exists(policyPath))profile=JsonSerializer.Deserialize<PolicyRequest>(File.ReadAllText(policyPath))?.Profile??"";
        if(profile is not("balanced"or"low-memory"))throw new InvalidOperationException("서버 부하 설정은 balanced 또는 low-memory입니다.");
    }
    public string Profile { get {lock(gate)return profile;} }
    public IResult Change(PolicyRequest? request)
    {
        if(request?.Profile is not("balanced"or"low-memory"))return Results.BadRequest(new{error="저부하 또는 일반 모드를 선택하세요."});
        lock(gate)
        {
            var temporary=policyPath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,JsonSerializer.Serialize(request));File.Move(temporary,policyPath,true);profile=request.Profile;}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        return Results.Ok(new{profile=request.Profile});
    }
    public object Snapshot()
    {
        lock(gate)
        {
            if(sample is null||Environment.TickCount64-sampled>=10000){sample=Measure();sampled=Environment.TickCount64;sampledAt=DateTimeOffset.UtcNow;}
            var low=profile=="low-memory";
            return new{profile,sampledAt,
                limits=new{uploads=low?1:4,downloads=low?2:8,zip=low?1:2,downloadBytesPerSecond=low?(prefix=="Portal"?262144:1048576):0,
                    noticeImageBytes=FilePolicy.MaxNoticeImage,noticeImages=5,retentionDays=30},
                active=new{uploads,downloads,zips},sample};
        }
    }
    object Measure()
    {
        using var process=Process.GetCurrentProcess();var now=Stopwatch.GetTimestamp();var elapsed=previousTicks==0?0:(now-previousTicks)/(double)Stopwatch.Frequency;
        var cpu=process.TotalProcessorTime.Ticks;double? processCpu=elapsed>0?Math.Clamp((cpu-previousProcess)/(elapsed*TimeSpan.TicksPerSecond*Environment.ProcessorCount)*100,0,100):null;
        double? systemCpu=null;ulong total=0,available=0;
        if(OperatingSystem.IsWindows())
        {
            var memory=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};if(GlobalMemoryStatusEx(ref memory)){total=memory.TotalPhysical;available=memory.AvailablePhysical;}
            if(GetSystemTimes(out var idle,out var kernel,out var user))
            {
                var all=(kernel-previousKernel)+(user-previousUser);if(previousTicks!=0&&all>0)systemCpu=Math.Clamp((1-(idle-previousIdle)/(double)all)*100,0,100);
                previousIdle=idle;previousKernel=kernel;previousUser=user;
            }
        }
        long sent=0,received=0;var addresses=new List<string>();
        foreach(var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback))
            try{var statistics=nic.GetIPStatistics();sent+=statistics.BytesSent;received+=statistics.BytesReceived;addresses.AddRange(nic.GetIPProperties().UnicastAddresses.Where(a=>a.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork).Select(a=>a.Address.ToString()));}catch(NetworkInformationException){}
        double? sendRate=elapsed>0?Math.Max(0,(sent-previousSent)/elapsed):null,receiveRate=elapsed>0?Math.Max(0,(received-previousReceived)/elapsed):null;
        previousTicks=now;previousProcess=cpu;previousSent=sent;previousReceived=received;measuredSystemCpu=systemCpu;measuredProcessMemory=process.WorkingSet64;
        long filesBytes=0;using(var db=open())using(var query=db.CreateCommand())
        {query.CommandText=prefix=="Portal"?"SELECT COALESCE(SUM(Size),0) FROM Files WHERE DeletedAt IS NULL":"SELECT (SELECT COALESCE(SUM(Size),0) FROM Attachments WHERE DeletedAt IS NULL)+(SELECT COALESCE(SUM(Size),0) FROM ExternalFiles WHERE DeletedAt IS NULL)";filesBytes=Convert.ToInt64(query.ExecuteScalar());}
        var drive=new DriveInfo(Path.GetPathRoot(root)!);
        var processor=OperatingSystem.IsWindows()?Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0","ProcessorNameString",null)?.ToString()?.Trim():null;
        var os=OperatingSystem.IsWindows()?"Windows · 빌드 "+Environment.OSVersion.Version.Build+" · "+RuntimeInformation.OSArchitecture:RuntimeInformation.OSDescription;
        return new{os,processor,logicalProcessors=Environment.ProcessorCount,addresses=addresses.Distinct().ToArray(),
            totalMemoryBytes=total,availableMemoryBytes=available,systemCpuPercent=systemCpu,processCpuPercent=processCpu,processMemoryBytes=process.WorkingSet64,
            serverGc=GCSettings.IsServerGC,networkSendBytesPerSecond=sendRate,networkReceiveBytesPerSecond=receiveRate,
            dataDirectory=root,filesBytes,freeBytes=drive.AvailableFreeSpace,totalDiskBytes=drive.TotalSize};
    }
    public void Map(WebApplication app)
    {
        app.MapGet("/api/admin/server-resources",()=>Snapshot()).RequireAuthorization("Admin");
        app.MapPost("/api/admin/server-resources",(PolicyRequest request)=>Change(request)).RequireAuthorization("Admin");
    }
    public void Use(WebApplication app)
    {
        app.Use(async(context,next)=>
        {
            var path=context.Request.Path.Value??"";
            var upload=context.Request.Method=="POST"&&(path=="/api/timetable/import"||path.EndsWith("/attachments",StringComparison.Ordinal)&&(path.StartsWith("/api/",StringComparison.Ordinal)||path.StartsWith("/bridge/",StringComparison.Ordinal)));
            var zip=context.Request.Method=="GET"&&path.StartsWith("/api/submission-requests/",StringComparison.Ordinal)&&path.EndsWith("/zip",StringComparison.Ordinal);
            var download=zip||context.Request.Method=="GET"&&path.StartsWith("/api/attachments/",StringComparison.Ordinal)&&(path.EndsWith("/download",StringComparison.Ordinal)||path.EndsWith("/view",StringComparison.Ordinal));
            if(context.GetEndpoint() is null||!upload&&!download){await next();return;}
            var admitted=false;var overloaded=false;var rate=0;
            lock(gate)
            {
                var low=profile=="low-memory";
                if(low)
                {
                    if(sample is null||Environment.TickCount64-sampled>=10000){sample=Measure();sampled=Environment.TickCount64;sampledAt=DateTimeOffset.UtcNow;}
                    if(OperatingSystem.IsWindows()){var m=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};overloaded=GlobalMemoryStatusEx(ref m)&&m.AvailablePhysical<536870912;}
                    overloaded|=measuredSystemCpu>=85||measuredProcessMemory>805306368;
                }
                admitted=!overloaded&&(upload?uploads<(low?1:4):downloads<(low?2:8)&&(!zip||zips<(low?1:2)));
                if(admitted){if(upload)uploads++;else{downloads++;if(zip)zips++;}if(low&&download)rate=prefix=="Portal"?262144:1048576;}
            }
            if(!admitted){context.Response.StatusCode=overloaded?503:429;context.Response.Headers.RetryAfter="2";context.Response.Headers.CacheControl="no-store";await context.Response.WriteAsJsonAsync(new{error="서버 파일 작업이 많거나 메모리가 부족합니다. 잠시 후 다시 시도하세요."});return;}
            var original=context.Response.Body;
            try{if(rate>0)context.Response.Body=new PacedWriteStream(original,rate,context.RequestAborted);await next();}
            finally{context.Response.Body=original;lock(gate){if(upload)uploads--;else{downloads--;if(zip)zips--;}}}
        });
    }
    [StructLayout(LayoutKind.Sequential)] struct MemoryStatus {public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
}

sealed class PacedWriteStream(Stream inner,int bytesPerSecond,CancellationToken aborted):Stream
{
    readonly Stopwatch clock=Stopwatch.StartNew();long written;
    public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
    public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override void Flush()=>inner.FlushAsync(aborted).GetAwaiter().GetResult();public override Task FlushAsync(CancellationToken token)=>inner.FlushAsync(token);
    public override void Write(byte[] buffer,int offset,int count)=>WriteAsync(buffer.AsMemory(offset,count),aborted).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken token)=>WriteAsync(buffer.AsMemory(offset,count),token).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default)
    {
        while(!buffer.IsEmpty){aborted.ThrowIfCancellationRequested();var chunk=buffer[..Math.Min(buffer.Length,32768)];await inner.WriteAsync(chunk,token);written+=chunk.Length;buffer=buffer[chunk.Length..];
            var delay=written/(double)bytesPerSecond-clock.Elapsed.TotalSeconds;if(delay>0)await Task.Delay(TimeSpan.FromSeconds(delay),aborted);}
    }
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
}
