. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $RepoRoot
try {
    & $Dotnet restore tools/Workbench.DesktopFixture --locked-mode -v quiet
    if($LASTEXITCODE -ne 0){throw 'Locked restore failed.'}
    & $Dotnet build tools/Workbench.DesktopFixture --no-restore -v quiet
    if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
    $transientOutput=Join-Path $RepoRoot ('artifacts/verification/transient-windows-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
    & $Dotnet tools/Workbench.DesktopFixture/bin/Debug/net10.0-windows10.0.19041.0/Workbench.DesktopFixture.dll --verify-transient-windows $transientOutput
    if($LASTEXITCODE -ne 0){throw "Transient test failed; preserve evidence: $transientOutput"}
    $transientReport=Get-Content -LiteralPath (Join-Path $transientOutput 'report.json') -Raw | ConvertFrom-Json
    if($transientReport.status -ne 'PASS' -or $transientReport.checks.Count -ne 20 -or $transientReport.destroyedDuringBinding -ne 20 -or $transientReport.transientBindingRetries -ne 20 -or -not $transientReport.rootCloseRejected -or $transientReport.activeAfterDispose -ne 0){throw 'Incomplete transient evidence.'}
    Write-Host "20 real native close-during-bind checks: $transientOutput"
} finally {Pop-Location}
