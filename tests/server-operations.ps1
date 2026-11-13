param([Parameter(Mandatory=$true)][string]$FixtureRoot)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$FixtureRoot=[IO.Path]::GetFullPath($FixtureRoot)
New-Item -ItemType Directory -Path $FixtureRoot -Force | Out-Null
$configPath=Join-Path $FixtureRoot 'server-operations.local.json'
. (Join-Path $root 'scripts\Server-Operations.ps1') -ConfigurationPath $configPath
$checks=[Collections.Generic.List[string]]::new()
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Check([string]$Message){$checks.Add($Message)}
foreach($name in @('Server-Operations.ps1','Configure-ServerOperations.ps1','Inspect-ServerComputer.ps1','Stage-ServerUpdate.ps1')){
    $file=Join-Path $root ('scripts\'+$name);$tokens=$null;$errors=$null
    [Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)|Out-Null
    Assert (-not $errors) ('Parser error: '+$name)
    $bytes=[IO.File]::ReadAllBytes($file);Assert ($bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) ('UTF8 BOM missing: '+$name)
}
Check 'PowerShell 5 parsers and Korean UTF8 BOM'
$config=Get-Content -LiteralPath (Join-Path $root 'config\server-operations.example.json') -Raw -Encoding UTF8|ConvertFrom-Json
$config.service='office';$config.tunnelOnly=$false;$config.allowedNetworks='192.0.2.0/24';$config.publicHost='fixture.example.org';$config.certificateThumbprint='0'*40
$config.executable=Join-Path $FixtureRoot 'old-version\server.exe';$config.dataDirectory=Join-Path $FixtureRoot 'data';$config.backupDirectory=Join-Path $FixtureRoot 'backup'
$config|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $configPath -Encoding UTF8
$loaded=Read-OperationsConfig $configPath;$readiness=Test-OperationsReadiness $loaded
Assert (-not $readiness.ready -and 'ups-not-ready' -in $readiness.issues -and 'network-isolation-unverified' -in $readiness.issues -and 'encrypted-backup-device-unverified' -in $readiness.issues) 'Incomplete deployment incorrectly ready'
Check 'Missing UPS, isolated network and backup device block production startup'
$bad=$config|ConvertTo-Json -Depth 8|ConvertFrom-Json;$bad.backupDirectory=Join-Path $bad.dataDirectory 'backup'
$bad|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $configPath -Encoding UTF8
$rejected=$false;try{Read-OperationsConfig $configPath|Out-Null}catch{$rejected=$true};Assert $rejected 'Nested backup accepted'
$bad=$config|ConvertTo-Json -Depth 8|ConvertFrom-Json;$bad.upsInstalledAndTested='false'
$bad|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $configPath -Encoding UTF8
$rejected=$false;try{Read-OperationsConfig $configPath|Out-Null}catch{$rejected=$true};Assert $rejected 'String readiness accepted'
$config|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $configPath -Encoding UTF8
Check 'Nested paths and incorrectly typed readiness confirmations rejected'
$key=ConvertTo-SecureString ('Synthetic-'+[Guid]::NewGuid().ToString()+[Guid]::NewGuid().ToString()) -AsPlainText -Force
$secrets=[pscustomobject]@{monitorKey=$key;initialAdminPassword=$null;bridgeKey=$null;smtpCredential=$null}
$secrets|Export-Clixml -LiteralPath (Join-Path $FixtureRoot 'server-operations.secrets.xml')
$decoded=Read-OperationsSecrets $configPath
Assert ((Unprotect-OperationsValue $decoded.monitorKey) -eq (Unprotect-OperationsValue $key)) 'DPAPI round trip failed'
Assert (-not(Get-Content -LiteralPath (Join-Path $FixtureRoot 'server-operations.secrets.xml') -Raw).Contains((Unprotect-OperationsValue $key))) 'Plaintext key exported'
$before=[Environment]::GetEnvironmentVariable('Operations__MonitorKey','Process');$heapBefore=[Environment]::GetEnvironmentVariable('DOTNET_GCHeapHardLimit','Process');$previous=Set-OperationsEnvironment $config $secrets
Assert ([Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT','Process') -eq 'Production') 'Development environment inherited'
Assert ([Environment]::GetEnvironmentVariable('Operations__ResourceProfile','Process') -eq 'low-memory') 'Resource profile not applied'
Assert ([Environment]::GetEnvironmentVariable('DOTNET_GCHeapHardLimit','Process') -eq '20000000') 'Heap budget must be 512MiB in hexadecimal'
Restore-OperationsEnvironment $previous
Assert ([string][Environment]::GetEnvironmentVariable('DOTNET_GCHeapHardLimit','Process') -eq [string]$heapBefore) 'Heap environment left behind'
Check 'Low-memory profile and 512MiB managed heap budget apply only to the launched process and restore afterward'
Assert ([string][Environment]::GetEnvironmentVariable('Operations__MonitorKey','Process') -eq [string]$before) 'Secret environment left behind'
Check 'DPAPI key storage and temporary secret environment restored'
$mailConfig=$config|ConvertTo-Json -Depth 8|ConvertFrom-Json
$mailConfig.feedbackEmailEnabled=$true;$mailConfig.automaticBugReportsEnabled=$true
$mailConfig.alerts.sender='sender@example.invalid';$mailConfig.alerts.recipient='recipient@example.invalid'
$oldCredential=$secrets.smtpCredential;$mailSecretBefore=[Environment]::GetEnvironmentVariable('FeedbackMail__Password','Process');$mailEnvironment=$null
try{
    $secrets.smtpCredential=[Management.Automation.PSCredential]::new('sender@example.invalid',(ConvertTo-SecureString 'SYNTHETIC-MAIL-PASSWORD' -AsPlainText -Force))
    $mailEnvironment=Set-OperationsEnvironment $mailConfig $secrets
    Assert ([Environment]::GetEnvironmentVariable('FeedbackMail__Recipient','Process') -eq 'recipient@example.invalid') 'Feedback recipient not configured'
    Assert ([Environment]::GetEnvironmentVariable('FeedbackMail__Password','Process') -eq 'SYNTHETIC-MAIL-PASSWORD') 'Protected SMTP credential not passed to child runtime'
    Assert ([Environment]::GetEnvironmentVariable('Feedback__AutomaticReportsEnabled','Process') -eq 'True') 'Automatic report flag not passed'
}finally{if($null -ne $mailEnvironment){Restore-OperationsEnvironment $mailEnvironment};$secrets.smtpCredential=$oldCredential}
Assert ([string][Environment]::GetEnvironmentVariable('FeedbackMail__Password','Process') -eq [string]$mailSecretBefore) 'SMTP password left in parent environment'
Check 'Feedback mail uses protected SMTP credentials and restores parent process secrets'
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
public sealed class OperationsTlsFixture : IDisposable {
 public string LastError;
 readonly TcpListener listener;readonly X509Certificate2 certificate;readonly Thread worker;
 public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
 public OperationsTlsFixture(X509Certificate2 cert) {
  certificate=cert;listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
  worker=new Thread(()=> {try {while(true) {using(var client=listener.AcceptTcpClient()) {
   client.ReceiveTimeout=3000;client.SendTimeout=3000;
   try {using(var stream=new SslStream(client.GetStream(),false)) {
    stream.ReadTimeout=3000;stream.WriteTimeout=3000;stream.AuthenticateAsServer(certificate,false,System.Security.Authentication.SslProtocols.Tls12,false);
    using(var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true)){string line;int count=0;while(!String.IsNullOrEmpty(line=reader.ReadLine())){if((count+=line.Length)>8192)throw new IOException();}}
    var body="{\"status\":\"ok\",\"issues\":[]}";
    var response=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: "+body.Length+"\r\n\r\n"+body);stream.Write(response,0,response.Length);
   }} catch(Exception error) {LastError=error.ToString();}
  }}}catch(Exception error){LastError=error.GetType().Name;} });worker.IsBackground=true;worker.Start();
 }
 public void Dispose(){listener.Stop();worker.Join(4000);}
}
'@
$rsa=[Security.Cryptography.RSACng]::new(2048)
$request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
$generated=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddHours(1))
# Schannel on .NET Framework needs an importable key handle; no certificate store is changed.
$certificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new($generated.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx),'',[Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet)
$generated.Dispose()
$tls=[OperationsTlsFixture]::new($certificate)
try{
    $probeConfig=[pscustomobject]@{port=$tls.Port;certificateThumbprint=$certificate.Thumbprint}
    try{$probe=Get-OperationsHealth $probeConfig $secrets}catch{throw ('Synthetic TLS fixture: '+$tls.LastError+' Client: '+$_.Exception.Message)}
    Assert ($probe.status -eq 'ok') 'Pinned TLS probe failed'
    $probeConfig.certificateThumbprint='0'*40;$rejected=$false
    try{Get-OperationsHealth $probeConfig $secrets|Out-Null}catch{$rejected=$true};Assert $rejected 'Incorrect TLS pin accepted'
}finally{$tls.Dispose();$certificate.Dispose();$rsa.Dispose()}
Check 'Native localhost HTTPS probe accepts the pinned certificate and rejects a different pin'
$now=[DateTimeOffset]::UtcNow
$old=[pscustomobject]@{signature='disk-low';deliveredAt=$now.ToString('o');notificationError=$false}
Assert (Get-OperationsAlertDecision $null 'disk-low' $now 6) 'First failure missing'
Assert (-not(Get-OperationsAlertDecision $old 'disk-low' $now.AddMinutes(1) 6)) 'Duplicate failure not throttled'
Assert (Get-OperationsAlertDecision $old 'disk-low' $now.AddHours(7) 6) 'Reminder missing'
Assert (Get-OperationsAlertDecision $old 'ok' $now 6) 'Recovery missing'
Assert (-not(Get-OperationsAlertDecision $null 'ok' $now 6)) 'Healthy startup unnecessarily alerts'
$old.deliveredAt=$null;Assert (Get-OperationsAlertDecision $old 'disk-low' $now 6) 'Failed SMTP not retried'
$old.signature='ok';$old.notificationError=$true;Assert (Get-OperationsAlertDecision $old 'ok' $now 6) 'Failed recovery SMTP not retried'
Check 'Failure, duplicate suppression, reminders, recovery and SMTP retry decisions'
Assert ((Get-OperationsAlertText 'backup-failed,disk-low').Contains('백업 작업에 실패')) 'Human-readable email text missing'
Assert (-not(Get-OperationsAlertText 'unknown-sensitive-value').Contains('unknown-sensitive-value')) 'Unknown response data leaked into email'
New-Item -ItemType Directory -Path $config.backupDirectory -Force|Out-Null
$script:healthIssue='disk-low';$script:sent=0;$script:mailFailure=$false
function Get-OperationsHealth($Config,$Secrets){[pscustomobject]@{status=if($script:healthIssue){'degraded'}else{'ok'};issues=@($script:healthIssue|Where-Object {$_})}}
function Send-OperationsAlert($Config,$Secrets,[string]$Signature){if($script:mailFailure){throw 'Synthetic SMTP failure'};$script:sent+=1}
$state=Invoke-OperationsMonitor $config $secrets $configPath
Assert ($state.signature -eq 'disk-low' -and $script:sent -eq 1) 'First monitor mail missing'
$state=Invoke-OperationsMonitor $config $secrets $configPath;Assert ($script:sent -eq 1) 'Repeated monitor mail'
$script:healthIssue='';$state=Invoke-OperationsMonitor $config $secrets $configPath
Assert ($state.signature -eq 'ok' -and $script:sent -eq 2) 'Monitor recovery missing'
$state=Invoke-OperationsMonitor $config $secrets $configPath;Assert ($script:sent -eq 2) 'Repeated recovery mail'
$script:healthIssue='backup-failed';$script:mailFailure=$true;$rejected=$false
try{Invoke-OperationsMonitor $config $secrets $configPath|Out-Null}catch{$rejected=$true}
$pending=Get-Content -LiteralPath (Join-Path $FixtureRoot 'server-operations-state.json') -Raw|ConvertFrom-Json
Assert ($rejected -and $pending.notificationError -and -not $pending.deliveredAt) 'Failed delivery incorrectly recorded as success'
$script:mailFailure=$false;$state=Invoke-OperationsMonitor $config $secrets $configPath
Assert ($state.deliveredAt -and -not $state.notificationError) 'Pending notification not retried'
Check 'Persisted monitor state, recovery and failed-delivery retry; no real email sent'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$config.service='office';$config.tunnelOnly=$false;$config|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $configPath -Encoding UTF8
New-Item -ItemType Directory -Path (Join-Path $FixtureRoot 'settings') -Force|Out-Null
$stageConfig=Join-Path $FixtureRoot 'settings\server-operations.local.json'
$config|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $stageConfig -Encoding UTF8
foreach($kind in @('clean','escape','private')){
    $zipPath=Join-Path $FixtureRoot ($kind+'.zip');$zip=[IO.Compression.ZipFile]::Open($zipPath,'Create')
    try{
        $entry=$zip.CreateEntry('server/SchoolMessenger.Server.exe');$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write('synthetic package');$writer.Dispose()
        if($kind -ne 'clean'){$name=if($kind -eq 'escape'){'server/../escape.txt'}else{'server/school.db'};$entry=$zip.CreateEntry($name);$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write('synthetic');$writer.Dispose()}
    }finally{$zip.Dispose()}
    $hash=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    $stage=Join-Path $FixtureRoot ('version-'+$kind);$rejected=$false
    try{& (Join-Path $root 'scripts\Stage-ServerUpdate.ps1') -ConfigurationPath $stageConfig -Package $zipPath -ExpectedSha256 $hash -VersionDirectory $stage|Out-Null}catch{$rejected=$true;if($kind -eq 'clean'){throw}}
    Assert ($rejected -eq ($kind -ne 'clean')) ('Package validation incorrect: '+$kind)
    Assert (-not(Test-Path -LiteralPath $stage)) 'Validation changed installation'
}
Check 'Update package traversal/private-data rejection and mutation-free validation'
& {
    $script:startupEnabled=0;$script:startupDisabled=0;$script:startupReady=$true;$script:fakeStartup=$null
    function Get-ScheduledTask {param($TaskName) $script:fakeStartup}
    function Enable-ScheduledTask {param($TaskName) $script:startupEnabled++}
    function Disable-ScheduledTask {param($TaskName) $script:startupDisabled++}
    function Test-OperationsReadiness {param($Config) [pscustomobject]@{ready=$script:startupReady}}
    Assert (Set-OperationsStartup $config $configPath 'Enable') 'Missing startup task must require registration'
    Assert (-not(Set-OperationsStartup $config $configPath 'Status')) 'Status must not register tasks'
    $script:fakeStartup=[pscustomobject]@{State='Disabled';Principal=[pscustomobject]@{UserId=[Security.Principal.WindowsIdentity]::GetCurrent().Name;RunLevel='Limited'};Actions=@([pscustomobject]@{Execute=(Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe');Arguments=('-NoProfile -NonInteractive -File "'+(Join-Path $root 'scripts\Server-Operations.ps1')+'" -Mode Start -ConfigurationPath "'+[IO.Path]::GetFullPath($configPath)+'"')})}
    Set-OperationsStartup $config $configPath 'Enable' | Out-Null
    Set-OperationsStartup $config $configPath 'Disable' | Out-Null
    Assert ($script:startupEnabled -eq 1 -and $script:startupDisabled -eq 1) 'Owned task enable/disable failed'
    $script:startupReady=$false;$rejected=$false;try{Set-OperationsStartup $config $configPath 'Enable'|Out-Null}catch{$rejected=$true};Assert $rejected 'Readiness bypassed'
    $script:fakeStartup.Actions[0].Arguments='-Command untrusted';$rejected=$false;try{Set-OperationsStartup $config $configPath 'Disable'|Out-Null}catch{$rejected=$true};Assert $rejected 'Unrelated command accepted'
    Assert ($script:startupEnabled -eq 1 -and $script:startupDisabled -eq 1) 'Rejected changes still mutated tasks'
}
Check 'Mocked startup tasks enforce ownership, exact command, readiness and explicit enable/disable without registering real tasks'
[pscustomobject]@{checks=$checks.ToArray();realEmailSent=$false;tasksRegistered=$false;systemSettingsChanged=$false}|ConvertTo-Json -Depth 5
