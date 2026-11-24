# End to end on a throwaway Windows (CI runs as administrator): install the real server installer, then let a standard
# user's update helper hand a signed installer to the SYSTEM task, and check what is refused. Needs Python with
# cryptography for test signatures. Removes everything it installed.
param([string]$Installer)
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){Write-Host 'SKIP the automatic update test installs into Program Files and needs an administrator (it runs in CI)';exit 0}
$projectRoot=Split-Path $PSScriptRoot -Parent
$version=[string]([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if(-not $Installer){$Installer=Join-Path $projectRoot "artifacts\yymessenger-$version-server-setup-win-x64.exe"}
$taskName='GyomuTalk Server Update'
$app=Join-Path $env:ProgramFiles 'GyomuTalkServer'
if(Test-Path -LiteralPath $app){throw "A server is already installed in $app; run this test on a throwaway machine."}
$shared=Join-Path $env:PUBLIC ('gyomutalk-update-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $shared | Out-Null
$user='gtupd'+(Get-Random -Minimum 10000 -Maximum 99999)
$password='Aa1!'+[guid]::NewGuid().ToString('N')
function Check([string]$Name){Write-Host "PASS $Name"}
function Quote([string]$Value){'"'+$Value+'"'}
function TaskInfo{Get-ScheduledTaskInfo -TaskName $taskName}
# Runs PowerShell as the standard user and waits; the user's own profile gets the helper's last-result.json.
function AsUser([string[]]$Arguments){
    $credential=New-Object Management.Automation.PSCredential($user,(ConvertTo-SecureString $password -AsPlainText -Force))
    $process=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Credential $credential -LoadUserProfile -WorkingDirectory $shared -ArgumentList (@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass')+$Arguments) -WindowStyle Hidden -Wait -PassThru
    $process.ExitCode
}
function Helper([string]$Staged,[string]$Signature){
    $result=Join-Path $env:SystemDrive "Users\$user\AppData\Local\GyomutalkUpdater\server\last-result.json"
    Remove-Item -LiteralPath $result -Force -ErrorAction SilentlyContinue
    $hash=(Get-FileHash -LiteralPath $Staged -Algorithm SHA256).Hash
    [void](AsUser @('-File',(Quote (Join-Path $shared 'Finish-DesktopUpdate.ps1')),'-ParentId','999999','-ParentStart','0','-Installer',(Quote $Staged),'-ExpectedHash',$hash,'-Application',(Quote (Join-Path $shared 'none.exe')),'-Product','server','-Signature',$Signature,'-Unattended'))
    Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
}
try{
    $setup=Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/TYPE=office',('/LOG='+(Quote (Join-Path $shared 'install.log')))) -Wait -PassThru
    if($setup.ExitCode -ne 0){throw "Server installer failed: $($setup.ExitCode)"}
    $task=Get-ScheduledTask -TaskName $taskName
    if($task.Principal.UserId -notmatch 'SYSTEM$' -or $task.Actions[0].Arguments -notmatch [regex]::Escape((Join-Path $app 'scripts\Install-ServerUpdate.ps1'))){throw 'The update task does not run the installed script as SYSTEM.'}
    Check 'the server installer registers the SYSTEM update task for the installed script'

    # A test key stands in for the release key, so this run can sign installers.
    $keys=python -c "import base64;from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey as K;from cryptography.hazmat.primitives import serialization as s;k=K.generate();print(base64.b64encode(k.private_bytes(s.Encoding.Raw,s.PrivateFormat.Raw,s.NoEncryption())).decode());print(base64.b64encode(k.public_key().public_bytes(s.Encoding.Raw,s.PublicFormat.Raw)).decode())"
    if($LASTEXITCODE -ne 0){throw 'Python with cryptography is needed for test signatures.'}
    @{product='server';repository='yd043105-pixel/gyomutalk-server';publicKey=$keys[1]}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $app 'scripts\update-publisher.json') -Encoding ASCII
    function Sign([string]$File){python -c "import base64,sys;from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey as K;print(base64.b64encode(K.from_private_bytes(base64.b64decode(sys.argv[1])).sign(open(sys.argv[2],'rb').read())).decode())" $keys[0] $File}
    $staged=Join-Path $shared 'setup.exe';Copy-Item -LiteralPath $Installer -Destination $staged
    $signature=Sign $staged

    net user $user $password /add /y | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Could not create the standard test user.'}
    icacls $shared /grant "${user}:(OI)(CI)M" | Out-Null
    # The 관리자 프로그램 runs the shipped helper from a copy in the user's own folder, as here.
    Copy-Item -LiteralPath (Join-Path $app 'admin\Finish-DesktopUpdate.ps1') -Destination $shared
    $before=(TaskInfo).LastRunTime
    $outcome=Helper $staged $signature
    $info=TaskInfo
    if(-not $outcome.success -or $info.LastTaskResult -ne 0 -or $info.LastRunTime -le $before){throw "Signed update not installed: $($outcome.message) / task $($info.LastTaskResult)"}
    if((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{AE5EF18D-119A-4E9F-A7F3-E078F33E2B18}_is1').DisplayVersion -ne $version){throw 'The server is not installed after the automatic update.'}
    if(Get-ChildItem -LiteralPath (Join-Path $env:ProgramData 'GyomuTalk\server-update') -File){throw 'The helper left the installer in the drop folder.'}
    Check 'a standard user hands a signed installer to the SYSTEM task, which installs it without the administrator prompt'

    $changed=Join-Path $shared 'changed.exe';Copy-Item -LiteralPath $staged -Destination $changed
    $stream=[IO.File]::Open($changed,'Open','ReadWrite');$stream.Position=$stream.Length-1;$last=$stream.ReadByte();$stream.Position=$stream.Length-1;$stream.WriteByte($last -bxor 1);$stream.Dispose()
    $outcome=Helper $changed $signature
    if($outcome.success -or (TaskInfo).LastTaskResult -ne 11){throw "A changed installer was not refused: $((TaskInfo).LastTaskResult)"}
    $outcome=Helper $staged (Sign $changed)
    if($outcome.success -or (TaskInfo).LastTaskResult -ne 11){throw 'A signature for a different file was not refused.'}
    Check 'a changed installer, or a signature for another file, is refused and nothing runs'

    # The standard user may start the task, but not change it or the script it runs.
    $probe=Join-Path $shared 'probe.txt'
    $commands="`$r=@();try{Add-Content -LiteralPath '$(Join-Path $app 'scripts\Install-ServerUpdate.ps1')' -Value 'whoami';`$r+='script-changed'}catch{};try{Disable-ScheduledTask -TaskName '$taskName' -ErrorAction Stop|Out-Null;`$r+='task-disabled'}catch{};try{Unregister-ScheduledTask -TaskName '$taskName' -Confirm:`$false -ErrorAction Stop;`$r+='task-removed'}catch{};Set-Content -LiteralPath '$probe' -Value ('done '+(`$r -join ' '))"
    [void](AsUser @('-Command',$commands))
    $text=(Get-Content -LiteralPath $probe -Raw).Trim()
    if($text -ne 'done'){throw "A standard user changed the update task or its script: $text"}
    Check 'a standard user cannot change the update task or its script'
}finally{
    if(Test-Path -LiteralPath (Join-Path $app 'unins000.exe')){
        $removal=Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
        if($removal.ExitCode -eq 0 -and -not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)){Check 'uninstalling removes the update task'}else{Write-Host 'FAIL uninstall left the update task';$failed=$true}
    }
    net user $user /delete 2>$null | Out-Null
    Remove-Item -LiteralPath $shared -Recurse -Force -ErrorAction SilentlyContinue
}
if($failed){exit 1}
