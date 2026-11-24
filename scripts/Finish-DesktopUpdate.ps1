param([int]$ParentId,[long]$ParentStart,[string]$Installer,[string]$ExpectedHash,[string]$Application,[ValidateSet('server','windows')][string]$Product,[string]$Signature,[switch]$Unattended)
$ErrorActionPreference='Stop'
$result='not-installed'
try {
    $parent=Get-Process -Id $ParentId -ErrorAction SilentlyContinue
    if($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $ParentStart){if(-not $parent.WaitForExit(120000)){throw 'The application did not stop safely. Update cancelled.'}}
    if((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ne $ExpectedHash){throw 'The staged installer was modified. Update cancelled.'}
    # On a server PC set up by the installer, the SYSTEM task installs the signed file without the administrator prompt.
    $task=if($Product -eq 'server' -and $Signature){Get-ScheduledTask -TaskName 'GyomuTalk Server Update' -ErrorAction SilentlyContinue}
    if($task){
        $drop=Join-Path $env:ProgramData 'GyomuTalk\server-update'
        try{
            Copy-Item -LiteralPath $Installer -Destination (Join-Path $drop 'update.exe') -Force
            Set-Content -LiteralPath (Join-Path $drop 'update.sig') -Value $Signature -Encoding ASCII
            $before=(Get-ScheduledTaskInfo -InputObject $task).LastRunTime
            Start-ScheduledTask -InputObject $task
        }catch{if($Unattended){throw};$task=$null}
    }
    if($task){
        $deadline=(Get-Date).AddMinutes(30)
        do{Start-Sleep -Seconds 2;$info=Get-ScheduledTaskInfo -InputObject $task;$state=(Get-ScheduledTask -TaskName $task.TaskName).State}
        while(($info.LastRunTime -eq $before -or $state -eq 'Running') -and (Get-Date) -lt $deadline)
        Remove-Item -LiteralPath (Join-Path $drop 'update.exe'),(Join-Path $drop 'update.sig') -Force -ErrorAction SilentlyContinue
        if($state -eq 'Running'){throw 'The automatic installer did not finish in 30 minutes.'}
        $code=$info.LastTaskResult
        if($code -eq 0){$result='installed'}
        elseif($code -notin @(10,11)){throw "Automatic installer returned $code. Existing application will be reopened."}
    }
    if($result -ne 'installed'){
        # Nobody is at the PC for an unattended update, so the administrator prompt would only wait; try again later instead.
        if($Unattended){throw 'Automatic installation is not available. Install the update from the 관리자 프로그램.'}
        $setup=Start-Process -FilePath $Installer -ArgumentList @('/SILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-') -Wait -PassThru
        if($setup.ExitCode -notin @(0,3010)){throw "Installer returned $($setup.ExitCode). Existing application will be reopened."}
        $result='installed'
    }
} catch { $result=$_.Exception.Message }
finally {
    $result | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'result.txt') -Encoding UTF8
    $state=Join-Path ([Environment]::GetFolderPath('LocalApplicationData','Create')) ('GyomutalkUpdater\'+$Product)
    New-Item -ItemType Directory -Force $state | Out-Null
    @{success=($result -eq 'installed');message=$result}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $state 'last-result.json') -Encoding UTF8
    if(Test-Path -LiteralPath $Application){if($Product -eq 'server'){Start-Process -FilePath $Application -ArgumentList '--server-manager'}else{Start-Process -FilePath $Application}}
}
