# 本地开发检查的可测试边界：进程、报告、映射和文档。函数不执行发布或部署。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-CheckedProcess {
    param([string] $File, [string[]] $Arguments, [string] $WorkingDirectory,
          [string] $LogPath, [int] $TimeoutSeconds, [System.Collections.Generic.List[object]] $History)
    $info = [System.Diagnostics.ProcessStartInfo]::new($File)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $started = [DateTimeOffset]::UtcNow
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    $timedOut = $false
    try {
        Assert-Check $process.Start() "不能启动 $File"
        # 同时消费两条管道，防止大量构建输出阻塞子进程。
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            $process.Kill($true) # 只终止本函数拥有的进程树，不按进程名清理其他开发任务。
            Assert-Check $process.WaitForExit(10000) "超时进程树不能结束。"
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($LogPath, $output)
        $record = [ordered]@{ file = $File; arguments = $Arguments; startedUtc = $started
            finishedUtc = [DateTimeOffset]::UtcNow; exitCode = $process.ExitCode; timedOut = $timedOut; log = $LogPath }
        if ($null -ne $History) { $History.Add($record) }
        Assert-Check (-not $timedOut) "命令超时：$File，日志：$LogPath"
        Assert-Check ($process.ExitCode -eq 0) "命令失败（$($process.ExitCode)）：$File，日志：$LogPath"
        Assert-Check (-not [string]::IsNullOrWhiteSpace($output)) "命令未产生输出：$File"
        return $record
    } finally { $process.Dispose() }
}

function Get-SourceHash([string] $Root) {
    $paths = @(& git -C $Root ls-files --cached --others --exclude-standard | Sort-Object -Unique)
    Assert-Check ($LASTEXITCODE -eq 0) '不能枚举源码。'
    $rows = foreach ($path in $paths) {
        # 文档允许在测量期间补充结果；所有影响行为的源码、构建配置、锁、门禁/profile 均进入摘要。
        if ($path -match '^(src|tests|tools)/' -or $path -match '^docs/examples/m2/.*\.dsl$' -or $path -match '^[^/]+\.(props|targets|slnx|json)$') {
            $full = Join-Path $Root $path
            if (Test-Path -LiteralPath $full -PathType Leaf) { "$path=$((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash)" }
        }
    }
    Assert-Check (@($rows).Count -gt 0) '源码清单为空。'
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join [char]10))))
}

function Read-CheckedTrx([string] $Path, [DateTimeOffset] $Since) {
    Assert-Check (Test-Path -LiteralPath $Path) "缺少 TRX：$Path"
    $xml = [xml][IO.File]::ReadAllText($Path)
    $ns = [Xml.XmlNamespaceManager]::new($xml.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $times = $xml.SelectSingleNode('/t:TestRun/t:Times', $ns)
    Assert-Check ($null -ne $times) 'TRX 缺少时间。'
    Assert-Check ([DateTimeOffset]::Parse($times.start) -ge $Since.AddSeconds(-2)) 'TRX 来自旧运行。'
    Assert-Check ([DateTimeOffset]::Parse($times.finish) -ge [DateTimeOffset]::Parse($times.start)) 'TRX 时间顺序不合法。'
    $counter = $xml.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $ns)
    $summary = $xml.SelectSingleNode('/t:TestRun/t:ResultSummary', $ns)
    Assert-Check ($summary.GetAttribute('outcome') -notin @('Failed', 'Aborted', 'Error')) 'TRX 整体运行失败。'
    Assert-Check ($null -ne $counter) 'TRX 缺少统计。'
    $results = @($xml.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns))
    $definitions = @($xml.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest', $ns))
    Assert-Check ($results.Count -gt 0) '零测试不能通过。'
    foreach ($name in @('total', 'executed', 'passed')) {
        Assert-Check ($counter.GetAttribute($name) -eq [string]$results.Count) "TRX $name 与实际结果不一致。"
    }
    foreach ($attribute in $counter.Attributes) {
        if ($attribute.Name -notin @('total', 'executed', 'passed')) { Assert-Check ([int]$attribute.Value -eq 0) "TRX 包含非通过统计：$($attribute.Name)" }
    }
    Assert-Check ($definitions.Count -eq $results.Count) 'TRX 定义和执行数不一致。'
    Assert-Check (@($results.executionId | Select-Object -Unique).Count -eq $results.Count) 'TRX 执行身份重复。'
    $rows = foreach ($result in $results) {
        Assert-Check ($result.outcome -ceq 'Passed') "测试未通过：$($result.testName)"
        $definition = @($definitions | Where-Object { $_.id -ceq $result.testId })
        Assert-Check ($definition.Count -eq 1) 'TRX 执行结果没有唯一测试定义。'
        $method = $definition[0].SelectSingleNode('t:TestMethod', $ns)
        Assert-Check ($null -ne $method) 'TRX 缺少方法。'
        [pscustomobject]@{ name = [string]$result.testName; method = "$($method.className).$($method.name)" }
    }
    return @($rows)
}

