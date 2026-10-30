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
if ($Version -ne $buildProperties.Project.PropertyGroup.Version) { throw 'Release version must match Directory.Build.props. Keep 1.0.0 until the first release is authorized.' }
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) { $dotnetExecutable = $localDotnet }
else { $dotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ROOT = Split-Path $dotnetExecutable -Parent
Push-Location $projectRoot
try {
    foreach ($project in @('Server','Desktop','AnnouncementServer')) {
        & $dotnetExecutable restore "src\SchoolMessenger.$project" --locked-mode --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "$project 패키지 잠금 검증 실패" }
        & $dotnetExecutable build "src\SchoolMessenger.$project" "-p:Version=$Version" --no-restore --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "$project 빌드 실패" }
    }
    if (-not $SkipTests) {
        node tests\message-storage.mjs
        if ($LASTEXITCODE -ne 0) { throw 'PC message storage verification failed' }
        node tests\school-directory-servers.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Separate school server identity and session isolation verification failed' }
        & $dotnetExecutable run --project tests\IdleChecks -p:RestoreLockedMode=true
        if ($LASTEXITCODE -ne 0) { throw 'Windows inactivity deadline verification failed' }
        node tests\counseling.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Counseling integration verification failed' }
        if ($HeadlessTests) { node tests\electives.mjs }
        else { node tests\electives.mjs --ui }
        if ($LASTEXITCODE -ne 0) { throw 'Elective registration permissions and workflow verification failed' }
        node tests\server-resources.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Server resource control verification failed' }
        node tests\feedback.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Feedback privacy and permissions verification failed' }
        node tests\feedback-mail.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Feedback email verification failed' }
        node tests\feedback-collection.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Feedback collection verification failed' }
        node tests\release-policy.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Release policy verification failed' }
        if (-not $HeadlessTests) {
            node tests\administrator-ui.mjs
            if ($LASTEXITCODE -ne 0) { throw 'Native administrator interface verification failed' }
        }
        node tests\server-operations.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Server operations verification failed' }
        node tests\update-notice.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Update notice verification failed' }
        node tests\remote-channel.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Remote test channel verification failed' }
        & powershell -NoProfile -ExecutionPolicy Bypass -File tests\remote-firewall.ps1
        if ($LASTEXITCODE -ne 0) { throw '원격 지원 방화벽 스크립트 검증 실패' }
        if ($HeadlessTests) { node tests\smoke.mjs --features }
        else { node tests\smoke.mjs --ui }
        if ($LASTEXITCODE -ne 0) { throw '송수신 검증 실패' }
        node tests\announcements.mjs
        if ($LASTEXITCODE -ne 0) { throw 'External announcement verification failed' }
        node tests\enrollment.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Student and parent enrollment verification failed' }
        node tests\registration-policy.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Registration agreement verification failed' }
        node tests\graduation.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Graduation erasure verification failed' }
        node tests\announcement-bridge.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Announcement bridge verification failed' }
        node tests\backend-hardening.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Backend hardening verification failed' }
        node tests\security-logging.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Privacy-safe request logging verification failed' }
        node tests\data-boundaries.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Server data isolation verification failed' }
        node tests\deployment-review.mjs
        if ($LASTEXITCODE -ne 0) { throw 'School deployment review and destination controls verification failed' }
        & (Join-Path $projectRoot 'tests\deployment-review-package.ps1') -FixtureRoot (Join-Path $projectRoot '.test-data')
        & $dotnetExecutable build tests\TimetableChecks -p:RestoreLockedMode=true --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Timetable checks build failed' }
        & $dotnetExecutable run --project tests\DiscoveryChecks -p:RestoreLockedMode=true --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Server discovery certificate and packet checks failed' }
        node tests\timetable.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Timetable import and replica verification failed' }
    }
    $packageDirectory = Join-Path $projectRoot 'artifacts\여양고-교무메신저'
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
    & $dotnetExecutable publish src\SchoolMessenger.AnnouncementServer -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:RestoreLockedMode=true -o (Join-Path $packageDirectory 'announcement-server') --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'External announcement package failed' }
    foreach($role in @(@('server','SchoolMessenger.Server'),@('announcement-server','SchoolMessenger.AnnouncementServer'))){
        $folder=Join-Path $packageDirectory $role[0]
        @{schema=1;deploymentReviewCheck=1;executableSha256=(Get-FileHash -LiteralPath (Join-Path $folder ($role[1]+'.exe')) -Algorithm SHA256).Hash;assemblySha256=(Get-FileHash -LiteralPath (Join-Path $folder ($role[1]+'.dll')) -Algorithm SHA256).Hash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $folder 'server-capabilities.json') -Encoding UTF8
    }
    if(-not $SkipTests){
        node tests\security-logging.mjs --package
        if($LASTEXITCODE -ne 0){throw 'Packaged privacy-safe logging verification failed'}
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $packageDirectory 'scripts') | Out-Null
    Copy-Item -LiteralPath 'scripts\Start-Server.ps1','scripts\Start-AnnouncementServer.ps1','scripts\Configure-RemoteFirewall.ps1' -Destination (Join-Path $packageDirectory 'scripts')
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
    Copy-Item -LiteralPath 'config\server-operations.example.json','config\cloudflared.example.yml' -Destination $packageConfig
    & (Join-Path $PSScriptRoot 'Test-DeploymentReviewPackage.ps1') -PackageDirectory $packageDirectory
    & (Join-Path $projectRoot 'tests\deployment-review-operations.ps1') -PackageDirectory $packageDirectory
    Set-Content -LiteralPath (Join-Path $packageDirectory 'VERSION.txt') -Value $Version -Encoding ASCII
    $privateFiles = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object { $_.Name -match '(?i)(\.db($|-)|\.sqlite3?($|-)|\.dpapi$|\.pfx$|\.pem$|\.key$|^login\.dat$|^\.env|^appsettings\.Production\.json$|^server-operations\.secrets\.xml$|^server-operations.*\.local\.json$|^preferences\.json$|^school-request-|^school-registration-decision-|^draft.*\.json$)' }
    if ($privateFiles) { throw '배포 폴더에 데이터 또는 비밀 파일이 있습니다. 압축을 중단합니다.' }
    Compress-Archive -LiteralPath $packageDirectory -DestinationPath 'artifacts\여양고-교무메신저.zip' -Force
    $releaseZip = Join-Path $projectRoot "artifacts\yymessenger-$Version-win-x64.zip"
    Copy-Item -LiteralPath 'artifacts\여양고-교무메신저.zip' -Destination $releaseZip -Force
    # Server-only ZIP has server/, announcement-server/, scripts/, config/, docs/ at its root.
    $serverZip=Join-Path $projectRoot "artifacts\yymessenger-$Version-servers-win-x64.zip"
    $serverEntries=@('server','announcement-server','scripts','config','docs','VERSION.txt','LICENSE','THIRD-PARTY-NOTICES.md') | ForEach-Object {Join-Path $packageDirectory $_}
    Compress-Archive -LiteralPath $serverEntries -DestinationPath $serverZip -Force
    $hash = (Get-FileHash -LiteralPath $releaseZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $serverHash=(Get-FileHash -LiteralPath $serverZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums=@("$hash  yymessenger-$Version-win-x64.zip","$serverHash  yymessenger-$Version-servers-win-x64.zip")
    $installer=Join-Path $projectRoot "artifacts\yymessenger-$Version-setup-win-x64.exe"
    if(Test-Path -LiteralPath $installer){$checksums+=((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()+"  yymessenger-$Version-setup-win-x64.exe")}
    $serverInstaller=Join-Path $projectRoot "artifacts\yymessenger-$Version-server-setup-win-x64.exe"
    if(Test-Path -LiteralPath $serverInstaller){$checksums+=((Get-FileHash -LiteralPath $serverInstaller -Algorithm SHA256).Hash.ToLowerInvariant()+"  yymessenger-$Version-server-setup-win-x64.exe")}
    Set-Content -LiteralPath 'artifacts\SHA256SUMS.txt' -Value $checksums -Encoding ASCII
    Write-Host '완료: artifacts\여양고-교무메신저.zip'
} finally { Pop-Location }
