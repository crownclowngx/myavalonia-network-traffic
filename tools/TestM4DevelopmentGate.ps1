# 正反例只证明检查器拒绝假成功，不计入87个产品测试或80项业务验收。
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'M4DevelopmentChecks.ps1')
$profile=Get-Content (Join-Path $PSScriptRoot 'm4-profile.json') -Raw | ConvertFrom-Json -AsHashtable
$map=Get-Content (Join-Path $PSScriptRoot 'm4-test-map.json') -Raw | ConvertFrom-Json
$rows=@($map.tests | ForEach-Object { $method=$_.method; $_.rows | ForEach-Object { [pscustomobject]@{method=$method;name=$method+$_} } })
$script:m4SelfChecks=0
function Reject-M4([string]$Name,[scriptblock]$Body) {
    $rejected=$false; try { & $Body | Out-Null } catch { $rejected=$true }
    Assert-Check $rejected "M4门禁漏拒绝：$Name"; $script:m4SelfChecks++; Write-Host "拒绝通过：$Name"
}
function Copy-M4($Value) { $Value | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable }
Test-M4Rows $map $rows $profile | Out-Null
Reject-M4 'G01 缺参数行' { Test-M4Rows $map @($rows | Select-Object -Skip 1) $profile }
Reject-M4 'G02 零测试' { Test-M4Rows $map @() $profile }
Reject-M4 'G03 重复参数行' { Test-M4Rows $map @($rows+$rows[0]) $profile }
$bad=Copy-M4 $map; $bad.scenarios[0].status='passed'
Reject-M4 'G03 分项越界标记完整场景' { Test-M4Rows $bad $rows $profile }
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$directory=Join-Path $tempRoot ('m4-gate-self-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
try {
    $identity=Join-Path $directory 'identity.json'; $since=[DateTimeOffset]::UtcNow
    @{schemaVersion=1;runId='self';sourceHash='hash';recordedUtc=$since;scenarios=@{case=@{value=1}}} | ConvertTo-Json -Depth 5 | Set-Content $identity
    Read-CheckedEvidence $identity 'self' 'hash' $since | Out-Null
    Reject-M4 'G04 混用运行身份' { Read-CheckedEvidence $identity 'other' 'hash' $since }
    Reject-M4 'G04 旧轮证据' { Read-CheckedEvidence $identity 'self' 'hash' $since.AddMinutes(1) }
    Reject-M4 'G05 源码身份错误' { Read-CheckedEvidence $identity 'self' 'wrong' $since }
    $send=@{}
    foreach($key in @('http','https')) { $send[$key]=@{requests=1;path='/echo?a=%2B';frozenHash='A'*64;receipt=@{Method='POST';Protocol='HTTP/1.1';Path='/echo?a=%2B';BodyHash='A'*64;Sni='localhost';Host='localhost:1234'}} }
    foreach($status in @(401,407,307,308)) { $send['challenge-'+$status]=@{status=$status;requests=1;pendingConnection=$false;protocol='1.1'} }
    Test-M4Send $send
    $badSend=Copy-M4 $send; $badSend['challenge-401'].requests=2
    Reject-M4 'G06 认证导致重发' { Test-M4Send $badSend }
    $badSend=Copy-M4 $send; $badSend.https.receipt.BodyHash='B'*64
    Reject-M4 'G07 正文与预览不一致' { Test-M4Send $badSend }
    $resources=@{}
    foreach($size in $profile.downloadSizes) { $resources['download-'+$size]=@{size=$size;observed=$size;stored=1048576;peakPrivateDelta=10;samples=@(100,110);milliseconds=1;peakReserved=4194304;reserved=0;active=0;receipts=1} }
    $resources['twenty-stops']=@{cycles=20;latencies=@(1)*20;active=0;reserved=0;remainingCaches=0}
    Test-M4Resources $resources $profile
    $badResource=Copy-M4 $resources; $badResource['twenty-stops'].active=1
    Reject-M4 'G08 关闭仍有任务' { Test-M4Resources $badResource $profile }
    $badResource=Copy-M4 $resources; $badResource['download-1073741824'].peakPrivateDelta=$profile.maximumPrivateDelta+1
    Reject-M4 'G08 内存超额' { Test-M4Resources $badResource $profile }
    $badResource=Copy-M4 $resources; $badResource['download-134217728'].observed=1024
    Reject-M4 'G08 下载仅观察前缀' { Test-M4Resources $badResource $profile }
    $image=Join-Path $directory 'self.png'; $bitmap=[System.Drawing.Bitmap]::new(4,3)
    try { $bitmap.Save($image,[System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
    $metadata=@{schemaVersion=1;runId='self';sourceHash='hash';width=4;height=3;realHost=$false;sha256=(Get-FileHash $image).Hash}
    Test-M4Image $image $metadata 'self' 'hash' 4 3
    Reject-M4 'G09 缺截图' { Test-M4Image (Join-Path $directory 'missing.png') $metadata 'self' 'hash' 4 3 }
    $metadata.sha256='wrong'; Reject-M4 'G09 截图摘要伪造' { Test-M4Image $image $metadata 'self' 'hash' 4 3 }
    $report=@{mode='M4Foundation';sourceHash='hash';fullM4Passed=$false;m3Passed=$false;manualAccepted=$false;hostVerified=$false;windowsCiExecuted=$false;releaseGateExecuted=$false;deployed=$false}
    Test-M4Report $report 'hash'
    Reject-M4 'G10 运行中源码变化' { Test-M4Report $report 'changed' }
    Test-M4Prerequisites $true 359 $true
    Reject-M4 'G11 缺M3真实能力' { Test-M4Prerequisites $false 359 $true }
    Reject-M4 'G11 累计场景缺项' { Test-M4Prerequisites $true 358 $true }
    Reject-M4 'G11 复用旧轮' { Test-M4Prerequisites $true 359 $false }
    foreach($flag in @('fullM4Passed','m3Passed','manualAccepted','hostVerified','windowsCiExecuted','releaseGateExecuted','deployed')) {
        $badReport=Copy-M4 $report; $badReport[$flag]=$true
        Reject-M4 "G12 范围越界 $flag" { Test-M4Report $badReport 'hash' }
    }
} finally {
    # 删除前验证最终绝对路径属于本次临时目录；只使用PowerShell原生命令，不跨shell拼接删除命令。
    $resolved=[IO.Path]::GetFullPath($directory)
    Assert-Check ($resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('m4-gate-self-')) '拒绝删除未知自测目录。'
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Host "M4 12组检查器自测通过：$script:m4SelfChecks 个拒绝反例，正例通过；产品与人工状态不由这些合成数据证明。"