function Get-RequiredScenarios {
    foreach ($group in @{ U = 14; P = 10; S = 6; B = 7; R = 7; L = 6 }.GetEnumerator()) {
        foreach ($i in 1..$group.Value) { '{0}{1:00}' -f $group.Key, $i }
    }
}

function Test-ScenarioMap($Map, [object[]] $Rows) {
    $required = @(Get-RequiredScenarios | Sort-Object)
    $ids = @($Map.scenarios.id | Sort-Object)
    Assert-Check (($required -join ',') -ceq ($ids -join ',')) '场景映射缺项、重复或含未知 ID。'
    foreach ($scenario in $Map.scenarios) {
        Assert-Check (@($scenario.tests).Count -gt 0) "场景 $($scenario.id) 没有真实测试。"
        foreach ($test in $scenario.tests) {
            $actual = @($Rows | Where-Object { $_.method -ceq $test.method })
            Assert-Check ($actual.Count -gt 0) "场景 $($scenario.id) 方法未执行：$($test.method)"
            foreach ($row in $test.rows) {
                Assert-Check (@($actual | Where-Object { $_.name -ceq ($test.method + $row) }).Count -eq 1) "场景 $($scenario.id) 缺少参数行：$row"
            }
        }
    }
    return @($Map.scenarios | ForEach-Object { [ordered]@{ id = $_.id; status = 'passed'; tests = $_.tests } })
}

function Read-CheckedEvidence([string] $Path, [string] $RunId, [string] $SourceHash, [DateTimeOffset] $Since) {
    Assert-Check (Test-Path -LiteralPath $Path) "缺少证据：$Path"
    $data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    Assert-Check ($data.schemaVersion -eq 1 -and $data.runId -ceq $RunId -and $data.sourceHash -ceq $SourceHash) '证据 schema/运行/源码身份不匹配。'
    Assert-Check ([DateTimeOffset]::Parse($data.recordedUtc.ToString()) -ge $Since.AddSeconds(-2)) '证据来自旧运行。'
    Assert-Check ($data.scenarios.Count -gt 0) '证据场景为空。'
    return $data.scenarios
}

