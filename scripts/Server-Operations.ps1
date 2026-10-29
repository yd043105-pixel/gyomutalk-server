param(
    [ValidateSet('Preflight','Start','Monitor','ExternalMonitor','TestEmail','Backup','RestoreAttachments')][string]$Mode='Preflight',
    [Parameter(Mandatory=$true)][string]$ConfigurationPath
)
$ErrorActionPreference='Stop'

function Read-OperationsConfig([string]$Path,[bool]$ExternalOnly=$false,[bool]$MailOnly=$false) {
    $config=Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if($config.PSObject.Properties['resourceProfile'] -and $config.resourceProfile -notin @('low-memory','balanced')){throw '부하 설정은 low-memory 또는 balanced입니다.'}
    if($config.schema -ne 1 -or $config.service -notin @('office','announcements')){throw '운영 설정의 형식과 서버 종류를 확인하세요.'}
    if(-not $ExternalOnly -and -not $MailOnly -and $config.PSObject.Properties['registrationPolicyFile'] -and $config.registrationPolicyFile){
        $policyPath=[string]$config.registrationPolicyFile
        if(-not [IO.Path]::IsPathRooted($policyPath) -or $policyPath -ne [IO.Path]::GetFullPath($policyPath) -or -not(Test-Path -LiteralPath $policyPath -PathType Leaf)){throw '학교별 약관 파일에는 존재하는 파일의 절대 경로가 필요합니다.'}
        if((Get-Item -LiteralPath $policyPath).Length -gt 131072){throw '학교별 약관 파일은 128KB 이하여야 합니다.'}
        $policy=Get-Content -LiteralPath $policyPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if($policy.approvedForProduction -isnot [bool] -or -not $policy.operator -or -not $policy.contact){throw '학교별 약관의 운영 주체·문의처·확정 상태를 확인하세요.'}
    }
    foreach($name in @('tunnelOnly','attachmentBackup','networkIsolationVerified','upsInstalledAndTested','backupDeviceAndEncryptionVerified')){if($config.$name -isnot [bool]){throw '운영 확인 항목은 true/false로 입력하세요.'}}
    if($config.alerts.enabled -isnot [bool]){throw '메일 활성화 항목은 true/false로 입력하세요.'}
    if($config.alerts.deliveryVerified -isnot [bool]){throw '메일 수신 확인 항목은 true/false로 입력하세요.'}
    if(-not $ExternalOnly -and -not $MailOnly){foreach($name in @('executable','dataDirectory','backupDirectory')){
        $value=[string]$config.$name
        if(-not [IO.Path]::IsPathRooted($value) -or $value -ne [IO.Path]::GetFullPath($value) -or $value.TrimEnd('\') -eq [IO.Path]::GetPathRoot($value).TrimEnd('\')){throw '실행 파일·데이터·백업에는 전체 경로가 필요합니다.'}
        for($parent=[IO.DirectoryInfo]::new((Split-Path $value -Parent));$null -ne $parent;$parent=$parent.Parent){
            if($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '운영 경로에 링크를 사용할 수 없습니다.'}
        }
    }
    $data=$config.dataDirectory.TrimEnd('\')+'\';$backup=$config.backupDirectory.TrimEnd('\')+'\'
    if($data.StartsWith($backup,[StringComparison]::OrdinalIgnoreCase) -or $backup.StartsWith($data,[StringComparison]::OrdinalIgnoreCase)){throw '데이터와 백업 폴더는 서로 포함될 수 없습니다.'}
    if($config.port -lt 1 -or $config.port -gt 65535 -or $config.certificateThumbprint -notmatch '^[0-9a-fA-F]{40}$'){throw '서버 포트와 인증서 지문을 확인하세요.'}
    }
    if($ExternalOnly -and $config.service -ne 'announcements'){throw '교내 서버를 외부 감시에 공개하지 마세요.'}
    if(-not $MailOnly -and $config.service -eq 'announcements' -and ($config.publicHost -notmatch '^[a-zA-Z0-9.-]+$' -or $config.publicHost.EndsWith('.invalid') -or $config.publicHost -notmatch '\.')){throw '실제 학교 앱 도메인이 필요합니다.'}
    if(-not $MailOnly -and $config.service -eq 'office' -and (-not $config.allowedNetworks -or $config.tunnelOnly)){throw '교내 서버는 학교 허용 대역을 지정하고 외부 터널에 연결하지 마세요.'}
    return $config
}
function Set-OperationsStartup($config,[string]$Path,[ValidateSet('Status','Enable','Disable')][string]$Action){
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $name='YeoyangMessenger-'+$config.service+'-Start'
    $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if($task){
        $owner=[Security.Principal.NTAccount]::new($task.Principal.UserId).Translate([Security.Principal.SecurityIdentifier])
        if($owner -ne $identity.User){throw '해당 작업을 등록한 Windows 운영 계정으로 설정하세요.'}
        $script=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Server-Operations.ps1'))
        $expected='-NoProfile -NonInteractive -File "'+$script+'" -Mode Start -ConfigurationPath "'+[IO.Path]::GetFullPath($Path)+'"'
        $powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if(@($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ne $powershell -or $task.Actions[0].Arguments -ne $expected -or $task.Principal.RunLevel -ne 'Limited'){throw '기존 작업의 실행 경로·설정·권한이 다릅니다. 운영 담당자가 확인하세요.'}
        if($Action -eq 'Status'){Write-Host ('자동 구동 작업: '+$task.State);return $false}
        if($Action -eq 'Enable'){
            if(-not(Test-OperationsReadiness $config).ready){throw '운영 준비 점검을 완료한 후 자동 구동을 켜세요.'}
            Enable-ScheduledTask -TaskName $name | Out-Null;Write-Host '자동 구동을 켰습니다. 다음 Windows 시작 시 적용됩니다.'
        }else{Disable-ScheduledTask -TaskName $name | Out-Null;Write-Host '자동 구동을 껐습니다. 현재 실행 중인 서버와 장애 감시는 종료하지 않습니다.'}
        return $false
    }
    if($Action -eq 'Status'){Write-Host '자동 구동 작업: 미등록';return $false}
    if($Action -eq 'Disable'){Write-Host '등록된 자동 구동 작업이 없습니다.';return $false}
    return $true
}
function Read-OperationsSecrets([string]$Path){
    $secrets=Import-Clixml -LiteralPath (Join-Path (Split-Path $Path -Parent) 'server-operations.secrets.xml')
    if($secrets.monitorKey -isnot [Security.SecureString]){throw '동일한 Windows 운영 계정에서 감시 키를 다시 등록하세요.'}
    return $secrets
}
function Unprotect-OperationsValue([Security.SecureString]$Value){
    $pointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try{return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)}
}
function Test-OperationsReadiness($Config){
    $issues=[Collections.Generic.List[string]]::new()
    if(-not(Test-Path -LiteralPath $Config.executable -PathType Leaf)){$issues.Add('server-package-missing')}
    if(-not $Config.securityReviewFile){$issues.Add('security-review-missing')}
    elseif(-not(Test-OperationsDeploymentReview $Config)){$issues.Add('security-review-invalid')}
    if(-not $Config.networkIsolationVerified){$issues.Add('network-isolation-unverified')}
    if(-not $Config.upsInstalledAndTested){$issues.Add('ups-not-ready')}
    if(-not $Config.backupDeviceAndEncryptionVerified){$issues.Add('encrypted-backup-device-unverified')}
    if(-not(Test-Path -LiteralPath $Config.backupDirectory -PathType Container)){$issues.Add('backup-device-unavailable')}
    if([IO.Path]::GetPathRoot($Config.dataDirectory) -eq [IO.Path]::GetPathRoot($Config.backupDirectory)){$issues.Add('backup-same-volume')}
    if(-not $Config.alerts.enabled -or -not $Config.alerts.sender -or -not $Config.alerts.recipient){$issues.Add('email-alerts-not-ready')}
    if($Config.alerts.deliveryVerified -ne $true){$issues.Add('email-delivery-unverified')}
    if($Config.alerts.smtpPort -ne 587 -or $Config.alerts.smtpHost -notmatch '^[a-zA-Z0-9.-]+$'){$issues.Add('smtp-tls-settings-invalid')}
    try{
        $cert=Get-Item -LiteralPath ('Cert:\LocalMachine\My\'+$Config.certificateThumbprint)
        if(-not $cert.HasPrivateKey -or $cert.NotAfter -le [DateTime]::Now -or $cert.NotBefore -gt [DateTime]::Now){$issues.Add('https-certificate-invalid')}
    }catch{$issues.Add('https-certificate-unavailable')}
    # Standard service accounts cannot query BitLocker reliably. An administrator verifies the
    # device beforehand; this flag records that attestation, not automatic cryptographic proof.
    [pscustomobject]@{ready=($issues.Count -eq 0);issues=@($issues.ToArray());backupEncryptionEvidence='school-confirmation-required';networkIsolationEvidence='school-confirmation-required'}
}
function Test-OperationsDeploymentReview($Config){
    $expectedExecutable=if($Config.service -eq 'office'){'SchoolMessenger.Server.exe'}else{'SchoolMessenger.AnnouncementServer.exe'}
    if([IO.Path]::GetFileName([string]$Config.executable) -ne $expectedExecutable){return $false}
    if(-not $Config.securityReviewFile -or -not [IO.Path]::IsPathRooted([string]$Config.securityReviewFile) -or -not(Test-Path -LiteralPath $Config.securityReviewFile -PathType Leaf) -or -not(Test-Path -LiteralPath $Config.executable -PathType Leaf)){return $false}
    # Do not invoke a legacy binary that might interpret an unknown check flag as normal startup.
    $capabilityFile=Join-Path (Split-Path $Config.executable -Parent) 'server-capabilities.json'
    $assembly=[IO.Path]::ChangeExtension([string]$Config.executable,'.dll')
    try{
        if(-not(Test-Path -LiteralPath $capabilityFile -PathType Leaf) -or (Get-Item -LiteralPath $capabilityFile).Length -gt 8192 -or -not(Test-Path -LiteralPath $assembly -PathType Leaf)){return $false}
        $capability=Get-Content -LiteralPath $capabilityFile -Raw -Encoding UTF8|ConvertFrom-Json
        if($capability.schema -ne 1 -or $capability.deploymentReviewCheck -ne 1 -or $capability.executableSha256 -ne (Get-FileHash -LiteralPath $Config.executable -Algorithm SHA256).Hash -or $capability.assemblySha256 -ne (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash){return $false}
    }catch{return $false}
    $binding=if($Config.service -eq 'announcements' -and $Config.tunnelOnly){'127.0.0.1'}else{'0.0.0.0'}
    $arguments=@('--deployment-review-check','--environment','Production','--urls',('https://'+$binding+':'+$Config.port),'--Operations:SecurityReviewFile',[string]$Config.securityReviewFile,('--Operations:SchoolId='+[string]$Config.schoolId),
        '--Portal:TunnelOnly',[string][bool]$Config.tunnelOnly,('--Portal:PublicHost='+[string]$Config.publicHost),('--Portal:AllowedHosts='+[string]$Config.publicHost+',localhost,127.0.0.1'),
        '--FeedbackMail:Enabled',[string]($Config.feedbackEmailEnabled -eq $true),('--FeedbackMail:Host='+[string]$Config.alerts.smtpHost),('--FeedbackMail:Recipient='+[string]$Config.alerts.recipient),
        '--Operations:AlertMailEnabled',[string][bool]$Config.alerts.enabled,('--Operations:AlertMailHost='+[string]$Config.alerts.smtpHost),('--Operations:AlertMailRecipient='+[string]$Config.alerts.recipient))
    $bridge=if($Config.service -eq 'office' -and $Config.publicHost){'https://'+$Config.publicHost}else{''}
    $arguments+=('--Announcements:Address='+$bridge)
    try{$result=& $Config.executable @arguments 2>&1;if($LASTEXITCODE -ne 0){return $false};$summary=($result -join [Environment]::NewLine)|ConvertFrom-Json;return ($summary.state -eq 'school-record-checked' -and $summary.certificationVerified -eq $false)}catch{return $false}
}
function Set-OperationsEnvironment($Config,$Secrets){
    $prefix=if($Config.service -eq 'office'){'School'}else{'Portal'}
    $values=@{ASPNETCORE_ENVIRONMENT='Production';Operations__MonitorKey=(Unprotect-OperationsValue $Secrets.monitorKey);ASPNETCORE_FORWARDEDHEADERS_ENABLED='false';DOTNET_FORWARDEDHEADERS_ENABLED='false'}
    # Explicitly override the operator's ambient setting, including when no school policy is selected.
    $values['Registration__PolicyFile']=if($Config.PSObject.Properties['registrationPolicyFile']){[string]$Config.registrationPolicyFile}else{''}
    $values['Operations__SecurityReviewFile']=[string]$Config.securityReviewFile
    $values['Operations__SchoolId']=[string]$Config.schoolId
    $values['Operations__AlertMailEnabled']=[string][bool]$Config.alerts.enabled
    $values['Operations__AlertMailHost']=[string]$Config.alerts.smtpHost
    $values['Operations__AlertMailRecipient']=[string]$Config.alerts.recipient
    $values['Portal__PublicHost']=[string]$Config.publicHost
    $values['Announcements__Address']=''
    $values['Announcements__BridgeKey']=''
    if($Config.PSObject.Properties['resourceProfile']){
        $values['Operations__ResourceProfile']=$Config.resourceProfile
        if($Config.resourceProfile -eq 'low-memory'){$values['DOTNET_GCHeapHardLimit']='20000000'}
    }
    $values[$prefix+'__DataDirectory']=$Config.dataDirectory
    $values[$prefix+'__BackupDirectory']=$Config.backupDirectory
    $values[$prefix+'__AttachmentBackup']=[string][bool]$Config.attachmentBackup
    $values[$prefix+'__CertificateThumbprint']=$Config.certificateThumbprint
    $values['FeedbackMail__Enabled']=[string]($Config.feedbackEmailEnabled -eq $true)
    $values['Feedback__AutomaticReportsEnabled']=[string]($Config.automaticBugReportsEnabled -eq $true)
    if($Config.feedbackEmailEnabled -eq $true -and $Secrets.smtpCredential -is [Management.Automation.PSCredential]){
        $values['FeedbackMail__Host']=$Config.alerts.smtpHost
        $values['FeedbackMail__Port']=[string]$Config.alerts.smtpPort
        $values['FeedbackMail__Sender']=$Config.alerts.sender
        $values['FeedbackMail__Recipient']=$Config.alerts.recipient
        $values['FeedbackMail__Username']=$Secrets.smtpCredential.UserName
        $values['FeedbackMail__Password']=$Secrets.smtpCredential.GetNetworkCredential().Password
    }
    if($Secrets.initialAdminPassword){$values[$prefix+'__AdminPassword']=Unprotect-OperationsValue $Secrets.initialAdminPassword}
    if($Config.service -eq 'announcements'){
        $values['Portal__AllowedHosts']=$Config.publicHost+',localhost,127.0.0.1'
        $values['Portal__TunnelOnly']=[string][bool]$Config.tunnelOnly
        if(-not $Secrets.bridgeKey){throw '학교 공지 연결 키가 필요합니다.'}
        $values['Portal__BridgeKey']=Unprotect-OperationsValue $Secrets.bridgeKey
    }else{
        $values['School__AllowedNetworks']=$Config.allowedNetworks
        if($Config.publicHost){$values['Announcements__Address']='https://'+$Config.publicHost}
        if($Secrets.bridgeKey){$values['Announcements__BridgeKey']=Unprotect-OperationsValue $Secrets.bridgeKey}
    }
    $previous=@{}
    foreach($name in $values.Keys){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process');[Environment]::SetEnvironmentVariable($name,$values[$name],'Process')}
    return $previous
}
function Restore-OperationsEnvironment($Previous){foreach($name in $Previous.Keys){[Environment]::SetEnvironmentVariable($name,$Previous[$name],'Process')}}
function Get-OperationsHealth($Config,$Secrets){
    if(-not('Yeoyang.OperationsProbe' -as [type])){
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
namespace Yeoyang {
 public static class OperationsProbe {
  public static string Read(int port,string thumbprint,string key) {
   var request=(HttpWebRequest)WebRequest.Create("https://localhost:"+port+"/health/operations");
   request.Proxy=null;request.AllowAutoRedirect=false;request.Timeout=5000;request.ReadWriteTimeout=5000;
   request.Headers["X-Operations-Key"]=key;
   request.ServerCertificateValidationCallback=(sender,cert,chain,errors)=> {
    if(cert==null)return false;var leaf=new X509Certificate2(cert);
    return String.Equals(leaf.Thumbprint,thumbprint,StringComparison.OrdinalIgnoreCase)&&leaf.NotBefore<=DateTime.Now&&leaf.NotAfter>DateTime.Now;
   };
   HttpWebResponse response;
   try{response=(HttpWebResponse)request.GetResponse();}catch(WebException error){response=error.Response as HttpWebResponse;if(response==null)throw;}
   using(response)using(var reader=new StreamReader(response.GetResponseStream())) {
    if(response.StatusCode!=HttpStatusCode.OK && (int)response.StatusCode!=503)throw new IOException("Probe rejected");
    var buffer=new char[16385];var count=reader.ReadBlock(buffer,0,buffer.Length);
    if(count>=buffer.Length)throw new IOException("Probe too large");return new string(buffer,0,count);
   }
  }
 }
}
'@
    }
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    [Yeoyang.OperationsProbe]::Read($Config.port,$Config.certificateThumbprint,(Unprotect-OperationsValue $Secrets.monitorKey)) | ConvertFrom-Json
}
function Get-OperationsAlertDecision($Previous,[string]$Signature,[DateTimeOffset]$Now,[int]$ReminderHours){
    $reminder=[Math]::Max(1,[Math]::Min(24,$ReminderHours))
    $changed=$null -eq $Previous -or $Previous.signature -ne $Signature
    $due=$null -eq $Previous -or -not $Previous.deliveredAt -or ($Now-[DateTimeOffset]::Parse($Previous.deliveredAt)).TotalHours -ge $reminder
    # A recovered service gets one message; unchanged failures are throttled, failed delivery is retried.
    return (($Signature -ne 'ok' -and ($changed -or $due)) -or ($Signature -eq 'ok' -and $null -ne $Previous -and ($Previous.signature -ne 'ok' -or $Previous.notificationError)))
}
function Get-OperationsAlertText([string]$Signature){
    if($Signature -eq 'ok'){return '서버가 다시 정상적으로 작동하고 있습니다.'}
    $messages=@{
        'server-unreachable'='서버가 응답하지 않습니다.'
        'backup-failed'='백업 작업에 실패했습니다.'
        'backup-stale'='최근 30시간 동안 정상 백업이 확인되지 않았습니다.'
        'backup-missing'='정상 백업 기록이 없습니다.'
        'backup-copy-unavailable'='최근 백업 사본을 확인할 수 없습니다.'
        'backup-device-unavailable'='백업 장치에 접근할 수 없습니다.'
        'disk-low'='서버의 남은 저장 공간이 부족합니다.'
        'storage-unavailable'='서버 저장소를 확인할 수 없습니다.'
        'cleanup-failed'='보관 기간이 지난 자료의 정리가 완료되지 않았습니다.'
        'graduation-failed'='졸업 정보 파기 작업을 완료하지 못했습니다.'
        'tunnel-stopped'='학교 밖 접속 통로가 중단되었습니다.'
        'tunnel-service-missing'='학교 밖 접속 통로가 등록되지 않았습니다.'
        'public-endpoint-unavailable'='학교 밖에서 앱 서버에 접속할 수 없습니다.'
        'probe-invalid'='서버 상태를 확인하지 못했습니다.'
    }
    return (@($Signature.Split(',')|ForEach-Object {if($messages.ContainsKey($_)){$messages[$_]}else{'서버 운영 상태 확인이 필요합니다.'}})-join "`r`n")
}
function Send-OperationsAlert($Config,$Secrets,[string]$Signature,[switch]$Test){
    if(-not $Config.alerts.enabled -or $Secrets.smtpCredential -isnot [Management.Automation.PSCredential]){throw '메일 알림 설정이 필요합니다.'}
    if(-not(Test-OperationsDeploymentReview $Config)){throw '장애 메일의 수신처와 외부 서비스에 대한 학교 보안 검토를 먼저 확인하세요.'}
    $service=if($Config.service -eq 'office'){'교내 업무 서버'}else{'학교 소식 서버'}
    $message=[Net.Mail.MailMessage]::new($Config.alerts.sender,$Config.alerts.recipient)
    $message.Subject=if($Signature -eq 'ok'){'교무톡 서버 정상 복구'}else{'교무톡 서버 점검 필요'}
    $message.Body=$service+"`r`n"+(Get-OperationsAlertText $Signature)+"`r`n확인 시각(한국): "+[TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow,'Korea Standard Time').ToString('yyyy-MM-dd HH:mm:ss')
    $message.BodyEncoding=[Text.Encoding]::UTF8;$message.SubjectEncoding=[Text.Encoding]::UTF8
    if($Test){$message.Subject='교무톡 서버 알림 수신 시험';$message.Body='이 메일은 알림 수신 확인용입니다. 실제 서버의 정상 작동을 확인한 결과는 아닙니다.'}
    $client=[Net.Mail.SmtpClient]::new($Config.alerts.smtpHost,$Config.alerts.smtpPort)
    $client.EnableSsl=$true;$client.Timeout=10000;$client.UseDefaultCredentials=$false;$client.Credentials=$Secrets.smtpCredential.GetNetworkCredential()
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    try{$client.Send($message)}finally{$client.Dispose();$message.Dispose()}
}
function Invoke-OperationsMonitor($Config,$Secrets,[string]$Path,[bool]$External=$false){
    $issues=[Collections.Generic.List[string]]::new()
    if(-not $External){try{
        $status=Get-OperationsHealth $Config $Secrets
        foreach($code in $status.issues){if($code -match '^[a-z-]{1,50}$'){$issues.Add($code)}}
        if($status.status -notin @('ok','degraded')){$issues.Add('probe-invalid')}
    }catch{$issues.Add('server-unreachable')}
    if(-not(Test-Path -LiteralPath $Config.backupDirectory)){$issues.Add('backup-device-unavailable')}
    }
    if($Config.service -eq 'announcements' -and ($Config.tunnelOnly -or $External)){
        if(-not $External){
        try{if((Get-Service -Name cloudflared -ErrorAction Stop).Status -ne 'Running'){$issues.Add('tunnel-stopped')}}catch{$issues.Add('tunnel-service-missing')}
        }
        try{
            $response=Invoke-WebRequest -Uri ('https://'+$Config.publicHost+'/health') -UseBasicParsing -TimeoutSec 10 -MaximumRedirection 0
            $health=$response.Content|ConvertFrom-Json
            if($response.StatusCode -ne 200 -or $health.status -ne 'ok' -or $health.service -ne 'announcements'){$issues.Add('public-endpoint-unavailable')}
        }catch{$issues.Add('public-endpoint-unavailable')}
    }
    $signature=if($issues.Count){(@($issues.ToArray()|Sort-Object -Unique)-join ',')}else{'ok'}
    $statePath=Join-Path (Split-Path $Path -Parent) 'server-operations-state.json';$previous=$null
    if(Test-Path -LiteralPath $statePath){try{$previous=Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json}catch{}}
    $now=[DateTimeOffset]::UtcNow;$delivered=$false;$notificationError=$false
    if(Get-OperationsAlertDecision $previous $signature $now $Config.alerts.reminderHours){
        try{Send-OperationsAlert $Config $Secrets $signature;$delivered=$true}catch{$notificationError=$true}
    }
    $state=[pscustomobject]@{checkedAt=$now.ToString('o');signature=$signature;deliveredAt=if($delivered){$now.ToString('o')}elseif($previous -and $previous.signature -eq $signature){$previous.deliveredAt}else{$null};notificationError=$notificationError}
    $temporary=$statePath+'.tmp';$state|ConvertTo-Json|Set-Content -LiteralPath $temporary -Encoding UTF8;Move-Item -LiteralPath $temporary -Destination $statePath -Force
    # Task history receives only fixed status codes; SMTP exception details are never printed.
    if($notificationError){throw '장애 메일 발송 실패. 운영 상태 파일을 확인하세요.'}
    return $state
}

if($MyInvocation.InvocationName -ne '.'){
    $config=Read-OperationsConfig $ConfigurationPath ($Mode -eq 'ExternalMonitor') ($Mode -eq 'TestEmail')
    if($Mode -eq 'Preflight'){
        Test-OperationsReadiness $config | ConvertTo-Json -Depth 4
    }else{
        $secrets=Read-OperationsSecrets $ConfigurationPath
        if($Mode -eq 'TestEmail'){Send-OperationsAlert $config $secrets 'ok' -Test;Write-Host 'SMTP 서버가 시험 메일을 수락했습니다. 실제 수신함을 확인한 뒤 deliveryVerified를 true로 기록하세요.'}
        elseif($Mode -in @('Monitor','ExternalMonitor')){
            if($Mode -eq 'ExternalMonitor' -and $config.service -ne 'announcements'){throw '교내 서버를 외부 감시에 공개하지 마세요.'}
            Invoke-OperationsMonitor $config $secrets $ConfigurationPath ($Mode -eq 'ExternalMonitor') | ConvertTo-Json
        }
        else{
            if($Mode -eq 'Start' -and -not(Test-OperationsReadiness $config).ready){throw '운영 준비 점검을 통과한 뒤 시작하세요.'}
            $previous=Set-OperationsEnvironment $config $secrets
            Push-Location (Split-Path $config.executable -Parent)
            try{
                if($Mode -eq 'Start'){
                    $binding=if($config.service -eq 'announcements' -and $config.tunnelOnly){'127.0.0.1'}else{'0.0.0.0'}
                    & $config.executable --urls ("https://${binding}:"+$config.port)
                }elseif($Mode -eq 'Backup'){& $config.executable --operations-backup}
                else{& $config.executable --operations-restore-attachments}
                if($LASTEXITCODE -ne 0){throw '서버 운영 명령이 실패했습니다. 운영 계정의 로컬 기록을 확인하세요.'}
            }finally{Pop-Location;Restore-OperationsEnvironment $previous}
        }
    }
}
