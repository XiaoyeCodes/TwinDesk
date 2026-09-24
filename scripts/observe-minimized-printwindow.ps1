[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][long]$WindowHandle,
    [Parameter(Mandatory = $true)][int]$Width,
    [Parameter(Mandatory = $true)][int]$Height,
    [Parameter(Mandatory = $true)][string]$Output,
    [ValidateSet(0,2)][int]$Flags = 0,
    [switch]$RequireMinimized
)

$ErrorActionPreference = 'Stop'
if ($Width -lt 100 -or $Height -lt 100 -or $Width -gt 8192 -or $Height -gt 8192) {
    throw 'Supply the independently observed, non-minimized window dimensions (100..8192).'
}

$nativeSource = @'
using System;
using System.Runtime.InteropServices;
public static class ReadOnlyWindowCapture {
    [DllImport("user32.dll", SetLastError = true)] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
Add-Type -TypeDefinition $nativeSource
Add-Type -AssemblyName System.Drawing

$hwnd = [IntPtr]::new($WindowHandle)
if (-not [ReadOnlyWindowCapture]::IsWindow($hwnd)) { throw 'HWND is no longer valid.' }
$processId = [uint32]0
$threadId = [ReadOnlyWindowCapture]::GetWindowThreadProcessId($hwnd, [ref]$processId)
if ($threadId -eq 0 -or $processId -eq 0) { throw 'HWND process identity unavailable.' }
$target = Get-Process -Id $processId -ErrorAction Stop
if ($target.ProcessName -ne 'ugraf') { throw 'This diagnostic is restricted to the selected NX process.' }
$minimized = [ReadOnlyWindowCapture]::IsIconic($hwnd)
if ($RequireMinimized -and -not $minimized) { throw 'Target is not minimized.' }
$outputPath = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $outputPath) { throw 'Output already exists.' }
$parent = Split-Path -Parent $outputPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null

$bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = [IntPtr]::Zero
try {
    $graphics.Clear([System.Drawing.Color]::Magenta)
    $hdc = $graphics.GetHdc()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $printed = [ReadOnlyWindowCapture]::PrintWindow($hwnd, $hdc, [uint32]$Flags)
    $watch.Stop()
} finally {
    if ($hdc -ne [IntPtr]::Zero) { $graphics.ReleaseHdc($hdc) }
    $graphics.Dispose()
}
try {
    $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $bitmap.Dispose()
}
$fileHash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash
[ordered]@{
    mode = 'READ_ONLY_PRINTWINDOW_SINGLE_FRAME'
    hwnd = $WindowHandle
    processId = $processId
    processStartedAtUtc = $target.StartTime.ToUniversalTime().ToString('o')
    minimizedAtStart = $minimized
    minimizedAtEnd = [ReadOnlyWindowCapture]::IsIconic($hwnd)
    flags = $Flags
    width = $Width
    height = $Height
    returnedSuccess = $printed
    elapsedMs = $watch.Elapsed.TotalMilliseconds
    imagePath = $outputPath
    imageSha256 = $fileHash
    limitation = 'A returned frame may be stale, blank or partial. This does not prove live graphics, native input, menus, or NX workflow.'
} | ConvertTo-Json -Depth 3
