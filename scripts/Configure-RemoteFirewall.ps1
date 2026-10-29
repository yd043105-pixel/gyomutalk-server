#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ClientPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'client\SchoolMessenger.exe'),
    [string[]]$RemoteAddresses = @('LocalSubnet'),
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$ruleName = 'YeoyangSchoolMessenger-RemotePeer'
if ($Remove) {
    if ($PSCmdlet.ShouldProcess($ruleName, '원격 지원 방화벽 규칙 삭제')) {
        Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    }
    return
}
$clientExecutable = (Resolve-Path -LiteralPath $ClientPath -ErrorAction Stop).ProviderPath
if ([IO.Path]::GetFileName($clientExecutable) -ne 'SchoolMessenger.exe') { throw '교사용 SchoolMessenger.exe 경로를 지정하세요.' }
if ($RemoteAddresses.Count -eq 0) { throw '교직원망 주소를 지정하세요.' }
foreach ($schoolNetwork in $RemoteAddresses) {
    if ($schoolNetwork -eq 'LocalSubnet') { continue }
    $addressParts = $schoolNetwork.Split('/')
    $networkAddress = $null
    $prefixLength = 0
    if ($addressParts.Count -ne 2 -or -not [Net.IPAddress]::TryParse($addressParts[0], [ref]$networkAddress) -or
        -not [int]::TryParse($addressParts[1], [ref]$prefixLength)) { throw "CIDR 형식의 교직원망 주소를 지정하세요: $schoolNetwork" }
    $maximumPrefix = if ($networkAddress.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) { 32 } else { 128 }
    if ($prefixLength -lt 1 -or $prefixLength -gt $maximumPrefix) { throw "교직원망 범위를 확인하세요: $schoolNetwork" }
}
if ($PSCmdlet.ShouldProcess("$clientExecutable / $($RemoteAddresses -join ',')", '교직원망에서만 원격 지원 TCP 49152~65535 허용')) {
    $existingRule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($existingRule) {
        $existingRule | Set-NetFirewallRule -Program $clientExecutable -Direction Inbound -Action Allow -Enabled True -Profile Domain,Private -Protocol TCP -LocalPort '49152-65535' -RemoteAddress $RemoteAddresses
    } else {
        New-NetFirewallRule -Name $ruleName -DisplayName '교무톡 PC 직접 원격 지원' -Program $clientExecutable -Direction Inbound -Action Allow -Enabled True -Profile Domain,Private -Protocol TCP -LocalPort '49152-65535' -RemoteAddress $RemoteAddresses | Out-Null
    }
    Write-Host '원격 지원 규칙 적용 완료. 두 교직원 PC 사이의 학교 네트워크 경로도 확인하세요.'
}
