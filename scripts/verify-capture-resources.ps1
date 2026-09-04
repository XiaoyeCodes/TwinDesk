param([switch]$WindowsOnly,[switch]$ItemsOnly,[switch]$ItemEvents,[switch]$NativeEvents,[switch]$AfterClosed,[switch]$RawDelegate,[switch]$LongRun,[switch]$SteadyState,[switch]$Lifetimes,[switch]$SharedDevice)
. (Join-Path $PSScriptRoot 'environment.ps1')
$ErrorActionPreference='Stop'
Push-Location $RepoRoot
try {
    & $Dotnet restore tools/Workbench.DesktopFixture --locked-mode -v quiet
    if($LASTEXITCODE -ne 0){throw 'Locked restore failed.'}
    & $Dotnet build tools/Workbench.DesktopFixture --no-restore -v quiet
    if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
    $captureResourcePath=Join-Path $RepoRoot ('artifacts/verification/capture-resources-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
    if(($WindowsOnly -and ($ItemsOnly -or $ItemEvents)) -or ($ItemEvents -and -not $ItemsOnly)){throw 'Select one isolated mode; ItemEvents requires ItemsOnly.'}
    if($NativeEvents -and -not($ItemsOnly -and $ItemEvents)){throw 'NativeEvents requires ItemsOnly and ItemEvents.'}
    if($AfterClosed -and -not $NativeEvents){throw 'AfterClosed requires NativeEvents.'}
    if($RawDelegate -and (-not $NativeEvents -or $AfterClosed)){throw 'RawDelegate requires NativeEvents and cannot yet combine with AfterClosed.'}
    if(($LongRun -or $SteadyState -or $Lifetimes) -and ($WindowsOnly -or $ItemsOnly -or $ItemEvents -or $NativeEvents -or $AfterClosed -or $RawDelegate)){throw 'LongRun/SteadyState/Lifetimes require full capture mode.'}
    if(@($LongRun,$SteadyState,$Lifetimes | Where-Object {$_}).Count -gt 1){throw 'Choose one full capture experiment.'}
    if($SharedDevice -and -not $Lifetimes){throw 'SharedDevice currently requires Lifetimes.'}
    $expectedCycles=if($Lifetimes){10}elseif($SteadyState){600}elseif($LongRun){120}else{40}
    $expectedRounds=if($Lifetimes){12}elseif($SteadyState){1}else{3}
    $resourceMode=if($Lifetimes){'--verify-resource-lifetimes'}elseif($SteadyState){'--verify-resource-steady'}elseif($LongRun){'--verify-resource-trend'}elseif($WindowsOnly){'--verify-window-resources'}elseif($AfterClosed){'--verify-closed-callbacks'}elseif($RawDelegate){'--verify-raw-events'}elseif($NativeEvents){'--verify-native-events'}elseif($ItemEvents){'--verify-item-events'}elseif($ItemsOnly){'--verify-item-resources'}else{'--verify-resources'}
    if($SharedDevice){$resourceMode='--verify-resource-shared'}
    & $Dotnet tools/Workbench.DesktopFixture/bin/Debug/net10.0-windows10.0.19041.0/Workbench.DesktopFixture.dll $resourceMode $captureResourcePath
    if($LASTEXITCODE -ne 0){throw "Resource observation failed: $captureResourcePath"}
    $captureReport=Get-Content -LiteralPath (Join-Path $captureResourcePath 'report.json') -Raw | ConvertFrom-Json
    if($captureReport.status -ne 'OBSERVED_NOT_ENDURANCE' -or $captureReport.rounds.Count -ne $expectedRounds -or
        @($captureReport.rounds | Where-Object {$_.cycles -ne $expectedCycles -or (-not ($WindowsOnly -or $ItemsOnly) -and ($_.sceneTransitions -ne (1+2*$expectedCycles) -or $_.activeAfterDispose -ne 0 -or $_.actualDestroyNotifications -lt $expectedCycles))}).Count){throw 'Capture resource evidence incomplete.'}
    Write-Host "Observed $expectedRounds x $expectedCycles actual owner cycles; inspect resource trend, not endurance PASS: $captureResourcePath"
    if($AfterClosed -and @($captureReport.rounds | Where-Object callbacks -ne 40).Count){throw 'Missing real Closed callbacks.'}
    if(-not ($WindowsOnly -or $ItemsOnly) -and ($captureReport.rootClosure.status -ne 'PASS' -or -not $captureReport.rootClosure.rejectedOldStream -or $captureReport.rootClosure.activeAfterDispose -ne 0)){throw 'Root closure did not fail closed.'}
    if($SharedDevice -and (-not $captureReport.sharedDevice -or $captureReport.sharedActiveAfterDispose -ne 0 -or @($captureReport.rounds | Where-Object graphicsDeviceIdentity -ne $captureReport.graphicsDeviceIdentity).Count)){throw 'Shared device lifetime mismatch.'}
} finally {Pop-Location}
