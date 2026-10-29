param(
    [string]$RequestFile,
    [ValidateSet('Approve','Reject')][string]$Decision,
    [string]$DirectoryPath,
    [string]$ReviewedBy,
    [string]$Reason,
    [string]$DecisionDirectory,
    [switch]$SchoolAuthorityVerified,
    [switch]$PublicEndpointsConfirmed
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Update-SchoolDirectory.ps1') -DirectoryPath $DirectoryPath
function Read-PendingSchoolRequest([string]$Path){
    if((Get-Item -LiteralPath $Path).Length -gt 8192){throw '학교 신청서는 8KB 이하여야 합니다.'}
    $value=Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json
    $fields=@('schema','status','requestId','schoolId','schoolName','region','contactPhone','createdAt','officeAddress','announcementsAddress')
    if(@($value.PSObject.Properties.Name|Where-Object {$_ -cnotin $fields}).Count -or @($value.PSObject.Properties).Count -ne $fields.Count){throw '학교 신청서 항목을 확인하세요.'}
    foreach($field in @('status','requestId','schoolId','schoolName','region','contactPhone','createdAt','officeAddress','announcementsAddress')){if($value.$field -isnot [string] -or $value.$field -match '[\x00-\x1f\x7f]'){throw '학교 신청서 문자열을 확인하세요.'}}
    if($value.schema -ne 1 -or $value.status -cne 'pending' -or $value.requestId -cnotmatch '^[a-f0-9]{32}$' -or $value.schoolId -cnotmatch '^[A-Za-z0-9_-]{5,64}$' -or -not $value.schoolName.Trim() -or $value.schoolName.Length -gt 80 -or -not $value.region.Trim() -or $value.region.Length -gt 80){throw '대기 중인 학교 신청서만 검토할 수 있습니다.'}
    $digits=$value.contactPhone -replace '[^0-9]',''
    if($value.contactPhone.Length -gt 24 -or $value.contactPhone -notmatch '^[0-9 ()-]+$' -or $digits -notmatch '^0[0-9]{8,10}$' -or $digits -match '^01[016789]'){throw '공개된 학교 대표전화를 확인하세요.'}
    $created=[DateTimeOffset]::Parse($value.createdAt)
    if($created -gt [DateTimeOffset]::UtcNow.AddMinutes(5)){throw '신청 시각을 확인하세요.'}
    if(-not $value.officeAddress -and -not $value.announcementsAddress){throw '신청한 학교 주소가 없습니다.'}
    return $value
}
function Review-SchoolRegistration([string]$RequestFile,[string]$Decision,[string]$DirectoryPath,[string]$ReviewedBy,[string]$Reason,[string]$DecisionDirectory,[bool]$SchoolAuthorityVerified,[bool]$PublicEndpointsConfirmed){
    if($Decision -notin @('Approve','Reject') -or -not $ReviewedBy -or $ReviewedBy.Length -gt 80 -or $ReviewedBy -match '[\x00-\x1f\x7f]'){throw '승인/거절과 실제 검토자 이름을 입력하세요.'}
    $request=Read-PendingSchoolRequest $RequestFile
    if(-not $DecisionDirectory){$DecisionDirectory=Join-Path $env:LOCALAPPDATA 'YyMessengerRegistry\decisions'}
    $DecisionDirectory=[IO.Path]::GetFullPath($DecisionDirectory)
    New-Item -ItemType Directory -Path $DecisionDirectory -Force|Out-Null
    $record=Join-Path $DecisionDirectory ('school-registration-decision-'+$request.requestId+'.json')
    if(Test-Path -LiteralPath $record){throw '이미 검토한 신청서입니다. 변경 신청은 새 신청서로 처리하세요.'}
    if($Decision -eq 'Approve'){
        if(-not $SchoolAuthorityVerified -or -not $PublicEndpointsConfirmed){throw '학교의 도입 의사·담당 권한과 주소 소유·공개 범위를 실제 확인한 뒤 승인하세요.'}
        if(-not $DirectoryPath){throw '승인할 로컬 공개 목록 경로를 입력하세요.'}
        $office='';$portal=''
        foreach($role in @(@('office','officeAddress'),@('announcements','announcementsAddress'))){
            if(-not $request.($role[1])){continue}
            $file=Join-Path $DecisionDirectory ($request.requestId+'-'+$role[0]+'.json')
            @{schema=1;schoolId=$request.schoolId;schoolName=$request.schoolName;service=$role[0];address=$request.($role[1])}|ConvertTo-Json|Set-Content -LiteralPath $file -Encoding UTF8
            if($role[0] -eq 'office'){$office=$file}else{$portal=$file}
        }
        Update-SchoolDirectory $DirectoryPath $office $portal $request.region
    }else{if(-not $Reason -or $Reason.Length -gt 500 -or $Reason -match '[\x00-\x1f\x7f]'){throw '거절 사유를 500자 이내로 입력하세요.'}}
    $outcome=if($Decision -eq 'Approve'){'approved'}else{'rejected'}
    $receipt=@{schema=1;requestId=$request.requestId;schoolId=$request.schoolId;decision=$outcome;reviewedBy=$ReviewedBy;reviewedAt=[DateTimeOffset]::UtcNow.ToString('O');requestSha256=(Get-FileHash -LiteralPath $RequestFile -Algorithm SHA256).Hash;reason=[string]$Reason;published=$false}
    $receipt|ConvertTo-Json|Set-Content -LiteralPath $record -Encoding UTF8
    Write-Host ('학교 검색 등록 검토: '+$outcome+'. 검토 기록은 로컬에 저장했습니다. GitHub 게시나 결과 메일 발송은 수행하지 않았습니다.')
}
if($MyInvocation.InvocationName -ne '.'){
    Review-SchoolRegistration $RequestFile $Decision $DirectoryPath $ReviewedBy $Reason $DecisionDirectory ([bool]$SchoolAuthorityVerified) ([bool]$PublicEndpointsConfirmed)
}
