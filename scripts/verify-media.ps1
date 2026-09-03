param([int]$Frames = 90)
. (Join-Path $PSScriptRoot 'environment.ps1')
if ($Frames -lt 30 -or $Frames -gt 18000) { throw 'Frames must be 30..18000.' }
$mediaRunPath = Join-Path $RepoRoot ('artifacts/verification/media-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $mediaRunPath | Out-Null
$mediaChecks = [Collections.Generic.List[object]]::new()
Push-Location $RepoRoot
try {
    & $Dotnet restore tests/Workbench.Windows.Tests --locked-mode -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Locked test restore failed.' }
    & $Dotnet test tests/Workbench.Windows.Tests --no-restore -v minimal --logger 'trx;LogFileName=unit-tests.trx' --results-directory $mediaRunPath
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    & $Dotnet restore tools/Workbench.Probe --locked-mode -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Locked probe restore failed.' }
    & $Dotnet build tools/Workbench.Probe --no-restore -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    & $Node -e 'const fs=require("fs"),vm=require("vm");new vm.Script(fs.readFileSync("tools/Workbench.MediaProbe/probe.html","utf8").match(/<script>([\s\S]*?)<\/script>/)[1],{filename:"probe-inline.js"})'
    if ($LASTEXITCODE -ne 0) { throw 'Browser JavaScript syntax check failed.' }
    & $Node --test tests/scene-timeline.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Scene timeline tests failed.' }
    $mediaProbeDll = Join-Path $RepoRoot 'tools/Workbench.Probe/bin/Debug/net10.0-windows10.0.19041.0/Workbench.Probe.dll'
    foreach ($mediaBackend in @('hardware','software')) {
        $mediaOutputPath = Join-Path $mediaRunPath ($mediaBackend + '.h264')
        $mediaArguments = @('--encode-test','--frames',"$Frames",'--output',$mediaOutputPath)
        if ($mediaBackend -eq 'software') { $mediaArguments += '--software' }
        $mediaLines = & $Dotnet $mediaProbeDll @mediaArguments
        if ($LASTEXITCODE -ne 0) { throw "$mediaBackend actual encoding failed." }
        $mediaResult = ($mediaLines -join "`n") | ConvertFrom-Json
        $mediaPassed = $mediaResult.result.inputFrames -eq $Frames -and $mediaResult.result.outputFrames -eq $Frames -and
            $mediaResult.result.outputBytes -eq (Get-Item -LiteralPath $mediaOutputPath).Length -and $mediaResult.frames[0].keyFrame -eq $true
        if (-not $mediaPassed) { throw "$mediaBackend encoding evidence mismatch." }
        $mediaChecks.Add([pscustomobject]@{name="$mediaBackend-actual-encode";status='PASS';result=$mediaResult.result;hash=(Get-FileHash -LiteralPath $mediaOutputPath).Hash})
    }
    $invalidPath = Join-Path $mediaRunPath 'invalid-must-not-exist.h264'
    $invalidOutput = & $Dotnet $mediaProbeDll --encode-test --frames 0 --output $invalidPath 2>&1
    if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $invalidPath)) { throw 'Invalid frame count was not rejected before output creation.' }
    $mediaChecks.Add([pscustomobject]@{name='invalid-frame-count-no-artifact';status='PASS'})
    foreach ($invalidWindowArguments in @(
        @('--encode-window','--seconds','0'), @('--encode-window','--software'),
        @('--encode-window','--encode-test'), @('--encode-window','--include-hidden'),
        @('--encode-window','--process','ug-workbench-absent-process','--seconds','1')
        @('--encode-test','--owned'), @('--owned')
    )) {
        $windowInvalidPath = Join-Path $mediaRunPath ('invalid-window-' + [Guid]::NewGuid().ToString('N') + '.h264')
        $windowInvalidOutput = & $Dotnet $mediaProbeDll @invalidWindowArguments --output $windowInvalidPath 2>&1
        if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $windowInvalidPath)) { throw 'Invalid window request produced output or did not fail.' }
        $mediaChecks.Add([pscustomobject]@{name=('rejected-' + ($invalidWindowArguments -join ' '));status='PASS'})
    }
    $existingPath = Join-Path $mediaRunPath 'hardware.h264'
    $beforeHash = (Get-FileHash -LiteralPath $existingPath).Hash
    $existingOutput = & $Dotnet $mediaProbeDll --encode-test --output $existingPath 2>&1
    if ($LASTEXITCODE -ne 1 -or (Get-FileHash -LiteralPath $existingPath).Hash -ne $beforeHash) { throw 'Existing evidence preservation failed.' }
    $mediaChecks.Add([pscustomobject]@{name='existing-encoded-evidence-preserved';status='PASS'})
    $mediaSources = @(Get-ChildItem src/Workbench.Windows,tools/Workbench.Probe,tools/Workbench.MediaProbe,tests/Workbench.Windows.Tests,tests -File |
        Where-Object { $_.Extension -in @('.cs','.csproj','.html','.js','.cjs') -or $_.Name -eq 'packages.lock.json' } |
        Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
    [ordered]@{time=(Get-Date).ToString('o');scope='C# tests including synthetic GPU alpha pixels, JavaScript scene association tests, parse, actual generated-frame encoders; not browser or NX acceptance';checks=$mediaChecks.ToArray();sources=$mediaSources} |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $mediaRunPath 'report.json') -Encoding utf8
    Write-Host "Media evidence: $mediaRunPath"
} finally { Pop-Location }
