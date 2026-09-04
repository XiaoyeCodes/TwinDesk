param([ValidateSet('item','item-raw','item-factory','item-native','item-roinit','pool','session','copy')][string[]]$Modes=@('session'),[ValidateRange(0,180)][int]$SettleSeconds=0)
. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $RepoRoot
try {
    & $Dotnet restore tools/Workbench.DesktopFixture --locked-mode -v quiet
    if($LASTEXITCODE -ne 0){throw 'Locked restore failed.'}
    & $Dotnet build tools/Workbench.DesktopFixture --no-restore -v quiet
    if($LASTEXITCODE -ne 0){throw 'Build failed.'}
    foreach($mode in $Modes){
        $sessionOutput=Join-Path $RepoRoot ('artifacts/verification/capture-session-'+$mode+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
        & $Dotnet tools/Workbench.DesktopFixture/bin/Debug/net10.0-windows10.0.19041.0/Workbench.DesktopFixture.dll --verify-capture-session $mode $sessionOutput --settle-seconds $SettleSeconds
        if($LASTEXITCODE -ne 0){throw "Capture session verification failed: $sessionOutput"}
        $sessionReport=Get-Content -LiteralPath (Join-Path $sessionOutput 'report.json') -Raw | ConvertFrom-Json
        $expectedFrames=if($mode -in @('session','copy')){60}else{0}
        if($sessionReport.status -ne 'OBSERVED_NOT_ENDURANCE' -or $sessionReport.frames -ne $expectedFrames -or $sessionReport.samples.Count -ne (8+[Math]::Ceiling($SettleSeconds/10))){throw 'Incomplete session evidence.'}
    }
} finally {Pop-Location}
