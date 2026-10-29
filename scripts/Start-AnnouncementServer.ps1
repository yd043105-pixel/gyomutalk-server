param([switch]$LocalTest,[string]$CertificateThumbprint,[string]$PublicHost,[string]$DataDirectory,[string]$BackupDirectory,[ValidateRange(1,65535)][int]$Port=8443)
$ErrorActionPreference='Stop'
$packageRoot=Split-Path $PSScriptRoot -Parent
$serverExecutable=Join-Path $packageRoot 'announcement-server\SchoolMessenger.AnnouncementServer.exe'
if(-not(Test-Path -LiteralPath $serverExecutable)){throw 'Build the announcement server package first.'}
if(-not $DataDirectory){$DataDirectory=Join-Path $packageRoot 'portal-data'}
$DataDirectory=[IO.Path]::GetFullPath($DataDirectory)
if(-not $LocalTest -and (-not $CertificateThumbprint -or -not $PublicHost)){throw 'Production requires CertificateThumbprint and PublicHost.'}
function Read-Secret([string]$prompt){$secureValue=Read-Host $prompt -AsSecureString;$pointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue);try{return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)}}
if(-not(Test-Path -LiteralPath (Join-Path $DataDirectory '.initialized'))){$env:Portal__AdminPassword=Read-Secret 'Initial announcement administrator password (12-128 characters)'}
if(-not $env:Portal__BridgeKey){$env:Portal__BridgeKey=Read-Secret 'Dedicated announcement bridge key (at least 32 random characters; same value on school server)'}
$env:Portal__DataDirectory=$DataDirectory
if($BackupDirectory){$env:Portal__BackupDirectory=[IO.Path]::GetFullPath($BackupDirectory)}
if($LocalTest){$env:ASPNETCORE_ENVIRONMENT='Development';$serverUrl="http://127.0.0.1:$Port";$env:Portal__AllowedHosts='localhost,127.0.0.1';Write-Host 'Local-only test: antivirus is skipped.'}
else{$env:ASPNETCORE_ENVIRONMENT='Production';$env:Portal__CertificateThumbprint=$CertificateThumbprint;$env:Portal__AllowedHosts="$PublicHost,localhost,127.0.0.1";$serverUrl="https://0.0.0.0:$Port"}
Push-Location (Split-Path $serverExecutable)
try{Write-Host "Announcement server: $serverUrl (Ctrl+C to stop)";& $serverExecutable --urls $serverUrl;if($LASTEXITCODE -ne 0){throw "Server exit code: $LASTEXITCODE"}}
finally{Pop-Location;Remove-Item Env:Portal__AdminPassword -ErrorAction SilentlyContinue;Remove-Item Env:Portal__BridgeKey -ErrorAction SilentlyContinue}
