param(
    [Parameter(Mandatory=$true)][string]$ConfigurationPath,
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedSha256,
    [Parameter(Mandatory=$true)][string]$VersionDirectory,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Server-Operations.ps1') -ConfigurationPath $ConfigurationPath
$config=Read-OperationsConfig $ConfigurationPath
if((Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash -ne $ExpectedSha256){throw '업데이트 ZIP의 체크섬이 일치하지 않습니다.'}
$target=[IO.Path]::GetFullPath($VersionDirectory)
if(-not [IO.Path]::IsPathRooted($VersionDirectory) -or $target.TrimEnd('\') -eq [IO.Path]::GetPathRoot($target).TrimEnd('\')){throw '새 버전 전용 전체 경로가 필요합니다.'}
foreach($protected in @($config.dataDirectory,$config.backupDirectory,(Split-Path $config.executable -Parent),(Split-Path ([IO.Path]::GetFullPath($ConfigurationPath)) -Parent))){
    $left=$target.TrimEnd('\')+'\';$right=$protected.TrimEnd('\')+'\'
    if($left.StartsWith($right,[StringComparison]::OrdinalIgnoreCase) -or $right.StartsWith($left,[StringComparison]::OrdinalIgnoreCase)){throw '버전 폴더는 기존 실행·데이터·백업·설정 폴더와 분리하세요.'}
}
if(Test-Path -LiteralPath $target){throw '기존 폴더를 덮어쓰지 않습니다. 새 버전 폴더를 지정하세요.'}
for($parent=Split-Path $target -Parent;$parent;$parent=Split-Path $parent -Parent){if((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '업데이트 경로에 링크를 사용할 수 없습니다.'}}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Package))
try{
    $role=if($config.service -eq 'office'){'server'}else{'announcement-server'}
    $exe=if($config.service -eq 'office'){'SchoolMessenger.Server.exe'}else{'SchoolMessenger.AnnouncementServer.exe'}
    $main=@($archive.Entries|Where-Object {$_.FullName.Replace('\','/').EndsWith($role+'/'+$exe,[StringComparison]::Ordinal)})
    if($main.Count -ne 1){throw '선택한 서버 종류의 실행 파일이 정확히 하나 있어야 합니다.'}
    $base=$main[0].FullName.Replace('\','/');$base=$base.Substring(0,$base.Length-$exe.Length)
    $entries=@($archive.Entries|Where-Object {$_.FullName.Replace('\','/').StartsWith($base,[StringComparison]::Ordinal) -and $_.Name})
    if($entries.Count -gt 20000 -or ($entries|Measure-Object Length -Sum).Sum -gt 2GB){throw '업데이트 패키지가 허용 크기를 초과했습니다.'}
    $destinations=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $entries){
        $relative=$entry.FullName.Replace('\','/').Substring($base.Length)
        if($relative -match '(^|/)\.\.(/|$)|:|(^|/)(data|portal-data|keys|backups)/|\.db($|-)|\.sqlite3?$|\.(pfx|pem|key|dpapi)$|(^|/)(server-operations.*|appsettings\.Production\.json|\.env.*)$'){throw '데이터·비밀 파일 또는 잘못된 경로가 포함된 패키지입니다.'}
        $unixType=($entry.ExternalAttributes -shr 16) -band 0xF000
        if($unixType -eq 0xA000){throw '링크가 포함된 패키지를 사용할 수 없습니다.'}
        $destination=[IO.Path]::GetFullPath((Join-Path $target $relative))
        if(-not $destination.StartsWith($target.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $destinations.Add($destination)){throw '중복되거나 범위를 벗어난 패키지 경로입니다.'}
    }
    if(-not $Apply){Write-Host '체크섬·서버 종류·추출 경로를 확인했습니다. 서버를 정지한 후 -Apply로 실행하세요.';return}
    # The offline backup refuses a running server before changing any version/configuration files.
    & (Join-Path $PSScriptRoot 'Server-Operations.ps1') -Mode Backup -ConfigurationPath $ConfigurationPath
    New-Item -ItemType Directory -Path $target | Out-Null
    foreach($entry in $entries){
        $destination=Join-Path $target $entry.FullName.Replace('\','/').Substring($base.Length)
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$destination,$false)
    }
    $newExe=Join-Path $target $exe
    $oldVersion=[version]([Diagnostics.FileVersionInfo]::GetVersionInfo($config.executable).FileVersion)
    $newVersion=[version]([Diagnostics.FileVersionInfo]::GetVersionInfo($newExe).FileVersion)
    if($newVersion -lt $oldVersion){throw '이전 버전으로 자동 교체하지 않습니다.'}
    $previousConfig=Join-Path (Split-Path $ConfigurationPath -Parent) 'server-operations.previous.local.json'
    if(Test-Path -LiteralPath $previousConfig){throw '이전 교체 기록을 검토한 후 새 업데이트를 진행하세요.'}
    Copy-Item -LiteralPath $ConfigurationPath -Destination $previousConfig
    $config.executable=$newExe
    $temporary=$ConfigurationPath+'.tmp';$config|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $ConfigurationPath -Force
    Write-Host '백업 검증 후 실행 경로를 교체했습니다. 서버 시작·접속 검증은 운영자가 진행하세요. 데이터·키·백업 경로는 유지됩니다.'
}finally{$archive.Dispose()}
