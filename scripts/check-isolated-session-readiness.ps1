param(
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

# Read-only inventory. In particular, this never calls WTSEnableChildSessions,
# changes Remote Desktop settings, starts a service, or launches NX/TIA.
if (-not ('TwinDeskChildSessionReadiness' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class TwinDeskChildSessionReadiness
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSIsChildSessionsEnabled(
        [MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();
}
'@
}

$enabled = $false
$querySucceeded = [TwinDeskChildSessionReadiness]::WTSIsChildSessionsEnabled([ref] $enabled)
$queryError = if ($querySucceeded) { $null } else { [Runtime.InteropServices.Marshal]::GetLastWin32Error() }
$version = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$edition = [string] $version.EditionID
$rdpHostSupportedEdition = $edition -match '^(Professional|ProfessionalN|ProfessionalEducation|ProfessionalWorkstation|Enterprise|EnterpriseS|Education|EducationN|Server)'
$rdpSetting = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -ErrorAction SilentlyContinue
$termService = Get-Service -Name TermService -ErrorAction SilentlyContinue

$appProcesses = @(Get-Process -Name ugraf,Siemens.Automation.Portal,Portal -ErrorAction SilentlyContinue |
    Select-Object @{ Name = 'name'; Expression = { $_.ProcessName } },
        @{ Name = 'pid'; Expression = { $_.Id } },
        @{ Name = 'sessionId'; Expression = { $_.SessionId } },
        @{ Name = 'hasMainWindow'; Expression = { $_.MainWindowHandle -ne [IntPtr]::Zero } })
$nx = @($appProcesses | Where-Object { $_.name -eq 'ugraf' })
$tia = @($appProcesses | Where-Object { $_.name -ne 'ugraf' })
$consoleSessionId = [TwinDeskChildSessionReadiness]::WTSGetActiveConsoleSessionId()
$twoNxInOneIsolatedSession = $nx.Count -ge 2 -and
    @($nx | Select-Object -ExpandProperty sessionId -Unique).Count -eq 1 -and
    $nx[0].sessionId -ne $consoleSessionId -and
    @($nx | Where-Object { $_.hasMainWindow }).Count -ge 2

$reasons = @()
if (-not $rdpHostSupportedEdition) { $reasons += 'WINDOWS_EDITION_NOT_SUPPORTED_AS_RDP_HOST' }
if (-not $querySucceeded) { $reasons += 'CHILD_SESSION_QUERY_FAILED' }
elseif (-not $enabled) { $reasons += 'CHILD_SESSIONS_NOT_ENABLED' }
if ($tia.Count -eq 0) { $reasons += 'NO_RUNNING_TIA_INSTANCE_OBSERVED' }
if (-not $twoNxInOneIsolatedSession) { $reasons += 'TWO_VISIBLE_NX_PROCESSES_IN_ONE_ISOLATED_SESSION_NOT_OBSERVED' }

$result = [ordered]@{
    schema = 'twindesk.isolated-session-readiness.v1'
    observedAtUtc = [DateTime]::UtcNow.ToString('o')
    scope = 'Read-only host and running-process inventory; not an NX/TIA streaming or input test'
    windows = [ordered]@{
        editionId = $edition
        build = [string] $version.CurrentBuild
        displayVersion = [string] $version.DisplayVersion
        supportedRdpHostEdition = [bool] $rdpHostSupportedEdition
        childSessionsQuerySucceeded = [bool] $querySucceeded
        childSessionsQueryWin32Error = $queryError
        childSessionsEnabled = if ($querySucceeded) { [bool] $enabled } else { $null }
        termServiceStatus = if ($termService) { [string] $termService.Status } else { 'NotFound' }
        rdpConnectionsDenied = if ($rdpSetting) { [bool] ($rdpSetting.fDenyTSConnections -ne 0) } else { $null }
        consoleSessionId = $consoleSessionId
    }
    runningApplications = [ordered]@{
        nx = $nx
        tiaCandidates = $tia
    }
    canStartSupportedChildSessionTrialNow = [bool] ($rdpHostSupportedEdition -and $querySucceeded -and $enabled)
    twoNxInOneIsolatedSessionObserved = [bool] $twoNxInOneIsolatedSession
    blockingReasons = $reasons
    note = 'This result does not determine NX/TIA license or virtual-GPU compatibility, and does not modify the system.'
}

$json = $result | ConvertTo-Json -Depth 8
if ($OutputPath) {
    $absolutePath = [IO.Path]::GetFullPath($OutputPath)
    $directory = [IO.Path]::GetDirectoryName($absolutePath)
    if (-not [IO.Directory]::Exists($directory)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    [IO.File]::WriteAllText($absolutePath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
$json
