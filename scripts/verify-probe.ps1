[CmdletBinding()]
param([switch]$CaptureNx)
. (Join-Path $PSScriptRoot 'environment.ps1')
$ErrorActionPreference = 'Stop'
$runPath = Join-Path $RepoRoot ('artifacts/verification/probe-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $runPath | Out-Null
$checks = [System.Collections.Generic.List[object]]::new()
$probeProject = Join-Path $RepoRoot 'tools/Workbench.Probe/Workbench.Probe.csproj'

function Invoke-Probe([string[]]$ProbeArguments) {
    $lines = & $Dotnet run --project $probeProject --no-build -- @ProbeArguments 2>&1
    $probeExit = $LASTEXITCODE
    [pscustomobject]@{ exitCode = $probeExit; output = ($lines | ForEach-Object { "$_" }) -join "`n" }
}
function Record-Check([string]$Name, [bool]$Passed, [object]$Evidence) {
    $checks.Add([pscustomobject]@{ name=$Name; status=$(if ($Passed) {'PASS'} else {'FAIL'}); evidence=$Evidence })
    Write-Host "$Name : $($checks[$checks.Count-1].status)"
}

Push-Location $RepoRoot
try {
    & $Dotnet restore Workbench.slnx --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & $Dotnet build Workbench.slnx -c Debug --no-restore --nologo "-bl:$runPath/build.binlog"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Record-Check 'locked-restore-and-build' $true 'build.binlog'

    $result = Invoke-Probe @('--help')
    Record-Check 'help' ($result.exitCode -eq 0 -and $result.output.Contains('Local diagnostic tool')) $result
    $result = Invoke-Probe @('--process')
    Record-Check 'missing-option-value-rejected' ($result.exitCode -eq 1 -and $result.output.Contains('Missing value')) $result
    $missingProcess = 'ug-workbench-not-a-real-process-' + [Guid]::NewGuid().ToString('N')
    $result = Invoke-Probe @('--process', $missingProcess, '--list')
    Record-Check 'absent-process-empty-list' ($result.exitCode -eq 0 -and @($result.output | ConvertFrom-Json).Count -eq 0) $result
    $result = Invoke-Probe @('--process', $missingProcess)
    Record-Check 'absent-process-capture-rejected' ($result.exitCode -eq 1 -and $result.output.Contains('exactly one')) $result

    $displayPath = Join-Path $runPath 'displays.json'
    $result = Invoke-Probe @('--displays','--report',$displayPath)
    $displayData = $result.output | ConvertFrom-Json
    Record-Check 'native-display-enumeration' ($result.exitCode -eq 0 -and @($displayData.displays).Count -gt 0) $displayData
    $beforeHash = (Get-FileHash -LiteralPath $displayPath -Algorithm SHA256).Hash
    $result = Invoke-Probe @('--displays','--report',$displayPath)
    Record-Check 'existing-evidence-not-overwritten' ($result.exitCode -eq 1 -and $beforeHash -eq (Get-FileHash -LiteralPath $displayPath -Algorithm SHA256).Hash) $result

    $result = Invoke-Probe @('--encoders','--report',(Join-Path $runPath 'encoders.json'))
    $encoderData = $result.output | ConvertFrom-Json
    Record-Check 'encoder-probe-report' ($result.exitCode -eq 0 -and $encoderData.stage -eq 'enumeration-and-activation-only') $encoderData

    if ($CaptureNx) {
        $result = Invoke-Probe @('--process','ugraf','--seconds','1','--output',(Join-Path $runPath 'nx.png'))
        $captureData = $result.output | ConvertFrom-Json
        Record-Check 'nx-first-frame-only' ($result.exitCode -eq 0 -and $captureData.frames -ge 1 -and $captureData.width -gt 0) $captureData
    }
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'src/Workbench.Windows'),(Join-Path $RepoRoot 'tools/Workbench.Probe') -Filter '*.cs' -File |
        Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
    $report = [ordered]@{
        recordedAt=[DateTimeOffset]::Now.ToString('o'); scope='Probe smoke checks only, not M1 completion or real workflow acceptance.'
        checks=$checks.ToArray(); sources=$sources; captureNx=$CaptureNx.IsPresent
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runPath 'report.json') -Encoding utf8
    Write-Host "Probe smoke evidence: $runPath"
    if (@($checks | Where-Object status -eq 'FAIL').Count -gt 0) { exit 1 }
}
finally { Pop-Location }
