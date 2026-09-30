# 这些图像/数字仅为校验器正反例，不能复制到业务报告。先完整执行M0/M1/UX自测。
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'TestUXDevelopmentGate.ps1')
. (Join-Path $PSScriptRoot 'M2DevelopmentChecks.ps1')
$m2Profile=Get-Content (Join-Path $PSScriptRoot 'm2-profile.json') -Raw|ConvertFrom-Json -AsHashtable
$m2Hash=(Get-FileHash (Join-Path $PSScriptRoot 'm2-profile.json')).Hash
$m2Map=Get-Content (Join-Path $PSScriptRoot 'm2-test-map.json') -Raw|ConvertFrom-Json
$allRows+=@($m2Map.scenarios.tests|ForEach-Object{[pscustomobject]@{method=$_.method;name=$_.method}})
$m2Data=Get-M2ExpectedValues
$coverageFixture=@{Retained=12;SourceExcluded=1;OutsideWindow=2;FilterFalse=0;FilterUnknown=0;FilterTrue=9;GroupedRecords=7;UnresolvedGroupRecords=2;Total=12;Evicted=0}
$m2Data['V5-Q01']+=@{groups=@(3,1,2,1);alternate=@(5,1,1);unresolved=@('Value / Missing','Value / NotReady');Coverage=$coverageFixture;
    members=@(@('r1','r2','r7'),@('r3'),@('r4','r5'),@('r6'));expectedMembers=@(@('r1','r2','r7'),@('r3'),@('r4','r5'),@('r6'));
    AnalysisId=[guid]::NewGuid();SourceId=[guid]::NewGuid();SessionId=[guid]::NewGuid();RuleRevisionId=[guid]::NewGuid();SnapshotRevision=1;RuleHash='A'*64;From='2026-09-30T12:00:00Z';To='2026-09-30T12:30:00Z'}
