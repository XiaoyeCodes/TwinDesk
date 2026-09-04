[CmdletBinding()]
param([ValidateRange(1,40)][int]$Cycles = 20)
. (Join-Path $PSScriptRoot 'environment.ps1')
$sceneRunDirectory = Join-Path $RepoRoot ('artifacts/verification/scene-fixture-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
Push-Location $RepoRoot
try {
    & $Dotnet restore tools/Workbench.DesktopFixture --locked-mode -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Locked fixture restore failed.' }
    & $Dotnet build tools/Workbench.DesktopFixture --no-restore -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
    & $Dotnet tools/Workbench.DesktopFixture/bin/Debug/net10.0-windows10.0.19041.0/Workbench.DesktopFixture.dll --verify-scene $sceneRunDirectory --cycles $Cycles
    $sceneExitCode = $LASTEXITCODE
    $sceneReportPath = Join-Path $sceneRunDirectory 'report.json'
    if (-not (Test-Path -LiteralPath $sceneReportPath)) { throw 'Fixture produced no report; inspect startup/UI failure.' }
    $sceneReport = Get-Content -Raw -LiteralPath $sceneReportPath | ConvertFrom-Json
    $sceneSources = @(Get-ChildItem src/Workbench.Windows,tools/Workbench.DesktopFixture -File |
        Where-Object { $_.Extension -in @('.cs','.csproj') -or $_.Name -eq 'packages.lock.json' } |
        Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
    [ordered]@{time=(Get-Date).ToString('o');scope='Synthetic real Windows/WGC/GPU fixture only; no browser or injected input';sources=$sceneSources} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $sceneRunDirectory 'source-identity.json') -Encoding utf8
    if ($sceneExitCode -ne 0 -or $sceneReport.status -ne 'PASS' -or $sceneReport.checks.Count -ne (7 + 2*$Cycles) -or $sceneReport.activeCapturesAfterDispose -ne 0) {
        throw "Fixture did not pass; preserve evidence at $sceneRunDirectory"
    }
    Write-Host "SC02 fixture: $($sceneReport.checks.Count) checks; resource samples are observations, not an endurance PASS."
    Write-Host "Scene evidence: $sceneRunDirectory"
} finally { Pop-Location }
