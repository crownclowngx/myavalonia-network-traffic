# 正反例是隔离校验器夹具，不是业务证据。复用旧自测，并让所有 V4 破坏项经过同一个完整汇总入口。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestM1DevelopmentGate.ps1')
. (Join-Path $PSScriptRoot 'UXDevelopmentChecks.ps1')
$uxProfile = Get-Content (Join-Path $PSScriptRoot 'ux-profile.json') -Raw | ConvertFrom-Json -AsHashtable
$uxHash = (Get-FileHash (Join-Path $PSScriptRoot 'ux-profile.json')).Hash
$uxMap = [pscustomobject]@{ schemaVersion = 1; scenarios = @(Get-RequiredUXScenarios | ForEach-Object { [pscustomobject]@{ id = $_; tests = @([pscustomobject]@{ method = 'UXExample.Method'; rows = @(''); results = 1 }) } }) }
$allRows += [pscustomobject]@{ method = 'UXExample.Method'; name = 'UXExample.Method' }
function UXEvidence([string] $Name, $Data) {
    @{ schemaVersion = 1; runId = 'self'; sourceHash = 'hash'; profileHash = $profileHash; uxProfileHash = $uxHash; recordedUtc = [DateTimeOffset]::UtcNow; scenarioIds = @($Data.Keys); scenarios = $Data } |
        ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $directory "$Name.json") -Encoding utf8
}
$layout = @{}
foreach ($spec in @($uxProfile.layouts) + @($uxProfile.behaviors | ForEach-Object { @{ key = $_; width = 900; height = 600; tool = $false; dark = $false } })) {
    $name = "ux-$($spec.key).png"; $path = Join-Path $directory $name
    $bitmap = [System.Drawing.Bitmap]::new([int]$spec.width, [int]$spec.height)
    try { $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
    $item = @{ name = $name; width = $spec.width; height = $spec.height; scaling = 1; renderer = 'Avalonia Headless / Skia'; realHost = $false;
        sha256 = (Get-FileHash $path).Hash; test = 'NetWorkTrafficPlugin.Tests.Ui.UXCaptureUiTests'; measurements = @{
            width = $spec.width; height = $spec.height; dark = $spec.dark; tool = $spec.tool; top = 76; rowHeight = 48; fullRows = 9; containers = 10; bodyHeight = 200;
            stopReachable = $true; stop = @{ x = 50; y = 6; width = 74; height = 28 } } }
    $item | ConvertTo-Json -Depth 20 | Set-Content "$path.json" -Encoding utf8
    $layout[$spec.key] = $item
}
UXEvidence 'ux-layout' $layout
UXEvidence 'ux-reading' @{ independent = @{ listPending = 0; eventPending = 1; listFollowing = $false; eventFollowing = $false; duplicateCount = 3; retainedCount = 2 };
    anchor = @{ beforeId = 'one'; afterId = 'one'; anchorError = 0; pending = 1; restorations = 2; input = 'MouseWheel'; resumed = $true };
    stream = @{ total = 271; retained = 256; pending = 1; listFollowing = $true; eventFollowing = $false; observed = 7000 } }
UXEvidence 'ux-interactions' @{ 'late-query' = @{ lateApplied = 0; QueryPending = $false; path = '/b' };
    draft = @{ oldRows = 2; appliedRows = 2; debounceMs = 200; combinationPreserved = $true; invalidPreserved = $true };
    range = @{ total = 10001; matched = 5000; restored = 10001; otherPage = 200; activePreserved = $true };
    'feedback-find' = @{ queryLimit = 256; successMilliseconds = 3000; staleApplied = 0; wrapPosition = 0 };
    'proxy-states' = @{ starts = 1; disposes = 1; states = @('Stopped','Starting','Running','Stopping','Stopped'); shared = $true } }
UXEvidence 'ux-content-storage' @{ 'save-range' = @{ matched = 1; saved = 2; requestBytes = 6; responseBytes = 5; responseHash = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('first')))); reopenedRunning = $false };
    content = @{ modes = 3; preservedMatch = 4; reservedAfter = 0; unicode = $true; bodyWasNotRedacted = $true } }
