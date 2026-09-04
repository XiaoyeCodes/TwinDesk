[CmdletBinding()]
param([switch]$Jpeg,[switch]$FullHd)
. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $RepoRoot
try {
    & $Dotnet restore tools/Workbench.MediaProbe --locked-mode -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & $Dotnet build tools/Workbench.MediaProbe --no-restore -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Media probe build failed.' }
    & $Dotnet build tools/Workbench.Probe --no-restore -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Local enumerator build failed.' }
    $probeDll = 'tools/Workbench.Probe/bin/Debug/net10.0-windows10.0.19041.0/Workbench.Probe.dll'
    $f0Candidates = @(
        foreach ($processName in @('dotnet','Workbench.DesktopFixture')) {
            $windowJson = & $Dotnet $probeDll --process $processName --list
            if ($LASTEXITCODE -ne 0) { throw 'Local window enumeration failed.' }
            @($windowJson -join "`n" | ConvertFrom-Json) | Where-Object title -eq 'TwinDesk F0 input fixture — NOT NX / TIA'
        }
    )
    if ($f0Candidates.Count -ne 1) { throw 'Start exactly one DesktopFixture --interactive in another terminal; this script never selects NX/TIA or guesses a window.' }
    $f0Target = $f0Candidates[0]
    Write-Host 'F0 ONLY: activate the local fixture before using the browser. Loopback http://127.0.0.1:8091; no LAN or NX/TIA input. Ctrl+C stops this diagnostic server.'
    $codecArguments=@();if($Jpeg){$codecArguments=@('--jpeg')}
    if($FullHd){$codecArguments+=@('--1080p')}
    & $Dotnet tools/Workbench.MediaProbe/bin/Debug/net10.0-windows10.0.19041.0/Workbench.MediaProbe.dll --process $f0Target.processName --window "$($f0Target.handle)" --owned --input-fixture @codecArguments
} finally { Pop-Location }
