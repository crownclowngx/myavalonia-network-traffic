param([ValidateSet('Docs', 'M0')][string] $Mode = 'M0')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevelopmentChecks.ps1')
$root = Split-Path $PSScriptRoot
$started = [DateTimeOffset]::UtcNow
$runId = $started.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$directory = Join-Path $root "TestResults/NetworkTrafficM0/$runId"
[IO.Directory]::CreateDirectory($directory) | Out-Null
$history = [System.Collections.Generic.List[object]]::new()
$sourceHash = Get-SourceHash $root
$originalEnvironment = @{}
foreach ($name in @('NETWORK_TRAFFIC_TEST_ARTIFACTS', 'NETWORK_TRAFFIC_RUN_ID', 'NETWORK_TRAFFIC_SOURCE_HASH')) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$report = [ordered]@{
    schemaVersion = 1; runId = $runId; mode = $Mode; sourceHash = $sourceHash; startedUtc = $started
    head = (& git -C $root rev-parse HEAD); dirty = @(& git -C $root status --porcelain).Count -gt 0
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription; architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    sdk = (& dotnet --version); automaticPassed = $false; testCount = 0; commands = $history
    realClientVerified = $false; hostVerified = $false; manualAccepted = $false; deployed = $false
    windowsCiExecuted = $false; releaseGateExecuted = $false
    pending = @('H01/H02 实际界面操作', 'H03 指定真实 Host', 'H04 指定真实客户端')
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
    Assert-Check (([DateTimeOffset]::UtcNow - $started).TotalSeconds -lt 1800) '整轮开发检查超时。'
    Write-Host "[$runId] $Name"
    Invoke-CheckedProcess -File $File -Arguments $CommandArguments -WorkingDirectory $root -TimeoutSeconds $Timeout -History $history -LogPath (Join-Path $directory "$Name.log") | Out-Null
}
try {
    Check-Docs
    if ($Mode -eq 'M0') {
        Run-Step 'gate-self-test' 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'test-development-gate.ps1')) 60
        Run-Step 'restore-locked' 'dotnet' @('restore', 'NetWorkTrafficPlugin.slnx', '--locked-mode') 300
        Run-Step 'build-debug' 'dotnet' @('build', 'NetWorkTrafficPlugin.slnx', '-c', 'Debug', '--no-restore', '-warnaserror') 300
        $env:NETWORK_TRAFFIC_TEST_ARTIFACTS = $directory
        $env:NETWORK_TRAFFIC_RUN_ID = $runId
        $env:NETWORK_TRAFFIC_SOURCE_HASH = $sourceHash
        $ordinaryStarted = [DateTimeOffset]::UtcNow
        Run-Step 'ordinary-tests' 'dotnet' @('test', 'tests/NetWorkTrafficPlugin.Tests', '-c', 'Debug', '--no-build', '--no-restore',
            '--filter', 'Category!=ResourceProbe', '--logger', 'trx;LogFileName=ordinary.trx', '--results-directory', $directory) 300
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
        [ordered]@{ schemaVersion = 1; runId = $runId; sourceHash = $sourceHash; scenarios = $coverage } |
            ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $directory 'scenario-coverage.json') -Encoding utf8
        $report.testCount = $ordinary.Count + $resourceRows.Count
    }
    Check-Docs
    Assert-Check ((Get-SourceHash $root) -ceq $sourceHash) '测量期间行为源码发生变化，本轮不能通过。'
    $report.automaticPassed = $true
    $report['conclusion'] = if ($Mode -eq 'Docs') { '仅文档检查通过，未执行 M0 业务验证。' } else { 'M0 本地开发自动门禁通过；人工、真实 Host、真实客户端及发布未验。' }
} catch {
    $report['failure'] = $_.Exception.Message
    Write-Host "检查失败：$($_.Exception.Message)"
} finally {
    $report['finishedUtc'] = [DateTimeOffset]::UtcNow
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $directory 'verification.json') -Encoding utf8
    foreach ($name in $originalEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name]) }
    Write-Host "报告：$directory"
}
if (-not $report.automaticPassed) { exit 1 }
Write-Host $report.conclusion
