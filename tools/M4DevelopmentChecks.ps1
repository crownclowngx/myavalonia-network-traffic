# M4分项验收与完整里程碑明确分开；缺前置是失败，不能借定向测试通过发布完整结论。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevelopmentChecks.ps1')

function Test-M4Rows($Map, [object[]]$Rows, $Profile) {
    Assert-Check ($Map.schemaVersion -eq 1 -and $Map.scope -ceq 'M4Foundation') 'M4映射版本或范围错误。'
    $expected = @($Map.tests | ForEach-Object { $method = $_.method; $_.rows | ForEach-Object { $method + $_ } })
    Assert-Check ($expected.Count -eq $Profile.foundationTests -and @($expected | Select-Object -Unique).Count -eq $expected.Count) 'M4固定测试集合缺项或重复。'
    $actual = @($Rows | Where-Object method -Match '^NetWorkTrafficPlugin.Tests.(Requests.|Integration.M4|Ui.M4)' | Select-Object -ExpandProperty name)
    Assert-Check ((($expected | Sort-Object) -join "`n") -ceq (($actual | Sort-Object) -join "`n")) 'M4 TRX未完整执行映射中的精确参数行。'
    $ids = @(foreach ($group in @('A','E','B','S','X','H','D','U','R','C')) { foreach ($number in 1..8) { 'V7-{0}{1:00}' -f $group,$number } })
    Assert-Check ((($ids | Sort-Object) -join ',') -ceq (($Map.scenarios.id | Sort-Object) -join ',')) 'M4完整场景登记缺项或重复。'
    Assert-Check (@($Map.scenarios | Where-Object status -CNE 'not-accepted').Count -eq 0) '基础门禁不能标记完整业务场景已验收。'
    return $expected.Count
}
function Test-M4Send($Data) {
    Assert-Check (@($Data.Keys).Count -eq 6) 'M4端点证据集合错误。'
    foreach ($key in @('http','https')) {
        Assert-Check $Data.Contains($key) '缺少真实HTTP或HTTPS端点。'
        $value = $Data[$key]
        Assert-Check ($value.requests -eq 1 -and $value.receipt.Method -ceq 'POST' -and $value.receipt.Protocol -ceq 'HTTP/1.1' -and
            $value.receipt.Path -ceq $value.path -and $value.receipt.BodyHash -ceq $value.frozenHash -and $value.frozenHash -match '^[A-F0-9]{64}$') '目标收到的请求与冻结输入不一致。'
        if ($key -eq 'https') { Assert-Check ($value.receipt.Sni -ceq 'localhost' -and $value.receipt.Host -match '^localhost:\d+$') 'HTTPS目标身份不一致。' }
    }
    foreach ($status in @(401,407,307,308)) {
        $value = $Data['challenge-' + $status]
        Assert-Check ($value.status -eq $status -and $value.requests -eq 1 -and $value.pendingConnection -eq $false -and $value.protocol -ceq '1.1') '挑战/重定向发生了额外发送或证据不全。'
    }
}
function Test-M4Resources($Data, $Profile) {
    Assert-Check (@($Data.Keys).Count -eq 3) 'M4资源证据缺项。'
    foreach ($size in $Profile.downloadSizes) {
        $value = $Data['download-' + $size]
        Assert-Check ($value.size -eq $size -and $value.observed -eq $size -and $value.stored -ge 0 -and $value.stored -le 1048576 -and
            $value.peakPrivateDelta -le $Profile.maximumPrivateDelta -and $value.samples.Count -ge 2 -and $value.milliseconds -gt 0 -and
            $value.peakReserved -le $Profile.sharedContentBytes -and $value.reserved -eq 0 -and $value.active -eq 0 -and $value.receipts -eq 1) 'M4下载观测或预算不符合固定要求。'
        $peak = ($value.samples | Measure-Object -Maximum).Maximum - $value.samples[0]
        Assert-Check ($peak -eq $value.peakPrivateDelta) 'M4内存峰值与原始采样不符。'
    }
    $stop = $Data['twenty-stops']
    Assert-Check ($stop.cycles -eq $Profile.stopCycles -and $stop.latencies.Count -eq $Profile.stopCycles -and $stop.active -eq 0 -and $stop.reserved -eq 0 -and $stop.remainingCaches -eq 0) 'M4关闭资源未归零。'
    foreach ($value in $stop.latencies) { Assert-Check ($value -ge 0 -and $value -le $Profile.maximumStopMilliseconds) 'M4停止超出期限。' }
}
function Test-M4Image([string]$Path, $Metadata, [string]$RunId, [string]$SourceHash, [int]$Width, [int]$Height) {
    Assert-Check (Test-Path -LiteralPath $Path -PathType Leaf) '缺少M4生产截图。'
    Assert-Check ($Metadata.schemaVersion -eq 1 -and $Metadata.runId -ceq $RunId -and $Metadata.sourceHash -ceq $SourceHash -and
        $Metadata.width -eq $Width -and $Metadata.height -eq $Height -and $Metadata.realHost -eq $false) 'M4截图身份/尺寸/Host范围错误。'
    Assert-Check ((Get-FileHash -LiteralPath $Path).Hash -ceq $Metadata.sha256) 'M4截图摘要错误。'
    $bytes = [IO.File]::ReadAllBytes($Path)
    Assert-Check ($bytes.Length -gt 24 -and [Convert]::ToHexString($bytes[0..7]) -ceq '89504E470D0A1A0A') 'M4截图不是有效PNG。'
    $actualWidth = [int64]$bytes[16]*16777216 + [int64]$bytes[17]*65536 + [int64]$bytes[18]*256 + $bytes[19]
    $actualHeight = [int64]$bytes[20]*16777216 + [int64]$bytes[21]*65536 + [int64]$bytes[22]*256 + $bytes[23]
    Assert-Check ($actualWidth -eq $Width -and $actualHeight -eq $Height) 'M4 PNG尺寸与声明不符。'
}
function Test-M4Report($Report, [string]$EndingHash) {
    Assert-Check ($Report.mode -ceq 'M4Foundation' -and $Report.sourceHash -ceq $EndingHash) 'M4范围错误或运行期间源码改变。'
    foreach ($flag in @('fullM4Passed','m3Passed','manualAccepted','hostVerified','windowsCiExecuted','releaseGateExecuted','deployed')) {
        Assert-Check ($Report[$flag] -eq $false) "M4分项报告越界宣称：$flag"
    }
}
function Test-M4Prerequisites([bool]$M3Supported, [int]$Cumulative, [bool]$SameRun) {
    Assert-Check ($M3Supported -and $Cumulative -eq 359 -and $SameRun) '完整M4被阻断：需要M3真实能力与359项同轮场景，不能复用旧证据。'
}
