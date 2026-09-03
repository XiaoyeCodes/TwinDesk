$ErrorActionPreference = 'Stop'
$httpProbeCases = @(
    @{name='loopback-health';path='/health/live';headers=@{};expected=200},
    @{name='wrong-host';path='/health/live';headers=@{Host='unexpected.example:8091'};expected=403},
    @{name='source-not-served';path='/server.py';headers=@{};expected=404},
    @{name='invalid-ws-request';path='/ws?frames=30';headers=@{Origin='https://unexpected.example'};expected=403}
)
$httpProbeResults = @(foreach ($httpProbeCase in $httpProbeCases) {
    $httpProbeResponse = Invoke-WebRequest -Uri ('http://127.0.0.1:8091'+$httpProbeCase.path) -Headers $httpProbeCase.headers -SkipHttpErrorCheck -NoProxy
    [pscustomobject]@{name=$httpProbeCase.name;expected=$httpProbeCase.expected;actual=[int]$httpProbeResponse.StatusCode;status=$(if($httpProbeResponse.StatusCode -eq $httpProbeCase.expected){'PASS'}else{'FAIL'})}
})
foreach ($wsProbeCase in @(
    @{name='websocket-real-handshake-wrong-origin';query='frames=30';origin='https://unexpected.example';expected=403},
    @{name='websocket-invalid-duration';query='frames=0';origin='http://127.0.0.1:8091';expected=400},
    @{name='websocket-no-browser-retarget';query='frames=30&window=527938';origin='http://127.0.0.1:8091';expected=400},
    @{name='websocket-duplicate-duration';query='frames=30&frames=60';origin='http://127.0.0.1:8091';expected=400}
)) {
    $wsProbeClient = [Net.WebSockets.ClientWebSocket]::new()
    $wsProbeTimeout = [Threading.CancellationTokenSource]::new(5000)
    try {
        $wsProbeClient.Options.Proxy = $null
        $wsProbeClient.Options.CollectHttpResponseDetails = $true
        $wsProbeClient.Options.SetRequestHeader('Origin', $wsProbeCase.origin)
        try { $wsProbeClient.ConnectAsync([Uri]('ws://127.0.0.1:8091/ws?' + $wsProbeCase.query),$wsProbeTimeout.Token).GetAwaiter().GetResult() } catch { }
        $wsProbeStatus = [int]$wsProbeClient.HttpStatusCode
        $httpProbeResults += [pscustomobject]@{name=$wsProbeCase.name;expected=$wsProbeCase.expected;actual=$wsProbeStatus;status=$(if($wsProbeStatus -eq $wsProbeCase.expected){'PASS'}else{'FAIL'})}
    } finally { $wsProbeClient.Dispose(); $wsProbeTimeout.Dispose() }
}
$httpProbeDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/verification/media-probe'
[IO.Directory]::CreateDirectory($httpProbeDirectory) | Out-Null
$httpProbePath = Join-Path $httpProbeDirectory ('http-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff') + '.json')
$httpProbeReport = [ordered]@{time=(Get-Date).ToString('o');scope='Local media probe HTTP restrictions only, not product authentication acceptance';checks=@($httpProbeResults)}
$httpProbeReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $httpProbePath -Encoding utf8
$httpProbeResults | Format-Table
Write-Output $httpProbePath
if (@($httpProbeResults | Where-Object status -eq 'FAIL').Count -ne 0) { exit 1 }
