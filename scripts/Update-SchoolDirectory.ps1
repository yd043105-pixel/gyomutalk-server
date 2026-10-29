param(
    [string]$DirectoryPath,
    [string]$OfficeRegistrationFile,
    [string]$AnnouncementsRegistrationFile,
    [string]$Region
)
$ErrorActionPreference='Stop'
function Read-SchoolRegistration([string]$Path,[string]$Service){
    if(-not $Path){return $null}
    if((Get-Item -LiteralPath $Path).Length -gt 4096){throw '학교 등록자료는 4KB 이하여야 합니다.'}
    $value=Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json
    if(@($value.PSObject.Properties.Name|Where-Object {$_ -cnotin @('schema','schoolId','schoolName','service','address')}).Count){throw '등록자료에 불필요한 정보가 있습니다.'}
    if($value.schema -ne 1 -or $value.service -cne $Service -or $value.schoolId -cnotmatch '^[A-Za-z0-9_-]{5,64}$' -or -not $value.schoolName -or $value.schoolName.Length -gt 80 -or $value.schoolName -match '[\x00-\x1f\x7f]'){throw '학교 식별자·이름·서버 역할을 확인하세요.'}
    $address=$null
    if(-not [Uri]::TryCreate([string]$value.address,[UriKind]::Absolute,[ref]$address) -or $address.Scheme -ne 'https' -or $address.UserInfo -or $address.Query -or $address.Fragment -or $address.AbsolutePath -ne '/' -or $value.address.Length -gt 240 -or $address.IsLoopback){throw '다른 기기에서 연결할 수 있는 HTTPS 기본 주소가 필요합니다.'}
    $value.address=$address.AbsoluteUri
    return $value
}
function Confirm-SchoolRegistration($Request){
    Add-Type -AssemblyName System.Net.Http
    $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false;$handler.UseCookies=$false
    $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(6);$client.MaxResponseContentBufferSize=2048
    try{
        $url=[Uri]::new([Uri]$Request.address,'server-info')
        $identity=$client.GetStringAsync($url).GetAwaiter().GetResult()|ConvertFrom-Json
        if($identity.directoryProduct -cne 'SchoolMessenger.School.1' -or $identity.schoolId -cne $Request.schoolId -or $identity.schoolName -cne $Request.schoolName -or $identity.service -cne $Request.service){throw '학교 등록자료와 실제 서버 정보가 다릅니다.'}
    }finally{$client.Dispose()}
}
function Update-SchoolDirectory([string]$DirectoryPath,[string]$OfficeRegistrationFile,[string]$AnnouncementsRegistrationFile,[string]$Region){
    if(-not $DirectoryPath -or (-not $OfficeRegistrationFile -and -not $AnnouncementsRegistrationFile) -or -not $Region -or $Region.Length -gt 80 -or $Region -match '[\x00-\x1f\x7f]'){throw '목록 경로·역할별 등록자료·지역을 입력하세요.'}
    $office=Read-SchoolRegistration $OfficeRegistrationFile 'office';$portal=Read-SchoolRegistration $AnnouncementsRegistrationFile 'announcements';$school=if($office){$office}else{$portal}
    if($office -and $portal -and ($office.schoolId -cne $portal.schoolId -or $office.schoolName -cne $portal.schoolName -or $office.address -eq $portal.address)){throw '같은 학교의 서로 다른 두 역할 주소가 필요합니다.'}
    foreach($request in @($office,$portal)){if($request){Confirm-SchoolRegistration $request}}
    $directory=Get-Content -LiteralPath $DirectoryPath -Raw -Encoding UTF8|ConvertFrom-Json
    if($directory.schema -ne 1){throw '학교 목록 형식을 확인하세요.'}
    $existing=@($directory.schools|Where-Object {$_.id -ceq $school.schoolId})
    if($existing.Count -gt 1){throw '중복 학교 식별자가 있습니다.'}
    $entry=[ordered]@{id=$school.schoolId;name=$school.schoolName;region=$Region;officeAddress='';announcementsAddress=''}
    if($existing.Count -eq 1){$entry.officeAddress=$existing[0].officeAddress;$entry.announcementsAddress=$existing[0].announcementsAddress}
    if($office){$entry.officeAddress=$office.address};if($portal){$entry.announcementsAddress=$portal.address}
    foreach($other in @($directory.schools|Where-Object {$_.id -cne $school.schoolId})){foreach($address in @($entry.officeAddress,$entry.announcementsAddress)){if($address -and $address -in @($other.officeAddress,$other.announcementsAddress)){throw '다른 학교가 사용하는 주소는 등록할 수 없습니다.'}}}
    if($entry.officeAddress -and $entry.officeAddress -eq $entry.announcementsAddress){throw '두 역할의 주소를 분리하세요.'}
    $directory.schools=@(@($directory.schools|Where-Object {$_.id -cne $school.schoolId})+[pscustomobject]$entry|Sort-Object name,region)
    if($directory.schools.Count -gt 4096){throw '학교 목록 한도를 초과했습니다.'}
    $json=$directory|ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($DirectoryPath),$json,[Text.UTF8Encoding]::new($false))
    Write-Host '로컬 목록을 갱신했습니다. 실제 학교의 도입 의사·관리자·주소 소유와 공개 범위를 확인한 뒤 중앙 목록에 게시하세요. 자동 게시하지 않습니다.'
}
if($MyInvocation.InvocationName -ne '.'){
    Update-SchoolDirectory $DirectoryPath $OfficeRegistrationFile $AnnouncementsRegistrationFile $Region
}
