# 隔离夹具明确是校验器输入，绝不作为产品行为证据。先跑原全部自测，再经正式共用 M1 完整校验路径逐项破坏。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-development-gate.ps1')
. (Join-Path $PSScriptRoot 'M1DevelopmentChecks.ps1')
$m1Profile = Get-Content (Join-Path $PSScriptRoot 'm1-resource-profile.json') -Raw | ConvertFrom-Json -AsHashtable
$profileHash = (Get-FileHash (Join-Path $PSScriptRoot 'm1-resource-profile.json')).Hash
$since = [DateTimeOffset]::UtcNow.AddMinutes(-20)
$m1Map = [pscustomobject]@{ schemaVersion = 1; scenarios = @(Get-RequiredM1Scenarios | ForEach-Object {
    [pscustomobject]@{ id = $_; tests = @([pscustomobject]@{ method = 'M1Example.Method'; rows = @(''); results = 1 }) }
}) }
$allRows = @($rows) + @([pscustomobject]@{ method = 'M1Example.Method'; name = 'M1Example.Method' })
function Evidence([string] $Name, $Data) {
    @{ schemaVersion = 1; runId = 'self'; sourceHash = 'hash'; profileHash = $profileHash; recordedUtc = [DateTimeOffset]::UtcNow; scenarios = $Data } |
        ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $directory "$Name.json") -Encoding utf8
}
Evidence 'protocol-probes' $protocol; Evidence 'stream-probes' $stream; Evidence 'resource-probes' $resource
$capture = @{}
foreach ($h2 in @($false,$true)) { foreach ($encoding in @('gzip','br')) {
    $version = if ($h2) { '2.0' } else { '1.1' }
    $capture["encoded-$h2-$encoding"] = @{ RecordId = [guid]::NewGuid(); ClientProtocol = $version; OriginProtocol = $version; representation = 'EncodedEntity'; requestObserved = 8; requestStored = 8; responseObserved = 9; responseStored = 9; requestHash = $hash; responseHash = $hash; receipts = 1 }
} }
foreach ($known in @($false,$true)) { foreach ($cancel in @($false,$true)) {
    $receiptHashes = if ($cancel -and $known) { @() } elseif ($cancel) { @('B' * 64) } else { @($hash) }
    $capture["upload-$known-$cancel"] = @{ knownLength = $known; cancel = $cancel; observedBeforeEnd = 65536; observed = $(if ($cancel) {65536} else {2097152}); state = 'Partial'; transport = $(if ($cancel) {'Failed'} else {'Completed'}); receipts = @($receiptHashes).Count;
        receiptHashes = @($receiptHashes); expectedCompleteHash = $hash; expectedObservedHash = $(if ($cancel) {'B' * 64} else {$hash}); stateBeforeStop = 'Partial' }
} }
foreach ($pair in @(@(200,'HEAD',0),@(204,'GET',0),@(304,'GET',0),@(302,'GET',0),@(206,'GET',4))) {
    $capture["response-$($pair[0])-$($pair[1])"] = @{ status = $pair[0]; method = $pair[1]; bytes = $pair[2]; observed = $pair[2]; receipts = 1 }
}
Evidence 'm1-capture' $capture
$storage = @{ roundtrip = @{ SnapshotId = [guid]::NewGuid(); Records = 3; totalBodyBytes = 20; source = 'one'; actual = 'one'; state = 'Complete'; bodyBytes = 8; reopenedRunning = $false }
    'snapshot-watermark' = @{ frozen = 5; copied = 5; BodyBytes = 5; state = 'Receiving' } }
