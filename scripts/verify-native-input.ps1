[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'environment.ps1')
$nativeInputDirectory = Join-Path $RepoRoot ('artifacts/verification/native-input-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
Push-Location $RepoRoot
try {
    & $Dotnet restore tools/Workbench.DesktopFixture --locked-mode -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Locked fixture restore failed.' }
    & $Dotnet build tools/Workbench.DesktopFixture --no-restore -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native input fixture build failed.' }
    Write-Host 'Click the Start button in the OWN WINDOW ONLY fixture, then leave keyboard/mouse alone until it closes. No NX/TIA or user files are targeted.'
    & $Dotnet tools/Workbench.DesktopFixture/bin/Debug/net10.0-windows10.0.19041.0/Workbench.DesktopFixture.dll --verify-input $nativeInputDirectory
    $nativeExitCode = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath (Join-Path $nativeInputDirectory 'report.json'))) { throw 'No native report; test was interrupted before evidence could be saved.' }
    $nativeReport = Get-Content -Raw -LiteralPath (Join-Path $nativeInputDirectory 'report.json') | ConvertFrom-Json
    $nativeSources = @(Get-ChildItem src/Workbench.Windows,tools/Workbench.DesktopFixture -File |
        Where-Object { $_.Extension -in @('.cs','.csproj') -or $_.Name -eq 'packages.lock.json' } |
        Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
    [ordered]@{time=(Get-Date).ToString('o');scope='Native self-window input integration; no browser/NX/TIA';sources=$nativeSources} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $nativeInputDirectory 'source-identity.json') -Encoding utf8
    if ($nativeExitCode -ne 0 -or $nativeReport.status -ne 'PASS' -or $nativeReport.checks.Count -ne 20 -or $nativeReport.pendingUnicode -ne $false -or $nativeReport.session.heldCount -ne 0) {
        throw "Native input test not passed; preserve $nativeInputDirectory"
    }
    Write-Host "Native input evidence: $nativeInputDirectory"
} finally { Pop-Location }
