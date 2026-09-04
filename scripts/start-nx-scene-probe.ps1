[CmdletBinding()]
param([switch]$PrepareOnly,[switch]$Jpeg,[switch]$FullHd)
. (Join-Path $PSScriptRoot 'environment.ps1')
$ErrorActionPreference='Stop'
Push-Location $RepoRoot
try {
    & $Dotnet restore tools/Workbench.MediaProbe --locked-mode -v quiet
    if($LASTEXITCODE -ne 0){throw 'Media restore failed.'}
    & $Dotnet restore tools/Workbench.Probe --locked-mode -v quiet
    if($LASTEXITCODE -ne 0){throw 'Enumerator restore failed.'}
    & $Dotnet build tools/Workbench.MediaProbe --no-restore -v minimal
    if($LASTEXITCODE -ne 0){throw 'Media build failed.'}
    & $Dotnet build tools/Workbench.Probe --no-restore -v minimal
    if($LASTEXITCODE -ne 0){throw 'Enumerator build failed.'}
    $probeDll=Join-Path $RepoRoot 'tools/Workbench.Probe/bin/Debug/net10.0-windows10.0.19041.0/Workbench.Probe.dll'
    $mediaDll=Join-Path $RepoRoot 'tools/Workbench.MediaProbe/bin/Debug/net10.0-windows10.0.19041.0/Workbench.MediaProbe.dll'
    $windowLines=& $Dotnet $probeDll --process ugraf --list
    if($LASTEXITCODE -ne 0){throw 'NX enumeration failed.'}
    # Native tooltip shadows can be ownerless too; they are not NX document roots.
    # Still reject multiple titled roots instead of selecting the first process window.
    $roots=@(($windowLines -join "`n" | ConvertFrom-Json) | Where-Object {
        $_.owner -eq 0 -and $_.visible -and -not $_.cloaked -and
        $_.className -ne 'SysShadow' -and $_.title -match '^NX\s'
    })
    if($roots.Count -ne 1){throw 'Exactly one current NX root is required; no window guessed or application started.'}
    $nxRoot=$roots[0]
    $nxRun=Join-Path $RepoRoot ('artifacts/verification/sc04-nx-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
    $modelDirectory=Join-Path $nxRun 'model'
    New-Item -ItemType Directory -Path $modelDirectory | Out-Null
    [ordered]@{
        recordedAt=[DateTimeOffset]::Now.ToString('o')
        status='PREPARED_NOT_UI_VERIFIED'
        scope='SC04 read-only media preparation; no input, NX edit, model save or browser acceptance performed by this script'
        target=$nxRoot
        temporaryModelDirectory=$modelDirectory
        mediaBinary=(Get-FileHash -LiteralPath $mediaDll -Algorithm SHA256).Hash
        windowsBinary=(Get-FileHash -LiteralPath (Join-Path (Split-Path $mediaDll) 'Workbench.Windows.dll') -Algorithm SHA256).Hash
        requiredObservations=@('N0 create/save/reopen locally','same-stream parameter dialog visible','parameter dialog disappears after cancel','real browser decoded scene changes')
        acceptance='NOT_RUN'
        codec=$(if($Jpeg){'jpeg'}else{'h264'})
        profile=$(if($FullHd){'1080p'}else{'720p'})
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $nxRun 'preparation.json') -Encoding utf8
    Write-Host "Preparation only, NOT a PASS: $nxRun"
    Write-Host "Use only a new temporary part in: $modelDirectory"
    if($PrepareOnly){return}
    Write-Host 'READ-ONLY NX media at http://127.0.0.1:8091. No input endpoint enabled. Follow docs/M1_NX_VALIDATION.md; Ctrl+C stops only this diagnostic process.'
    $codecArguments=@();if($Jpeg){$codecArguments=@('--jpeg')}
    if($FullHd){$codecArguments+=@('--1080p')}
    & $Dotnet $mediaDll --process ugraf --window "$($nxRoot.handle)" --owned @codecArguments
    if($LASTEXITCODE -ne 0){throw 'NX media server exited unsuccessfully.'}
} finally { Pop-Location }
