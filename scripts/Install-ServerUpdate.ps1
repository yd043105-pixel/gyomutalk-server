# Runs as SYSTEM from the "GyomuTalk Server Update" task that the server installer registers, so the 관리자 프로그램
# can update without the Windows administrator prompt. Its only input is the drop folder, which any signed-in user can
# write: it installs a file only if it carries this product's Ed25519 update signature (the same key WinSparkle checks),
# from a private copy that nobody else can change. The installer itself refuses older versions.
# Exit codes: 0 installed, 10 nothing staged, 11 signature not valid, 12 programs still running, 100+n installer exit n.
param([string]$Check,[string]$Signature,[string]$PublicKey)
$ErrorActionPreference='Stop'

# ── Ed25519 verification (RFC 8032), on BigInteger so it runs on the PowerShell every Windows 10/11 PC has ──
$Q=[bigint]::Pow(2,255)-19
$L=[bigint]::Pow(2,252)+[bigint]::Parse('27742317777372353535851937790883648493')
function Get-Reduced($n){$r=$n % $Q;if($r.Sign -lt 0){$r+=$Q};$r}
function Get-Inverse($n){[bigint]::ModPow($n,$Q-2,$Q)}
$Dc=Get-Reduced ([bigint](-121665)*(Get-Inverse 121666))
$Sqrt1=[bigint]::ModPow(2,($Q-1)/4,$Q)
function ConvertFrom-LittleEndian([byte[]]$bytes){[bigint]::new([byte[]]($bytes+[byte]0))}
function Get-Point([byte[]]$encoded){
    $copy=[byte[]]$encoded.Clone();$sign=$copy[31] -shr 7;$copy[31]=$copy[31] -band 0x7F
    $y=ConvertFrom-LittleEndian $copy;if($y -ge $Q){return $null}
    $x2=Get-Reduced (($y*$y-1)*(Get-Inverse ($Dc*$y*$y+1)))
    if($x2.IsZero){if($sign){return $null};$x=[bigint]0}
    else{
        $x=[bigint]::ModPow($x2,($Q+3)/8,$Q)
        if(-not (Get-Reduced ($x*$x-$x2)).IsZero){$x=Get-Reduced ($x*$Sqrt1)}
        if(-not (Get-Reduced ($x*$x-$x2)).IsZero){return $null}
        if([int]($x % 2) -ne $sign){$x=$Q-$x}
    }
    ,@($x,$y,[bigint]1,(Get-Reduced ($x*$y)))
}
function Add-Point($p1,$p2){
    $ta=Get-Reduced (($p1[1]-$p1[0])*($p2[1]-$p2[0]));$tb=Get-Reduced (($p1[1]+$p1[0])*($p2[1]+$p2[0]))
    $tc=Get-Reduced ($p1[3]*2*$Dc*$p2[3]);$td=Get-Reduced ($p1[2]*2*$p2[2])
    $te=$tb-$ta;$tf=$td-$tc;$tg=$td+$tc;$th=$tb+$ta
    ,@((Get-Reduced ($te*$tf)),(Get-Reduced ($tg*$th)),(Get-Reduced ($tf*$tg)),(Get-Reduced ($te*$th)))
}
function Get-Multiple([bigint]$scalar,$point){
    $sum=@([bigint]0,[bigint]1,[bigint]1,[bigint]0)
    while($scalar.Sign -gt 0){if(-not $scalar.IsEven){$sum=Add-Point $sum $point};$point=Add-Point $point $point;$scalar=$scalar/2}
    ,$sum
}
function Test-SamePoint($p1,$p2){(Get-Reduced ($p1[0]*$p2[2]-$p2[0]*$p1[2])).IsZero -and (Get-Reduced ($p1[1]*$p2[2]-$p2[1]*$p1[2])).IsZero}
function Test-Signature([IO.Stream]$message,[byte[]]$signature,[byte[]]$key){
    if($signature.Length -ne 64 -or $key.Length -ne 32){return $false}
    $publicPoint=Get-Point $key;$rBytes=[byte[]]$signature[0..31];$rPoint=Get-Point $rBytes
    $s=ConvertFrom-LittleEndian ([byte[]]$signature[32..63])
    if($null -eq $publicPoint -or $null -eq $rPoint -or $s -ge $L){return $false}
    $sha=[Security.Cryptography.SHA512]::Create()
    try{
        [void]$sha.TransformBlock($rBytes,0,32,$null,0);[void]$sha.TransformBlock($key,0,32,$null,0)
        $buffer=New-Object byte[] 1048576;$message.Position=0
        while(($read=$message.Read($buffer,0,$buffer.Length)) -gt 0){[void]$sha.TransformBlock($buffer,0,$read,$null,0)}
        [void]$sha.TransformFinalBlock([byte[]]@(),0,0);$k=(ConvertFrom-LittleEndian $sha.Hash) % $L
    }finally{$sha.Dispose()}
    $by=Get-Reduced (4*(Get-Inverse 5));$base=Get-Point ([byte[]]($by.ToByteArray()+(New-Object byte[] 32))[0..31])
    Test-SamePoint (Get-Multiple $s $base) (Add-Point $rPoint (Get-Multiple $k $publicPoint))
}

