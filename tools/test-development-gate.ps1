# 失败注入只验证门禁边界；构造的报告永远不进入业务证据目录。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevelopmentChecks.ps1')
$root = Split-Path $PSScriptRoot
$directory = Join-Path $root ('TestResults/GateSelfTests/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$count = 0
function Reject([string] $Name, [scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Check $rejected "自测失败：错误输入被接受：$Name"
    $script:count++
}
function Save([string] $Name, [string] $Content) {
    $path = Join-Path $directory $Name
    [IO.File]::WriteAllText($path, $Content)
    return $path
}
$now = [DateTimeOffset]::UtcNow
$utc = $now.ToString('O')
$valid = @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
 <Times start="$utc" finish="$utc" />
 <Results><UnitTestResult testId="one" executionId="first" testName="Example.Method(value: 1)" outcome="Passed" /></Results>
 <TestDefinitions><UnitTest id="one"><TestMethod className="Example" name="Method" /></UnitTest></TestDefinitions>
 <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary>
</TestRun>
"@
$path = Save 'good.trx' $valid
$rows = @(Read-CheckedTrx $path $now)
Assert-Check ($rows.Count -eq 1) '有效 TRX 被拒绝。'
Reject '缺报告' { Read-CheckedTrx (Join-Path $directory 'missing.trx') $now }
Reject '旧报告' { Read-CheckedTrx $path $now.AddHours(1) }
Reject '截断 XML' { Read-CheckedTrx (Save 'broken.trx' '<TestRun>') $now }
foreach ($outcome in @('Skipped', 'NotExecuted', 'Aborted', 'Failed', 'Inconclusive')) {
    Reject $outcome { Read-CheckedTrx (Save 'outcome.trx' $valid.Replace('outcome="Passed"', "outcome=""$outcome""")) $now }
}
Reject '统计矛盾' { Read-CheckedTrx (Save 'counter.trx' $valid.Replace('passed="1"', 'passed="0"')) $now }
Reject '定义丢失' { Read-CheckedTrx (Save 'definition.trx' $valid.Replace('testId="one"', 'testId="missing"')) $now }
Reject '零测试' { Read-CheckedTrx (Save 'zero.trx' ($valid -replace '<UnitTestResult[^>]+/>', '')) $now }
$map = [pscustomobject]@{ scenarios = @(Get-RequiredScenarios | ForEach-Object {
    [pscustomobject]@{ id = $_; tests = @([pscustomobject]@{ method = 'Example.Method'; rows = @('(value: 1)') }) }
}) }
Test-ScenarioMap $map $rows | Out-Null
$copy = $map | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$copy.scenarios = @($copy.scenarios | Select-Object -Skip 1)
Reject '缺必测 ID' { Test-ScenarioMap $copy $rows }
Reject '方法未执行' { Test-ScenarioMap $map @([pscustomobject]@{ method = 'Example.Missing'; name = 'Example.Missing' }) }
Reject '缺理论行' { Test-ScenarioMap $map @([pscustomobject]@{ method = 'Example.Method'; name = 'Example.Method(value: 2)' }) }
$json = @{ schemaVersion = 1; runId = 'self'; sourceHash = 'hash'; recordedUtc = $utc; scenarios = @{ P01 = @{ observed = '1.1' } } } | ConvertTo-Json -Depth 10
$jsonPath = Save 'good.json' $json
Read-CheckedEvidence $jsonPath 'self' 'hash' $now | Out-Null
Reject '损坏 JSON' { Read-CheckedEvidence (Save 'broken.json' '{') 'self' 'hash' $now }
Reject '旧 runId' { Read-CheckedEvidence $jsonPath 'other' 'hash' $now }
Reject '源码身份变化' { Read-CheckedEvidence $jsonPath 'self' 'changed' $now }
Reject '旧证据时间' { Read-CheckedEvidence $jsonPath 'self' 'hash' $now.AddHours(1) }
Reject '缺协议回执' { Test-ProtocolEvidence @{} @{} }
Reject '缺资源场景' { Test-ResourceEvidence @{} $directory @{} }
# 构造一套完整有效证据，再逐项破坏内容；防止检查器永远拒绝或只验证 JSON 能解析。
$hash = 'A' * 64
$receipt = @{ Target = 'B'; Sni = 'localhost'; Host = 'localhost:123'; BodyHash = $hash }
$mapping = @{ aReceipts = @(1..6 | ForEach-Object { @{ Target = 'A' } }); bReceipts = @(1..6 | ForEach-Object { $receipt })
    observed = '1.1'; mappingHttp2Supported = $false; exactHttp2Rejected = $true }
$protocol = @{ P01 = @{}; P02 = @{}; 'R01-R04-h1' = $mapping; 'R01-R04-P08-fallback' = $mapping
    'P07-P09' = @{ assessmentPassed = $true; supported = $true; clientProtocol = '2.0'; originProtocol = '2.0'; isolatedCancellation = $true; receipts = @(1,2,3) }
    R07 = @{ incremental = $true; aReceipts = @(); bReceipts = @(1,2); digest = @{ Bytes = 134217728; Hash = $hash } } }
$stream = @{ 'S01-S02' = @{ incrementalBeforeCompletion = $true; observedBytes = 20; first = 'data: 中文😀'
    clientFirstUtc = $utc; recorderObservedUtc = $utc; completedUtc = $now.AddSeconds(1).ToString('O') } }
Test-ProtocolEvidence $protocol $stream
$badProtocol = $protocol | ConvertTo-Json -Depth 15 | ConvertFrom-Json -AsHashtable
$badProtocol['P07-P09'].originProtocol = 'unknown'
Reject '伪 h2 支持' { Test-ProtocolEvidence $badProtocol $stream }
$badProtocol = $protocol | ConvertTo-Json -Depth 15 | ConvertFrom-Json -AsHashtable
$badProtocol['R01-R04-h1'].bReceipts = @()
Reject '缺 B 回执' { Test-ProtocolEvidence $badProtocol $stream }
$badStream = $stream | ConvertTo-Json -Depth 15 | ConvertFrom-Json -AsHashtable
$badStream['S01-S02'].clientFirstUtc = $now.AddHours(1).ToString('O')
Reject '结束后才收到首事件' { Test-ProtocolEvidence $protocol $badStream }
$profilePath = Join-Path $PSScriptRoot 'm0-resource-profile.json'
$profile = Get-Content $profilePath -Raw | ConvertFrom-Json -AsHashtable
Copy-Item -LiteralPath $profilePath -Destination (Join-Path $directory 'resource-profile.json')
$transfers = foreach ($mode in @('None', 'MetadataOnly', 'BoundedBody')) {
    foreach ($size in $profile.sizes) {
        foreach ($i in 0..2) { @{ Mode = $mode; Size = $size; Iteration = $i; Hash = $hash; Samples = 2; Milliseconds = 1; PeakDelta = 1 } }
    }
}
$modes = foreach ($mode in @('None', 'MetadataOnly', 'BoundedBody')) {
    $stored = if ($mode -eq 'BoundedBody') { 1 } else { 0 }
    @{ mode = $mode; ExitCode = 0; Milliseconds = 1; portReleased = $true; filesClosed = $true
        Metrics = @{ ActiveRequests = 0; QueueBytes = 0; StoredBytes = $stored }; Handles = 1
        baseline = @{ Handles = 1; PrivateBytes = 1 }; fileBytes = $stored; streamSeconds = 600 }
}
$samples = foreach ($i in 0..1200) {
    [pscustomobject]@{ phase = 'sustained-stream'; utc = $now.AddMilliseconds($i * 500).ToString('O')
        privateBytes = 1; workingSet = 1; managedBytes = 1; handles = 1; activeRequests = 1; queueBytes = 0; storedBytes = 0 }
}
$samples | Export-Csv -LiteralPath (Join-Path $directory 'resource-samples.csv') -NoTypeInformation
$resource = @{ 'B01-B07-S06' = @{ budgetPassed = $true; transfers = @($transfers); modes = @($modes)
    directDigest = @{ Hash = $hash }; sampleCount = 1201; sustainedSampleCount = 1201 } }
Test-ResourceEvidence $resource $directory $profile
$badResource = $resource | ConvertTo-Json -Depth 15 | ConvertFrom-Json -AsHashtable
$badResource['B01-B07-S06'].transfers[0].Hash = 'B' * 64
Reject '错误传输哈希' { Test-ResourceEvidence $badResource $directory $profile }
$badResource = $resource | ConvertTo-Json -Depth 15 | ConvertFrom-Json -AsHashtable
$badResource['B01-B07-S06'].modes[2].portReleased = $false
Reject '监听残留' { Test-ResourceEvidence $badResource $directory $profile }
$badResource = $resource | ConvertTo-Json -Depth 15 | ConvertFrom-Json -AsHashtable
$badResource['B01-B07-S06'].transfers[0].PeakDelta = $profile.maxPrivateDeltaBytes + 1
Reject '超内存预算' { Test-ResourceEvidence $badResource $directory $profile }
$badResource = $resource | ConvertTo-Json -Depth 15 | ConvertFrom-Json -AsHashtable
$badResource['B01-B07-S06'].modes[2].streamSeconds = 1
Reject '缩短持续流' { Test-ResourceEvidence $badResource $directory $profile }
Rename-Item -LiteralPath (Join-Path $directory 'resource-samples.csv') -NewName 'hidden-samples.csv'
Reject '缺资源采样' { Test-ResourceEvidence $resource $directory $profile }
Rename-Item -LiteralPath (Join-Path $directory 'hidden-samples.csv') -NewName 'resource-samples.csv'
$validMarkdown = Save 'good.md' "# 中文标题"
Test-MarkdownFiles @((Save 'link.md' '[有效](good.md#中文标题)'))
Reject '坏链接' { Test-MarkdownFiles @((Save 'bad.md' '[不存在](missing.md)')) }
Reject '坏锚点' { Test-MarkdownFiles @((Save 'bad-anchor.md' '[不存在](good.md#错误)')) }
Reject '未跟踪文档' { Test-MarkdownFiles @((Save 'untracked.md' '[新文档坏链接](absent.md)')) }
Invoke-CheckedProcess 'pwsh' @('-NoProfile', '-Command', 'Write-Output ok; exit 0') $root (Join-Path $directory 'ok.log') 10 $null | Out-Null
Reject '非零退出' { Invoke-CheckedProcess 'pwsh' @('-NoProfile', '-Command', 'Write-Output failed; exit 7') $root (Join-Path $directory 'fail.log') 10 $null }
# 写一个真正带子进程的超时程序，验证 Kill(entireProcessTree) 不只结束父进程。
$childFile = Join-Path $directory 'child-id.txt'
$timeoutScript = Save 'timeout.ps1' @'
param([string] $PidFile)
$start = [Diagnostics.ProcessStartInfo]::new('pwsh')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
foreach ($item in @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30')) { $start.ArgumentList.Add($item) }
$child = [Diagnostics.Process]::Start($start)
[IO.File]::WriteAllText($PidFile, [string]$child.Id)
Write-Output waiting
Start-Sleep -Seconds 30
'@
Reject '进程树超时' { Invoke-CheckedProcess 'pwsh' @('-NoProfile', '-File', $timeoutScript, '-PidFile', $childFile) $root (Join-Path $directory 'timeout.log') 3 $null }
Assert-Check (Test-Path -LiteralPath $childFile) '超时自测没有真正创建子进程。'
$childId = [int][IO.File]::ReadAllText($childFile)
Assert-Check ($null -eq (Get-Process -Id $childId -ErrorAction SilentlyContinue)) '超时子进程仍残留。'
Write-Output "门禁自测通过：$count 个失败注入及有效输入检查；$directory"
