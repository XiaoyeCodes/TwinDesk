param([switch]$Bgra,[switch]$Trend)
. (Join-Path $PSScriptRoot 'environment.ps1')
if($Bgra -and $Trend){throw 'Trend compares full NV12 source; use Bgra separately.'}
Push-Location $RepoRoot
try {
    & $Dotnet restore tools/Workbench.DesktopFixture --locked-mode -v quiet
    if($LASTEXITCODE -ne 0){throw 'Locked restore failed.'}
    & $Dotnet build tools/Workbench.DesktopFixture --no-restore -v quiet
    if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
    $sharedOutput=Join-Path $RepoRoot ('artifacts/verification/shared-capture-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
    $sharedMode=if($Trend){'--verify-shared-capture-trend'}elseif($Bgra){'--verify-shared-capture-bgra'}else{'--verify-shared-capture'}
    & $Dotnet tools/Workbench.DesktopFixture/bin/Debug/net10.0-windows10.0.19041.0/Workbench.DesktopFixture.dll $sharedMode $sharedOutput
    if($LASTEXITCODE -ne 0){throw "Shared capture verification failed: $sharedOutput"}
    $sharedReport=Get-Content -LiteralPath (Join-Path $sharedOutput 'report.json') -Raw | ConvertFrom-Json
    $expectedResources=if($Trend){25}else{19}
    if($sharedReport.status -ne 'PASS' -or $sharedReport.checks.Count -ne 13 -or $sharedReport.resources.Count -ne $expectedResources){throw 'Incomplete shared capture evidence.'}
    Write-Host "Shared capture checks complete; inspect resource trend separately: $sharedOutput"
} finally {Pop-Location}
