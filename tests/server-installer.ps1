param([string]$OutputDirectory,[string]$PackageDirectory,[string]$Compiler,[switch]$ShortcutOnly)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if(-not $OutputDirectory){$OutputDirectory=Join-Path $projectRoot 'artifacts'}
if(-not $PackageDirectory){$PackageDirectory=Join-Path $projectRoot 'artifacts\여양고-교무메신저'}
if(-not $Compiler){$Compiler=Join-Path $projectRoot '.tools\inno-6.7.3\ISCC.exe'}
$version=[string]([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$testGuid=[guid]::NewGuid().ToString()
$testGroup='YY-Installer-Test-'+$testGuid
$startupEntry='YyMessengerInstallerTest-'+$testGuid
$work=Join-Path $projectRoot ".test-data\server-installer-$testGuid"
$install=Join-Path $work 'installed'
$regName="Software\Microsoft\Windows\CurrentVersion\Uninstall\{$testGuid}_is1"
New-Item -ItemType Directory -Path $work -Force | Out-Null
$checks=[Collections.Generic.List[string]]::new()
$owned=[Collections.Generic.List[Diagnostics.Process]]::new()
function Check([string]$Name){$checks.Add($Name);Write-Host "PASS $Name"}
function Key{[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($regName,$true)}
function TaskNames{$scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();@($scheduler.GetFolder('\').GetTasks(1) | Where-Object Name -Like 'YeoyangMessenger-*' | ForEach-Object Name | Sort-Object)}
function Setup([string]$Name,[string]$Components='office,admin',[switch]$Reject,[switch]$CreateIcons,[string]$Tasks='',[string]$InstallType=''){
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART',"/GROUP=$testGroup","/TASKS=$Tasks",('/DIR="'+$install+'"'),('/LOG="'+(Join-Path $work ($Name+'.log'))+'"'))
    if($InstallType){$arguments+="/TYPE=$InstallType"}else{$arguments+="/COMPONENTS=$Components"}
    if(-not $CreateIcons){$arguments+='/NOICONS'}
    $process=Start-Process -FilePath $testSetup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if($Reject){if($process.ExitCode -eq 0){throw 'Unsafe installation was accepted'}}elseif($process.ExitCode -ne 0){throw "Installation failed: $Name / $($process.ExitCode) / $work"}
}
function Uninstall([switch]$Reject){
    $process=Start-Process -FilePath (Join-Path $install 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
    if($Reject){if($process.ExitCode -eq 0){throw 'Unsafe uninstall was accepted'}}elseif($process.ExitCode -ne 0){throw "Uninstall failed: $($process.ExitCode)"}
}
function StartFixture([string]$Exe,[string]$Data){
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
    $url="http://127.0.0.1:$port"
    $old=@{};foreach($name in @('ASPNETCORE_ENVIRONMENT','School__DataDirectory','School__AdminPassword','DOTNET_PROCESSOR_COUNT')){$old[$name]=[Environment]::GetEnvironmentVariable($name)}
    try{
        $env:ASPNETCORE_ENVIRONMENT='Development';$env:School__DataDirectory=$Data;$env:School__AdminPassword='Synthetic-'+[guid]::NewGuid().ToString();$env:DOTNET_PROCESSOR_COUNT='2'
        $process=Start-Process -FilePath $Exe -ArgumentList @('--urls',$url) -WorkingDirectory (Split-Path $Exe -Parent) -RedirectStandardOutput ($Data+'-stdout.log') -RedirectStandardError ($Data+'-stderr.log') -WindowStyle Hidden -PassThru
        $owned.Add($process)
    }finally{foreach($name in $old.Keys){[Environment]::SetEnvironmentVariable($name,$old[$name])}}
    for($i=0;$i -lt 100;$i++){
        if($process.HasExited){throw "Synthetic server exited: $Data"}
        try{if((Invoke-WebRequest -UseBasicParsing -Uri ($url+'/health') -TimeoutSec 1).StatusCode -eq 200){return $process}}catch{}
        Start-Sleep -Milliseconds 100
    }
    throw 'Synthetic server did not start'
}
function StopFixture($Process){if(-not $Process.HasExited){$Process.Kill();$Process.WaitForExit()}}
try{
    $original=Join-Path $OutputDirectory "yymessenger-$version-server-setup-win-x64.exe"
    $expected=(Get-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') | Where-Object {$_ -match "  yymessenger-$([regex]::Escape($version))-server-setup-win-x64\.exe$"}).Split(' ')[0]
    if((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw 'Production server installer hash mismatch'}
    & (Join-Path $projectRoot 'scripts\Build-ServerInstaller.ps1') -OutputDirectory (Join-Path $work 'setup') -PackageDirectory $PackageDirectory -Compiler $Compiler -InstallerAppId "{$testGuid}"
    $testSetup=Join-Path $work "setup\yymessenger-$version-server-setup-win-x64.exe"
    $tasksBefore=@(TaskNames)
    Setup 'office' -CreateIcons -Tasks 'desktopicon,autostart'
    foreach($path in @('server\SchoolMessenger.Server.exe','admin\SchoolMessenger.exe','scripts\Server-Operations.ps1','config\server-operations.example.json','docs\server-installer.md')){
        if(-not(Test-Path -LiteralPath (Join-Path $install $path))){throw "Missing installed file: $path"}
    }
    if(Test-Path -LiteralPath (Join-Path $install 'announcement-server\SchoolMessenger.AnnouncementServer.exe')){throw 'Unselected public server was installed'}
    $key=Key;if($null -eq $key){throw 'Uninstall registration missing'};$key.Dispose()
    Check 'Office selection installs the self-contained server, administrator messenger and operating tools; unselected public server is absent'
    $shortcutPath=Join-Path ([Environment]::GetFolderPath('Programs')) ($testGroup+'\관리자 프로그램.lnk')
    if(-not(Test-Path -LiteralPath $shortcutPath)){throw 'Server manager shortcut missing'}
    # The hosted runner's WScript shortcut reader can return empty properties for
    # this existing Korean-named link; Explorer's Unicode link reader resolves it.
    $shellFolder=(New-Object -ComObject Shell.Application).NameSpace((Split-Path $shortcutPath -Parent))
    $shortcut=$shellFolder.ParseName((Split-Path $shortcutPath -Leaf)).GetLink
    $expectedTarget=Join-Path $install 'admin\SchoolMessenger.exe'
    if($shortcut.Path -ne $expectedTarget -or $shortcut.Arguments -ne '--server-manager'){
        $evidence=@{expectedTarget=$expectedTarget;target=[string]$shortcut.Path;arguments=[string]$shortcut.Arguments;shortcut=$shortcutPath;shortcutBytes=(Get-Item -LiteralPath $shortcutPath).Length}
        try{
            $legacy=(New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
            $evidence.wscriptTarget=[string]$legacy.TargetPath;$evidence.wscriptArguments=[string]$legacy.Arguments
        }catch{$evidence.wscriptInspectionError=$_.Exception.Message}
        $evidence|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'server-shortcut-failure.json') -Encoding UTF8
        throw ('Incorrect server manager shortcut: '+($evidence|ConvertTo-Json -Compress))
    }
    $desktop=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($startupEntry+'.lnk')
    if(-not(Test-Path -LiteralPath $desktop)){throw 'Selected desktop shortcut was not created'}
    $run=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run');try{if($run.GetValue($startupEntry) -ne ('"'+(Join-Path $install 'admin\SchoolMessenger.exe')+'" --server-manager --startup')){throw 'Selected startup option was not applied'}}finally{$run.Dispose()}
    Check 'Server manager opens before login; selected desktop and current-user startup options are applied without starting a backend'
    Setup 'startup-disabled' -CreateIcons
    $run=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run');try{if($null -ne $run.GetValue($startupEntry)){throw 'Deselected startup option was not removed'}}finally{$run.Dispose()}
    Check 'Deselecting startup removes only this test installation startup command'
    if($ShortcutOnly){
        Uninstall
        if(Test-Path -LiteralPath $shortcutPath){throw 'Uninstall left the trial shortcut behind'}
        if(Test-Path -LiteralPath $desktop){throw 'Uninstall left its desktop shortcut behind'}
        Setup 'public-manager' -Components 'announcements' -CreateIcons
        if(-not(Test-Path -LiteralPath (Join-Path $install 'announcement-server\SchoolMessenger.AnnouncementServer.exe')) -or -not(Test-Path -LiteralPath (Join-Path $install 'admin\SchoolMessenger.exe')) -or (Test-Path -LiteralPath (Join-Path $install 'server\SchoolMessenger.Server.exe'))){throw 'Public role manager payload is incorrect'}
        if(Test-Path -LiteralPath $desktop){throw 'Unselected desktop shortcut was created'}
        Check 'Public-only installation includes its server manager while unchecked desktop creation remains off'
        Uninstall
        Setup 'administrator-only' -InstallType 'administrator' -CreateIcons
        if(-not(Test-Path -LiteralPath (Join-Path $install 'admin\SchoolMessenger.exe')) -or (Test-Path -LiteralPath (Join-Path $install 'server\SchoolMessenger.Server.exe')) -or (Test-Path -LiteralPath (Join-Path $install 'announcement-server\SchoolMessenger.AnnouncementServer.exe')) -or -not(Test-Path -LiteralPath $shortcutPath)){throw 'Administrator-only installation incorrectly requires backend payloads'}
        Check 'Administrator-only custom installation has the management program and shortcut without installing either backend'
        $direct=Start-Process -FilePath (Join-Path $install 'admin\SchoolMessenger.exe') -ArgumentList @('--configure-startup','enable','--startup-entry-name',$startupEntry) -WindowStyle Hidden -Wait -PassThru
        if($direct.ExitCode -ne 0){throw 'Direct administrator executable startup configuration failed'}
        $run=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run');try{if($run.GetValue($startupEntry) -ne ('"'+(Join-Path $install 'admin\SchoolMessenger.exe')+'" --server-manager --startup')){throw 'Direct administrator executable was treated as a teacher messenger'}}finally{$run.Dispose()}
        Check 'Installed administrator executable identifies its own program mode without a launcher flag and keeps its separate startup entry'
        Uninstall
        if((@($tasksBefore) -join "`n") -ne (@(TaskNames) -join "`n")){throw 'Installer changed operational tasks'}
        Check 'Shortcut-only verification removes its isolated test installation and leaves operating tasks unchanged'
        @{version=$version;checks=@($checks);realSchoolData=$false;serverStarted=$false}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $OutputDirectory 'server-launcher-checks.json') -Encoding UTF8
        return
    }
    $localConfig=Join-Path $install 'server\appsettings.Production.json';Set-Content -LiteralPath $localConfig -Value '{"SyntheticPreserved":true}' -Encoding UTF8
    $sentinel=Join-Path $install 'data\fake-attachment.txt';New-Item -ItemType Directory -Path (Split-Path $sentinel -Parent) | Out-Null;Set-Content -LiteralPath $sentinel -Value 'Synthetic retained attachment'
    $outside=Join-Path $work 'operator-secrets.xml';Set-Content -LiteralPath $outside -Value 'SYNTHETIC NOT A REAL SECRET'
    $retained=@($localConfig,$sentinel,$outside);$hashes=@{};foreach($file in $retained){$hashes[$file]=(Get-FileHash -LiteralPath $file).Hash}
    Setup 'update'
    foreach($file in $retained){if((Get-FileHash -LiteralPath $file).Hash -ne $hashes[$file]){throw 'Update modified retained data/configuration'}}
    Check 'Update preserves added production configuration, unmanaged attachments and external operator secrets'
    $key=Key;$key.SetValue('DisplayVersion','9.0.0');$key.Dispose();Setup 'downgrade' -Reject
    $key=Key;$key.SetValue('DisplayVersion',$version);$key.Dispose()
    Check 'A numerically newer installed server cannot be overwritten by this installer'
    $data=Join-Path $work 'synthetic-office-data'
    $running=StartFixture (Join-Path $install 'server\SchoolMessenger.Server.exe') $data
    Setup 'running-block' -Reject
    $log=[IO.File]::ReadAllText((Join-Path $work 'running-block.log'))
    if($log -notmatch '실행 중'){throw 'Update failure did not identify the running server'}
    Uninstall -Reject
    if(-not(Test-Path -LiteralPath (Join-Path $install 'server\SchoolMessenger.Server.exe'))){throw 'Running server was removed'}
    StopFixture $running
    $db=Join-Path $data 'school.db';$dbHash=(Get-FileHash -LiteralPath $db).Hash
    Check 'The installed server actually starts on loopback; running-server updates and removal are rejected without killing it'
    $unrelated=StartFixture (Join-Path $PackageDirectory 'server\SchoolMessenger.Server.exe') (Join-Path $work 'unrelated-office-data')
    Setup 'unrelated-server'
    if($unrelated.HasExited){throw 'Installer stopped an unrelated server'}
    StopFixture $unrelated
    Check 'A school server executing from a different directory does not block installation and is not stopped'
    Uninstall
    foreach($file in $retained){if((Get-FileHash -LiteralPath $file).Hash -ne $hashes[$file]){throw 'Uninstall removed retained data/configuration'}}
    if((Get-FileHash -LiteralPath $db).Hash -ne $dbHash){throw 'Uninstall changed the operating database'}
    if(Test-Path -LiteralPath (Join-Path $install 'server\SchoolMessenger.Server.exe')){throw 'Owned executable was not removed'}
    Check 'Uninstall removes program files and retains the real synthetic DB, attachments, added configuration and external secrets'
    Setup 'public-only' -Components 'announcements'
    if(-not(Test-Path -LiteralPath (Join-Path $install 'announcement-server\SchoolMessenger.AnnouncementServer.exe')) -or (Test-Path -LiteralPath (Join-Path $install 'server\SchoolMessenger.Server.exe')) -or -not(Test-Path -LiteralPath (Join-Path $install 'admin\SchoolMessenger.exe'))){throw 'Public-only selection must include its local server manager'}
    Uninstall
    $tasksAfter=@(TaskNames)
    if(($tasksBefore -join "`n") -ne ($tasksAfter -join "`n")){throw 'Installer changed operational scheduled tasks'}
    Check 'Public-only installation and reinstallation work; no server tasks are registered or changed'
    @{version=$version;verifiedAt=[DateTimeOffset]::UtcNow.ToString('o');checks=@($checks);testDirectory=$work;realSchoolData=$false;tasksRegistered=$false;productionElevationTested=$false}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $OutputDirectory 'server-installer-checks.json') -Encoding UTF8
}finally{foreach($process in $owned){StopFixture $process}}