$m2Data['V5-Q03']+=@{SourceExcluded=0;OutsideWindow=0;GroupedRecords=1;UnresolvedGroupRecords=0;Total=3;Evicted=0}
$m2Data['V5-UI05'].coverage=@{Retained=101;SourceExcluded=0;OutsideWindow=0;FilterFalse=0;FilterUnknown=0;FilterTrue=101;GroupedRecords=100;UnresolvedGroupRecords=1;Total=101;Evicted=0}
$m2Data['V5-L09']+=@{start=12;expectedStart=12}
$m2Data['V5-Q06'].columns=@('capital')
$m2Data['V5-T01']+=@{From='2026-09-30T12:00:00Z';To='2026-09-30T12:30:00Z'}
$m2Data['V5-T02']+=@{expected=@('a','b','c','d');actual=@('a','b','c','d')}
$m2Data['V5-T03'].rows=@(@{ElapsedMilliseconds=120000;state='Receiving'},@{ElapsedMilliseconds=0;state='Cancelled'})
$m2Data['V5-T06']+=@{To='2026-09-30T12:20:00.0000001Z';reopenedTo='2026-09-30T12:20:00.0000001Z'}
$m2Data['V5-S03'].hash='A'*64
$m2Data['V5-I01']+=@{recordIds=@('a','b','c','d','e','f');bodyBytes=24}
$m2Data['V5-UI04']+=@{selected='a';opened='a'}
$m2Data['V5-R01'].counts=@(50,53,40)
$m2Data['V5-R02']+=@{beforeId='a';afterId='a'}
$shots=@()
foreach($spec in @($m2Profile.layouts)+@($m2Profile.behaviors|ForEach-Object{@{key=$_;width=800;height=600;editor=$false;dark=$true}})) {
    $path=Join-Path $directory "m2-$($spec.key).png"
    $bitmap=[System.Drawing.Bitmap]::new([int]$spec.width,[int]$spec.height)
    try{$bitmap.Save($path,[System.Drawing.Imaging.ImageFormat]::Png)}finally{$bitmap.Dispose()}
    $item=@{key=$spec.key;name="m2-$($spec.key).png";width=$spec.width;height=$spec.height;scaling=1;renderer='Avalonia Headless / Skia';realHost=$false;
        sha256=(Get-FileHash $path).Hash;measurements=@{editor=$spec.editor;dark=$spec.dark;stop=@{x=5;y=5;width=50;height=20};action=@{x=5;y=50;width=60;height=20}}}
    $item|ConvertTo-Json -Depth 20|Set-Content "$path.json" -Encoding utf8
    $shots+=,$item
}
$m2Data['V5-UI08'].shots=$shots
$m2Data['V5-P01']+=@{measurements=@(1,2,3,4,5,6,7,8,9,10);operations=@(320000)*10;p95=10;peak=1000000}
$m2Data['V5-P02']+=@{reads=259;contentPeak=33554431;analysisPeak=1000000}
$m2Data['V5-P04'].measurements=@(1)*20
$points=@(foreach($i in 0..2399) {
    [ordered]@{Utc=[DateTimeOffset]::UtcNow.AddSeconds(-600+$i*.25).ToString('O');Seconds=$i*.25;PrivateBytes=1000000;Handles=100;
        Queue=0;Content=100;Analysis=200;Tasks=1;Active=5;Retained=10000;Delay=2}
})
$csv=Join-Path $directory 'm2-performance-samples.csv'
$points|ForEach-Object{[pscustomobject]$_}|Export-Csv $csv -NoTypeInformation
$runs=@(foreach($i in 1..50) {
    @{milliseconds=100;SnapshotRevision=$i;AnalysisId=[guid]::NewGuid();SourceId='test';RuleHash='A'*64;From='2026-09-30T12:00:00Z';To='2026-09-30T12:30:00Z';
        Coverage=@{Retained=10000;SourceExcluded=0;OutsideWindow=0;FilterFalse=0;FilterUnknown=0;FilterTrue=10000;GroupedRecords=10000;UnresolvedGroupRecords=0;Total=10000;Evicted=0}}
})
$m2Data['V5-P03']+=@{seconds=600;successes=50;launched=50;sampleCount=2400;p95=2;maximum=2;baseline=1000000;baselineHandles=100;stoppedHandles=100;stopMs=10;runs=$runs;csvSha256=(Get-FileHash $csv).Hash}
function Write-M2Fixture([string]$Name,$Data) {
    @{schemaVersion=1;runId='self';sourceHash='hash';profileHash=$profileHash;uxProfileHash=$uxHash;m2ProfileHash=$m2Hash;
        recordedUtc=[DateTimeOffset]::UtcNow;scenarioIds=@($Data.Keys);scenarios=$Data}|ConvertTo-Json -Depth 40|Set-Content (Join-Path $directory "$Name.json") -Encoding utf8
}
foreach($group in @('language','grouping','timeline','rules-storage','lifetime','layout','performance','regression')) {
    $entries=@{};foreach($id in $m2Data.Keys){if((Get-M2EvidenceGroup $id) -ceq $group){$entries[$id]=$m2Data[$id]}}
    Write-M2Fixture "m2-$group" $entries
}
Write-M2Fixture 'm2-desktop' @{sustained=$m2Data['V5-P03']}
function CompleteM2 {
    $result=@(Test-CompleteM2Evidence $directory 'self' 'hash' $profileHash $uxHash $m2Hash $since $allRows $map $m1Map $uxMap $m2Map $profile $m1Profile $uxProfile $m2Profile)
    Assert-Check ($result.Count -eq 207) '自测正例缺少207项。'
}
function BrokenM2([string]$Name,[scriptblock]$Change) {
    $file=Join-Path $directory "$Name.json";$saved=[IO.File]::ReadAllText($file)
    try{$d=$saved|ConvertFrom-Json -AsHashtable;&$Change $d;$d|ConvertTo-Json -Depth 40|Set-Content $file -Encoding utf8;Reject $Name {CompleteM2}}
    finally{[IO.File]::WriteAllText($file,$saved)}
}
CompleteM2
$validMap=$m2Map|ConvertTo-Json -Depth 20
foreach($id in @(Get-RequiredM2Scenarios)){
    $m2Map.scenarios=@($m2Map.scenarios|Where-Object id -CNE $id)
    Reject "缺$id" {Test-M2ScenarioMap $m2Map $allRows}
    $m2Map=$validMap|ConvertFrom-Json
}
$m2Map.scenarios=@($m2Map.scenarios|Select-Object -Skip 1);Reject 'G02完整入口缺场景' {CompleteM2};$m2Map=$validMap|ConvertFrom-Json
foreach($change in @({$m2Map.scenarios+=$m2Map.scenarios[0]},{$m2Map.scenarios[0].id='unknown'},{$m2Map.scenarios[0].tests=@()},{$m2Map.scenarios[0].tests[0].method='absent'},{$m2Map.scenarios[0].tests[0].rows=@('(missing:1)')})){
    &$change;Reject 'G02映射错误' {Test-M2ScenarioMap $m2Map $allRows};$m2Map=$validMap|ConvertFrom-Json
}
foreach($field in @('schemaVersion','runId','sourceHash','profileHash','uxProfileHash','m2ProfileHash','recordedUtc','scenarioIds','scenarios')){
    BrokenM2 'm2-language' {param($d)$d.Remove($field)|Out-Null}
}
foreach($field in @('runId','sourceHash','profileHash','uxProfileHash','m2ProfileHash')){BrokenM2 'm2-language' {param($d)$d[$field]='old'}}
BrokenM2 'm2-language' {param($d)$d.recordedUtc=[DateTimeOffset]::UtcNow.AddDays(1)}
BrokenM2 'm2-language' {param($d)$d.scenarios['V5-L01'].extractors='6'}
BrokenM2 'm2-grouping' {param($d)$d.scenarios['V5-E04'].unknownNot=$false}
BrokenM2 'm2-grouping' {param($d)$d.scenarios['V5-E04'].fallbackState='Missing'}
BrokenM2 'm2-grouping' {param($d)$d.scenarios['V5-Q01'].members[0][0]='r3'}
BrokenM2 'm2-grouping' {param($d)$d.scenarios['V5-Q01'].Coverage.GroupedRecords=9}
BrokenM2 'm2-timeline' {param($d)$d.scenarios['V5-T02'].actual=@('b','a','c','d')}
BrokenM2 'm2-timeline' {param($d)$d.scenarios['V5-T06'].reopenedTo=[DateTimeOffset]::UtcNow}
BrokenM2 'm2-rules-storage' {param($d)$d.scenarios['V5-S05'].unchangedOnFailure=$false}
BrokenM2 'm2-lifetime' {param($d)$d.scenarios['V5-I03'].running=$true}
BrokenM2 'm2-lifetime' {param($d)$d.scenarios['V5-I05'].oldApplied=1}
BrokenM2 'm2-performance' {param($d)$d.scenarios['V5-P04'].measurements[0]=1001}
BrokenM2 'm2-layout' {param($d)$d.scenarios['V5-UI08'].shots=@($d.scenarios['V5-UI08'].shots|Select-Object -Skip 1)}
BrokenM2 'm2-layout' {param($d)$d.scenarios['V5-UI08'].shots[0].measurements.stop.x=100000}
$png=Join-Path $directory 'm2-diagnostic.png';$bytes=[IO.File]::ReadAllBytes($png)
try{[IO.File]::WriteAllText($png,'broken');Reject 'G09坏PNG' {CompleteM2}}finally{[IO.File]::WriteAllBytes($png,$bytes)}
BrokenM2 'm2-performance' {param($d)$d.scenarios['V5-P01'].measurements=@(1)}
BrokenM2 'm2-performance' {param($d)$d.scenarios['V5-P03'].p95=1}
$csvBytes=[IO.File]::ReadAllBytes($csv)
try{Add-Content $csv 'bad';Reject 'G10原曲线改变' {CompleteM2}}finally{[IO.File]::WriteAllBytes($csv,$csvBytes)}
$oldBudget=$m2Profile.maxAnalysisBytes;$m2Profile.maxAnalysisBytes++;Reject 'G10额度放宽' {CompleteM2};$m2Profile.maxAnalysisBytes=$oldBudget
foreach($name in @('protocol-probes','m1-storage','ux-reading')){
    $file=Join-Path $directory "$name.json";$saved=[IO.File]::ReadAllBytes($file)
    try{[IO.File]::Delete($file);Reject 'G11缺旧证据' {CompleteM2}}finally{[IO.File]::WriteAllBytes($file,$saved)}
}
$scope.mode='M2';Test-DevelopmentScopeReport $scope 'M2'
foreach($flag in @('realClientVerified','hostVerified','manualAccepted','deployed','windowsCiExecuted','releaseGateExecuted')){
    $scope[$flag]=$true;Reject "G12越界$flag" {Test-DevelopmentScopeReport $scope 'M2'};$scope[$flag]=$false
}
$scope.mode='Docs';Reject 'G12文档冒充M2' {Test-DevelopmentScopeReport $scope 'M2'}
CompleteM2
Write-Output "M0+M1+UX+M2门禁自测通过；累计$count 个拒绝检查；隔离夹具：$directory"