foreach ($stage in @('snapshot','body','records','manifest','commit')) { $storage["failure-$stage"] = @{ point = $stage; temporaryDirectories = 0; targetPreserved = $true } }
Evidence 'm1-storage' $storage
Evidence 'm1-lifetime' @{ pages = @{ rounds = 20; reservedAfter = 0; peak = 4194304; running = $true } }
Evidence 'm1-query' @{ retention = @{ Total = 10011; retained = 10001; Evicted = 10; matched = 10000; activeRetained = $true } }
Evidence 'm1-stream' @{ 'independent-budget' = @{ Total = 301; retained = 256; Evicted = 45; stored = 4; gap = $true }; 'h2-isolation' = @{ activeRecordId = 'stream-a'; endedRecordId = 'stream-b'; ClientProtocol = '2.0'; OriginProtocol = '2.0'; events = 15; activeCount = 1; idleCancellationSupported = $false; idlePolicy = 'notify-only'; diagnostic = 'UnsupportedH2Cancellation'; transportAtIdle = 'Receiving'; captureAtIdle = 'Partial'; sameClientConnection = $true; continuedAfterCancel = $true; stoppedActiveCount = 0 } }
$m1Transfers = [Collections.Generic.List[object]]::new(); $m1Samples = [Collections.Generic.List[object]]::new()
foreach ($mode in $m1Profile.modes) { foreach ($direction in $m1Profile.directions) { foreach ($size in $m1Profile.sizes) { foreach ($repeat in 0..2) {
    $phase = "$mode-$direction-$size-$repeat"; $stored = if ($mode -eq 'MetadataOnly') { 0 } elseif ($mode -eq 'BoundedBody') { 1048576 } else { $size }
    $state = if ($mode -eq 'MetadataOnly') { 'MetadataOnly' } elseif ($mode -eq 'BoundedBody') { 'Partial' } else { 'Complete' }
    $m1Transfers.Add(@{ phase = $phase; mode = $mode; direction = $direction; size = $size; repeat = $repeat; knownLength = $repeat -ne 1; expectContinue = $direction -eq 'upload' -and $repeat -eq 2;
        hash = $hash; storedHash = $hash; expectedPrefix = $hash; observed = $size; stored = $stored; state = $state; baseline = @{ PrivateBytes = 100; Handles = 1 };
        peak = 3; sampleCount = 3; milliseconds = 100; stopMilliseconds = 1; stoppedHandles = 1; physical = $stored; portReleased = $true })
    foreach ($n in 1..3) { $m1Samples.Add([pscustomobject]@{ Phase = $phase; Utc = $now.ToString('O'); PrivateBytes = 100 + $n; WorkingSet = 1; ManagedBytes = 1; Handles = 1; ActiveRequests = 1; QueueBytes = 0; StoredBytes = $stored }) }
} } } }
$m1Samples | Export-Csv -LiteralPath (Join-Path $directory 'm1-resource-samples.csv') -NoTypeInformation
Evidence 'm1-resources' @{ transfers = @{ transfers = @($m1Transfers.ToArray()); sampleCount = $m1Samples.Count; csvSha256 = (Get-FileHash (Join-Path $directory 'm1-resource-samples.csv')).Hash; profileSha256 = $profileHash } }
$ui = @{}; $images = @{}
function FixtureImage([string] $Name, [int] $Width, [int] $Height) {
    $path = Join-Path $directory $Name
    $bitmap = [System.Drawing.Bitmap]::new($Width,$Height)
    try { $bitmap.Save($path,[System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
    $images[$Name] = @{ name = $Name; width = $Width; height = $Height; sha256 = (Get-FileHash $path).Hash; renderer = 'Avalonia Headless / Skia'; realHost = $false }
}
foreach ($tool in @($false,$true)) { foreach ($dark in @($false,$true)) {
    $sizes = if ($tool) { @(420,280) } else { @(1200,800,520) }
    foreach ($width in $sizes) {
        $height = 420
        $ui["layout-$tool-$dark-$width"] = @{ width = $width; height = $height; dark = $dark; tool = $tool; stopReachable = $true; realHost = $false }
        $name = 'm1-' + $(if ($tool) {'tool'} else {'document'}) + '-' + $(if ($dark) {'dark'} else {'light'}) + "-$width.png"
        FixtureImage $name $width $height
    }
} }
foreach ($name in @('m1-sse.png','m1-resource.png','m1-quota.png','m1-readonly-partial.png')) { FixtureImage $name 300 200 }
Evidence 'm1-ui' $ui; Evidence 'm1-ui-screenshots' $images
$nativePoints = @(foreach ($n in 0..199) { @{ utc = $now.AddSeconds(-600 + $n * (599.5/199)); seconds = $n * (599.5/199); privateBytes = 101 + $n; handles = 10; queue = 0; content = 100; active = 5; retained = 10005; delay = 1 } })
$nativePath = Join-Path $directory 'm1-ui-native-samples.json'; $nativePoints | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $nativePath
Evidence 'm1-ui-native' @{ sustained = @{ renderer = 'Avalonia Desktop'; realHost = $false; physicalInput = $false; smoke = $false; seconds = 600; rounds = 20; sampleCount = 200;
    latency = @(1..200 | ForEach-Object { 1 }); p95 = 1; maximum = 1; baseline = 100; maxPrivate = 300; contentPeak = 100; contentAfter = 0; stopMs = 1;
    virtualized = @(0,5000,9999 | ForEach-Object { @{ index = $_; containers = 10; visible = 5 } }); parsedUtc = $now; visibleUtc = $now.AddMilliseconds(1);
    pausedContinued = $true; originReceipts = 10020; curveSha256 = (Get-FileHash $nativePath).Hash } }
function Complete { Test-CompleteM1Evidence $directory 'self' 'hash' $profileHash $since $allRows $map $m1Map $profile $m1Profile | Out-Null }
function BrokenEvidence([string] $Name, [scriptblock] $Change) {
    $path = Join-Path $directory "$Name.json"; $original = [IO.File]::ReadAllText($path)
    try { $data = $original | ConvertFrom-Json -AsHashtable; & $Change $data; $data | ConvertTo-Json -Depth 30 | Set-Content $path; Reject $Name { Complete } }
    finally { [IO.File]::WriteAllText($path, $original) }
}
Complete # 正例必须能通过完整继承路径。
$validMap = $m1Map | ConvertTo-Json -Depth 15
foreach ($id in @(Get-RequiredM1Scenarios)) {
    $m1Map = $validMap | ConvertFrom-Json; $m1Map.scenarios = @($m1Map.scenarios | Where-Object id -ne $id); Reject "缺 $id" { Complete }
    $m1Map = $validMap | ConvertFrom-Json; $m1Map.scenarios += @($m1Map.scenarios | Where-Object id -eq $id); Reject "重复 $id" { Complete }
}
$m1Map = $validMap | ConvertFrom-Json
$m1Map.scenarios[0].id = 'unknown'; Reject '未知 ID' { Complete }; $m1Map = $validMap | ConvertFrom-Json
$m1Map.scenarios[0].tests = @(); Reject '空映射' { Complete }; $m1Map = $validMap | ConvertFrom-Json
$m1Map.scenarios[0].tests[0].method = 'Fake.Method'; Reject '假方法' { Complete }; $m1Map = $validMap | ConvertFrom-Json
$m1Map.scenarios[0].tests[0].rows = @('(missing: 1)'); Reject '缺参数行' { Complete }; $m1Map = $validMap | ConvertFrom-Json
foreach ($field in @('schemaVersion','runId','sourceHash','profileHash','recordedUtc','scenarios')) {
    BrokenEvidence 'm1-capture' { param($d) $d.Remove($field) | Out-Null }
}
BrokenEvidence 'm1-capture' { param($d) $d.schemaVersion = 9 }
BrokenEvidence 'm1-capture' { param($d) $d.runId = 'old' }
BrokenEvidence 'm1-capture' { param($d) $d.sourceHash = 'changed' }
BrokenEvidence 'm1-capture' { param($d) $d.profileHash = 'changed' }
BrokenEvidence 'm1-capture' { param($d) $d.recordedUtc = $since.AddDays(-1) }
BrokenEvidence 'm1-capture' { param($d) $d.scenarios['encoded-True-br'].receipts = '1' }
BrokenEvidence 'm1-capture' { param($d) $d.scenarios['upload-True-True'].cancel = 'true' }
BrokenEvidence 'm1-capture' { param($d) $d.scenarios['upload-False-True'].receiptHashes = @($d.scenarios['upload-False-True'].expectedCompleteHash) }
BrokenEvidence 'm1-capture' { param($d) $d.scenarios['upload-False-True'].stateBeforeStop = 'Complete' }
BrokenEvidence 'm1-capture' { param($d) $d.scenarios['upload-False-True'].transport = 'Completed' }
BrokenEvidence 'm1-storage' { param($d) $d.scenarios['snapshot-watermark'].copied = 6 }
BrokenEvidence 'm1-storage' { param($d) $d.scenarios['snapshot-watermark'].state = 'Completed' }
BrokenEvidence 'm1-storage' { param($d) $d.scenarios.roundtrip.reopenedRunning = $true }
BrokenEvidence 'm1-lifetime' { param($d) $d.scenarios.pages.reservedAfter = 1 }
BrokenEvidence 'm1-query' { param($d) $d.scenarios.retention.activeRetained = $false }
BrokenEvidence 'm1-stream' { param($d) $d.scenarios['h2-isolation'].idleCancellationSupported = $true }
BrokenEvidence 'm1-stream' { param($d) $d.scenarios['h2-isolation'].idlePolicy = 'cancel' }
BrokenEvidence 'm1-stream' { param($d) $d.scenarios['h2-isolation'].diagnostic = '' }
BrokenEvidence 'm1-stream' { param($d) $d.scenarios['h2-isolation'].transportAtIdle = 'Failed' }
BrokenEvidence 'm1-stream' { param($d) $d.scenarios['h2-isolation'].captureAtIdle = 'Complete' }
BrokenEvidence 'm1-stream' { param($d) $d.scenarios['h2-isolation'].sameClientConnection = $false }
BrokenEvidence 'm1-stream' { param($d) $d.scenarios['h2-isolation'].continuedAfterCancel = $false }
BrokenEvidence 'm1-stream' { param($d) $d.scenarios['h2-isolation'].stoppedActiveCount = 1 }
foreach ($field in @('peak','sampleCount','stopMilliseconds','physical')) { BrokenEvidence 'm1-resources' { param($d) $d.scenarios.transfers.transfers[0][$field] = [long]::MaxValue } }
BrokenEvidence 'm1-resources' { param($d) $d.scenarios.transfers.transfers = @($d.scenarios.transfers.transfers | Select-Object -Skip 1) }
BrokenEvidence 'm1-resources' { param($d) $d.scenarios.transfers.transfers[0].state = 'Complete' }
BrokenEvidence 'm1-resources' { param($d) $d.scenarios.transfers.transfers[1].knownLength = $true }
BrokenEvidence 'm1-resources' { param($d) $d.scenarios.transfers.csvSha256 = 'changed' }
BrokenEvidence 'm1-ui' { param($d) $d.scenarios.Remove('layout-False-False-520') | Out-Null }
BrokenEvidence 'm1-ui' { param($d) $d.scenarios['layout-False-False-520'].stopReachable = $false }
BrokenEvidence 'm1-ui-screenshots' { param($d) $d.scenarios['m1-sse.png'].width = 1 }
BrokenEvidence 'm1-ui-screenshots' { param($d) $d.scenarios.Remove('m1-resource.png') | Out-Null }
foreach ($field in @('smoke','realHost','physicalInput')) { BrokenEvidence 'm1-ui-native' { param($d) $d.scenarios.sustained[$field] = $true } }
foreach ($field in @('contentPeak','contentAfter','maximum','p95')) { BrokenEvidence 'm1-ui-native' { param($d) $d.scenarios.sustained[$field] = [long]::MaxValue } }
BrokenEvidence 'm1-ui-native' { param($d) $d.scenarios.sustained.virtualized[0].containers = 10000 }
BrokenEvidence 'm1-ui-native' { param($d) $d.scenarios.sustained.seconds = 60 }
BrokenEvidence 'm1-ui-native' { param($d) $d.scenarios.sustained.curveSha256 = 'changed' }
$file = Join-Path $directory 'protocol-probes.json'; $backup = [IO.File]::ReadAllText($file)
try { [IO.File]::Delete($file); Reject 'M1 缺仅 M0 必需证据' { Complete } } finally { [IO.File]::WriteAllText($file,$backup) }
$savedRows = $allRows; $allRows = @($allRows | Where-Object method -ne 'Example.Method'); Reject 'M1 漏 M0 测试集合' { Complete }; $allRows = $savedRows
$png = Join-Path $directory 'm1-sse.png'; $pngBytes = [IO.File]::ReadAllBytes($png)
try {
    [IO.File]::WriteAllText($png, 'invalid PNG')
    BrokenEvidence 'm1-ui-screenshots' { param($d) $d.scenarios['m1-sse.png'].sha256 = (Get-FileHash $png).Hash }
} finally { [IO.File]::WriteAllBytes($png,$pngBytes) }
$csv = Join-Path $directory 'm1-resource-samples.csv'; $csvText = [IO.File]::ReadAllText($csv)
try {
    $constant = @(Import-Csv $csv); foreach ($point in $constant) { $point.PrivateBytes = '100' }; $constant | Export-Csv $csv -NoTypeInformation
    BrokenEvidence 'm1-resources' { param($d) $d.scenarios.transfers.csvSha256 = (Get-FileHash $csv).Hash }
} finally { [IO.File]::WriteAllText($csv,$csvText) }
$scope = @{ mode = 'M1'; testCount = 1; automaticPassed = $false; realClientVerified = $false; hostVerified = $false; manualAccepted = $false; deployed = $false; windowsCiExecuted = $false; releaseGateExecuted = $false }
Test-DevelopmentScopeReport $scope 'M1'
foreach ($flag in @('realClientVerified','hostVerified','manualAccepted','deployed','windowsCiExecuted','releaseGateExecuted')) {
    $scope[$flag] = $true; Reject "越界声明 $flag" { Test-DevelopmentScopeReport $scope 'M1' }; $scope[$flag] = $false
}
$scope.mode = 'Docs'; Reject 'Docs 冒充 M1' { Test-DevelopmentScopeReport $scope 'M1' }
$sourceBefore = Get-SourceHash $root; $sourceProbe = Join-Path $PSScriptRoot ('gate-source-selftest-' + [guid]::NewGuid().ToString('N') + '.txt')
try { [IO.File]::WriteAllText($sourceProbe,'changed'); Assert-Check ((Get-SourceHash $root) -cne $sourceBefore) '新行为文件未纳入源码摘要。' }
finally { [IO.File]::Delete($sourceProbe) }
Assert-Check ((Get-SourceHash $root) -ceq $sourceBefore) '源码摘要自测未复原。'
Complete # 逐项复原后再证明校验器仍能接受完整正例。
Write-Output "M0 + M1 完整路径门禁自测通过：$count 个拒绝检查；夹具：$directory"
