param(
    [string]$OutputDirectory,
    [string]$ClientDirectory,
    [string]$Compiler,
    [ValidatePattern('^\{[0-9A-Fa-f-]{36}\}$')][string]$InstallerAppId
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Install-WinSparkle.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$properties = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
$version = [string]$properties.Project.PropertyGroup.Version
$notes = Get-Content -LiteralPath (Join-Path $projectRoot 'src\SchoolMessenger.Shared\updates.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($version -ne $notes.version) { throw 'Installer version must match bundled update notes.' }
if (-not $ClientDirectory) {
    $ClientDirectory = Join-Path $OutputDirectory 'installer-client'
    $localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet) { $dotnetExecutable = $localDotnet } else { $dotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source }
    & $dotnetExecutable publish (Join-Path $projectRoot 'src\SchoolMessenger.Desktop') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:RestoreLockedMode=true "-p:Version=$version" -o $ClientDirectory --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
}
$ClientDirectory = [IO.Path]::GetFullPath($ClientDirectory)
$executable = Join-Path $ClientDirectory 'SchoolMessenger.exe'
if (-not (Test-Path -LiteralPath $executable) -or (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion.Split('+')[0] -ne $version) { throw 'Published desktop executable/version mismatch.' }
# Reject private data and server executables before recursive packaging.
$privateFiles = Get-ChildItem -LiteralPath $ClientDirectory -Recurse -File | Where-Object { $_.Name -match '(?i)(\.db($|-)|\.sqlite3?($|-)|\.dpapi$|\.pfx$|\.pem$|\.key$|^login\.dat$|^\.env|^preferences\.json$|^draft|^appsettings\.|^secrets\.json$|SchoolMessenger\.(Server|AnnouncementServer)\.exe$)' }
if ($privateFiles) { throw 'Private data or server configuration found in installer input.' }
if (-not $Compiler) { $Compiler = & (Join-Path $PSScriptRoot 'Install-InnoCompiler.ps1') }
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Inno Setup compiler unavailable.' }
$arguments = @("/DAppVersion=$version", "/DClientDirectory=$ClientDirectory", "/DArtifactDirectory=$OutputDirectory")
if ($InstallerAppId) { $arguments += @("/DInstallerAppId=$InstallerAppId", "/DStartupEntryName=YyMessengerInstallerTest-$($InstallerAppId.Trim('{}'))", "/DDesktopLinkName=YyMessengerInstallerTest-$($InstallerAppId.Trim('{}'))") }
& $Compiler @arguments (Join-Path $projectRoot 'installer\SchoolMessenger.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $OutputDirectory "yymessenger-$version-setup-win-x64.exe"
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$hashes = Join-Path $OutputDirectory 'SHA256SUMS.txt'
$lines = @(); if (Test-Path -LiteralPath $hashes) { $lines = @(Get-Content -LiteralPath $hashes | Where-Object { -not $_.EndsWith("  yymessenger-$version-setup-win-x64.exe") }) }
Set-Content -LiteralPath $hashes -Value @($lines + "$hash  $(Split-Path $setup -Leaf)") -Encoding ASCII
Write-Host "Installer: $setup"
