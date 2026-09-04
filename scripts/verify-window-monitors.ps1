. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $RepoRoot
try {
    & $Dotnet restore tools/Workbench.DesktopFixture --locked-mode -v quiet
    if($LASTEXITCODE -ne 0){throw 'Locked restore failed.'}
    & $Dotnet build tools/Workbench.DesktopFixture --no-restore -v quiet
    if($LASTEXITCODE -ne 0){throw 'Build failed.'}
    $monitorOutput=Join-Path $RepoRoot ('artifacts/verification/window-monitors-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
    & $Dotnet tools/Workbench.DesktopFixture/bin/Debug/net10.0-windows10.0.19041.0/Workbench.DesktopFixture.dll --verify-window-monitors $monitorOutput
    if($LASTEXITCODE -ne 0){throw "Monitor verification failed: $monitorOutput"}
    $monitorReport=Get-Content -LiteralPath (Join-Path $monitorOutput 'report.json') -Raw | ConvertFrom-Json
    if($monitorReport.status -ne 'OBSERVED_NOT_ENDURANCE' -or $monitorReport.callbacks -ne 60 -or $monitorReport.samples.Count -ne 7){throw 'Incomplete monitor evidence.'}
} finally {Pop-Location}