function Test-ProtocolEvidence($Protocol, $Stream) {
    foreach ($id in @('P01', 'P02', 'P07-P09', 'R01-R04-h1', 'R01-R04-P08-fallback', 'R07')) {
        Assert-Check $Protocol.Contains($id) "缺少协议证据：$id"
    }
    $h2 = $Protocol['P07-P09']
    Assert-Check ($h2.assessmentPassed -eq $true -and $h2.supported -eq $true -and $h2.clientProtocol -eq '2.0' -and
        $h2.originProtocol -eq '2.0' -and $h2.isolatedCancellation -eq $true -and $h2.receipts.Count -ge 3) 'h2 声明缺少两端实际观测。'
    foreach ($key in @('R01-R04-h1', 'R01-R04-P08-fallback')) {
        $mapping = $Protocol[$key]
        Assert-Check ($mapping.aReceipts.Count -eq 6 -and $mapping.bReceipts.Count -eq 6 -and $mapping.observed -eq '1.1' -and
            $mapping.mappingHttp2Supported -eq $false -and $mapping.exactHttp2Rejected -eq $true) '映射证据或降级策略不完整。'
        foreach ($receipt in $mapping.bReceipts) {
            Assert-Check ($receipt.Target -eq 'B' -and $receipt.Sni -eq 'localhost' -and $receipt.Host -match '^localhost:\d+$' -and
                $receipt.BodyHash -match '^[A-F0-9]{64}$') 'B 身份/SNI/正文证据不完整。'
        }
        foreach ($receipt in $mapping.aReceipts) { Assert-Check ($receipt.Target -eq 'A') '未命中请求路由错误。' }
    }
    Assert-Check $Stream.Contains('S01-S02') '缺少流式证据。'
    $sse = $Stream['S01-S02']
    Assert-Check ($sse.incrementalBeforeCompletion -eq $true -and $sse.observedBytes -gt 0 -and $sse.first -eq 'data: 中文😀' -and
        [DateTimeOffset]$sse.clientFirstUtc -lt [DateTimeOffset]$sse.completedUtc -and
        [DateTimeOffset]$sse.recorderObservedUtc -lt [DateTimeOffset]$sse.completedUtc) '首事件/记录观察未发生在结束前。'
    $mapped = $Protocol['R07']
    Assert-Check ($mapped.incremental -eq $true -and $mapped.aReceipts.Count -eq 0 -and $mapped.bReceipts.Count -eq 2 -and
        $mapped.digest.Bytes -eq 134217728 -and $mapped.digest.Hash -match '^[A-F0-9]{64}$') '映射流/大文件证据不完整。'
}

function Test-ResourceEvidence($Resource, [string] $Directory, $Profile) {
    Assert-Check $Resource.Contains('B01-B07-S06') '资源证据场景缺失。'
    $data = $Resource['B01-B07-S06']
    Assert-Check ($data.budgetPassed -eq $true -and $data.transfers.Count -eq 18 -and $data.modes.Count -eq 3) '资源批次不完整。'
    $samplesPath = Join-Path $Directory 'resource-samples.csv'
    Assert-Check (Test-Path -LiteralPath $samplesPath) '资源采样缺失。'
    $samples = @(Import-Csv -LiteralPath $samplesPath)
    Assert-Check ($samples.Count -eq $data.sampleCount -and $samples.Count -gt 1200) '资源采样数量不一致。'
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $Directory 'resource-profile.json')).Hash -eq
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'm0-resource-profile.json')).Hash) '运行 profile 与冻结预算不一致。'
    foreach ($mode in @('None', 'MetadataOnly', 'BoundedBody')) {
        $medians = foreach ($size in $Profile.sizes) {
            $batch = @($data.transfers | Where-Object { $_.Mode -eq $mode -and $_.Size -eq $size })
            Assert-Check ($batch.Count -eq 3 -and @($batch.Iteration | Sort-Object -Unique).Count -eq 3) '资源大小对照不完整。'
            foreach ($transfer in $batch) {
                Assert-Check ($transfer.Hash -match '^[A-F0-9]{64}$' -and $transfer.Samples -ge 2 -and $transfer.Milliseconds -gt 0 -and
                    $transfer.PeakDelta -le $Profile.maxPrivateDeltaBytes) '传输摘要/过程峰值无效。'
                if ($size -eq 134217728) { Assert-Check ($transfer.Hash -eq $data.directDigest.Hash) '直连与代理摘要不一致。' }
            }
            # 各模式同一大小摘要必须一致；测试侧另外与生成器独立计算的 SHA-256 比较。
            Assert-Check (@($data.transfers | Where-Object Size -eq $size | Select-Object -ExpandProperty Hash -Unique).Count -eq 1) '大小相同的传输摘要不一致。'
            @($batch.PeakDelta | Sort-Object)[1]
        }
        Assert-Check ($medians[1] - $medians[0] -le $Profile.maxMedianGrowthBytes) '大文件内存中位数增长超预算。'
        $result = @($data.modes | Where-Object mode -eq $mode)
        Assert-Check ($result.Count -eq 1) '记录模式缺失。'
        $result = $result[0]
        Assert-Check ($result.ExitCode -eq 0 -and $result.Milliseconds -le $Profile.maxStopMilliseconds -and $result.portReleased -eq $true -and
            $result.filesClosed -eq $true -and $result.Metrics.ActiveRequests -eq 0 -and $result.Metrics.QueueBytes -eq 0 -and
            $result.Handles -le $result.baseline.Handles + $Profile.maxHandleGrowthAfterStop -and
            $result.fileBytes -eq $result.Metrics.StoredBytes -and $result.fileBytes -le $Profile.maxDiskBytes) '退出或记录释放证据不完整。'
        if ($mode -eq 'BoundedBody') { Assert-Check ($result.streamSeconds -ge $Profile.streamSeconds -and $result.fileBytes -gt 0) '持续流时长或正文记录不足。' }
        else { Assert-Check ($result.fileBytes -eq 0) '无正文模式产生正文文件。' }
    }
    foreach ($sample in $samples) {
        Assert-Check ([long]$sample.privateBytes -gt 0 -and [long]$sample.workingSet -gt 0 -and [long]$sample.managedBytes -gt 0 -and
            [int]$sample.handles -gt 0 -and [int]$sample.queueBytes -ge 0 -and [int]$sample.queueBytes -le $Profile.maxQueueBytes -and
            [int]$sample.activeRequests -ge 0 -and [int]$sample.activeRequests -le $Profile.maxActiveRequests) '资源样本字段/预算错误。'
        $modeName = if ($sample.phase.StartsWith('None')) { 'None' } elseif ($sample.phase.StartsWith('MetadataOnly')) { 'MetadataOnly' } else { 'BoundedBody' }
        $baseline = @($data.modes | Where-Object mode -eq $modeName)[0].baseline
        Assert-Check ([long]$sample.privateBytes - [long]$baseline.PrivateBytes -le $Profile.maxPrivateDeltaBytes -and
            [long]$sample.storedBytes -ge 0 -and [long]$sample.storedBytes -le $Profile.maxDiskBytes) '全过程内存/磁盘预算超限。'
    }
    $sustained = @($samples | Where-Object phase -eq 'sustained-stream')
    Assert-Check ($sustained.Count -eq $data.sustainedSampleCount -and $sustained.Count -ge 1200 -and
        ([DateTimeOffset]$sustained[-1].utc - [DateTimeOffset]$sustained[0].utc).TotalSeconds -ge 599) '持续流采样时间不足。'
}

