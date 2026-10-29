$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root '.tools\update-frameworks'
$archive=Join-Path $folder 'WinSparkle-0.9.4.zip'
New-Item -ItemType Directory -Force $folder | Out-Null
$expected='6037df37fc263bd1650a1c4949681a9d40ffe991d01f35892a406cb5d103c976'
if(-not (Test-Path -LiteralPath $archive)){Invoke-WebRequest 'https://github.com/vslavik/winsparkle/releases/download/v0.9.4/WinSparkle-0.9.4.zip' -OutFile $archive}
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw 'WinSparkle archive checksum mismatch'}
$target=Join-Path $folder 'winsparkle'
if(-not (Test-Path -LiteralPath (Join-Path $target 'WinSparkle-0.9.4\x64\Release\WinSparkle.dll'))){Expand-Archive -LiteralPath $archive -DestinationPath $target -Force}
