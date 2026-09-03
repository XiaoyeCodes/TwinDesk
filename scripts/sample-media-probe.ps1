param([Parameter(Mandatory)][int]$ProcessId, [int]$Seconds = 600)
$ErrorActionPreference = 'Stop'
if ($Seconds -lt 1 -or $Seconds -gt 600) { throw 'Duration must be 1..600 seconds.' }
$sampleProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId"
if ($sampleProcess.Name -ne 'dotnet.exe' -or $sampleProcess.CommandLine -notlike '*Workbench.MediaProbe.dll*') { throw 'Not the expected media probe process.' }
$sampleStarted = (Get-Process -Id $ProcessId).StartTime
$sampleRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/verification/media-probe'
[IO.Directory]::CreateDirectory($sampleRoot) | Out-Null
$samplePath = Join-Path $sampleRoot ('resources-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff') + '.jsonl')
$sampleFile = [IO.FileStream]::new($samplePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$sampleWriter = [IO.StreamWriter]::new($sampleFile)
$sampleClock = [Diagnostics.Stopwatch]::StartNew()
try {
    while ($sampleClock.Elapsed.TotalSeconds -lt $Seconds) {
        $sampleCurrent = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if (-not $sampleCurrent -or $sampleCurrent.StartTime -ne $sampleStarted) { break }
        $sampleWriter.WriteLine(([ordered]@{time=(Get-Date).ToString('o');elapsedSeconds=$sampleClock.Elapsed.TotalSeconds;processId=$ProcessId;cpuSeconds=$sampleCurrent.TotalProcessorTime.TotalSeconds;workingSetBytes=$sampleCurrent.WorkingSet64;privateBytes=$sampleCurrent.PrivateMemorySize64;handles=$sampleCurrent.HandleCount;threads=$sampleCurrent.Threads.Count} | ConvertTo-Json -Compress))
        $sampleWriter.Flush()
        Start-Sleep -Seconds 5
    }
} finally { $sampleWriter.Dispose() }
Write-Output $samplePath
