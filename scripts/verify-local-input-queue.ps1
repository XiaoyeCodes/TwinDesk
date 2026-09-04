$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'environment.ps1')
$queueRun = Join-Path $RepoRoot ('artifacts/verification/local-input-queue-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $queueRun | Out-Null
Push-Location $RepoRoot
try {
    & $Dotnet restore tests/Workbench.Windows.Tests --locked-mode -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & $Dotnet test tests/Workbench.Windows.Tests --no-restore -v minimal --logger 'trx;LogFileName=unit-tests.trx' --results-directory $queueRun
    if ($LASTEXITCODE -ne 0) { throw 'C# tests failed.' }
    & $Node --test tests/scene-timeline.test.cjs tests/frame-presenter.test.cjs tests/input-client.test.cjs tests/input-move-queue.test.cjs tests/jpeg-decoder.test.cjs tests/f0-pointer-calibration.test.cjs tests/local-console.test.cjs |
        Tee-Object -FilePath (Join-Path $queueRun 'javascript.txt')
    if ($LASTEXITCODE -ne 0) { throw 'JS tests failed.' }
    & $Dotnet restore tools/Workbench.MediaProbe --locked-mode -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Probe restore failed.' }
    & $Dotnet build tools/Workbench.MediaProbe --no-restore -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    $files = @(Get-ChildItem src/Workbench.Windows,tools/Workbench.MediaProbe,tests/Workbench.Windows.Tests,tests -File |
        Where-Object Extension -in @('.cs','.csproj','.js','.cjs','.html') | Get-FileHash | Select-Object Path,Hash)
    [ordered]@{time=(Get-Date).ToString('o');status='PASS';scope='Protocol/state/queue logic and build only. Synthetic scheduling is not NX response latency or physical device evidence.';sourceBase=(& git rev-parse HEAD);sources=$files} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $queueRun 'report.json') -Encoding utf8
    $queueRun | Set-Content -LiteralPath (Join-Path $RepoRoot 'artifacts/verification/latest-local-input-queue.txt') -Encoding utf8
    Write-Host "Input queue evidence: $queueRun"
} finally { Pop-Location }
