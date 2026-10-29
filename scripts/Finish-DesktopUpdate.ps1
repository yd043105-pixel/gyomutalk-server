param([int]$ParentId,[long]$ParentStart,[string]$Installer,[string]$ExpectedHash,[string]$Application,[ValidateSet('server','windows')][string]$Product)
$ErrorActionPreference='Stop'
$result='not-installed'
try {
    $parent=Get-Process -Id $ParentId -ErrorAction SilentlyContinue
    if($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $ParentStart){if(-not $parent.WaitForExit(120000)){throw 'The application did not stop safely. Update cancelled.'}}
    if((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ne $ExpectedHash){throw 'The staged installer was modified. Update cancelled.'}
    $setup=Start-Process -FilePath $Installer -ArgumentList @('/SILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-') -Wait -PassThru
    if($setup.ExitCode -notin @(0,3010)){throw "Installer returned $($setup.ExitCode). Existing application will be reopened."}
    $result='installed'
} catch { $result=$_.Exception.Message }
finally {
    $result | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'result.txt') -Encoding UTF8
    $state=Join-Path $env:LOCALAPPDATA ('GyomutalkUpdater\'+$Product)
    New-Item -ItemType Directory -Force $state | Out-Null
    @{success=($result -eq 'installed');message=$result}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $state 'last-result.json') -Encoding UTF8
    if(Test-Path -LiteralPath $Application){if($Product -eq 'server'){Start-Process -FilePath $Application -ArgumentList '--server-manager'}else{Start-Process -FilePath $Application}}
}
