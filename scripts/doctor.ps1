[CmdletBinding()]
param([string]$OutputPath)
. (Join-Path $PSScriptRoot 'environment.ps1')
if (-not $OutputPath) { $OutputPath = Join-Path $RepoRoot ('artifacts/verification/environment-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fffffff') + '.json') }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw "Report already exists; select a new evidence path: $OutputPath" }
$os = Get-CimInstance Win32_OperatingSystem
$computer = Get-CimInstance Win32_ComputerSystem
$release = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$installed = Get-ItemProperty -Path @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
) -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match '(?i)Siemens|Unigraphics|\bNX\b|TIA Portal' }
$displayEvidence = $null
$probeDll = Join-Path $RepoRoot 'tools/Workbench.Probe/bin/Debug/net10.0-windows10.0.19041.0/Workbench.Probe.dll'
if ((Test-Path -LiteralPath $Dotnet) -and (Test-Path -LiteralPath $probeDll)) {
    $displayOutput = & $Dotnet $probeDll --displays 2>&1
    if ($LASTEXITCODE -eq 0) { $displayEvidence = ($displayOutput -join "`n") | ConvertFrom-Json }
}
$shortcutEvidence = [System.Collections.Generic.List[object]]::new()
$menuRoots = @([Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('Programs')) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
$shortcutFiles = if (Get-Command rg -ErrorAction SilentlyContinue) {
    @(rg --files @menuRoots -g '*.lnk' 2>$null | Where-Object { $_ -match '(?i)Siemens|NX 10|TIA Portal' })
} else {
    @(Get-ChildItem -LiteralPath $menuRoots -Filter '*.lnk' -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object FullName -match '(?i)Siemens|NX 10|TIA Portal' | ForEach-Object FullName)
}
if ($shortcutFiles.Count -gt 0) {
    $shortcutShell = New-Object -ComObject WScript.Shell
    try {
        foreach ($shortcutPath in $shortcutFiles) {
            $shortcut = $shortcutShell.CreateShortcut($shortcutPath)
            try {
                $shortcutEvidence.Add([pscustomobject]@{ shortcut=$shortcutPath; target=$shortcut.TargetPath; workingDirectory=$shortcut.WorkingDirectory; hasArguments=([bool]$shortcut.Arguments) })
            }
            finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) }
        }
    }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcutShell) }
}
$actualDotnetVersion = if (Test-Path -LiteralPath $Dotnet) { (& $Dotnet --version) -join '' } else { $null }
$actualNodeVersion = if (Test-Path -LiteralPath $Node) { (& $Node --version) -join '' } else { $null }
$report = [ordered]@{
    schemaVersion = 2
    recordedAt = [DateTimeOffset]::Now.ToString('o')
    operatingSystem = @{ caption=$os.Caption; version=$os.Version; edition=$release.EditionID; displayVersion=$release.DisplayVersion; build=$release.CurrentBuild; revision=$release.UBR; architecture=$os.OSArchitecture }
    cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
    memoryGiB = [Math]::Round($computer.TotalPhysicalMemory / 1GB,2)
    video = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution)
    displayTopology = $displayEvidence
    applications = @($installed | Select-Object DisplayName,DisplayVersion,InstallLocation)
    installedLaunchShortcuts = $shortcutEvidence.ToArray()
    projectDotnetReady = (Test-Path -LiteralPath $Dotnet)
    projectNodeReady = (Test-Path -LiteralPath $Node)
    expectedDotnet = $DependencyLock.dotnet.version
    expectedNode = $DependencyLock.node.version
    actualDotnetVersion = $actualDotnetVersion
    actualNodeVersion = $actualNodeVersion
    interactiveUserSession = [Environment]::UserInteractive
    currentProcessSessionId = (Get-Process -Id $PID).SessionId
    observations = @('This report does not test application licensing, native capture, input or codec operation.', 'TIA component and OS compatibility require a separate check.', 'Shortcut metadata is read only, not launched. Argument values are omitted to avoid exporting sensitive configuration.', 'Missing displayTopology means the native probe has not been built or failed; not zero displays.')
}
$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Environment evidence: $OutputPath"
$report | ConvertTo-Json -Depth 8
