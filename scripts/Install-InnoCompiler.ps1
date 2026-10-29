param([string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) '.tools\inno-6.7.3'))
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
$compiler = Join-Path $Destination 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) {
    # Some official builds report 0.0.0.0. Reinstall from the pinned, signed
    # download when metadata cannot identify the cache; never bypass verification.
    if ((Get-Item -LiteralPath $compiler).VersionInfo.FileVersion -like '6.7.3*') {
        $cachedSignature = Get-AuthenticodeSignature -LiteralPath $compiler
        if ($cachedSignature.Status -ne 'Valid' -or $cachedSignature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') { throw 'Cached Inno Setup publisher signature invalid.' }
        Write-Output $compiler
        exit 0
    }
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$download = Join-Path $Destination 'innosetup-6.7.3.exe'
Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732') { throw 'Inno Setup download hash mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') { throw 'Inno Setup publisher signature invalid.' }
$install = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CURRENTUSER','/NOICONS','/TASKS=', ('/DIR="' + $Destination + '"')) -WindowStyle Hidden -Wait -PassThru
if ($install.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) { throw 'Inno Setup compiler installation failed.' }
Write-Output $compiler
