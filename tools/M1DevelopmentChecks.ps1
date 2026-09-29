# M1 只扩展本地开发校验。进程/TRX/M0 协议与资源检查复用旧入口，不能用新证据替代旧证据。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RequiredM1Scenarios {
    foreach ($group in @{ U = 16; P = 6; Q = 4; S = 4; B = 5; D = 6; L = 4; UI = 6; R = 2 }.GetEnumerator()) {
        foreach ($number in 1..$group.Value) { 'V3-{0}{1:00}' -f $group.Key, $number }
    }
}
function Test-M1ScenarioMap($Map, [object[]] $Rows) {
    $required = @(Get-RequiredM1Scenarios | Sort-Object)
    Assert-Check ($Map.schemaVersion -eq 1 -and ($required -join ',') -ceq (@($Map.scenarios.id | Sort-Object) -join ',')) 'M1 53 项映射缺失、重复或包含未知 ID。'
    foreach ($scenario in $Map.scenarios) {
        Assert-Check (@($scenario.tests).Count -gt 0) "空映射：$($scenario.id)"
        foreach ($test in $scenario.tests) {
            $actual = @($Rows | Where-Object method -CEQ $test.method)
            Assert-Check ($test.results -gt 0 -and $actual.Count -eq $test.results -and @($test.rows).Count -eq $test.results) "M1 方法/结果行数错误：$($scenario.id) / $($test.method)"
            foreach ($row in $test.rows) { Assert-Check (@($actual | Where-Object name -CEQ ($test.method + $row)).Count -eq 1) "缺少 M1 参数行：$($test.method)$row" }
        }
    }
    return @($Map.scenarios | ForEach-Object { @{ id = $_.id; tests = $_.tests; status = 'executed' } })
}
function Test-DevelopmentScopeReport($Report, [string] $ExpectedMode) {
    Require-M1Fields $Report @('mode','testCount','automaticPassed')
    Assert-Check ($Report.mode -ceq $ExpectedMode -and $Report.automaticPassed -is [bool]) 'Docs/业务报告范围不匹配。'
    if ($ExpectedMode -eq 'Docs') { Assert-Check ($Report.testCount -eq 0) 'Docs 不得声称执行了业务测试。' }
    foreach ($flag in @('realClientVerified','hostVerified','manualAccepted','deployed','windowsCiExecuted','releaseGateExecuted')) { Require-M1Boolean $Report $flag $false }
}
function Require-M1Fields($Object, [string[]] $Names) {
    Assert-Check ($Object -is [System.Collections.IDictionary]) '证据对象类型不正确。'
    $numeric = @('schemaVersion','requestObserved','requestStored','responseObserved','responseStored','receipts','observedBeforeEnd','observed','status','bytes','Records','totalBodyBytes','BodyBytes','bodyBytes','frozen','copied','temporaryDirectories','rounds','reservedAfter','peak','Total','retained','Evicted','matched','sampleCount','stored','milliseconds','stopMilliseconds','stoppedHandles','physical','width','height','seconds','p95','maximum','baseline','maxPrivate','contentPeak','contentAfter','stopMs','originReceipts','handles','queue','content','active','delay')
    foreach ($name in $Names) {
        Assert-Check ($Object.Contains($name) -and $null -ne $Object[$name]) "证据缺少字段：$name"
        if ($name -cin $numeric -and -not ($name -eq 'baseline' -and $Object[$name] -is [System.Collections.IDictionary])) {
            $value = $Object[$name]
            Assert-Check (($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal]) -and [double]::IsFinite([double]$value)) "数值字段类型错误：$name"
        }
    }
}
function Require-M1Boolean($Object, [string] $Name, [bool] $Expected) {
    Require-M1Fields $Object @($Name)
    Assert-Check ($Object[$Name] -is [bool] -and $Object[$Name] -eq $Expected) "布尔字段错误：$Name"
}
function Read-M1Evidence([string] $Path, [string] $RunId, [string] $SourceHash, [string] $ProfileHash, [DateTimeOffset] $Since) {
    $data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    Require-M1Fields $data @('schemaVersion', 'runId', 'sourceHash', 'profileHash', 'recordedUtc', 'scenarios')
    Assert-Check ($data.profileHash -is [string] -and $data.profileHash -ceq $ProfileHash) 'M1 profile 身份不匹配。'
    Assert-Check ([DateTimeOffset]$data.recordedUtc -le [DateTimeOffset]::UtcNow.AddSeconds(2)) '证据时间来自未来。'
    return Read-CheckedEvidence $Path $RunId $SourceHash $Since
}
function Test-M1CaptureEvidence($Capture, $Storage, $Lifetime, $Query) {
    foreach ($h2 in @('False', 'True')) {
        foreach ($encoding in @('gzip', 'br')) {
            $item = $Capture["encoded-$h2-$encoding"]
            Require-M1Fields $item @('RecordId','ClientProtocol','OriginProtocol','representation','requestObserved','requestStored','responseObserved','responseStored','requestHash','responseHash','receipts')
            $protocol = if ($h2 -eq 'True') { '2.0' } else { '1.1' }
            Assert-Check ($item.ClientProtocol -eq $protocol -and $item.OriginProtocol -eq $protocol -and $item.representation -eq 'EncodedEntity' -and
                $item.requestObserved -gt 0 -and $item.requestObserved -eq $item.requestStored -and $item.responseObserved -eq $item.responseStored -and
                $item.requestHash -cmatch '^[A-F0-9]{64}$' -and $item.responseHash -cmatch '^[A-F0-9]{64}$' -and $item.receipts -eq 1) '双向实体/协议/额外请求断言不成立。'
        }
    }
    foreach ($known in @('False','True')) { foreach ($cancel in @('False','True')) {
        $item = $Capture["upload-$known-$cancel"]
        Require-M1Fields $item @('knownLength','cancel','observedBeforeEnd','observed','state','transport','receipts')
        Require-M1Boolean $item 'knownLength' ($known -eq 'True'); Require-M1Boolean $item 'cancel' ($cancel -eq 'True')
        Assert-Check ($item.observedBeforeEnd -ge 65536 -and $item.observed -ge $item.observedBeforeEnd -and $item.state -ne 'Complete') '增量/取消上传被误判完整。'
        if ($cancel -eq 'True') { Assert-Check ($item.receipts -eq 0) '取消上传却产生完整上游回执。' }
        else { Assert-Check ($item.observed -eq 2097152 -and $item.receipts -eq 1) '完整上传长度/回执不一致。' }
    } }
    foreach ($pair in @(@(200,'HEAD',0),@(204,'GET',0),@(304,'GET',0),@(302,'GET',0),@(206,'GET',4))) {
        $item = $Capture["response-$($pair[0])-$($pair[1])"]
        Require-M1Fields $item @('status','method','bytes','observed','receipts')
        Assert-Check ($item.status -eq $pair[0] -and $item.method -eq $pair[1] -and $item.bytes -eq $pair[2] -and $item.observed -eq $pair[2] -and $item.receipts -eq 1) '无正文/重定向/分段响应证据错误。'
    }
    $round = $Storage['roundtrip']; Require-M1Fields $round @('SnapshotId','Records','totalBodyBytes','source','actual','state','bodyBytes','reopenedRunning')
    Require-M1Boolean $round 'reopenedRunning' $false
    Assert-Check ($round.source -ceq $round.actual -and $round.Records -eq 3 -and $round.totalBodyBytes -ge $round.bodyBytes -and $round.state -eq 'Complete') '重开记录身份/范围错误。'
    $watermark = $Storage['snapshot-watermark']; Require-M1Fields $watermark @('frozen','copied','BodyBytes','state')
    Assert-Check ($watermark.frozen -eq 5 -and $watermark.copied -eq 5 -and $watermark.BodyBytes -eq 5 -and $watermark.state -eq 'Receiving') '快照包含后到字节或篡改接收中状态。'
    foreach ($point in @('snapshot','body','records','manifest','commit')) {
        $item = $Storage["failure-$point"]; Require-M1Fields $item @('point','temporaryDirectories','targetPreserved'); Require-M1Boolean $item 'targetPreserved' $true
        Assert-Check ($item.point -eq $point -and $item.temporaryDirectories -eq 0) '保存失败后目标/临时目录错误。'
    }
    $pages = $Lifetime['pages']; Require-M1Fields $pages @('rounds','reservedAfter','peak','running'); Require-M1Boolean $pages 'running' $true
    Assert-Check ($pages.rounds -eq 20 -and $pages.reservedAfter -eq 0 -and $pages.peak -le 33554432) '页面寿命或内容租约超限。'
    $retention = $Query['retention']; Require-M1Fields $retention @('Total','retained','Evicted','matched','activeRetained'); Require-M1Boolean $retention 'activeRetained' $true
    Assert-Check ($retention.Total -eq 10011 -and $retention.retained -eq 10001 -and $retention.Evicted -eq 10 -and $retention.matched -eq 10000) '摘要保留/查询范围错误。'
}
function Test-M1ResourceEvidence($Resource, [string] $Directory, $Profile) {
    $data = $Resource['transfers']; Require-M1Fields $data @('transfers','sampleCount','csvSha256','profileSha256')
    $curve = Join-Path $Directory 'm1-resource-samples.csv'
    Assert-Check ((Get-FileHash -LiteralPath $curve).Hash -ceq $data.csvSha256 -and
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'm1-resource-profile.json')).Hash -ceq $data.profileSha256) 'M1 曲线/profile 摘要不匹配。'
    $samples = @(Import-Csv -LiteralPath $curve)
    Assert-Check ($data.transfers.Count -eq 36 -and $samples.Count -eq $data.sampleCount -and $samples.Count -ge 108) 'M1 测量批次或采样缺失。'
    Assert-Check (@($samples.PrivateBytes | Sort-Object -Unique).Count -gt 1) 'M1 过程曲线全为常量。'
    foreach ($mode in $Profile.modes) { foreach ($direction in $Profile.directions) {
        $medians = foreach ($size in $Profile.sizes) {
            $batch = @($data.transfers | Where-Object { $_.mode -eq $mode -and $_.direction -eq $direction -and $_.size -eq $size })
            Assert-Check ($batch.Count -eq 3 -and (@($batch.repeat | Sort-Object) -join ',') -eq '0,1,2') 'M1 大小/策略/方向/重复数缺失。'
            foreach ($item in $batch) {
                Require-M1Fields $item @('phase','knownLength','expectContinue','hash','expectedPrefix','observed','stored','state','baseline','peak','sampleCount','milliseconds','stopMilliseconds','stoppedHandles','physical','portReleased')
                Require-M1Boolean $item 'portReleased' $true; Require-M1Boolean $item 'knownLength' ($item.repeat -ne 1)
                Require-M1Boolean $item 'expectContinue' ($direction -eq 'upload' -and $item.repeat -eq 2)
                $points = @($samples | Where-Object Phase -CEQ $item.phase)
                Assert-Check ($points.Count -eq $item.sampleCount -and $points.Count -ge 2) '资源阶段缺实际采样。'
                $peak = [Math]::Max(0, [long]($points.PrivateBytes | Measure-Object -Maximum).Maximum - [long]$item.baseline.PrivateBytes)
                Assert-Check ($peak -eq $item.peak -and $peak -le $Profile.maxPrivateDeltaBytes -and $item.observed -eq $size -and $item.stored -ge 0 -and $item.stored -le $size -and $item.milliseconds -gt 0 -and
                    $item.stopMilliseconds -le $Profile.maxStopMilliseconds -and $item.stoppedHandles -le $item.baseline.Handles + $Profile.maxHandlesAfterStop -and
                    $item.physical -le $Profile.maxSessionBytes + 256 -and $item.hash -cmatch '^[A-F0-9]{64}$') '资源/退出/正文观测超预算或不一致。'
                if ($item.stored -gt 0) { Assert-Check ($item.storedHash -ceq $item.expectedPrefix -and $item.storedHash -cmatch '^[A-F0-9]{64}$') '存储前缀哈希错误。' }
                Assert-Check (($item.state -eq 'Complete') -eq ($item.stored -eq $size)) '部分正文误标完整。'
                if ($mode -eq 'MetadataOnly') { Assert-Check ($item.stored -eq 0 -and $item.state -eq 'MetadataOnly') '元信息策略写入正文。' }
                if ($mode -eq 'BoundedBody') { Assert-Check ($item.stored -le 1048576) '有限正文超限。' }
            }
            @($batch.peak | Sort-Object)[1]
        }
        Assert-Check ($medians[1] - $medians[0] -le $Profile.maxMedianGrowthBytes) 'M1 大小对照中位数增长超限。'
    } }
    foreach ($size in $Profile.sizes) { Assert-Check (@($data.transfers | Where-Object size -eq $size | Select-Object -ExpandProperty hash -Unique).Count -eq 1) '同一生成体的上传/下载摘要不同。' }
    foreach ($point in $samples) {
        Assert-Check ([long]$point.PrivateBytes -gt 0 -and [long]$point.ManagedBytes -gt 0 -and [int]$point.QueueBytes -ge 0 -and [int]$point.QueueBytes -le $Profile.maxQueueBytes -and
            [int]$point.ActiveRequests -ge 0 -and [int]$point.ActiveRequests -le $Profile.activeRequests -and [long]$point.StoredBytes -le $Profile.maxSessionBytes) 'M1 采样字段/额度无效。'
    }
}
function Test-M1UiEvidence($Ui, $Images, $Native, [string] $Directory, $Profile) {
    foreach ($tool in @($false, $true)) { foreach ($dark in @($false, $true)) {
        $sizes = if ($tool) { @(420,280) } else { @(1200,800,520) }
        foreach ($width in $sizes) {
            $item = $Ui["layout-$tool-$dark-$width"]; Require-M1Fields $item @('width','height','dark','tool','stopReachable','realHost')
            Require-M1Boolean $item 'stopReachable' $true; Require-M1Boolean $item 'realHost' $false
            Require-M1Boolean $item 'dark' $dark; Require-M1Boolean $item 'tool' $tool
            Assert-Check ($item.width -eq $width) 'UI 布局尺寸不一致。'
            $name = 'm1-' + $(if ($tool) {'tool'} else {'document'}) + '-' + $(if ($dark) {'dark'} else {'light'}) + "-$width.png"
            Assert-Check $Images.Contains($name) "缺布局截图：$name"
        }
    } }
    foreach ($name in @('m1-sse.png','m1-resource.png','m1-quota.png','m1-readonly-partial.png')) { Assert-Check $Images.Contains($name) "缺专项截图：$name" }
    foreach ($image in $Images.Values) {
        Require-M1Fields $image @('name','width','height','sha256','renderer','realHost'); Require-M1Boolean $image 'realHost' $false
        Assert-Check ($image.name -ceq [IO.Path]::GetFileName($image.name) -and $image.renderer -eq 'Avalonia Headless / Skia') '截图路径或渲染来源无效。'
        $path = Join-Path $Directory $image.name
        Assert-Check ((Get-FileHash -LiteralPath $path).Hash -ceq $image.sha256) 'PNG 摘要不一致。'
        # Windows 本地解码验证真正 PNG；不是 Windows CI，也不是靠扩展名/文件大小判断。
        $bitmap = [System.Drawing.Bitmap]::FromFile($path)
        try { Assert-Check ($bitmap.Width -eq $image.width -and $bitmap.Height -eq $image.height -and $bitmap.Width -gt 0 -and $bitmap.Height -gt 0) 'PNG 实际尺寸不一致。' }
        finally { $bitmap.Dispose() }
    }
    $item = $Native['sustained']; Require-M1Fields $item @('renderer','realHost','physicalInput','smoke','seconds','rounds','sampleCount','latency','p95','maximum','baseline','maxPrivate','contentPeak','contentAfter','stopMs','virtualized','parsedUtc','visibleUtc','pausedContinued','originReceipts','curveSha256')
    foreach ($flag in @('realHost','physicalInput','smoke')) { Require-M1Boolean $item $flag $false }; Require-M1Boolean $item 'pausedContinued' $true
    Assert-Check ($item.renderer -eq 'Avalonia Desktop' -and $item.seconds -ge 600 -and $item.rounds -eq 20 -and $item.sampleCount -ge 200 -and $item.contentAfter -eq 0 -and
        $item.contentPeak -le $Profile.maxContentBytes -and $item.maxPrivate - $item.baseline -le $Profile.maxPrivateDeltaBytes -and $item.stopMs -le $Profile.maxStopMilliseconds -and $item.originReceipts -ge 10020) '桌面 UI 测量范围/预算错误。'
    $latency = @($item.latency | Sort-Object); $p95 = $latency[[int][Math]::Ceiling($latency.Count * 0.95) - 1]
    Assert-Check ($latency.Count -eq $item.sampleCount -and $p95 -eq $item.p95 -and $latency[-1] -eq $item.maximum -and $p95 -le $Profile.uiP95Milliseconds -and $latency[-1] -le $Profile.uiMaxMilliseconds) 'UI 延迟统计不一致或超预算。'
    Assert-Check (([DateTimeOffset]$item.visibleUtc - [DateTimeOffset]$item.parsedUtc).TotalMilliseconds -ge 0 -and ([DateTimeOffset]$item.visibleUtc - [DateTimeOffset]$item.parsedUtc).TotalMilliseconds -le 1000) '首预览到达顺序/时延错误。'
    Assert-Check ($item.virtualized.Count -eq 3 -and (@($item.virtualized.index | Sort-Object) -join ',') -eq '0,5000,9999') '虚拟滚动头/中/尾证据缺失。'
    foreach ($point in $item.virtualized) { Assert-Check ($point.containers -gt 0 -and $point.containers -le $point.visible * 3 + 20) '列表容器超出视口预算。' }
    $curve = Join-Path $Directory 'm1-ui-native-samples.json'; Assert-Check ((Get-FileHash $curve).Hash -ceq $item.curveSha256) 'UI 原始曲线被替换。'
    $points = @(Get-Content $curve -Raw | ConvertFrom-Json -AsHashtable)
    Assert-Check ($points.Count -eq $item.sampleCount -and $points[-1].seconds - $points[0].seconds -ge 599 -and @($points.privateBytes | Select-Object -Unique).Count -gt 1) 'UI 曲线时间/样本无效。'
    Assert-Check (($points.privateBytes | Measure-Object -Maximum).Maximum -eq $item.maxPrivate) 'UI 峰值不能由原始样本重算。'
    foreach ($point in $points) { Require-M1Fields $point @('utc','seconds','privateBytes','handles','queue','content','active','retained','delay'); Assert-Check ($point.active -eq 5 -and $point.retained -le 10020 -and $point.queue -le $Profile.maxQueueBytes -and $point.content -le $Profile.maxContentBytes -and $point.delay -le $Profile.uiMaxMilliseconds) 'UI 负载/预算曲线错误。' }
}
function Test-CompleteM1Evidence($Directory, $RunId, $SourceHash, $ProfileHash, $Since, $Rows, $M0Map, $M1Map, $M0Profile, $M1Profile) {
    # 自测和正式入口经过同一个完整校验路径；删除仅 M0 需要的证据也必须让 M1 失败。
    $m0Coverage = Test-ScenarioMap $M0Map $Rows
    $m1Coverage = Test-M1ScenarioMap $M1Map $Rows
    $protocol = Read-CheckedEvidence (Join-Path $Directory 'protocol-probes.json') $RunId $SourceHash $Since
    $stream = Read-CheckedEvidence (Join-Path $Directory 'stream-probes.json') $RunId $SourceHash $Since
    Test-ProtocolEvidence $protocol $stream
    Test-ResourceEvidence (Read-CheckedEvidence (Join-Path $Directory 'resource-probes.json') $RunId $SourceHash $Since) $Directory $M0Profile
    $data = @{}
    foreach ($name in @('m1-capture','m1-storage','m1-lifetime','m1-query','m1-stream','m1-resources','m1-ui','m1-ui-screenshots','m1-ui-native')) {
        $data[$name] = Read-M1Evidence (Join-Path $Directory "$name.json") $RunId $SourceHash $ProfileHash $Since
    }
    Test-M1CaptureEvidence $data['m1-capture'] $data['m1-storage'] $data['m1-lifetime'] $data['m1-query']
    Test-M1ResourceEvidence $data['m1-resources'] $Directory $M1Profile
    Test-M1UiEvidence $data['m1-ui'] $data['m1-ui-screenshots'] $data['m1-ui-native'] $Directory $M1Profile
    $independent = $data['m1-stream']['independent-budget']; Require-M1Fields $independent @('Total','retained','Evicted','stored','gap'); Require-M1Boolean $independent 'gap' $true
    Assert-Check ($independent.Total -eq 301 -and $independent.retained -eq 256 -and $independent.Evicted -eq 45 -and $independent.stored -eq 4) '流式观察和正文记录预算混淆。'
    $h2 = $data['m1-stream']['h2-isolation']; Require-M1Fields $h2 @('activeRecordId','endedRecordId','ClientProtocol','OriginProtocol','events','activeCount','idleCancellationSupported','idlePolicy','diagnostic','transportAtIdle','captureAtIdle','sameClientConnection','continuedAfterCancel','stoppedActiveCount')
    Assert-Check ($h2.ClientProtocol -eq '2.0' -and $h2.OriginProtocol -eq '2.0' -and $h2.events -ge 15 -and $h2.activeCount -eq 1 -and $h2.activeRecordId -ne $h2.endedRecordId) 'h2 流隔离证据不完整。'
    # 2026-09-29 用户明确接受产品降级。P06 不删除、不跳过，改验“空闲仅提示”的实际契约；
    # 不能把客户端取消记作自动取消，也不能破坏同连接其它流或伪造目标流已经结束。
    Require-M1Boolean $h2 'idleCancellationSupported' $false
    Require-M1Boolean $h2 'sameClientConnection' $true; Require-M1Boolean $h2 'continuedAfterCancel' $true
    Assert-Check ($h2.idlePolicy -ceq 'notify-only' -and $h2.diagnostic -ceq 'UnsupportedH2Cancellation' -and
        $h2.transportAtIdle -ceq 'Receiving' -and $h2.captureAtIdle -ceq 'Partial' -and $h2.stoppedActiveCount -is [long] -and $h2.stoppedActiveCount -eq 0) 'HTTP/2 产品降级、状态或全局停止证据不成立。'
    foreach ($scenario in $m1Coverage) { $scenario.status = 'passed' }
    ($m1Coverage | Where-Object id -eq 'V3-P06')['scope'] = 'HTTP/2 空闲仅提示；客户端单流取消与全局停止'
    return @($m0Coverage + $m1Coverage)
}
