# Run by the server installer with administrator rights. Registers "GyomuTalk Server Update", a task that runs
# Install-ServerUpdate.ps1 as SYSTEM when a signed-in user starts it, and the drop folder the 관리자 프로그램 puts a
# downloaded update into. -Remove takes both away again (uninstall).
param([switch]$Remove,[string]$TaskName='GyomuTalk Server Update')
$ErrorActionPreference='Stop'
$scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$root=$scheduler.GetFolder('\')
function Remove-Task{try{$root.DeleteTask($TaskName,0)}catch{}}
if($Remove){Remove-Task;return}

# SYSTEM may only run a script ordinary users cannot change: the program has to be under Program Files.
$app=Split-Path $PSScriptRoot -Parent
$programs=[Environment]::GetFolderPath('ProgramFiles').TrimEnd('\')+'\'
if(-not ($app.TrimEnd('\')+'\').StartsWith($programs,[StringComparison]::OrdinalIgnoreCase)){
    Remove-Task;Write-Host 'Automatic server updates need the program under Program Files; the task was not registered.';return
}

# Anyone signed in may leave a file here; only a file with the update signature is ever installed from it.
$drop=Join-Path $env:ProgramData 'GyomuTalk\server-update'
foreach($folder in (Split-Path $drop -Parent),$drop){
    if((Test-Path -LiteralPath $folder) -and ((Get-Item -LiteralPath $folder -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){(Get-Item -LiteralPath $folder -Force).Delete()}
}
$security=New-Object Security.AccessControl.DirectorySecurity
$security.SetAccessRuleProtection($true,$false)
$security.SetOwner((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
foreach($rule in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-11','Modify'))){
    $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $rule[0]),$rule[1],'ContainerInherit,ObjectInherit','None','Allow')))
}
if(Test-Path -LiteralPath $drop){Set-Acl -LiteralPath $drop -AclObject $security}else{[void][IO.Directory]::CreateDirectory($drop,$security)}

$definition=$scheduler.NewTask(0)
$definition.RegistrationInfo.Description='교무톡 관리자 프로그램이 내려받은 서버 업데이트를 서명을 확인한 뒤 설치합니다.'
$definition.Settings.ExecutionTimeLimit='PT1H'
$definition.Settings.MultipleInstances=2
$definition.Settings.DisallowStartIfOnBatteries=$false
$definition.Settings.StopIfGoingOnBatteries=$false
$definition.Settings.AllowDemandStart=$true
$definition.Principal.RunLevel=1
$action=$definition.Actions.Create(0)
$action.Path=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$action.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $PSScriptRoot 'Install-ServerUpdate.ps1')+'"'
# Administrators and SYSTEM own the task; every signed-in user may read its result and start it, nothing more.
[void]$root.RegisterTaskDefinition($TaskName,$definition,6,'SYSTEM',$null,5,'D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)')
Write-Host "Registered $TaskName"
