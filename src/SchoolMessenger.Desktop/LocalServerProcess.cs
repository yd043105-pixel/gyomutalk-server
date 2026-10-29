using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SchoolMessenger.Desktop;

internal sealed class LocalServerProcess(string root,string service)
{
    Process? child;
    EventWaitHandle? signal;
    bool production;
    readonly Queue<string> output=new();
    public string Executable=>Path.Combine(root,service=="office"?"server":"announcement-server",service=="office"?"SchoolMessenger.Server.exe":"SchoolMessenger.AnnouncementServer.exe");
    public bool Installed=>File.Exists(Executable);
    public bool Running=>child is not null&&!child.HasExited;
    public string Output{get{lock(output)return string.Join("\n",output);}}
    public void WriteOutput(string? text){if(string.IsNullOrWhiteSpace(text))return;lock(output){output.Enqueue(text.Length>600?text[..600]:text);while(output.Count>25)output.Dequeue();}}
    static string PowerShell=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
    public void Start(bool trial,string data,string config,string password,string bridgeKey)
    {
        if(Running)throw new InvalidOperationException("이미 이 프로그램에서 가동 중입니다.");
        if(!Installed)throw new InvalidOperationException("선택한 서버 역할이 설치되지 않았습니다.");
        child?.Dispose();child=null;signal?.Dispose();signal=null;lock(output)output.Clear();
        var start=new ProcessStartInfo(trial?Executable:PowerShell){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(Executable)!,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var key in start.Environment.Keys.Where(k=>new[]{"School__","Portal__","Announcements__","FeedbackMail__","Feedback__","Operations__","Registration__"}.Any(prefix=>k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))).ToArray())start.Environment.Remove(key);
        if(trial)
        {
            data=Path.GetFullPath(data);
            if(data.Equals(root,StringComparison.OrdinalIgnoreCase)||data.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("데이터는 프로그램 설치 폴더 밖에 저장하세요.");
            if(!File.Exists(Path.Combine(data,".initialized"))&&(password.Length is <12 or >128||string.IsNullOrWhiteSpace(password)))throw new InvalidOperationException("최초 관리자 비밀번호는 12~128자로 입력하세요.");
            var prefix=service=="office"?"School":"Portal";
            start.Environment["ASPNETCORE_ENVIRONMENT"]="Development";
            start.Environment["FeedbackMail__Enabled"]="false";start.Environment["Feedback__AutomaticReportsEnabled"]="false";
            start.Environment["Operations__AlertMailEnabled"]="false";start.Environment["Portal__TunnelOnly"]="false";
            start.Environment["Registration__PolicyFile"]="";start.Environment["Operations__SecurityReviewFile"]="";start.Environment["Operations__SchoolId"]="";
            start.Environment["ASPNETCORE_FORWARDEDHEADERS_ENABLED"]="false";start.Environment["DOTNET_FORWARDEDHEADERS_ENABLED"]="false";start.Environment["backend-restore"]="";
            start.Environment[prefix+"__DataDirectory"]=data;
            if(!File.Exists(Path.Combine(data,".initialized")))start.Environment[prefix+"__AdminPassword"]=password;
            if(service=="office"&&File.Exists(Path.Combine(root,"announcement-server","SchoolMessenger.AnnouncementServer.exe"))){start.Environment["Announcements__Address"]="http://127.0.0.1:5081";start.Environment["Announcements__BridgeKey"]=bridgeKey;}
            if(service=="announcements"){start.Environment["Portal__BridgeKey"]=bridgeKey;start.Environment["Portal__AllowedHosts"]="localhost,127.0.0.1";}
            start.ArgumentList.Add("--urls");start.ArgumentList.Add(service=="office"?"http://127.0.0.1:5080":"http://127.0.0.1:5081");
        }
        else
        {
            start.Environment["ASPNETCORE_ENVIRONMENT"]="Production";
            using var parsed=JsonDocument.Parse(File.ReadAllText(config));var value=parsed.RootElement;
            if(value.GetProperty("service").GetString()!=service||!Path.GetFullPath(value.GetProperty("executable").GetString()!).Equals(Executable,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("선택한 역할과 설치 경로에 맞는 운영 설정 파일을 선택하세요.");
            EnsureNoScheduledOwner(service);
            var script=Path.Combine(root,"scripts","Server-Operations.ps1");
            foreach(var argument in new[]{"-NoProfile","-NonInteractive","-File",script,"-Mode","Start","-ConfigurationPath",Path.GetFullPath(config)})start.ArgumentList.Add(argument);
        }
        var name=@"Local\YeoyangMessenger.Stop."+Guid.NewGuid().ToString("N");signal=new EventWaitHandle(false,EventResetMode.ManualReset,name);
        using(var parent=Process.GetCurrentProcess()){start.Environment["YY_MANAGED_PARENT_PID"]=parent.Id.ToString();start.Environment["YY_MANAGED_PARENT_START"]=parent.StartTime.ToUniversalTime().Ticks.ToString();}
        start.Environment["YY_MANAGED_STOP_EVENT"]=name;
        var process=new Process{StartInfo=start};process.OutputDataReceived+=(_,e)=>WriteOutput(e.Data);process.ErrorDataReceived+=(_,e)=>WriteOutput(e.Data);
        try{if(!process.Start())throw new InvalidOperationException("서버 프로세스를 시작하지 못했습니다.");child=process;production=!trial;process.BeginOutputReadLine();process.BeginErrorReadLine();}
        catch{child=null;process.Dispose();signal.Dispose();signal=null;throw;}
        finally{start.Environment.Remove("School__AdminPassword");start.Environment.Remove("Portal__AdminPassword");start.Environment.Remove("Portal__BridgeKey");start.Environment.Remove("Announcements__BridgeKey");}
    }
    static void EnsureNoScheduledOwner(string service)
    {
        // Mixing a GUI-owned process with an active monitor could restart a stopped server.
        dynamic scheduler=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;scheduler.Connect();
        dynamic tasks=scheduler.GetFolder(@"\").GetTasks(1);
        foreach(dynamic task in tasks)
            if((((string)task.Name).Equals("YeoyangMessenger-"+service+"-Start",StringComparison.OrdinalIgnoreCase)||((string)task.Name).Equals("YeoyangMessenger-"+service+"-Monitor",StringComparison.OrdinalIgnoreCase))&&(bool)task.Enabled)
                throw new InvalidOperationException("기존 자동 구동·감시 작업이 켜져 있습니다. 운영 담당자가 기존 작업을 전환한 뒤 이 프로그램에서 가동하세요.");
    }
    public async Task Stop()
    {
        if(!Running)return;if(production)EnsureNoScheduledOwner(service);signal!.Set();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try{await child!.WaitForExitAsync(timeout.Token);child.WaitForExit();}
        catch(OperationCanceledException){throw new InvalidOperationException("파일 작업을 마무리하는 중입니다. 강제 종료하지 않고 잠시 더 기다려 주세요.");}
    }
    public static string TrialBridgeKey(string directory)
    {
        Directory.CreateDirectory(directory);var path=Path.Combine(directory,"trial-bridge.dpapi");
        if(!File.Exists(path))File.WriteAllBytes(path,ProtectedData.Protect(RandomNumberGenerator.GetBytes(32),null,DataProtectionScope.CurrentUser));
        return Convert.ToHexString(ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser));
    }
}
