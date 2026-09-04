[CmdletBinding()]
param([string]$Directory)
$ErrorActionPreference = 'Stop'
$comparisonRepo = Split-Path -Parent $PSScriptRoot
$comparisonBase = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'TwinDeskComparison')).TrimEnd('\') + '\'
if (-not $Directory) { $Directory = (Get-Content -LiteralPath (Join-Path $comparisonRepo 'artifacts/comparison/latest.txt') -Raw).Trim() }
$Directory = (Resolve-Path -LiteralPath $Directory).Path
if (-not ($Directory + '\').StartsWith($comparisonBase,[StringComparison]::OrdinalIgnoreCase)) { throw 'Use the prepared LOCALAPPDATA/TwinDeskComparison directory.' }
if (Test-Path -LiteralPath (Join-Path $Directory 'processes.json')) { throw 'Stop the previous comparison first; process ownership file already exists.' }
$sunshineDirectory = Join-Path $Directory 'sunshine/Sunshine'
$moonlightDirectory = Join-Path $Directory 'moonlight-web/package'
$sunshineExe = Join-Path $sunshineDirectory 'sunshine.exe'
$moonlightExe = Join-Path $moonlightDirectory 'web-server.exe'
foreach ($binary in @($sunshineExe,$moonlightExe)) { if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing prepared binary: $binary" } }
$ports = @(8092,48984,48989,48990,48998,48999,49000,49002,49010)
if (@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object LocalPort -in $ports).Count -or
    @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object LocalPort -in $ports).Count) { throw 'Comparison ports are in use; existing processes were preserved.' }
$sunshineConfig = Join-Path $sunshineDirectory 'comparison.conf'
if (-not (Test-Path -LiteralPath $sunshineConfig)) {
    @'
sunshine_name = TwinDesk independent comparison
bind_address = 127.0.0.1
address_family = ipv4
port = 48989
origin_web_ui_allowed = pc
upnp = disabled
system_tray = disabled
controller = disabled
install_steam_audio_drivers = disabled
dd_configuration_option = disabled
global_prep_cmd = []
file_apps = comparison-apps.json
credentials_file = comparison-state.json
log_path = comparison.log
'@ | Set-Content -LiteralPath $sunshineConfig -Encoding ascii
    New-Item -ItemType Directory -Force -Path (Join-Path $sunshineDirectory 'config') | Out-Null
    '{"env":{},"apps":[]}' | Set-Content -LiteralPath (Join-Path $sunshineDirectory 'config/comparison-apps.json') -Encoding ascii
}
$configText = Get-Content -LiteralPath $sunshineConfig -Raw
foreach ($required in @('bind_address = 127.0.0.1','address_family = ipv4','port = 48989','upnp = disabled','controller = disabled','install_steam_audio_drivers = disabled','dd_configuration_option = disabled','global_prep_cmd = []')) {
    $key = ($required -split ' = ',2)[0]
    $definitions = @($configText -split '\r?\n' | Where-Object { $_ -match ('^\s*' + [regex]::Escape($key) + '\s*=') })
    if ($definitions.Count -ne 1 -or $definitions[0] -ne $required) { throw "Comparison boundary changed in config: $required" }
}
$moonlightConfig = Join-Path $moonlightDirectory 'server/config.json'
if (-not (Test-Path -LiteralPath $moonlightConfig)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $moonlightConfig) | Out-Null
    # Ask the pinned executable for its complete schema; v2.10 rejects partial nested objects.
    Push-Location $moonlightDirectory
    try {
        $generated = & $moonlightExe --config-path ./server/config.json --bind-address 127.0.0.1:8092 --disable-default-webrtc-ice-servers --webrtc-include-loopback-candidates true print-config
        if ($LASTEXITCODE -ne 0) { throw 'Moonlight default configuration generation failed.' }
        $defaults = ($generated -join "`n") | ConvertFrom-Json
        $defaults.moonlight.default_http_port = 48989
        $defaults.moonlight.pair_device_name = 'TwinDesk-local-comparison'
        $defaults.streamer_path = './streamer.exe'
        $defaults | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $moonlightConfig -Encoding ascii
    } finally { Pop-Location }
}
$webConfig = Get-Content -LiteralPath $moonlightConfig -Raw | ConvertFrom-Json
if ($webConfig.web_server.bind_address -ne '127.0.0.1:8092' -or @($webConfig.webrtc.ice_servers).Count -ne 0 -or $webConfig.webrtc.ice_server_script) { throw 'Moonlight comparison requires loopback with no STUN or ICE script.' }
$owned = @()
$launchLogs = Join-Path $Directory ('launch-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $launchLogs | Out-Null
try {
    $sunshineProcess = Start-Process -FilePath $sunshineExe -ArgumentList 'comparison.conf' -WorkingDirectory $sunshineDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $launchLogs 'sunshine-stdout.txt') -RedirectStandardError (Join-Path $launchLogs 'sunshine-stderr.txt')
    $owned += $sunshineProcess
    $moonlightProcess = Start-Process -FilePath $moonlightExe -ArgumentList '--bind-address 127.0.0.1:8092 --disable-default-webrtc-ice-servers' -WorkingDirectory $moonlightDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $launchLogs 'moonlight-stdout.txt') -RedirectStandardError (Join-Path $launchLogs 'moonlight-stderr.txt')
    $owned += $moonlightProcess
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 300
        foreach ($process in $owned) { $process.Refresh(); if ($process.HasExited) { throw "Comparison process exited: $($process.Id), inspect logs in $Directory" } }
        $tcp = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object OwningProcess -in $owned.Id)
        $udp = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object OwningProcess -in $owned.Id)
        if (@($tcp + $udp | Where-Object LocalAddress -ne '127.0.0.1').Count) { throw 'Non-loopback listener detected; stopping owned comparison processes.' }
        $ready = @($tcp | Where-Object LocalPort -eq 8092).Count -and @($tcp | Where-Object LocalPort -eq 48990).Count
    } while (-not $ready -and $deadline.Elapsed.TotalSeconds -lt 25)
    if (-not $ready) { throw 'Comparison startup deadline exceeded.' }
    @($owned | ForEach-Object { [ordered]@{id=$_.Id;path=$_.Path;startTime=$_.StartTime.ToUniversalTime().ToString('o')} }) |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'processes.json') -Encoding utf8
    [ordered]@{time=(Get-Date).ToString('o');status='LISTENERS_VERIFIED_NOT_STREAMED';logs=$launchLogs;scope='Independent server startup only; no pairing, desktop stream or NX input';tcp=@($tcp | Select-Object LocalAddress,LocalPort,OwningProcess);udp=@($udp | Select-Object LocalAddress,LocalPort,OwningProcess)} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'startup-report.json') -Encoding utf8
    Write-Host 'Comparison ready: Moonlight http://127.0.0.1:8092 ; Sunshine https://127.0.0.1:48990'
    Write-Host 'Application list is initially empty. Complete the isolated test instructions before pairing/streaming.'
} catch {
    foreach ($process in $owned) { $process.Refresh(); if (-not $process.HasExited) { Stop-Process -InputObject $process } }
    [ordered]@{time=(Get-Date).ToString('o');status='FAILED';error=$_.Exception.Message;logs=$launchLogs} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $launchLogs 'failure.json') -Encoding utf8
    throw
}
