[CmdletBinding()]
param([string]$Directory)
$ErrorActionPreference = 'Stop'
$comparisonRepo = Split-Path -Parent $PSScriptRoot
$comparisonBase = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'TwinDeskComparison')).TrimEnd('\') + '\'
if (-not $Directory) { $Directory = (Get-Content -LiteralPath (Join-Path $comparisonRepo 'artifacts/comparison/latest.txt') -Raw).Trim() }
$Directory = (Resolve-Path -LiteralPath $Directory).Path
if (-not ($Directory + '\').StartsWith($comparisonBase,[StringComparison]::OrdinalIgnoreCase)) { throw 'Use the prepared LOCALAPPDATA/TwinDeskComparison directory.' }
$processFile = Join-Path $Directory 'processes.json'
if (-not (Test-Path -LiteralPath $processFile)) { Write-Host 'No recorded comparison processes.'; return }
$records = @(Get-Content -LiteralPath $processFile -Raw | ConvertFrom-Json)
foreach ($record in $records) {
    $process = Get-Process -Id $record.id -ErrorAction SilentlyContinue
    if (-not $process) { continue }
    if ($process.Path -ne $record.path -or $process.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$record.startTime).UtcDateTime.Ticks -or
        -not $process.Path.StartsWith($Directory + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Process identity changed; preserved the process.' }
    # Stop only direct streamer children from this prepared distribution, if a trial was started.
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)")
    foreach ($child in $children) {
        if ($child.ExecutablePath -and $child.ExecutablePath.StartsWith($Directory + '\',[StringComparison]::OrdinalIgnoreCase) -and $child.Name -eq 'streamer.exe') {
            Stop-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
        }
    }
    Stop-Process -InputObject $process
}
Move-Item -LiteralPath $processFile -Destination (Join-Path $Directory ('stopped-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff') + '.json'))
Write-Host 'Stopped only recorded comparison processes. Configuration and evidence retained; TwinDesk and NX preserved.'