if($Check){
    # Used by the tests: verify one file and say yes (0) or no (11).
    $stream=[IO.File]::OpenRead($Check)
    try{$ok=Test-Signature $stream ([Convert]::FromBase64String($Signature)) ([Convert]::FromBase64String($PublicKey))}finally{$stream.Dispose()}
    if($ok){exit 0}else{exit 11}
}

$app=Split-Path $PSScriptRoot -Parent
$publisher=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'update-publisher.json') -Raw | ConvertFrom-Json
if($publisher.product -ne 'server'){exit 11}
$key=[Convert]::FromBase64String($publisher.publicKey)
$drop=Join-Path $env:ProgramData 'GyomuTalk\server-update'
$staged=Join-Path $drop 'update.exe'
try{
    if(-not (Test-Path -LiteralPath $staged -PathType Leaf) -or (Get-Item -LiteralPath $staged).Length -gt 1GB){exit 10}
    $signed=[Convert]::FromBase64String((Get-Content -LiteralPath (Join-Path $drop 'update.sig') -Raw).Trim())
}catch{exit 10}

# A private folder only SYSTEM and administrators can open, made with its permissions in one step.
$private=Join-Path ([IO.Path]::GetTempPath()) ('gyomutalk-update-'+[guid]::NewGuid().ToString('N'))
$security=New-Object Security.AccessControl.DirectorySecurity
$security.SetAccessRuleProtection($true,$false)
foreach($sid in 'S-1-5-18','S-1-5-32-544'){$security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
[void][IO.Directory]::CreateDirectory($private,$security)
$code=0;$hold=$null
try{
    $setup=Join-Path $private 'setup.exe'
    [IO.File]::Copy($staged,$setup)
    # Held open without write or delete sharing from the check until the installer has finished.
    $hold=[IO.File]::Open($setup,'Open','Read','Read')
    if(-not (Test-Signature $hold $signed $key)){$code=11}
    else{
        # The installer will not replace files of a running server or 관리자 프로그램; they close themselves first.
        $base=$app.TrimEnd('\')+'\'
        $deadline=(Get-Date).AddMinutes(3)
        while(@(Get-CimInstance Win32_Process -Filter "Name='SchoolMessenger.Server.exe' OR Name='SchoolMessenger.AnnouncementServer.exe' OR Name='SchoolMessenger.exe'" | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)}).Count -gt 0){
            if((Get-Date) -gt $deadline){$code=12;break}
            Start-Sleep -Seconds 2
        }
        if($code -eq 0){
            $process=Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/AUTOUPDATE=1') -Wait -PassThru
            if($process.ExitCode -notin @(0,3010)){$code=100+$process.ExitCode}
        }
    }
}finally{
    if($hold){$hold.Dispose()}
    Remove-Item -LiteralPath $private -Recurse -Force -ErrorAction SilentlyContinue
}
exit $code
