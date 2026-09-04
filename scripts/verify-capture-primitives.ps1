param([ValidateSet('hook','d3d','manager','manager-shared','nv12-shared','winrt-device','compositor','compositor-clear','compositor-wait','compositor-warp','compositor-no-video','compositor-no-workers','compositor-shared','compositor-init','gpu-clear')][string[]]$Modes=@('hook','d3d','manager','winrt-device'))
. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $RepoRoot
try {
    & $Dotnet restore tools/Workbench.DesktopFixture --locked-mode -v quiet
    if($LASTEXITCODE -ne 0){throw 'Locked restore failed.'}
    & $Dotnet build tools/Workbench.DesktopFixture --no-restore -v quiet
    if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
    foreach($mode in $Modes){
        $primitiveOutput=Join-Path $RepoRoot ('artifacts/verification/capture-primitive-'+$mode+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
        & $Dotnet tools/Workbench.DesktopFixture/bin/Debug/net10.0-windows10.0.19041.0/Workbench.DesktopFixture.dll --verify-capture-primitive $mode $primitiveOutput
        if($LASTEXITCODE -ne 0){throw "Primitive test failed: $primitiveOutput"}
        $primitiveReport=Get-Content -LiteralPath (Join-Path $primitiveOutput 'report.json') -Raw | ConvertFrom-Json
        if($primitiveReport.status -ne 'OBSERVED_NOT_ENDURANCE' -or $primitiveReport.samples.Count -ne 7){throw 'Incomplete primitive observation.'}
    }
} finally {Pop-Location}
