[CmdletBinding()]
param(
    [long]$LeftHandle,
    [long]$RightHandle,
    [switch]$PrepareOnly,
    [switch]$FullHd,
    [switch]$HighFrameRate
)

. (Join-Path $PSScriptRoot 'environment.ps1')
$ErrorActionPreference = 'Stop'
Push-Location $RepoRoot
try {
    $probeDll = Join-Path $RepoRoot 'tools/Workbench.Probe/bin/Release/net10.0-windows10.0.19041.0/Workbench.Probe.dll'
    $mediaDll = Join-Path $RepoRoot 'tools/Workbench.MediaProbe/bin/Release/net10.0-windows10.0.19041.0/Workbench.MediaProbe.dll'
    if (-not $PrepareOnly) {
        $listener = Get-NetTCPConnection -LocalPort 8093 -State Listen -ErrorAction SilentlyContinue
        if ($listener) { throw 'Port 8093 already has a listener. Stop the previous dual-NX probe before starting another.' }
        & $Dotnet restore tools/Workbench.Probe --locked-mode -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Locked Probe restore failed.' }
        & $Dotnet build tools/Workbench.Probe -c Release --no-restore -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
        & $Dotnet restore tools/Workbench.MediaProbe --locked-mode -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Locked MediaProbe restore failed.' }
        & $Dotnet build tools/Workbench.MediaProbe -c Release --no-restore -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'MediaProbe build failed.' }
    }
    if (-not (Test-Path -LiteralPath $probeDll) -or -not (Test-Path -LiteralPath $mediaDll)) {
        throw 'Release probe binaries are missing; run the launcher without -PrepareOnly to build them.'
    }
    $raw = & $Dotnet $probeDll --process ugraf --list
    if ($LASTEXITCODE -ne 0) { throw 'NX window enumeration failed.' }
    $windows = @($raw -join "`n" | ConvertFrom-Json | Where-Object {
        $_.owner -eq 0 -and $_.visible -and -not $_.cloaked -and -not $_.minimized -and
        $_.title.StartsWith('NX ') -and $_.bounds.width -gt 200 -and $_.bounds.height -gt 200
    })
    if ($windows.Count -ne 2 -or $windows[0].processId -eq $windows[1].processId -or
        $windows[0].sessionId -ne $windows[1].sessionId -or
        $windows[0].executablePath -ine $windows[1].executablePath) {
        throw 'Exactly two visible, non-minimized NX root windows from distinct processes in the same session are required.'
    }
    if (($LeftHandle -eq 0) -xor ($RightHandle -eq 0)) {
        throw 'Specify both -LeftHandle and -RightHandle, or neither.'
    }
    if ($LeftHandle -ne 0) {
        $left = @($windows | Where-Object handle -eq $LeftHandle)
        $right = @($windows | Where-Object handle -eq $RightHandle)
        if ($left.Count -ne 1 -or $right.Count -ne 1 -or $LeftHandle -eq $RightHandle) {
            throw 'Requested left/right HWNDs do not identify the two current NX roots.'
        }
        $left = $left[0]; $right = $right[0]
    } else {
        # Stable order for this launch; caller may override when a particular side matters.
        $ordered = @($windows | Sort-Object processStartedAtUtc, processId)
        $left = $ordered[0]; $right = $ordered[1]
    }

    $summary = [ordered]@{
        status = 'PREPARED_NOT_INPUT_VERIFIED'
        time = [DateTimeOffset]::Now.ToString('o')
        origin = 'http://127.0.0.1:8093/'
        left = $left
        right = $right
        profile = $(if ($FullHd) { '1080p' } else { '720p' })
        requestedFps = $(if ($HighFrameRate) { 60 } else { 30 })
    }
    $summary | ConvertTo-Json -Depth 8
    if ($PrepareOnly) { return }

    Write-Host 'Open the URL in a dedicated Edge/Chrome app window. F12 stops physical-input takeover.'
    Write-Host 'The two NX windows must remain non-minimized. No project is opened or saved by this script.'
    $mode = @()
    if ($FullHd) { $mode += '--1080p' }
    if ($HighFrameRate) { $mode += '--60fps' }
    & $Dotnet $mediaDll --process ugraf --window "$($left.handle)" --owned --dual-nx-window "$($right.handle)" --port 8093 @mode
    if ($LASTEXITCODE -ne 0) { throw 'Dual-NX MediaProbe exited unsuccessfully.' }
} finally {
    Pop-Location
}
