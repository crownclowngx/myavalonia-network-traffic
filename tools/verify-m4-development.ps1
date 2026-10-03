param([ValidateSet('M4Foundation','M4')][string]$Mode = 'M4Foundation')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'M4DevelopmentChecks.ps1')
$root = Split-Path $PSScriptRoot
$started = [DateTimeOffset]::UtcNow
$runId = $started.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$directory = Join-Path $root "TestResults/NetworkTraffic$Mode/$runId"
[IO.Directory]::CreateDirectory($directory) | Out-Null
$history = [System.Collections.Generic.List[object]]::new()
$sourceHash = Get-SourceHash $root
$profile = Get-Content (Join-Path $PSScriptRoot 'm4-profile.json') -Raw | ConvertFrom-Json -AsHashtable
$savedEnvironment = @{}
foreach ($name in @('NETWORK_TRAFFIC_TEST_ARTIFACTS','NETWORK_TRAFFIC_RUN_ID','NETWORK_TRAFFIC_SOURCE_HASH')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
$report = [ordered]@{
    schemaVersion=1; mode=$Mode; runId=$runId; sourceHash=$sourceHash; profileHash=(Get-FileHash (Join-Path $PSScriptRoot 'm4-profile.json')).Hash
    mapHash=(Get-FileHash (Join-Path $PSScriptRoot 'm4-test-map.json')).Hash; startedUtc=$started; head=(& git -C $root rev-parse HEAD)
    dirty=@(& git -C $root status --porcelain).Count -gt 0; sdk=(& dotnet --version); os=[Runtime.InteropServices.RuntimeInformation]::OSDescription
    automaticPassed=$false; fullM4Passed=$false; m3Passed=$false; manualAccepted=$false; realClientVerified=$false; hostVerified=$false
    windowsCiExecuted=$false; releaseGateExecuted=$false; deployed=$false; testCount=0; foundationTests=0
    pending=$profile.pending; commands=$history; documentationHash=(Get-DocumentationHash $root)
}
function Run-M4Step([string]$Name,[string]$File,[string[]]$Arguments,[int]$Timeout=300) {
    Assert-Check (([DateTimeOffset]::UtcNow-$started).TotalSeconds -lt 1800) 'M4分项门禁超出总期限。'
    Write-Host "[$runId] $Name"
    Invoke-CheckedProcess -File $File -Arguments $Arguments -WorkingDirectory $root -TimeoutSeconds $Timeout -History $history -LogPath (Join-Path $directory "$Name.log") | Out-Null
}
try {
    if ($Mode -eq 'M4') {
        $m3=Get-Content (Join-Path $PSScriptRoot 'm3-profile.json') -Raw | ConvertFrom-Json
        Test-M4Prerequisites $m3.runtimeIntegrationEnabled 0 $false
        throw '完整M4汇总实现尚未接通。'
    }
    Run-M4Step 'docs' 'pwsh' @('-NoProfile','-File','tools/verify-development.ps1','-Mode','Docs')
    Run-M4Step 'inherited-gate-self-test' 'pwsh' @('-NoProfile','-File','tools/TestM2DevelopmentGate.ps1')
    Run-M4Step 'm4-gate-self-test' 'pwsh' @('-NoProfile','-File','tools/TestM4DevelopmentGate.ps1')
    Run-M4Step 'restore-locked' 'dotnet' @('restore','NetWorkTrafficPlugin.slnx','--locked-mode')
    Run-M4Step 'build-debug' 'dotnet' @('build','NetWorkTrafficPlugin.slnx','-c','Debug','--no-restore','-warnaserror')
    $env:NETWORK_TRAFFIC_TEST_ARTIFACTS=$directory; $env:NETWORK_TRAFFIC_RUN_ID=$runId; $env:NETWORK_TRAFFIC_SOURCE_HASH=$sourceHash
    $testStart = [DateTimeOffset]::UtcNow
    Run-M4Step 'ordinary-tests' 'dotnet' @('test','tests/NetWorkTrafficPlugin.Tests','-c','Debug','--no-build','--no-restore','--filter',
        'Category!=ResourceProbe&Category!=M1ResourceProbe&Category!=NativeUiProbe&Category!=M2ResourceProbe&Category!=M4ResourceProbe',
        '--logger','trx;LogFileName=ordinary.trx','--results-directory',$directory) 900
    $ordinary = @(Read-CheckedTrx (Join-Path $directory 'ordinary.trx') $testStart)
    $resourceStart = [DateTimeOffset]::UtcNow
    Run-M4Step 'm4-resource-tests' 'dotnet' @('test','tests/NetWorkTrafficPlugin.Tests','-c','Debug','--no-build','--no-restore','--filter','Category=M4ResourceProbe',
        '--logger','trx;LogFileName=m4-resource.trx','--results-directory',$directory) 300
    $resource = @(Read-CheckedTrx (Join-Path $directory 'm4-resource.trx') $resourceStart)
    $map=Get-Content (Join-Path $PSScriptRoot 'm4-test-map.json') -Raw | ConvertFrom-Json
    $report.foundationTests=Test-M4Rows $map @($ordinary+$resource) $profile
    $report.testCount=$ordinary.Count+$resource.Count
    Test-M4Send (Read-CheckedEvidence (Join-Path $directory 'm4-send.json') $runId $sourceHash $testStart)
    Test-M4Resources (Read-CheckedEvidence (Join-Path $directory 'm4-resource.json') $runId $sourceHash $resourceStart) $profile
    foreach ($layout in $profile.layouts) { foreach ($theme in $profile.themes) { foreach ($view in $profile.views) {
        $path=Join-Path $directory "m4-$view-$theme-$($layout.width).png"
        $metadata=Get-Content "$path.json" -Raw | ConvertFrom-Json -AsHashtable
        Test-M4Image $path $metadata $runId $sourceHash $layout.width $layout.height
        Assert-Check ($metadata.kind -ceq $view -and $metadata.dark -eq ($theme -eq 'dark')) '截图视图或主题错误。'
    } } }
    Test-M4Report $report (Get-SourceHash $root)
    $report.automaticPassed=$true
    $report['conclusion']='M4独立分项本地Debug门禁通过；完整M3/M4、混合持续负载和人工验收仍未通过。'
} catch { $report['failure']=$_.Exception.Message; Write-Host "检查失败：$($_.Exception.Message)" }
finally {
    $report['finishedUtc']=[DateTimeOffset]::UtcNow; $report['endingSourceHash']=Get-SourceHash $root
    $report['endingDocumentationHash']=Get-DocumentationHash $root
    $files=@(Get-ChildItem $directory -File | Where-Object Name -NotIn @('verification.json','artifacts.json') | ForEach-Object { @{name=$_.Name;length=$_.Length;sha256=(Get-FileHash $_.FullName).Hash} })
    @{schemaVersion=1;runId=$runId;sourceHash=$sourceHash;files=$files} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $directory 'artifacts.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $directory 'verification.json') -Encoding utf8
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name]) }
    Write-Host "报告：$directory"
}
if (-not $report.automaticPassed) { exit 1 }
Write-Host $report.conclusion
