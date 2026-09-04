[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'environment.ps1')
$inputHttpRun = Join-Path $RepoRoot ('artifacts/verification/input-http-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $inputHttpRun | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$clients = [Collections.Generic.List[Net.WebSockets.ClientWebSocket]]::new()
$timeout = [Threading.CancellationTokenSource]::new(10000)
function New-Client([string]$Origin) {
    $client = [Net.WebSockets.ClientWebSocket]::new()
    $client.Options.Proxy = $null
    $client.Options.CollectHttpResponseDetails = $true
    $client.Options.SetRequestHeader('Origin',$Origin)
    $clients.Add($client)
    return $client
}
function Check([string]$Name,[bool]$Passed) {
    $checks.Add([pscustomobject]@{name=$Name;status=$(if($Passed){'PASS'}else{'FAIL'})})
    if(-not $Passed){throw "Failed: $Name"}
}
function Connect([Net.WebSockets.ClientWebSocket]$Client,[string]$Path) {
    try {$null=$Client.ConnectAsync([Uri]('ws://127.0.0.1:8091'+$Path),$timeout.Token).GetAwaiter().GetResult()}catch{}
    return [int]$Client.HttpStatusCode
}
try {
    $health=Invoke-RestMethod -Uri 'http://127.0.0.1:8091/health/live' -NoProxy
    if(-not $health.inputEnabled -or $health.busy){throw 'Start F0 input probe first, with no active browser session.'}
    Check 'video-without-control-rejected' ((Connect (New-Client 'http://127.0.0.1:8091') '/ws?frames=30') -eq 409)
    Check 'control-wrong-origin-rejected' ((Connect (New-Client 'https://unexpected.example') '/control') -eq 403)
    Check 'control-retarget-query-rejected' ((Connect (New-Client 'http://127.0.0.1:8091') '/control?window=1') -eq 403)
    $owner=New-Client 'http://127.0.0.1:8091'
    Check 'one-local-control-handshake' ((Connect $owner '/control') -eq 101)
    Check 'second-control-cannot-take-over' ((Connect (New-Client 'http://127.0.0.1:8091') '/control') -eq 409)
    # No video is attached and no down/text event is ever sent by this suite.
    $buffer=[byte[]]::new(65536)
    $read=$owner.ReceiveAsync([ArraySegment[byte]]::new($buffer),$timeout.Token).GetAwaiter().GetResult()
    $hello=[Text.Encoding]::UTF8.GetString($buffer,0,$read.Count)|ConvertFrom-Json
    Check 'hello-is-finite-control-only' ($read.EndOfMessage -and $hello.type -eq 'inputHello')
    $invalid='{"type":"input","command":{"lease":{"id":"00000000-0000-0000-0000-000000000001","generation":1},"sequence":1,"stamp":{"host":"00000000-0000-0000-0000-000000000001","stream":1,"epoch":1,"scene":1},"displayedFrame":1,"kind":"ReleaseAll"}}'
    $bytes=[Text.Encoding]::UTF8.GetBytes($invalid)
    $null=$owner.SendAsync([ArraySegment[byte]]::new($bytes),[Net.WebSockets.WebSocketMessageType]::Text,$true,$timeout.Token).GetAwaiter().GetResult()
    do {
        $read=$owner.ReceiveAsync([ArraySegment[byte]]::new($buffer),$timeout.Token).GetAwaiter().GetResult()
        if(-not $read.EndOfMessage){throw 'Unexpected fragmented diagnostic response.'}
        $response=[Text.Encoding]::UTF8.GetString($buffer,0,$read.Count)|ConvertFrom-Json
    } while($response.type -eq 'inputState')
    Check 'wrong-lease-release-rejected' ($response.type -eq 'inputResult' -and -not $response.outcome.accepted -and $response.outcome.code -eq 'LEASE_STALE')
} finally {
    foreach($client in $clients){$client.Abort();$client.Dispose()};$timeout.Dispose()
    [ordered]@{at=(Get-Date).ToString('o');scope='Loopback F0 probe WS boundaries only; no video or real input submitted; not A02 production authentication';checks=$checks.ToArray()} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $inputHttpRun 'report.json') -Encoding utf8
    Write-Host "Input HTTP evidence: $inputHttpRun"
}
