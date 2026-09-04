[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'environment.ps1')
$inputEvidenceDirectory = Join-Path $RepoRoot ('artifacts/verification/input-core-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $inputEvidenceDirectory | Out-Null
Push-Location $RepoRoot
try {
    & $Dotnet restore tests/Workbench.Windows.Tests --locked-mode -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Locked test restore failed.' }
    & $Dotnet test tests/Workbench.Windows.Tests --no-restore -v minimal --filter 'FullyQualifiedName~Input' --logger 'trx;LogFileName=input-core.trx' --results-directory $inputEvidenceDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Input core tests failed; preserve TRX evidence.' }
    $inputSources = @(Get-ChildItem src/Workbench.Windows,tests/Workbench.Windows.Tests -File |
        Where-Object { $_.Extension -in @('.cs','.csproj') -or $_.Name -eq 'packages.lock.json' } |
        Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
    [ordered]@{time=(Get-Date).ToString('o');status='PASS';scope='L0 input validation, coordinates and release ledger with deterministic time and fake backend ONLY; no native injection or browser workflow';sources=$inputSources} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $inputEvidenceDirectory 'report.json') -Encoding utf8
    Write-Host "Input core evidence: $inputEvidenceDirectory"
} finally { Pop-Location }
