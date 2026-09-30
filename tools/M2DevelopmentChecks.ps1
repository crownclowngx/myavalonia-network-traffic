# V5 本地开发门禁。固定集合独立于待检查映射；先验证143项前置，再核对64项新场景和原始观测。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Get-RequiredM2Scenarios {
    foreach ($group in @{ L=10; E=12; Q=8; T=6; S=8; I=6; UI=8; P=4; R=2 }.GetEnumerator()) {
        foreach ($n in 1..$group.Value) { 'V5-{0}{1:00}' -f $group.Key,$n }
    }
}
function Get-M2EvidenceGroup([string] $Id) {
    switch -Regex ($Id) {
        '^V5-L' { 'language'; break }
        '^V5-[EQ]' { 'grouping'; break }
        '^V5-T' { 'timeline'; break }
        '^V5-S' { 'rules-storage'; break }
        '^V5-I' { 'lifetime'; break }
        '^V5-UI' { 'layout'; break }
        '^V5-P' { 'performance'; break }
        '^V5-R' { 'regression'; break }
        default { throw "未知V5场景：$Id" }
    }
}
function Test-M2ScenarioMap($Map, $Rows) {
    Assert-Check ($Map.schemaVersion -eq 1 -and (@(Get-RequiredM2Scenarios | Sort-Object) -join ',') -ceq (@($Map.scenarios.id | Sort-Object) -join ',')) 'V5 64项集合缺失、重复或未知。'
    foreach ($scenario in $Map.scenarios) {
        Assert-Check ($scenario.evidenceFile -ceq ('m2-' + (Get-M2EvidenceGroup $scenario.id) + '.json') -and $scenario.observation -ceq $scenario.id) 'V5证据映射错误。'
        Assert-Check (@($scenario.tests).Count -eq 1) 'V5每项应映射一个完整业务方法。'
        foreach ($test in $scenario.tests) {
            $actual = @($Rows | Where-Object method -CEQ $test.method)
            Assert-Check ($test.results -eq 1 -and $actual.Count -eq 1 -and @($test.rows).Count -eq 1 -and $test.rows[0] -ceq '' -and $actual[0].name -ceq $test.method) "V5结果缺失/重复/参数不符：$($scenario.id)"
        }
    }
    Assert-Check (@($Map.scenarios.tests.method | Select-Object -Unique).Count -eq 64) 'V5不能用同一方法冒充多个独立场景。'
    $newRows=@($Rows|Where-Object method -Match '^NetWorkTrafficPlugin.Tests.(Dsl.|Analysis.|Rules.|Integration.M2|Ui.M2)')
    Assert-Check ($newRows.Count -eq 64 -and @($newRows|Where-Object method -CNotIn $Map.scenarios.tests.method).Count -eq 0) 'V5测试分类并集缺失、重复或有未映射方法。'
    return @($Map.scenarios | ForEach-Object { @{id=$_.id; tests=$_.tests; evidenceFile=$_.evidenceFile; observation=$_.observation; status='passed'} })
}
function Get-M2ExpectedValues {
    # 手工期望：合成输入固定值与边界。不是从测试结果或映射文件反推期望。
    return @{
        'V5-L01'=@{extractors=6;fields=2}; 'V5-L02'=@{rejected=5}; 'V5-L03'=@{precedence=$true;chainRejected=$true}
        'V5-L04'=@{rejected=5}; 'V5-L05'=@{rejected=7}; 'V5-L06'=@{forwardFields=3;cyclesRejected=2}
        'V5-L07'=@{sourceLimit=65536;depthLimit=32;rejected=3}; 'V5-L08'=@{queryOperations=5000001;recordState='BudgetExceeded';cancelled=$true}
        'V5-L09'=@{errors=2}; 'V5-L10'=@{cultures=3;exactDecimal=$true}
        'V5-E01'=@{repeated=3;invalid=4}; 'V5-E02'=@{headerRows=2;cookieRows=2}; 'V5-E03'=@{paths=5;duplicateRejected=$true}
        'V5-E04'=@{unknownNot=$true;fallbackState='NotReady';unselectedBodyReads=0}; 'V5-E05'=@{decoded='a b';segment='a/b';emptySegment=$true}
        'V5-E06'=@{partialStates=3;archivedState='Unavailable';reservedAfter=0}; 'V5-E07'=@{codecs=3;depthExceeded=$true;reservedAfter=0}
        'V5-E08'=@{completedRequestUsable=$true;missingMime='Unavailable'}; 'V5-E09'=@{conversionsRejected=3;valueLimit=4096;itemLimit=128}
        'V5-E10'=@{reads=1;reservedAfter=0}; 'V5-E11'=@{unavailable=3;bodyReads=0}; 'V5-E12'=@{masked='••••';bodyReads=0;reservedAfter=0}
        'V5-Q01'=@{context=1}; 'V5-Q02'=@{collisions=0;equivalent=4}; 'V5-Q03'=@{Retained=3;FilterFalse=1;FilterTrue=1;FilterUnknown=1}
        'V5-Q04'=@{businessCount=2;duplicateIdentityRejected=$true}; 'V5-Q05'=@{groups=2;excluded=1;explicitlyIncluded=1}
        'V5-Q06'=@{firstCount=2}; 'V5-Q07'=@{Count=3;Interfaces=2;Errors=1;Receiving=1}; 'V5-Q08'=@{records=10001;reservedAfter=0;leases=0}
        'V5-T01'=@{OutsideWindow=3}; 'V5-T02'=@{}; 'V5-T03'=@{}; 'V5-T04'=@{context=1;grouped=1;outside=2}
        'V5-T05'=@{firstCount=1;secondCount=2}; 'V5-T06'=@{empty=0;leases=0}
        'V5-S01'=@{retainedOnFailure=$true;previewApplied=$false;finalGroups=0}; 'V5-S02'=@{locations=6;commentsPreserved=$true}
        'V5-S03'=@{Generation=1}; 'V5-S04'=@{windowSeconds=300;restored=$true}; 'V5-S05'=@{unchangedOnFailure=$true;lateCancellationCommitted=2;temporaryFiles=0}
        'V5-S06'=@{conflicts=2;generation=1}; 'V5-S07'=@{rejected=3;originalPreserved=$true}; 'V5-S08'=@{views=49;revisions=1;oversizedRejected=$true}
        'V5-I01'=@{receipts=6;grouped=6}; 'V5-I02'=@{frozenCount=1;currentCount=2}
        'V5-I03'=@{records=1;unchanged=$true;running=$false}; 'V5-I04'=@{secondGroups=1;firstGroups=0;active=0;maxConcurrent=2}
        'V5-I05'=@{oldApplied=0;active=0;reservedAfter=0;leases=0}; 'V5-I06'=@{running=$true;reservedAfter=0}
        'V5-UI01'=@{diagnosticFocused=$true;oldResultPreserved=$true;repaired=$true}; 'V5-UI02'=@{previewSamples=20;applied=$false;sampleStart=21;lastSampleCount=1;keyStatesVisible=$true;invalidStartRejected=$true}
        'V5-UI03'=@{views=2;loaded='调用过程';applied=$false}; 'V5-UI04'=@{compactBack=$true}
        'V5-UI05'=@{invalidPreserved=$true;unknownRows=1}; 'V5-UI06'=@{groupsBefore=4;reset=$true}; 'V5-UI07'=@{rounds=20;preserved=$true;active=0}; 'V5-UI08'=@{}
        'V5-P01'=@{records=10000;fields=32;groups=5000;components=4;warmups=2;reservedAfter=0}
        'V5-P02'=@{encoded=33554432;decoded=33554432;bodyBytes=262144;bombReads=128;decodedBudgetRejected=$true;expansionState='BudgetExceeded';reservedAfter=0;contentAfter=0;leases=0}
        'V5-P03'=@{smoke=$false;renderer='Avalonia Desktop';realHost=$false;physicalInput=$false;concurrent=20;streams=5;retained=10000;cycles=20;analysisAfter=0;contentAfter=0;activeAfter=0}
        'V5-P04'=@{rounds=20;active=0;leases=0;reservedAfter=0}
        'V5-R01'=@{inherited=143;privateBytes=268435456;stopMilliseconds=10000}
        'V5-R02'=@{unchangedBody=$true;running=$true;analysisAfter=0;contentAfter=0}
    }
}
function Test-M2FixedValues($Actual, $Expected) {
    foreach ($field in $Expected.Keys) {
        Require-M1Fields $Actual @($field)
        $value = $Expected[$field]
        if ($value -is [bool]) { Require-M1Boolean $Actual $field $value }
        elseif ($value -is [string]) { Assert-Check ($Actual[$field] -is [string] -and $Actual[$field] -ceq $value) "V5文本不符：$field" }
        else { Require-UXNumbers $Actual @($field); Assert-Check ($Actual[$field] -eq $value) "V5数值不符：$field" }
    }
}
function Test-M2Coverage($C) {
    Require-UXNumbers $C @('Retained','SourceExcluded','OutsideWindow','FilterFalse','FilterUnknown','FilterTrue','GroupedRecords','UnresolvedGroupRecords','Total','Evicted')
    foreach ($name in @('Retained','SourceExcluded','OutsideWindow','FilterFalse','FilterUnknown','FilterTrue','GroupedRecords','UnresolvedGroupRecords','Total','Evicted')) {
        Assert-Check ($C[$name] -ge 0 -and $C[$name] -eq [Math]::Floor($C[$name])) 'V5计数不是非负整数。'
    }
    Assert-Check ($C.Retained -eq $C.SourceExcluded+$C.OutsideWindow+$C.FilterFalse+$C.FilterUnknown+$C.FilterTrue -and
        $C.FilterTrue -eq $C.GroupedRecords+$C.UnresolvedGroupRecords -and $C.Total -ge $C.Retained) 'V5统计桶不守恒。'
}
function Test-M2Profile($P, $Old) {
    Test-M2FixedValues $P @{schemaVersion=1;streamSeconds=600;pageCycles=20;uiSamples=200;concurrent=20;streams=5;refreshes=50;successes=45;warmups=2;repetitions=10;p95QueryMilliseconds=2000;maxAnalysisBytes=67108864;cancelMilliseconds=1000}
    Test-M2FixedValues $P.limits @{SourceBytes=65536;Tokens=8192;Depth=32;Fields=32;Diagnostics=50;ValueCharacters=4096;CollectionItems=128;JsonDepth=32;JsonTokens=16384;PathSegments=16;BodyBytes=262144;RecordOperations=10000;QueryOperations=5000000;Records=10000;Groups=5000;QueryBodyBytes=33554432;AnalysisBytes=67108864}
    foreach ($name in @('maxPrivateDeltaBytes','maxContentBytes','maxQueueBytes','maxStopMilliseconds','maxHandlesAfterStop','uiP95Milliseconds','uiMaxMilliseconds')) {
        Require-UXNumbers $P @($name); Assert-Check ($P[$name] -eq $Old[$name]) "V5不得放宽前置预算：$name"
    }
    Assert-Check ($P.layouts.Count -eq 12 -and ($P.behaviors -join ',') -ceq 'diagnostic,unresolved,archive,parallel') 'V5布局profile错误。'
    $keys=@()
    foreach($kind in @('analysis','editor')){foreach($theme in @('light','dark')){foreach($width in @(1200,800,520)){
        $key="$kind-$theme-$width";$keys+=$key;$spec=@($P.layouts|Where-Object key -CEQ $key)
        Assert-Check ($spec.Count -eq 1) 'V5布局组合被更改。'
        Test-M2FixedValues $spec[0] @{width=$width;height=$(if($width -eq 1200){720}elseif($width -eq 800){600}else{420});editor=($kind -eq 'editor');dark=($theme -eq 'dark')}
    }}}
}
function Test-M2Layout($Shots, $Directory, $Profile) {
    $expected = @($Profile.layouts.key) + @($Profile.behaviors)
    Assert-Check ($Shots.Count -eq 16 -and (@($Shots.key | Sort-Object) -join ',') -ceq (@($expected | Sort-Object) -join ',')) 'V5截图缺少、重复或未知。'
    foreach ($item in $Shots) {
        Require-M1Fields $item @('key','name','width','height','scaling','renderer','realHost','sha256','measurements')
        Require-M1Boolean $item 'realHost' $false
        Require-UXNumbers $item @('width','height','scaling')
        Assert-Check ($item.name -ceq "m2-$($item.key).png" -and $item.renderer -ceq 'Avalonia Headless / Skia' -and $item.scaling -eq 1) 'V5图像身份错误。'
        $path=Join-Path $Directory $item.name
        Assert-Check ((Get-FileHash $path).Hash -ceq $item.sha256) 'V5图像hash不符。'
        $bitmap=[System.Drawing.Bitmap]::FromFile($path)
        try { Assert-Check ($bitmap.Width -eq $item.width -and $bitmap.Height -eq $item.height) 'V5图像尺寸不符。' } finally { $bitmap.Dispose() }
        $side=Get-Content "$path.json" -Raw | ConvertFrom-Json -AsHashtable
        Assert-Check (($side|ConvertTo-Json -Depth 20 -Compress) -ceq ($item|ConvertTo-Json -Depth 20 -Compress)) 'V5图片sidecar与汇总不同。'
        $spec=@($Profile.layouts|Where-Object key -CEQ $item.key)
        if ($spec.Count -eq 0) { Assert-Check ($item.width -eq 800 -and $item.height -eq 600) 'V5行为图尺寸错误。'; continue }
        Assert-Check ($item.width -eq $spec[0].width -and $item.height -eq $spec[0].height) 'V5布局尺寸错误。'
        Require-M1Boolean $item.measurements 'editor' $spec[0].editor; Require-M1Boolean $item.measurements 'dark' $spec[0].dark
        foreach ($button in @('stop','action')) {
            $b=$item.measurements[$button]; Require-UXNumbers $b @('x','y','width','height')
            Assert-Check ($b.x -ge 0 -and $b.y -ge 0 -and $b.width -gt 0 -and $b.height -gt 0 -and $b.x+$b.width -le $item.width -and $b.y+$b.height -le $item.height) 'V5关键按钮不可达。'
        }
    }
}
function Test-M2Performance($Data, $Directory, $Profile) {
    $p=$Data['V5-P01']; Require-UXNumbers $p @('p95','peak')
    Assert-Check ($p.measurements.Count -eq 10 -and $p.operations.Count -eq 10 -and $p.p95 -eq @($p.measurements|Sort-Object)[9] -and $p.p95 -le $Profile.p95QueryMilliseconds -and $p.peak -le $Profile.maxAnalysisBytes) 'V5一万记录测量不足或超限。'
    foreach ($v in $p.measurements) { Assert-Check ($v -is [ValueType] -and [double]::IsFinite($v) -and $v -gt 0 -and $v -le 10000) 'V5查询计时无效。' }
    foreach ($v in $p.operations) { Assert-Check ($v -ge 320000 -and $v -le 5000000) 'V5未求值全部字段或操作超限。' }
    $p=$Data['V5-P02']; Require-UXNumbers $p @('reads','contentPeak','analysisPeak')
    Assert-Check ($p.reads -ge 257 -and $p.contentPeak -le $Profile.maxContentBytes -and $p.analysisPeak -le $Profile.maxAnalysisBytes) 'V5正文预算观察缺失。'
    $p=$Data['V5-P04']; Assert-Check ($p.measurements.Count -eq 20) 'V5取消轮次不足。'
    foreach ($v in $p.measurements) { Assert-Check ($v -is [ValueType] -and [double]::IsFinite($v) -and $v -ge 0 -and $v -le $Profile.cancelMilliseconds) 'V5取消释放超时。' }
    $p=$Data['V5-P03']; Require-UXNumbers $p @('seconds','successes','launched','sampleCount','p95','maximum','baseline','baselineHandles','stoppedHandles','stopMs')
    Assert-Check ($p.seconds -ge 600 -and $p.successes -ge 50 -and $p.successes -eq $p.launched -and $p.runs.Count -eq $p.successes -and $p.sampleCount -ge 200 -and $p.stopMs -le 10000 -and $p.stoppedHandles-$p.baselineHandles -le 32) 'V5持续负载/停止不满足。'
    foreach ($run in $p.runs) {
        Require-UXNumbers $run @('milliseconds','SnapshotRevision')
        Assert-Check ($run.milliseconds -gt 0 -and $run.milliseconds -le 10000 -and $run.Coverage.GroupedRecords -eq 10000 -and $run.RuleHash -cmatch '^[A-F0-9]{64}$') 'V5刷新未完整完成。'
        Test-M2Coverage $run.Coverage
        Assert-Check ([DateTimeOffset]$run.From -lt [DateTimeOffset]$run.To) 'V5执行窗口无效。'
    }
    Assert-Check (@($p.runs.AnalysisId|Select-Object -Unique).Count -eq $p.successes) 'V5重复使用同一结果冒充刷新。'
    $path=Join-Path $Directory 'm2-performance-samples.csv'
    Assert-Check ((Get-FileHash $path).Hash -ceq $p.csvSha256) 'V5原曲线hash错误。'
    $points=@(Import-Csv $path)
    Assert-Check ($points.Count -eq $p.sampleCount -and $points.Count -ge 1200 -and [double]$points[-1].Seconds-[double]$points[0].Seconds -ge 599) 'V5采样数量或时长不足。'
    $last=-1.0; $lastUtc=[DateTimeOffset]::MinValue
    foreach ($point in $points) {
        foreach ($name in @('Seconds','PrivateBytes','Handles','Queue','Content','Analysis','Tasks','Active','Retained','Delay')) {
            $n=0.0; Assert-Check ([double]::TryParse($point.$name,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$n) -and [double]::IsFinite($n) -and $n -ge 0) 'V5曲线不是有效非负数字。'
        }
        Assert-Check ([double]$point.Seconds -gt $last -and [DateTimeOffset]$point.Utc -gt $lastUtc) 'V5采样时间不单调。'
        $last=[double]$point.Seconds; $lastUtc=[DateTimeOffset]$point.Utc
        Assert-Check ([long]$point.PrivateBytes-$p.baseline -le $Profile.maxPrivateDeltaBytes -and [long]$point.Content -le $Profile.maxContentBytes -and [long]$point.Analysis -le $Profile.maxAnalysisBytes -and [long]$point.Queue -le $Profile.maxQueueBytes -and [int]$point.Tasks -le 2 -and [int]$point.Active -eq 5 -and [int]$point.Retained -eq 10000) 'V5持续资源/负载越界。'
    }
    $delays=@($points|ForEach-Object{[double]$_.Delay}|Sort-Object)
    Assert-Check ($p.p95 -eq $delays[[int][Math]::Ceiling($delays.Count*.95)-1] -and $p.maximum -eq $delays[-1] -and $p.p95 -le 200 -and $p.maximum -le 1000) 'V5 UI分位数不能从曲线复算或超限。'
}
function Test-CompleteM2Evidence($Directory,$RunId,$SourceHash,$ProfileHash,$UXProfileHash,$M2ProfileHash,$Since,$Rows,$M0Map,$M1Map,$UXMap,$M2Map,$M0Profile,$M1Profile,$UXProfile,$M2Profile) {
    $old=Test-CompleteUXEvidence $Directory $RunId $SourceHash $ProfileHash $UXProfileHash $Since $Rows $M0Map $M1Map $UXMap $M0Profile $M1Profile $UXProfile
    $coverage=Test-M2ScenarioMap $M2Map $Rows
    Test-M2Profile $M2Profile $M1Profile
    $data=@{}
    foreach ($group in @('language','grouping','timeline','rules-storage','lifetime','layout','performance','regression')) {
        $path=Join-Path $Directory "m2-$group.json"
        $raw=Get-Content $path -Raw|ConvertFrom-Json -AsHashtable
        Require-M1Fields $raw @('m2ProfileHash','scenarioIds','scenarios')
        Assert-Check ($raw.m2ProfileHash -is [string] -and $raw.m2ProfileHash -ceq $M2ProfileHash) 'V5 profile身份错误。'
        $expected=@(Get-RequiredM2Scenarios|Where-Object{(Get-M2EvidenceGroup $_) -ceq $group}|Sort-Object)
        Assert-Check (($expected -join ',') -ceq (@($raw.scenarioIds|Sort-Object)-join ',') -and ($expected -join ',') -ceq (@($raw.scenarios.Keys|Sort-Object)-join ',')) 'V5证据索引/内容不完整。'
        $entries=Read-UXEvidence $path $RunId $SourceHash $ProfileHash $UXProfileHash $Since
        foreach ($id in $entries.Keys) { $data[$id]=$entries[$id] }
    }
    $expectations=Get-M2ExpectedValues
    foreach ($id in @(Get-RequiredM2Scenarios)) { Test-M2FixedValues $data[$id] $expectations[$id] }
    $q=$data['V5-Q01']; Test-M2Coverage $q.Coverage
    Test-M2FixedValues $q.Coverage @{Retained=12;SourceExcluded=1;OutsideWindow=2;FilterTrue=9;GroupedRecords=7;UnresolvedGroupRecords=2}
    Assert-Check (($q.groups -join ',') -ceq '3,1,2,1' -and ($q.alternate -join ',') -ceq '5,1,1' -and ($q.unresolved -join ',') -ceq 'Value / Missing,Value / NotReady') 'V5成员数量或未知状态错误。'
    Assert-Check ($q.members.Count -eq 4 -and $q.expectedMembers.Count -eq 4 -and @($q.members|ForEach-Object{$_}|Select-Object -Unique).Count -eq 7) 'V5成员重复/缺失。'
    for($i=0;$i -lt 4;$i++) { Assert-Check (($q.members[$i] -join ',') -ceq ($q.expectedMembers[$i] -join ',') -and $q.members[$i].Count -eq $q.groups[$i]) 'V5分组成员不符。' }
    foreach ($name in @('AnalysisId','SourceId','SessionId','RuleRevisionId')) { $g=[guid]::Empty; Assert-Check ([guid]::TryParse($q[$name],[ref]$g) -and $g -ne [guid]::Empty) 'V5结果身份错误。' }
    Assert-Check ($q.RuleHash -cmatch '^[A-F0-9]{64}$' -and [DateTimeOffset]$q.From -lt [DateTimeOffset]$q.To) 'V5规则摘要/窗口错误。'
    Test-M2Coverage $data['V5-Q03']; Test-M2Coverage $data['V5-UI05'].coverage
    Assert-Check ($data['V5-L09'].start -eq $data['V5-L09'].expectedStart -and ($data['V5-Q06'].columns -join ',') -ceq 'capital') 'V5诊断跨度或列错误。'
    Assert-Check (($data['V5-T02'].actual -join ',') -ceq ($data['V5-T02'].expected -join ',') -and $data['V5-T02'].actual.Count -eq 4) 'V5时间线顺序错误。'
    Assert-Check ([DateTimeOffset]$data['V5-T01'].From -eq [DateTimeOffset]'2026-09-30T12:00:00Z' -and [DateTimeOffset]$data['V5-T01'].To -eq [DateTimeOffset]'2026-09-30T12:30:00Z') 'V5边界窗口错误。'
    Assert-Check ($data['V5-T03'].rows.Count -eq 2 -and $data['V5-T03'].rows[0].ElapsedMilliseconds -eq 120000 -and $data['V5-T03'].rows[0].state -ceq 'Receiving' -and $data['V5-T03'].rows[1].ElapsedMilliseconds -eq 0 -and $data['V5-T03'].rows[1].state -ceq 'Cancelled') 'V5耗时/开放端状态错误。'
    Assert-Check ([DateTimeOffset]$data['V5-T06'].To -eq [DateTimeOffset]$data['V5-T06'].reopenedTo -and [DateTimeOffset]$data['V5-T06'].To -eq [DateTimeOffset]'2026-09-30T12:20:00.0000001Z') 'V5历史锚点漂移。'
    Assert-Check ($data['V5-S03'].hash -cmatch '^[A-F0-9]{64}$' -and $data['V5-I01'].recordIds.Count -eq 6 -and $data['V5-I01'].bodyBytes -gt 0) 'V5保存摘要或实际捕获不足。'
    Assert-Check ($data['V5-UI04'].selected -ceq $data['V5-UI04'].opened -and $data['V5-R02'].beforeId -ceq $data['V5-R02'].afterId -and ($data['V5-R01'].counts -join ',') -ceq '50,53,40') 'V5请求身份或前置集合错误。'
    Test-M2Layout $data['V5-UI08'].shots $Directory $M2Profile
    Test-M2Performance $data $Directory $M2Profile
    $desktop=Read-UXEvidence (Join-Path $Directory 'm2-desktop.json') $RunId $SourceHash $ProfileHash $UXProfileHash $Since
    $desktopEnvelope=Get-Content (Join-Path $Directory 'm2-desktop.json') -Raw|ConvertFrom-Json -AsHashtable
    Assert-Check ($desktopEnvelope.m2ProfileHash -ceq $M2ProfileHash -and ($desktopEnvelope.scenarioIds -join ',') -ceq 'sustained') 'V5桌面原始证据身份错误。'
    Assert-Check (($desktop.sustained|ConvertTo-Json -Depth 30 -Compress) -ceq ($data['V5-P03']|ConvertTo-Json -Depth 30 -Compress)) 'V5桌面原始证据与测试观察不同。'
    return @($old+$coverage)
}
