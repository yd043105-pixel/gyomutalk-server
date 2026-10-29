param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'scripts\Server-Operations.ps1') -ConfigurationPath 'unused-check-only'
$folder=Join-Path $root ('.test-data\review-operations-'+[guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path $folder -Force|Out-Null
$evidence=Join-Path $folder 'evidence.txt'
[IO.File]::WriteAllText($evidence,'SYNTHETIC TEST ONLY - not a school approval or certificate.')
$documents=@{}
foreach($key in @('decision','overview','plan','requirements','network','security')){$documents[$key]=@{path='evidence.txt';sha256=(Get-FileHash -LiteralPath $evidence -Algorithm SHA256).Hash}}
foreach($role in @(@('office','server','SchoolMessenger.Server'),@('announcements','announcement-server','SchoolMessenger.AnnouncementServer'))){
    $record=Join-Path $folder ($role[0]+'.json')
    @{schema=1;status='approved';school='SYNTHETIC TEST ONLY';schoolId='YY-SYNTHETIC-ONLY';service=$role[0];basis='on-premises';institutionType='education-site';reviewAuthority='SYNTHETIC - no actual authority approval';reviewedAt=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('O');expiresAt=[DateTimeOffset]::UtcNow.AddDays(1).ToString('O');listenUrls=@('https://0.0.0.0:8443');storageCountries=@('KR');managementCountries=@('KR');documents=$documents;externalServices=@()}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $record -Encoding UTF8
    $config=[pscustomobject]@{service=$role[0];executable=(Join-Path ([IO.Path]::GetFullPath($PackageDirectory)) ($role[1]+'\'+$role[2]+'.exe'));securityReviewFile=$record;schoolId='YY-SYNTHETIC-ONLY';tunnelOnly=$false;port=8443;publicHost='';feedbackEmailEnabled=$false;alerts=@{enabled=$false;smtpHost='';recipient=''}}
    if(-not(Test-OperationsDeploymentReview $config)){throw ($role[0]+' packaged read-only verifier failed')}
    Write-Host ('PASS '+$role[0]+' packaged verifier accepts matching synthetic record without starting a server')
    $config.schoolId='WRONG-SCHOOL'
    if(Test-OperationsDeploymentReview $config){throw 'Cross-school record was accepted'}
    Write-Host ('PASS '+$role[0]+' packaged verifier rejects cross-school configuration')
    $config.executable=Join-Path $folder ($role[2]+'.exe')
    [IO.File]::WriteAllText($config.executable,'This is deliberately not executable. Never invoke it.')
    if(Test-OperationsDeploymentReview $config){throw 'Executable without capability proof was invoked'}
    Write-Host ('PASS '+$role[0]+' legacy or unverified executable is rejected before invocation')
}
Write-Host '6 packaged operations review checks passed; no listener, database or SMTP delivery.'
# The negative verifier cases intentionally return nonzero. The test suite itself passed.
$global:LASTEXITCODE=0
