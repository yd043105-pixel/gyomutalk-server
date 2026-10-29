param([string]$OutputDirectory,[string]$PackageDirectory,[string]$Compiler,[ValidatePattern('^\{[0-9A-Fa-f-]{36}\}$')][string]$InstallerAppId)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if(-not $OutputDirectory){$OutputDirectory=Join-Path $projectRoot 'artifacts'}
if(-not $PackageDirectory){$PackageDirectory=Join-Path $projectRoot 'artifacts\여양고-교무메신저'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$PackageDirectory=[IO.Path]::GetFullPath($PackageDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$version=[string]([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if((Get-Content -LiteralPath (Join-Path $PackageDirectory 'VERSION.txt') -Raw).Trim() -ne $version){throw 'Server package version mismatch. Build the current package first.'}
foreach($exe in @('server\SchoolMessenger.Server.exe','announcement-server\SchoolMessenger.AnnouncementServer.exe','client\SchoolMessenger.exe')){
    $file=Get-Item -LiteralPath (Join-Path $PackageDirectory $exe)
    if($file.VersionInfo.ProductVersion.Split('+')[0] -ne $version){throw "Package executable version mismatch: $exe"}
}
$entries=@(Get-ChildItem -LiteralPath $PackageDirectory -Recurse -Force)
if(((Get-Item -LiteralPath $PackageDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint) -or @($entries | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Linked installer input is not allowed.'}
if(@($entries | Where-Object {$_.Name -match '(?i)(\.db($|-)|\.sqlite3?($|-)|\.dpapi$|\.pfx$|\.pem$|\.key$|\.p12$|\.jks$|\.keystore$|^\.env|\.local\.json$|\.secrets\.xml$|^login\.dat$|^preferences\.json$|^school-request-|^school-registration-decision-|^draft|^secrets\.json$|^appsettings\.Production\.json$)'}).Count){throw 'Operational data or secrets found in server installer input.'}
& (Join-Path $PSScriptRoot 'Test-DeploymentReviewPackage.ps1') -PackageDirectory $PackageDirectory
if(-not $Compiler){$Compiler=& (Join-Path $PSScriptRoot 'Install-InnoCompiler.ps1')}
$arguments=@('/Q',"/DAppVersion=$version","/DPackageDirectory=$PackageDirectory","/DArtifactDirectory=$OutputDirectory")
if($InstallerAppId){$arguments+=@("/DInstallerAppId=$InstallerAppId",'/DInstallerTest=1',('/DStartupEntryName=YyMessengerInstallerTest-'+$InstallerAppId.Trim('{}')),('/DDesktopLinkName=YyMessengerInstallerTest-'+$InstallerAppId.Trim('{}')))}
& $Compiler @arguments (Join-Path $projectRoot 'installer\SchoolMessengerServer.iss')
if($LASTEXITCODE -ne 0){throw 'Server installer compilation failed.'}
$name="yymessenger-$version-server-setup-win-x64.exe"
$hash=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
$hashFile=Join-Path $OutputDirectory 'SHA256SUMS.txt'
$lines=@();if(Test-Path -LiteralPath $hashFile){$lines=@(Get-Content -LiteralPath $hashFile | Where-Object {$_ -notmatch '  yymessenger-.*-server-setup-win-x64\.exe$'})}
Set-Content -LiteralPath $hashFile -Value @($lines+"$hash  $name") -Encoding ASCII
Write-Host "Server installer: $(Join-Path $OutputDirectory $name)"
