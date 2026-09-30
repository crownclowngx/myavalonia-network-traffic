# V4 仅扩展本地 Debug 开发门禁。完整入口复用 M0/M1，不把截图存在等同于交互通过。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Get-RequiredUXScenarios {
    foreach ($group in @{ U = 20; I = 6; UI = 10; P = 2; R = 2 }.GetEnumerator()) {
        foreach ($number in 1..$group.Value) { 'V4-{0}{1:00}' -f $group.Key, $number }
    }
}
function Test-UXScenarioMap($Map, [object[]] $Rows) {
    $required = @(Get-RequiredUXScenarios | Sort-Object)
    Assert-Check ($Map.schemaVersion -eq 1 -and ($required -join ',') -ceq (@($Map.scenarios.id | Sort-Object) -join ',')) 'V4 40 项场景缺失、重复或未知。'
    foreach ($scenario in $Map.scenarios) {
        Assert-Check (@($scenario.tests).Count -gt 0) "V4 空映射：$($scenario.id)"
        foreach ($test in $scenario.tests) {
            $actual = @($Rows | Where-Object method -CEQ $test.method)
            Assert-Check ($test.results -gt 0 -and $actual.Count -eq $test.results -and @($test.rows).Count -eq $test.results) "V4 结果数量错误：$($scenario.id)"
            foreach ($row in $test.rows) { Assert-Check (@($actual | Where-Object name -CEQ ($test.method + $row)).Count -eq 1) "V4 参数行缺失：$($test.method)$row" }
        }
    }
    return @($Map.scenarios | ForEach-Object { @{ id = $_.id; tests = $_.tests; status = 'passed' } })
}
function Require-UXNumbers($Data, [string[]] $Names) {
    Require-M1Fields $Data $Names
    foreach ($name in $Names) {
        $value = $Data[$name]
        Assert-Check (($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal]) -and [double]::IsFinite([double]$value)) "V4 数值字段类型错误：$name"
    }
}
function Read-UXEvidence($Path, $RunId, $SourceHash, $ProfileHash, $UXProfileHash, $Since) {
    $data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    Require-M1Fields $data @('uxProfileHash','scenarioIds','scenarios')
    Assert-Check ($data.uxProfileHash -is [string] -and $data.uxProfileHash -ceq $UXProfileHash) 'V4 profile 身份不匹配。'
    Assert-Check ((@($data.scenarioIds | Sort-Object) -join ',') -ceq (@($data.scenarios.Keys | Sort-Object) -join ',')) 'V4 证据索引与内容不一致。'
    return Read-M1Evidence $Path $RunId $SourceHash $ProfileHash $Since
}
function Test-UXLayout($Layout, $Directory, $Profile) {
    $expected = @($Profile.layouts.key) + @($Profile.behaviors)
    Assert-Check ((@($Layout.Keys | Sort-Object) -join ',') -ceq (@($expected | Sort-Object) -join ',')) 'V4 12 个布局或 8 个行为图缺失/重复。'
    foreach ($key in $expected) {
        $item = $Layout[$key]; Require-M1Fields $item @('name','width','height','sha256','renderer','scaling','realHost','test','measurements')
        Require-M1Boolean $item 'realHost' $false; Require-UXNumbers $item @('width','height','scaling')
        Assert-Check ($item.name -ceq "ux-$key.png" -and $item.renderer -ceq 'Avalonia Headless / Skia' -and $item.scaling -eq 1 -and $item.test -ceq 'NetWorkTrafficPlugin.Tests.Ui.UXCaptureUiTests') 'V4 截图身份错误。'
        $path = Join-Path $Directory $item.name
        Assert-Check ((Get-FileHash $path).Hash -ceq $item.sha256) 'V4 PNG 摘要不匹配。'
        $bitmap = [System.Drawing.Bitmap]::FromFile($path)
        try { Assert-Check ($bitmap.Width -eq $item.width -and $bitmap.Height -eq $item.height -and $bitmap.Width -gt 0 -and $bitmap.Height -gt 0) 'V4 PNG 不能解码为期望尺寸。' }
        finally { $bitmap.Dispose() }
        $sidecar = Get-Content "$path.json" -Raw | ConvertFrom-Json -AsHashtable
        # sidecar 与汇总必须为同一实际观测；不能只改汇总躲开图片检查。
        Assert-Check (($sidecar | ConvertTo-Json -Depth 20 -Compress) -ceq ($item | ConvertTo-Json -Depth 20 -Compress)) 'V4 sidecar 与观测汇总不同。'
        $layoutProfile = @($Profile.layouts | Where-Object key -CEQ $key)
        if ($layoutProfile.Count -eq 0) { Assert-Check ($item.width -eq 900 -and $item.height -eq 600) '行为图尺寸错误。'; continue }
        $spec = $layoutProfile[0]; $m = $item.measurements
        Require-UXNumbers $m @('top','rowHeight','fullRows','containers','bodyHeight','width','height')
        Require-M1Boolean $m 'tool' $spec.tool; Require-M1Boolean $m 'dark' $spec.dark; Require-M1Boolean $m 'stopReachable' $true
        Assert-Check ($item.width -eq $spec.width -and $item.height -eq $spec.height -and $m.width -eq $spec.width -and $m.height -eq $spec.height) '布局实测/目标尺寸不同。'
        Require-UXNumbers $m.stop @('x','y','width','height')
        Assert-Check ($m.stop.x -ge 0 -and $m.stop.y -ge 0 -and $m.stop.width -gt 0 -and $m.stop.height -gt 0 -and $m.stop.x + $m.stop.width -le $item.width -and $m.stop.y + $m.stop.height -le $item.height) '停止按钮裁切。'
        if (-not $spec.tool) {
            Assert-Check ($m.rowHeight -ge $Profile.rowMin -and $m.rowHeight -le $Profile.rowMax -and $m.containers -gt 0 -and $m.containers -le $m.fullRows * 3 + 20) '行高或虚拟化超限。'
            if ($spec.width -ge 800) { Assert-Check ($m.top -ge 0 -and $m.top -le $Profile.topMax) '顶部占高超限。' }
            if ($spec.width -eq 800) { Assert-Check ($m.fullRows -ge $Profile.minFullRows) '800 窗口可见行数不足。' }
            if ($spec.width -eq 520) { Assert-Check ($m.bodyHeight -ge $Profile.bodyMin) '窄屏正文不足。' }
        }
    }
}
function Test-UXObservations($Data, $Directory, $Profile, $Native) {
    $reading = $Data['ux-reading']; $independent = $reading.independent
    Require-UXNumbers $independent @('listPending','eventPending','duplicateCount','retainedCount')
    Require-M1Boolean $independent 'listFollowing' $false; Require-M1Boolean $independent 'eventFollowing' $false
    Assert-Check ($independent.listPending -eq 0 -and $independent.eventPending -eq 1 -and $independent.duplicateCount -eq 3 -and $independent.retainedCount -eq 2) '两阅读状态混淆或修订重复计数。'
    $anchor = $reading.anchor; Require-UXNumbers $anchor @('anchorError','pending','restorations'); Require-M1Boolean $anchor 'resumed' $true
    Assert-Check ($anchor.beforeId -eq $anchor.afterId -and $anchor.anchorError -ge 0 -and $anchor.anchorError -le $Profile.anchorMax -and $anchor.pending -eq 1 -and $anchor.restorations -gt 0 -and $anchor.input -ceq 'MouseWheel') '实际列表锚点/恢复证据错误。'
    $stream = $reading.stream; Require-UXNumbers $stream @('total','retained','pending','observed')
    Require-M1Boolean $stream 'listFollowing' $true; Require-M1Boolean $stream 'eventFollowing' $false
    Assert-Check ($stream.total -eq 271 -and $stream.retained -eq 256 -and $stream.pending -eq 1 -and $stream.observed -gt 0) '事件暂停妨碍接收或半事件重复计数。'
    $interactions = $Data['ux-interactions']; $late = $interactions['late-query']; Require-UXNumbers $late @('lateApplied'); Require-M1Boolean $late 'QueryPending' $false
    Assert-Check ($late.lateApplied -eq 0 -and $late.path -ceq '/b') '迟到查询覆盖新结果。'
    $draft = $interactions.draft; Require-UXNumbers $draft @('oldRows','appliedRows','debounceMs'); Require-M1Boolean $draft 'combinationPreserved' $true; Require-M1Boolean $draft 'invalidPreserved' $true
    Assert-Check ($draft.oldRows -eq 2 -and $draft.appliedRows -eq 2 -and $draft.debounceMs -eq 200) '草稿/消抖契约错误。'
    $range = $interactions.range; Require-UXNumbers $range @('total','matched','restored','otherPage'); Require-M1Boolean $range 'activePreserved' $true
    Assert-Check ($range.total -eq 10001 -and $range.matched -eq 5000 -and $range.restored -eq 10001 -and $range.otherPage -eq 200) '范围/另一页被错误清空。'
    $feedback = $interactions['feedback-find']; Require-UXNumbers $feedback @('queryLimit','successMilliseconds','staleApplied','wrapPosition')
    Assert-Check ($feedback.queryLimit -eq $Profile.findLimit -and $feedback.successMilliseconds -eq 3000 -and $feedback.staleApplied -eq 0) '反馈代次或查找限制错误。'
    $states = $interactions['proxy-states']; Require-UXNumbers $states @('starts','disposes'); Require-M1Boolean $states 'shared' $true
    Assert-Check ($states.starts -eq 1 -and $states.disposes -eq 1 -and ($states.states -join ',') -ceq 'Stopped,Starting,Running,Stopping,Stopped') '主动作状态/共享实例错误。'
    $storage = $Data['ux-content-storage']['save-range']; Require-UXNumbers $storage @('matched','saved','requestBytes','responseBytes'); Require-M1Boolean $storage 'reopenedRunning' $false
    Assert-Check ($storage.matched -eq 1 -and $storage.saved -eq 2 -and $storage.requestBytes -eq 6 -and $storage.responseBytes -eq 5 -and $storage.responseHash -ceq ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('first'))))) '保存范围/方向/原字节摘要错误。'
    $content = $Data['ux-content-storage'].content; Require-UXNumbers $content @('modes','preservedMatch','reservedAfter'); Require-M1Boolean $content 'unicode' $true; Require-M1Boolean $content 'bodyWasNotRedacted' $true
    Assert-Check ($content.modes -eq 3 -and $content.preservedMatch -ge 0 -and $content.reservedAfter -eq 0) '内容模式/预算归还错误。'
    $lifetime = $Data['ux-lifetime']; $picker = $lifetime.picker; Require-UXNumbers $picker @('cancelCalls','lateWrites','disposedWrites','reservedAfter'); Require-M1Boolean $picker 'running' $true
    Assert-Check ($picker.cancelCalls -eq 1 -and $picker.lateWrites -eq 0 -and $picker.disposedWrites -eq 0 -and $picker.reservedAfter -eq 0) '选择器取消/迟到/关闭后发生写入。'
    $reattach = $lifetime.reattach; Require-UXNumbers $reattach @('rounds','mode'); Require-M1Boolean $reattach 'running' $true
    Assert-Check ($reattach.rounds -eq 20 -and $reattach.selectedBefore -eq $reattach.selectedAfter -and $reattach.mode -eq 2) '重附着串状态或停掉捕获。'
    $performance = $Data['ux-performance'].sustained; Require-UXNumbers $performance @('rounds','sampleCount','seconds','baselineHandles','stoppedHandles','contentAfter')
    Require-M1Boolean $performance 'realHost' $false; Require-M1Boolean $performance 'physicalInput' $false
    Assert-Check ($performance.rounds -eq 20 -and $performance.sampleCount -eq $Native.sampleCount -and $performance.seconds -ge 600 -and $performance.contentAfter -eq 0 -and $performance.renderer -ceq 'Avalonia Desktop' -and $performance.stoppedHandles - $performance.baselineHandles -le 32 -and $performance.curveSha256 -ceq $Native.curveSha256) 'UX 桌面测量缺失或资源超限。'
    Assert-Check ($performance.anchors.Count -eq 21 -and @($performance.anchors | Where-Object kind -EQ 'insert').Count -eq 1) '桌面锚点采样缺失。'
    foreach ($point in $performance.anchors) {
        Require-UXNumbers $point @('offsetBefore','offsetAfter','error')
        Assert-Check ($point.before -eq $point.after -and $point.error -eq [Math]::Abs($point.offsetBefore - $point.offsetAfter) -and $point.error -le $Profile.anchorMax -and $point.input -ceq 'Dispatcher command') '桌面插入或修订打断阅读。'
    }
}
function Test-CompleteUXEvidence($Directory, $RunId, $SourceHash, $ProfileHash, $UXProfileHash, $Since, $Rows, $M0Map, $M1Map, $UXMap, $M0Profile, $M1Profile, $UXProfile) {
    # 三代门禁同轮合并，任何仅 M0/M1 所需文件缺失仍然失败；不借用历史报告。
    $old = Test-CompleteM1Evidence $Directory $RunId $SourceHash $ProfileHash $Since $Rows $M0Map $M1Map $M0Profile $M1Profile
    $coverage = Test-UXScenarioMap $UXMap $Rows
    foreach ($field in @('maxPrivateDeltaBytes','maxContentBytes','maxQueueBytes','maxStopMilliseconds','maxHandlesAfterStop','uiP95Milliseconds','uiMaxMilliseconds','streamSeconds','pageCycles','uiSamples')) {
        Assert-Check ($UXProfile[$field] -eq $M1Profile[$field]) "UX 不得放宽前置预算：$field"
    }
    Assert-Check ($UXProfile.schemaVersion -eq 1 -and $UXProfile.layouts.Count -eq 12 -and $UXProfile.behaviors.Count -eq 8 -and $UXProfile.topMax -eq 104 -and $UXProfile.rowMin -eq 44 -and $UXProfile.rowMax -eq 52 -and $UXProfile.minFullRows -eq 8 -and $UXProfile.bodyMin -eq 160 -and $UXProfile.anchorMax -eq 2 -and $UXProfile.findLimit -eq 256) 'UX 固定量尺被放宽。'
    $data = @{}
    foreach ($name in @('ux-layout','ux-reading','ux-interactions','ux-content-storage','ux-lifetime','ux-performance')) { $data[$name] = Read-UXEvidence (Join-Path $Directory "$name.json") $RunId $SourceHash $ProfileHash $UXProfileHash $Since }
    Test-UXLayout $data['ux-layout'] $Directory $UXProfile
    $native = Read-M1Evidence (Join-Path $Directory 'm1-ui-native.json') $RunId $SourceHash $ProfileHash $Since
    Test-UXObservations $data $Directory $UXProfile $native.sustained
    return @($old + $coverage)
}
