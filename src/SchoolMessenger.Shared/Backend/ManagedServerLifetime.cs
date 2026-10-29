using System.Diagnostics;
using Microsoft.AspNetCore.Builder;

namespace SchoolMessenger.Shared;

// Local Windows process control only. No network shutdown endpoint is exposed.
public static class ManagedServerLifetime
{
    public static void Attach(WebApplication app)
    {
        var name=Environment.GetEnvironmentVariable("YY_MANAGED_STOP_EVENT");
        if(string.IsNullOrEmpty(name))return;
        const string prefix=@"Local\YeoyangMessenger.Stop.";
        if(!OperatingSystem.IsWindows()||!name.StartsWith(prefix,StringComparison.Ordinal)||!Guid.TryParseExact(name[prefix.Length..],"N",out _)
           ||!int.TryParse(Environment.GetEnvironmentVariable("YY_MANAGED_PARENT_PID"),out var id)
           ||!long.TryParse(Environment.GetEnvironmentVariable("YY_MANAGED_PARENT_START"),out var started))
            throw new InvalidOperationException("서버 관리 프로그램의 실행 정보를 확인하세요.");
        var parent=Process.GetProcessById(id);
        if(parent.StartTime.ToUniversalTime().Ticks!=started)throw new InvalidOperationException("서버 관리 프로그램이 바뀌었습니다.");
        var signal=EventWaitHandle.OpenExisting(name);
        app.Lifetime.ApplicationStarted.Register(()=>_ = Task.Run(()=>
        {
            try
            {
                while(!app.Lifetime.ApplicationStopping.IsCancellationRequested)
                    if(signal.WaitOne(1000)||parent.HasExited){app.Lifetime.StopApplication();break;}
            }
            catch(ObjectDisposedException){}catch(InvalidOperationException){app.Lifetime.StopApplication();}
        }));
        app.Lifetime.ApplicationStopped.Register(()=>{signal.Dispose();parent.Dispose();});
    }
}
