param([ValidateSet('Docs', 'M0', 'M1', 'UX', 'M2')][string] $Mode = 'M0')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevelopmentChecks.ps1')
. (Join-Path $PSScriptRoot 'M1DevelopmentChecks.ps1')
. (Join-Path $PSScriptRoot 'UXDevelopmentChecks.ps1')
. (Join-Path $PSScriptRoot 'M2DevelopmentChecks.ps1')
$root = Split-Path $PSScriptRoot
$started = [DateTimeOffset]::UtcNow
$runId = $started.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$directory = Join-Path $root "TestResults/NetworkTraffic$Mode/$runId"
[IO.Directory]::CreateDirectory($directory) | Out-Null
$history = [System.Collections.Generic.List[object]]::new()
$sourceHash = Get-SourceHash $root
$originalEnvironment = @{}
foreach ($name in @('NETWORK_TRAFFIC_TEST_ARTIFACTS', 'NETWORK_TRAFFIC_RUN_ID', 'NETWORK_TRAFFIC_SOURCE_HASH', 'NETWORK_TRAFFIC_M1_PROFILE_HASH', 'NETWORK_TRAFFIC_UX_PROFILE_HASH', 'NETWORK_TRAFFIC_M2_PROFILE_HASH')) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$report = [ordered]@{
    schemaVersion = 1; runId = $runId; mode = $Mode; sourceHash = $sourceHash; startedUtc = $started
    profileHash = (Get-FileHash (Join-Path $PSScriptRoot 'm1-resource-profile.json')).Hash
    uxProfileHash = (Get-FileHash (Join-Path $PSScriptRoot 'ux-profile.json')).Hash
    m2ProfileHash = (Get-FileHash (Join-Path $PSScriptRoot 'm2-profile.json')).Hash
    documentationHash = Get-DocumentationHash $root
    head = (& git -C $root rev-parse HEAD); dirty = @(& git -C $root status --porcelain).Count -gt 0
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription; architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    sdk = (& dotnet --version); automaticPassed = $false; testCount = 0; commands = $history
    realClientVerified = $false; hostVerified = $false; manualAccepted = $false; deployed = $false
    windowsCiExecuted = $false; releaseGateExecuted = $false
    productDowngrades = @('HTTP/2 空闲流只提示，不自动终止；客户端单流取消与全局停止仍须验证。')
    pending = @('H01/H02 实际界面操作', 'H03 指定真实 Host', 'H04 指定真实客户端')
}
if ($Mode -eq 'M2') {
    $report.pending = @('V5-H01 实际Standalone与物理缩放', 'V5-H02 指定真实Host', 'V5-H03 指定真实客户端', 'V5-H04 用户保存视图及历史重算验收')
}
function Check-Docs {
    # git 清单包含未跟踪 Markdown，不能用仅检查 diff 的方式漏掉新文档。
    $files = @(& git -C $root ls-files --cached --others --exclude-standard | Sort-Object -Unique |
        Where-Object { $_.EndsWith('.md') } | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path -LiteralPath $_ })
    Test-MarkdownFiles $files
    & git -C $root diff --check
    Assert-Check ($LASTEXITCODE -eq 0) 'git diff --check 失败。'
}
function Run-Step([string] $Name, [string] $File, [string[]] $CommandArguments, [int] $Timeout) {
    $remaining = $(if ($Mode -eq 'M2') { 6000 } elseif ($Mode -eq 'UX') { 4800 } elseif ($Mode -eq 'M1') { 3600 } else { 1800 }) - ([DateTimeOffset]::UtcNow - $started).TotalSeconds
    Assert-Check ($remaining -gt 1) '整轮开发检查超时。'
    Write-Host "[$runId] $Name"
    Invoke-CheckedProcess -File $File -Arguments $CommandArguments -WorkingDirectory $root -TimeoutSeconds ([int][Math]::Min($Timeout, $remaining)) -History $history -LogPath (Join-Path $directory "$Name.log") | Out-Null
}
try {
    Check-Docs
    if ($Mode -in @('M0','M1','UX','M2')) {
        $selfTest = if ($Mode -eq 'M2') { 'TestM2DevelopmentGate.ps1' } elseif ($Mode -eq 'UX') { 'TestUXDevelopmentGate.ps1' } elseif ($Mode -eq 'M1') { 'TestM1DevelopmentGate.ps1' } else { 'test-development-gate.ps1' }
        Run-Step 'gate-self-test' 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot $selfTest)) 300
        Run-Step 'restore-locked' 'dotnet' @('restore', 'NetWorkTrafficPlugin.slnx', '--locked-mode') 300
        Run-Step 'build-debug' 'dotnet' @('build', 'NetWorkTrafficPlugin.slnx', '-c', 'Debug', '--no-restore', '-warnaserror') 300
        $env:NETWORK_TRAFFIC_TEST_ARTIFACTS = $directory
        $env:NETWORK_TRAFFIC_RUN_ID = $runId
        $env:NETWORK_TRAFFIC_SOURCE_HASH = $sourceHash
        $env:NETWORK_TRAFFIC_M1_PROFILE_HASH = $report.profileHash
        $env:NETWORK_TRAFFIC_UX_PROFILE_HASH = $report.uxProfileHash
        $env:NETWORK_TRAFFIC_M2_PROFILE_HASH = $report.m2ProfileHash
        $ordinaryStarted = [DateTimeOffset]::UtcNow
        Run-Step 'ordinary-tests' 'dotnet' @('test', 'tests/NetWorkTrafficPlugin.Tests', '-c', 'Debug', '--no-build', '--no-restore',
            '--filter', 'Category!=ResourceProbe&Category!=M1ResourceProbe&Category!=NativeUiProbe&Category!=M2ResourceProbe', '--logger', 'trx;LogFileName=ordinary.trx', '--results-directory', $directory) $(if ($Mode -in @('UX','M2')) { 900 } else { 600 })
        $ordinary = @(Read-CheckedTrx (Join-Path $directory 'ordinary.trx') $ordinaryStarted)
        $protocol = Read-CheckedEvidence (Join-Path $directory 'protocol-probes.json') $runId $sourceHash $ordinaryStarted
        $stream = Read-CheckedEvidence (Join-Path $directory 'stream-probes.json') $runId $sourceHash $ordinaryStarted
        Test-ProtocolEvidence $protocol $stream
        $resourceStarted = [DateTimeOffset]::UtcNow
        Run-Step 'resource-tests' 'dotnet' @('test', 'tests/NetWorkTrafficPlugin.Tests', '-c', 'Debug', '--no-build', '--no-restore',
            '--filter', 'Category=ResourceProbe', '--logger', 'trx;LogFileName=resource.trx', '--results-directory', $directory) 1200
        $resourceRows = @(Read-CheckedTrx (Join-Path $directory 'resource.trx') $resourceStarted)
        $resource = Read-CheckedEvidence (Join-Path $directory 'resource-probes.json') $runId $sourceHash $resourceStarted
        $profile = Get-Content (Join-Path $PSScriptRoot 'm0-resource-profile.json') -Raw | ConvertFrom-Json -AsHashtable
        Test-ResourceEvidence $resource $directory $profile
        $map = Get-Content (Join-Path $PSScriptRoot 'm0-test-map.json') -Raw | ConvertFrom-Json
        $coverage = Test-ScenarioMap $map @($ordinary + $resourceRows)
        $report.testCount = $ordinary.Count + $resourceRows.Count
        if ($Mode -in @('M1','UX','M2')) {
            $m1Started = [DateTimeOffset]::UtcNow
            Run-Step 'm1-resource-tests' 'dotnet' @('test', 'tests/NetWorkTrafficPlugin.Tests', '-c', 'Debug', '--no-build', '--no-restore', '--filter', 'Category=M1ResourceProbe', '--logger', 'trx;LogFileName=m1-resource.trx', '--results-directory', $directory) 1200
            $m1Rows = @(Read-CheckedTrx (Join-Path $directory 'm1-resource.trx') $m1Started)
            $uiStarted = [DateTimeOffset]::UtcNow
            Run-Step 'native-ui-tests' 'dotnet' @('test', 'tests/NetWorkTrafficPlugin.Tests', '-c', 'Debug', '--no-build', '--no-restore', '--filter', 'Category=NativeUiProbe', '--logger', 'trx;LogFileName=ui.trx', '--results-directory', $directory) 1200
            $uiRows = @(Read-CheckedTrx (Join-Path $directory 'ui.trx') $uiStarted)
            $m1Map = Get-Content (Join-Path $PSScriptRoot 'm1-test-map.json') -Raw | ConvertFrom-Json
            $m1Profile = Get-Content (Join-Path $PSScriptRoot 'm1-resource-profile.json') -Raw | ConvertFrom-Json -AsHashtable
            $report.testCount += $m1Rows.Count + $uiRows.Count
            $report['requiredM1Scenarios'] = 53
            $coverage = @(Test-ScenarioMap $map @($ordinary + $resourceRows)) + @(Test-M1ScenarioMap $m1Map @($ordinary + $resourceRows + $m1Rows + $uiRows))
            @{ schemaVersion = 1; runId = $runId; sourceHash = $sourceHash; evidenceAssessed = $false; scenarios = $coverage } | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $directory 'scenario-coverage.json') -Encoding utf8
            if ($Mode -in @('UX','M2')) {
                $uxMap = Get-Content (Join-Path $PSScriptRoot 'ux-test-map.json') -Raw | ConvertFrom-Json
                $uxProfile = Get-Content (Join-Path $PSScriptRoot 'ux-profile.json') -Raw | ConvertFrom-Json -AsHashtable
                $report['requiredUXScenarios'] = 40
                if ($Mode -eq 'M2') {
                    $m2Started = [DateTimeOffset]::UtcNow
                    Run-Step 'm2-resource-tests' 'dotnet' @('test','tests/NetWorkTrafficPlugin.Tests','-c','Debug','--no-build','--no-restore','--filter','Category=M2ResourceProbe','--logger','trx;LogFileName=m2-resource.trx','--results-directory',$directory) 1200
                    $m2Rows = @(Read-CheckedTrx (Join-Path $directory 'm2-resource.trx') $m2Started)
                    $m2Map = Get-Content (Join-Path $PSScriptRoot 'm2-test-map.json') -Raw | ConvertFrom-Json
                    $m2Profile = Get-Content (Join-Path $PSScriptRoot 'm2-profile.json') -Raw | ConvertFrom-Json -AsHashtable
                    Assert-Check ($m2Rows.Count -eq 4 -and @($m2Rows | Where-Object method -CNotIn $m2Map.scenarios.Where({$_.id -match '^V5-P'}).tests.method).Count -eq 0) 'M2资源类别执行集合不完整。'
                    $report.testCount += $m2Rows.Count
                    $report['requiredM2Scenarios'] = 64; $report['requiredScenarios'] = 207
                    $coverage = Test-CompleteM2Evidence $directory $runId $sourceHash $report.profileHash $report.uxProfileHash $report.m2ProfileHash $started @($ordinary + $resourceRows + $m1Rows + $uiRows + $m2Rows) $map $m1Map $uxMap $m2Map $profile $m1Profile $uxProfile $m2Profile
                    Assert-Check (@($coverage).Count -eq 207) 'M2总场景数量错误。'
                } else {
                $coverage = Test-CompleteUXEvidence $directory $runId $sourceHash $report.profileHash $report.uxProfileHash $started @($ordinary + $resourceRows + $m1Rows + $uiRows) $map $m1Map $uxMap $profile $m1Profile $uxProfile
                }
            } else {
                $coverage = Test-CompleteM1Evidence $directory $runId $sourceHash $report.profileHash $started @($ordinary + $resourceRows + $m1Rows + $uiRows) $map $m1Map $profile $m1Profile
            }
        }
        [ordered]@{ schemaVersion = 1; runId = $runId; sourceHash = $sourceHash; scenarios = $coverage } |
            ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $directory 'scenario-coverage.json') -Encoding utf8
    }
    Check-Docs
    Assert-Check ((Get-SourceHash $root) -ceq $sourceHash) '测量期间行为源码发生变化，本轮不能通过。'
    Test-DevelopmentScopeReport $report $Mode
    $report.automaticPassed = $true
    $report['conclusion'] = if ($Mode -eq 'Docs') { '仅文档检查通过，未执行业务验证。' } else { "$Mode 本地开发自动门禁通过；人工、真实 Host、真实客户端及发布未验。" }
} catch {
    $report['failure'] = $_.Exception.Message
    Write-Host "检查失败：$($_.Exception.Message)"
} finally {
    $report['finishedUtc'] = [DateTimeOffset]::UtcNow
    $report['endingSourceHash'] = Get-SourceHash $root
    $report['endingDocumentationHash'] = Get-DocumentationHash $root
    $artifacts = @(Get-ChildItem -LiteralPath $directory -File | Where-Object Name -NotIn @('verification.json','artifacts.json') | ForEach-Object {
        @{ name = $_.Name; length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    @{ schemaVersion = 1; runId = $runId; sourceHash = $sourceHash; files = $artifacts } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $directory 'artifacts.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $directory 'verification.json') -Encoding utf8
    foreach ($name in $originalEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name]) }
    Write-Host "报告：$directory"
}
if (-not $report.automaticPassed) { exit 1 }
Write-Host $report.conclusion
