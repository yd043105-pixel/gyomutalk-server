param(
    [switch]$SkipTests,
    [switch]$HeadlessTests,
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Install-WinSparkle.ps1')
$releaseNotes = Get-Content -LiteralPath (Join-Path $projectRoot 'src\SchoolMessenger.Shared\updates.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $Version) { $Version = $releaseNotes.version }
if ($Version -ne $releaseNotes.version) { throw 'Release version must match the bundled updates.json version.' }
$buildProperties = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
if ($Version -ne $buildProperties.Project.PropertyGroup.Version) { throw 'Release version must match Directory.Build.props. Update both together for each release.' }
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) { $dotnetExecutable = $localDotnet }
else { $dotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ROOT = Split-Path $dotnetExecutable -Parent
Push-Location $projectRoot
try {
    foreach ($project in @('Server','Desktop')) {
        & $dotnetExecutable restore "src\SchoolMessenger.$project" --locked-mode --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "$project 패키지 잠금 검증 실패" }
        & $dotnetExecutable build "src\SchoolMessenger.$project" "-p:Version=$Version" --no-restore --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "$project 빌드 실패" }
    }
    if (-not $SkipTests) {
        foreach ($test in @('chat-survey','message-storage','timetable-neis','discovery-listener','file-scan')) {
            node "tests/$test.mjs"
            if ($LASTEXITCODE -ne 0) { throw "PC verification failed: $test" }
        }
        foreach ($test in @('IdleChecks','DiscoveryChecks','TaskChecks','PcOnlyChecks')) {
            & $dotnetExecutable run --project "tests/$test" -p:RestoreLockedMode=true
            if ($LASTEXITCODE -ne 0) { throw "Verification failed: $test" }
        }
    }
    $packageDirectory = Join-Path $projectRoot 'artifacts\gyomutalk-package'
    New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null
    if (Test-Path -LiteralPath (Join-Path $packageDirectory 'data')) { throw '배포 폴더에 운영 데이터가 있습니다. 데이터를 별도 경로로 옮긴 뒤 빌드하세요.' }
    # Publish does not remove old assets. Clean only these owned build folders,
    # after rejecting links and private files, so deleted UI assets cannot ship again.
    $packageEntries=@(Get-ChildItem -LiteralPath $packageDirectory -Recurse -Force)
    if((Get-Item -LiteralPath $packageDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint -or
       @($packageEntries | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw '배포 폴더의 링크를 제거한 뒤 빌드하세요.'}
    if(@($packageEntries | Where-Object {$_.Name -match '(?i)(\.db($|-)|\.sqlite3?($|-)|\.dpapi$|\.pfx$|\.pem$|\.key$|^login\.dat$|^\.env|^appsettings\.Production\.json$|^server-operations\.secrets\.xml$|^server-operations.*\.local\.json$|^preferences\.json$|^school-request-|^school-registration-decision-|^draft.*\.json$)'}).Count){throw '배포 폴더에 운영 데이터 또는 비밀 파일이 있습니다. 정리를 중단합니다.'}
    foreach($folder in @('server','client','announcement-server','scripts','config','docs')){
        $generated=[IO.Path]::GetFullPath((Join-Path $packageDirectory $folder))
        if(-not $generated.StartsWith([IO.Path]::GetFullPath($packageDirectory)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '배포 정리 경로 오류'}
        if(Test-Path -LiteralPath $generated){Remove-Item -LiteralPath $generated -Recurse -Force}
    }
    & $dotnetExecutable publish src\SchoolMessenger.Server -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:RestoreLockedMode=true -o (Join-Path $packageDirectory 'server') --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw '서버 배포 파일 생성 실패' }
    & $dotnetExecutable publish src\SchoolMessenger.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true "-p:Version=$Version" -p:RestoreLockedMode=true -o (Join-Path $packageDirectory 'client') --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw '교사용 배포 파일 생성 실패' }
    foreach($role in ,@('server','SchoolMessenger.Server')){
        $folder=Join-Path $packageDirectory $role[0]
        @{schema=1;deploymentReviewCheck=1;executableSha256=(Get-FileHash -LiteralPath (Join-Path $folder ($role[1]+'.exe')) -Algorithm SHA256).Hash;assemblySha256=(Get-FileHash -LiteralPath (Join-Path $folder ($role[1]+'.dll')) -Algorithm SHA256).Hash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $folder 'server-capabilities.json') -Encoding UTF8
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $packageDirectory 'scripts') | Out-Null
    Copy-Item -LiteralPath 'scripts\Start-Server.ps1','scripts\Configure-RemoteFirewall.ps1' -Destination (Join-Path $packageDirectory 'scripts')
    Copy-Item -LiteralPath 'scripts\Server-Operations.ps1','scripts\Configure-ServerOperations.ps1','scripts\Inspect-ServerComputer.ps1','scripts\Stage-ServerUpdate.ps1' -Destination (Join-Path $packageDirectory 'scripts')
    Copy-Item -LiteralPath 'LICENSE','THIRD-PARTY-NOTICES.md','installer\launchers\시작-로컬체험.cmd','installer\launchers\시작-교사용.cmd' -Destination $packageDirectory
    $packageDocs = Join-Path $packageDirectory 'docs'
    New-Item -ItemType Directory -Force -Path $packageDocs | Out-Null
    Copy-Item -LiteralPath 'docs\public\00_먼저읽기.md' -Destination (Join-Path $packageDirectory 'README.md')
    # Public packages contain curated user manuals, not historical school-specific reports.
    Get-ChildItem -LiteralPath 'docs\public' -File -Filter '*.md' | Copy-Item -Destination $packageDocs
    Copy-Item -LiteralPath 'docs\public\installer-note.txt' -Destination (Join-Path $packageDocs 'server-installer.md')
    Copy-Item -LiteralPath 'docs\public\학교도입_내부기안_작성양식.txt' -Destination $packageDocs
    Copy-Item -LiteralPath 'docs\public\교육현장_운영고지.txt' -Destination $packageDocs
    Copy-Item -LiteralPath 'docs\policies' -Destination $packageDocs -Recurse -Force
    $packageConfig=Join-Path $packageDirectory 'config'
    New-Item -ItemType Directory -Path $packageConfig -Force | Out-Null
    Copy-Item -LiteralPath 'config\registration-policy.draft.json' -Destination $packageConfig
    Copy-Item -LiteralPath 'config\deployment-review.draft.json' -Destination $packageConfig
    Copy-Item -LiteralPath 'config\server-operations.example.json' -Destination $packageConfig
    & (Join-Path $PSScriptRoot 'Test-DeploymentReviewPackage.ps1') -PackageDirectory $packageDirectory
    Set-Content -LiteralPath (Join-Path $packageDirectory 'VERSION.txt') -Value $Version -Encoding ASCII
    $privateFiles = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object { $_.Name -match '(?i)(\.db($|-)|\.sqlite3?($|-)|\.dpapi$|\.pfx$|\.pem$|\.key$|^login\.dat$|^\.env|^appsettings\.Production\.json$|^server-operations\.secrets\.xml$|^server-operations.*\.local\.json$|^preferences\.json$|^school-request-|^school-registration-decision-|^draft.*\.json$)' }
    if ($privateFiles) { throw '배포 폴더에 데이터 또는 비밀 파일이 있습니다. 압축을 중단합니다.' }
    Compress-Archive -LiteralPath $packageDirectory -DestinationPath 'artifacts\gyomutalk-package.zip' -Force
    $releaseZip = Join-Path $projectRoot "artifacts\yymessenger-$Version-win-x64.zip"
    Copy-Item -LiteralPath 'artifacts\gyomutalk-package.zip' -Destination $releaseZip -Force
    # Server-only ZIP contains the office server and its operating tools.
    $serverZip=Join-Path $projectRoot "artifacts\yymessenger-$Version-servers-win-x64.zip"
    $serverEntries=@('server','scripts','config','docs','VERSION.txt','LICENSE','THIRD-PARTY-NOTICES.md') | ForEach-Object {Join-Path $packageDirectory $_}
    Compress-Archive -LiteralPath $serverEntries -DestinationPath $serverZip -Force
    $hash = (Get-FileHash -LiteralPath $releaseZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $serverHash=(Get-FileHash -LiteralPath $serverZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums=@("$hash  yymessenger-$Version-win-x64.zip","$serverHash  yymessenger-$Version-servers-win-x64.zip")
    $installer=Join-Path $projectRoot "artifacts\yymessenger-$Version-setup-win-x64.exe"
    if(Test-Path -LiteralPath $installer){$checksums+=((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()+"  yymessenger-$Version-setup-win-x64.exe")}
    $serverInstaller=Join-Path $projectRoot "artifacts\yymessenger-$Version-server-setup-win-x64.exe"
    if(Test-Path -LiteralPath $serverInstaller){$checksums+=((Get-FileHash -LiteralPath $serverInstaller -Algorithm SHA256).Hash.ToLowerInvariant()+"  yymessenger-$Version-server-setup-win-x64.exe")}
    Set-Content -LiteralPath 'artifacts\SHA256SUMS.txt' -Value $checksums -Encoding ASCII
    Write-Host '완료: artifacts\gyomutalk-package.zip'
} finally { Pop-Location }
