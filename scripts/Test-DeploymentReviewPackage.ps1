param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference='Stop'
$PackageDirectory=[IO.Path]::GetFullPath($PackageDirectory)
$config=Join-Path $PackageDirectory 'config'
$allowed=@('registration-policy.draft.json','deployment-review.draft.json','server-operations.example.json','cloudflared.example.yml')
foreach($file in Get-ChildItem -LiteralPath $config -Recurse -File){
    if($file.DirectoryName -ne $config -or $file.Name -notin $allowed){throw 'Only public configuration templates may be packaged. Keep school review records outside the package.'}
}
$template=Join-Path $config 'deployment-review.draft.json'
if(-not(Test-Path -LiteralPath $template -PathType Leaf)){throw 'Public deployment-review draft missing.'}
$draft=Get-Content -LiteralPath $template -Raw -Encoding UTF8|ConvertFrom-Json
if($draft.status -ne 'draft' -or $draft.school -or $draft.schoolId -or $draft.reviewAuthority -or @($draft.externalServices).Count -ne 0){throw 'Deployment-review draft must not contain school-specific approvals or connection records.'}
foreach($property in $draft.documents.PSObject.Properties){if($property.Value.path -or $property.Value.sha256){throw 'Do not package actual review evidence paths or hashes.'}}
foreach($file in Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File -Filter '*.json'){
    if($file.Length -gt 262144){continue}
    try{$value=Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8|ConvertFrom-Json}catch{continue}
    if($value -and $value.PSObject.Properties['reviewAuthority'] -and $value.PSObject.Properties['externalServices'] -and $value.PSObject.Properties['documents'] -and $file.FullName -ne $template){throw 'A school review record was found in the distributable package.'}
    if($value -and $value.PSObject.Properties['requestId'] -and ($value.PSObject.Properties['contactPhone'] -or $value.PSObject.Properties['requestSha256'])){throw 'Private school registration requests or decisions must not be packaged.'}
}