function Test-MarkdownFiles([string[]] $Files) {
    foreach ($file in $Files) {
        $content = [IO.File]::ReadAllText($file)
        foreach ($match in [regex]::Matches($content, '\[[^\]\r\n]+\]\((?<target>[^)\r\n]+)\)')) {
            $target = $match.Groups['target'].Value.Trim('<', '>')
            if ($target -match '^[a-z][a-z0-9+.-]*://|^mailto:') { continue }
            $parts = $target.Split('#', 2)
            $relative = [Uri]::UnescapeDataString($parts[0])
            $destination = if ($relative.Length -eq 0) { $file } else { [IO.Path]::GetFullPath((Join-Path (Split-Path $file) $relative)) }
            Assert-Check (Test-Path -LiteralPath $destination) "坏链接：$file -> $target"
            if ($parts.Count -eq 2 -and $destination.EndsWith('.md')) {
                $slugs = [System.Collections.Generic.HashSet[string]]::new()
                foreach ($heading in [regex]::Matches([IO.File]::ReadAllText($destination), '(?m)^#{1,6}\s+(.+?)\s*#*\s*$')) {
                    $slug = $heading.Groups[1].Value.ToLowerInvariant() -replace '[^\p{L}\p{N}_\s-]', '' -replace '\s', '-'
                    $candidate = $slug; $suffix = 0
                    while (-not $slugs.Add($candidate)) { $suffix++; $candidate = "$slug-$suffix" }
                }
                Assert-Check ($slugs.Contains([Uri]::UnescapeDataString($parts[1]))) "坏锚点：$file -> $target"
            }
        }
    }
}
