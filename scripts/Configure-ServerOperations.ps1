param(
    [Parameter(Mandatory=$true)][string]$ConfigurationPath,
    [switch]$InitializeSecrets,
    [switch]$RegisterTasks,
    [switch]$ExternalMonitorOnly,
    [ValidateSet('','Status','Enable','Disable')][string]$StartupAction='',
    [switch]$Apply
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Server-Operations.ps1') -ConfigurationPath $ConfigurationPath
$config=Read-OperationsConfig $ConfigurationPath ([bool]$ExternalMonitorOnly)
if($StartupAction){
    if($ExternalMonitorOnly){throw '서버 자동 구동은 서버 운영 설정을 선택하세요.'}
    if(-not(Set-OperationsStartup $config $ConfigurationPath $StartupAction)){return}
    $RegisterTasks=$true;$Apply=$true
}
if(-not $Apply){if(-not $ExternalMonitorOnly){Test-OperationsReadiness $config | ConvertTo-Json};Write-Host '점검만 수행했습니다. 적용은 운영 서버에서 -Apply로 실행하세요.';return}
function Protect-OperationsDirectory([string]$Path){
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach($identity in @($sid,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))){
        $rule=[Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow');$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
if($InitializeSecrets){
    $folder=Split-Path ([IO.Path]::GetFullPath($ConfigurationPath)) -Parent
    if($folder -eq [IO.Path]::GetPathRoot($folder) -or $folder -eq $PSScriptRoot){throw '설정은 운영 계정 전용 별도 폴더에 저장하세요.'}
    Protect-OperationsDirectory $folder
    $secretPath=Join-Path $folder 'server-operations.secrets.xml'
    if(Test-Path -LiteralPath $secretPath){throw '기존 키를 자동 덮어쓰지 않습니다. 키 교체·복구 절차를 따르세요.'}
    $random=New-Object byte[] 48;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();try{$rng.GetBytes($random)}finally{$rng.Dispose()}
    $secrets=[pscustomobject]@{
        monitorKey=(ConvertTo-SecureString ([Convert]::ToBase64String($random)) -AsPlainText -Force)
        initialAdminPassword=if($ExternalMonitorOnly){$null}else{Read-Host '최초 서버 관리자 비밀번호(12~128자)' -AsSecureString}
        bridgeKey=if($ExternalMonitorOnly){$null}else{Read-Host '두 서버에 동일하게 등록할 무작위 연결 키(32자 이상)' -AsSecureString}
        smtpCredential=(Get-Credential -UserName $config.alerts.sender -Message '메일 발신 계정과 앱 비밀번호 입력. 일반 계정 비밀번호를 사용하지 마세요.')
    }
    if(-not $ExternalMonitorOnly -and ($secrets.initialAdminPassword.Length -lt 12 -or $secrets.initialAdminPassword.Length -gt 128 -or $secrets.bridgeKey.Length -lt 32)){throw '관리자 비밀번호와 연결 키 길이를 확인하세요.'}
    if($secrets.smtpCredential -isnot [Management.Automation.PSCredential]){throw '메일 인증 정보 입력을 완료하세요.'}
    $secrets|Export-Clixml -LiteralPath $secretPath
    if(-not $ExternalMonitorOnly){Protect-OperationsDirectory $config.dataDirectory
    if(-not(Test-Path -LiteralPath $config.backupDirectory)){throw '백업 장치를 먼저 연결하세요. 비밀 설정은 저장되었습니다.'}
    Protect-OperationsDirectory $config.backupDirectory
    }
    Write-Host '비밀 설정을 현재 Windows 운영 계정으로 보호했습니다. 비밀번호는 출력하지 않습니다.'
}
if($RegisterTasks){
    if(-not $ExternalMonitorOnly -and -not(Test-OperationsReadiness $config).ready){throw '망 분리·UPS·백업 암호화·인증서·메일 준비를 완료하세요.'}
    if($ExternalMonitorOnly -and ($config.service -ne 'announcements' -or -not $config.alerts.enabled -or $config.alerts.deliveryVerified -ne $true)){throw '학교 소식 서버의 메일 알림 설정과 실제 수신 확인이 필요합니다.'}
    $secrets=Read-OperationsSecrets $ConfigurationPath
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if($identity.IsSystem){throw '일반 운영 계정으로 등록하세요.'}
    $principal=[Security.Principal.WindowsPrincipal]::new($identity)
    if($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '관리자 권한을 가진 계정 대신 전용 표준 운영 계정을 사용하세요.'}
    $credential=Get-Credential -UserName $identity.Name -Message '로그인하지 않은 상태에서도 실행할 Windows 운영 계정 비밀번호'
    $taskSid=[Security.Principal.NTAccount]::new($credential.UserName).Translate([Security.Principal.SecurityIdentifier])
    if($taskSid -ne $identity.User){throw '비밀 설정을 만든 Windows 계정과 작업 실행 계정이 같아야 합니다.'}
    $powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $script=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Server-Operations.ps1'))
    $password=$credential.GetNetworkCredential().Password
    $created=[Collections.Generic.List[string]]::new()
    try{
        $kinds=if($ExternalMonitorOnly){@('ExternalMonitor')}else{@('Start','Monitor')}
        foreach($kind in $kinds){if(Get-ScheduledTask -TaskName ('YeoyangMessenger-'+$config.service+'-'+$kind) -ErrorAction SilentlyContinue){throw '같은 이름의 작업이 있습니다. 기존 작업의 설정을 검토한 후 교체하세요.'}}
        foreach($kind in $kinds){
            $name='YeoyangMessenger-'+$config.service+'-'+$kind
            if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw '같은 이름의 작업이 있습니다. 기존 작업의 설정을 검토한 후 교체하세요.'}
            $arguments='-NoProfile -NonInteractive -File "'+$script+'" -Mode '+$kind+' -ConfigurationPath "'+[IO.Path]::GetFullPath($ConfigurationPath)+'"'
            $action=New-ScheduledTaskAction -Execute $powershell -Argument $arguments
            $trigger=if($kind -eq 'Start'){New-ScheduledTaskTrigger -AtStartup}else{New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1)) -RepetitionInterval (New-TimeSpan -Minutes 1)}
            $limit=if($kind -eq 'Start'){[TimeSpan]::Zero}else{New-TimeSpan -Minutes 1}
            $settings=New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit $limit -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
            Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -User $identity.Name -Password $password -RunLevel Limited | Out-Null
            $created.Add($name)
        }
    }catch{foreach($name in $created){Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue};throw}finally{$password=$null}
    Write-Host '자동 시작과 분당 감시 작업을 등록했습니다. 지금 서버를 시작하거나 메일을 보내지는 않았습니다.'
}
