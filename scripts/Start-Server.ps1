param(
    [switch]$LocalTest,
    [string]$CertificateThumbprint,
    [string]$AllowedNetworks,
    [string]$DataDirectory,
    [string]$BackupDirectory,
    [int]$Port = 0
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$serverExe = Join-Path $projectRoot 'server\SchoolMessenger.Server.exe'
if (-not (Test-Path -LiteralPath $serverExe)) { throw '서버 실행 파일이 없습니다. 먼저 scripts\Build.ps1을 실행하세요.' }
if (-not $DataDirectory) { $DataDirectory = Join-Path $projectRoot 'data' }
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
if (-not $Port) { if ($LocalTest) { $Port = 5080 } else { $Port = 7443 } }
if (-not $LocalTest -and (-not $CertificateThumbprint -or -not $AllowedNetworks)) {
    throw '학교 운영에는 -CertificateThumbprint와 -AllowedNetworks가 필요합니다. 이 PC 체험은 -LocalTest를 사용하세요.'
}
if (-not (Test-Path -LiteralPath (Join-Path $DataDirectory '.initialized'))) {
    $initialPassword = Read-Host '최초 관리자(admin) 비밀번호 입력: 12~128자' -AsSecureString
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($initialPassword)
    try { $env:School__AdminPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer) }
}
$env:School__DataDirectory = $DataDirectory
if ($BackupDirectory) { $env:School__BackupDirectory = [IO.Path]::GetFullPath($BackupDirectory) }
if ($LocalTest) {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $serverUrl = "http://localhost:$Port"
    Write-Host '로컬 체험 모드: 이 PC만 접속 가능. 악성코드 검사는 생략됩니다.'
} else {
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:School__CertificateThumbprint = $CertificateThumbprint
    $env:School__AllowedNetworks = $AllowedNetworks
    $serverUrl = "https://0.0.0.0:$Port"
}
Write-Host "서버 시작: $serverUrl"
Write-Host '종료: Ctrl+C. 관리자 화면: PC 메신저에서 이 서버 주소로 관리자 계정에 로그인하세요.'
Push-Location (Split-Path $serverExe)
try { & $serverExe --urls $serverUrl; if ($LASTEXITCODE -ne 0) { throw "서버 종료 코드: $LASTEXITCODE" } }
finally { Pop-Location; Remove-Item Env:School__AdminPassword -ErrorAction SilentlyContinue }