UXEvidence 'ux-lifetime' @{ picker = @{ cancelCalls = 1; lateWrites = 0; disposedWrites = 0; reservedAfter = 0; running = $true };
    reattach = @{ rounds = 20; selectedBefore = 'one'; selectedAfter = 'one'; mode = 2; running = $true } }
UXEvidence 'ux-performance' @{ sustained = @{ rounds = 20; sampleCount = 200; seconds = 600; baselineHandles = 100; stoppedHandles = 102; contentAfter = 0;
    renderer = 'Avalonia Desktop'; realHost = $false; physicalInput = $false; curveSha256 = (Get-FileHash $nativePath).Hash;
    anchors = @(foreach ($n in 0..20) { @{ kind = $(if ($n -eq 0) { 'insert' } else { 'revision' }); input = 'Dispatcher command'; before = 'one'; after = 'one'; offsetBefore = -8; offsetAfter = -8; error = 0 } }) } }
function Complete { Test-CompleteUXEvidence $directory 'self' 'hash' $profileHash $uxHash $since $allRows $map $m1Map $uxMap $profile $m1Profile $uxProfile | Out-Null }
function BrokenUX([string] $Name, [scriptblock] $Mutation, [bool] $SyncImages = $false) {
    $file = Join-Path $directory "$Name.json"; $backup = [IO.File]::ReadAllText($file)
    try {
        $data = $backup | ConvertFrom-Json -AsHashtable; & $Mutation $data
        if ($SyncImages) { foreach ($image in $data.scenarios.Values) { $image | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $directory ($image.name + '.json')) -Encoding utf8 } }
        $data | ConvertTo-Json -Depth 30 | Set-Content $file -Encoding utf8
        Reject $Name { Complete }
    } finally {
        [IO.File]::WriteAllText($file, $backup)
        if ($SyncImages) { foreach ($image in ($backup | ConvertFrom-Json -AsHashtable).scenarios.Values) { $image | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $directory ($image.name + '.json')) -Encoding utf8 } }
    }
}
Complete
$validUXMap = $uxMap | ConvertTo-Json -Depth 20
foreach ($id in @(Get-RequiredUXScenarios)) {
    $uxMap.scenarios = @($uxMap.scenarios | Where-Object id -NE $id); Reject "缺 $id" { Complete }; $uxMap = $validUXMap | ConvertFrom-Json
}
foreach ($mutation in @({ $uxMap.scenarios += $uxMap.scenarios[0] }, { $uxMap.scenarios[0].id = 'unknown' }, { $uxMap.scenarios[0].tests = @() },
    { $uxMap.scenarios[0].tests[0].method = 'missing' }, { $uxMap.scenarios[0].tests[0].rows = @('(missing: 1)') }, { $uxMap.scenarios[0].tests[0].results = 2 })) {
    & $mutation; Reject '映射破坏' { Complete }; $uxMap = $validUXMap | ConvertFrom-Json
}
foreach ($field in @('schemaVersion','runId','sourceHash','profileHash','uxProfileHash','recordedUtc','scenarios','scenarioIds')) { BrokenUX 'ux-reading' { param($d) $d.Remove($field) | Out-Null } }
foreach ($field in @('runId','sourceHash','profileHash','uxProfileHash')) { BrokenUX 'ux-reading' { param($d) $d[$field] = 'old' } }
BrokenUX 'ux-reading' { param($d) $d.schemaVersion = 2 }
BrokenUX 'ux-reading' { param($d) $d.recordedUtc = $since.AddDays(-1) }
BrokenUX 'ux-reading' { param($d) $d.recordedUtc = [DateTimeOffset]::UtcNow.AddDays(1) }
BrokenUX 'ux-reading' { param($d) $d.scenarios.independent.listFollowing = 'false' }
BrokenUX 'ux-reading' { param($d) $d.scenarios.independent.listPending = '0' }
foreach ($key in @($layout.Keys)) { BrokenUX 'ux-layout' { param($d) $d.scenarios.Remove($key) | Out-Null; $d.scenarioIds = @($d.scenarios.Keys) } }
foreach ($field in @('top','rowHeight','containers')) { BrokenUX 'ux-layout' { param($d) $d.scenarios['document-light-800'].measurements[$field] = 10000 } $true }
foreach ($field in @('bodyHeight','fullRows')) { BrokenUX 'ux-layout' { param($d) $d.scenarios['document-light-520'].measurements.bodyHeight = 0; $d.scenarios['document-light-800'].measurements.fullRows = 0 } $true }
BrokenUX 'ux-layout' { param($d) $d.scenarios['document-light-800'].measurements.stopReachable = $false } $true
BrokenUX 'ux-layout' { param($d) $d.scenarios['document-light-800'].scaling = 2 } $true
BrokenUX 'ux-layout' { param($d) $d.scenarios['document-light-800'].width = 801 } $true
$png = Join-Path $directory 'ux-json-tree.png'; $bytes = [IO.File]::ReadAllBytes($png)
try { [IO.File]::WriteAllText($png, 'invalid'); BrokenUX 'ux-layout' { param($d) $d.scenarios['json-tree'].sha256 = (Get-FileHash $png).Hash } $true } finally { [IO.File]::WriteAllBytes($png, $bytes) }
BrokenUX 'ux-reading' { param($d) $d.scenarios.anchor.anchorError = 3 }
BrokenUX 'ux-reading' { param($d) $d.scenarios.stream.listFollowing = $false }
BrokenUX 'ux-reading' { param($d) $d.scenarios.stream.pending = 2 }
BrokenUX 'ux-reading' { param($d) $d.scenarios.stream.observed = 0 }
BrokenUX 'ux-interactions' { param($d) $d.scenarios['late-query'].lateApplied = 1 }
BrokenUX 'ux-content-storage' { param($d) $d.scenarios['save-range'].saved = 1 }
BrokenUX 'ux-content-storage' { param($d) $d.scenarios['save-range'].requestBytes = 5 }
BrokenUX 'ux-content-storage' { param($d) $d.scenarios['save-range'].reopenedRunning = $true }
BrokenUX 'ux-lifetime' { param($d) $d.scenarios.picker.lateWrites = 1 }
BrokenUX 'ux-lifetime' { param($d) $d.scenarios.picker.disposedWrites = 1 }
foreach ($field in @('protocol-probes','m1-storage','m1-ui-native')) {
    $file = Join-Path $directory "$field.json"; $backup = [IO.File]::ReadAllText($file)
    try { [IO.File]::Delete($file); Reject "UX 缺前置 $field" { Complete } } finally { [IO.File]::WriteAllText($file, $backup) }
}
foreach ($field in @('stoppedHandles','contentAfter')) { BrokenUX 'ux-performance' { param($d) $d.scenarios.sustained[$field] = 1000000000 } }
foreach ($field in @('sampleCount','rounds','seconds')) { BrokenUX 'ux-performance' { param($d) $d.scenarios.sustained[$field] = 1 } }
BrokenUX 'ux-performance' { param($d) $d.scenarios.sustained.anchors[0].error = 3 }
BrokenUX 'ux-performance' { param($d) $d.scenarios.sustained.curveSha256 = 'changed' }
BrokenEvidence 'm1-ui-native' { param($d) $d.scenarios.sustained.p95 = 201 }
BrokenEvidence 'm1-ui-native' { param($d) $d.scenarios.sustained.contentPeak = 33554433 }
BrokenEvidence 'm1-storage' { param($d) $d.scenarios['snapshot-watermark'].state = 'Complete' }
$oldLimit = $uxProfile.anchorMax; $uxProfile.anchorMax = 3; Reject '量尺放宽' { Complete }; $uxProfile.anchorMax = $oldLimit
$scope.mode = 'UX'; Test-DevelopmentScopeReport $scope 'UX'
foreach ($flag in @('realClientVerified','hostVerified','manualAccepted','deployed','windowsCiExecuted','releaseGateExecuted')) { $scope[$flag] = $true; Reject "UX 越界声明 $flag" { Test-DevelopmentScopeReport $scope 'UX' }; $scope[$flag] = $false }
$scope.mode = 'Docs'; Reject 'Docs 冒充 UX' { Test-DevelopmentScopeReport $scope 'UX' }
Complete
Write-Output "M0 + M1 + UX 完整路径门禁自测通过：$count 个拒绝检查；夹具：$directory"

