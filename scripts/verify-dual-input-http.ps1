[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'environment.ps1')
$ErrorActionPreference='Stop'
$directory=Join-Path $RepoRoot ('artifacts/verification/dual-input-http-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $directory | Out-Null
$checks=[Collections.Generic.List[object]]::new()
$clients=[Collections.Generic.List[Net.WebSockets.ClientWebSocket]]::new()
$timeout=[Threading.CancellationTokenSource]::new(10000)
function Connect-Local([string]$Path){
    $client=[Net.WebSockets.ClientWebSocket]::new();$clients.Add($client)
    $client.Options.Proxy=$null;$client.Options.CollectHttpResponseDetails=$true
    $client.Options.SetRequestHeader('Origin','http://127.0.0.1:8091')
    try{$null=$client.ConnectAsync([Uri]('ws://127.0.0.1:8091'+$Path),$timeout.Token).GetAwaiter().GetResult()}catch{}
    return $client
}
function Check([string]$Name,[bool]$Passed){
    $checks.Add([pscustomobject]@{name=$Name;status=$(if($Passed){'PASS'}else{'FAIL'})})
    if(-not $Passed){throw "Failed: $Name"}
}
function Read-Hello($Client){
    $buffer=[byte[]]::new(65536)
    $read=$Client.ReceiveAsync([ArraySegment[byte]]::new($buffer),$timeout.Token).GetAwaiter().GetResult()
    if(-not $read.EndOfMessage){throw 'Fragmented diagnostic hello'}
    return [Text.Encoding]::UTF8.GetString($buffer,0,$read.Count)|ConvertFrom-Json
}
try{
    $nx=Invoke-RestMethod 'http://127.0.0.1:8091/nx/health/live' -NoProxy
    $f0=Invoke-RestMethod 'http://127.0.0.1:8091/f0/health/live' -NoProxy
    Check 'two-fixed-distinct-streams' ($nx.dual -and $f0.dual -and $nx.streamId -eq 1 -and $f0.streamId -eq 2)
    $first=Connect-Local '/nx/control'
    Check 'nx-control-admitted' ([int]$first.HttpStatusCode -eq 101)
    $hello=Read-Hello $first
    Check 'nx-hello-stream-one' ($hello.type -eq 'inputHello' -and $hello.streamId -eq 1)
    $second=Connect-Local '/f0/control'
    Check 'f0-cannot-take-over-nx-control' ([int]$second.HttpStatusCode -eq 409)
    $first.Abort()
    # Wait only for the old executor's bounded server cleanup. No down/text/video or native input is sent.
    $replacement=$null
    for($attempt=0;$attempt -lt 10;$attempt++){
        Start-Sleep -Milliseconds 100
        $replacement=Connect-Local '/f0/control'
        if([int]$replacement.HttpStatusCode -eq 101){break}
    }
    Check 'f0-admitted-after-nx-cleanup' ([int]$replacement.HttpStatusCode -eq 101)
    $hello2=Read-Hello $replacement
    Check 'f0-hello-distinct-lease-and-stream' ($hello2.streamId -eq 2 -and $hello2.lease.id -ne $hello.lease.id -and $hello2.hostInstanceId -eq $hello.hostInstanceId)
    $third=Connect-Local '/nx/control'
    Check 'nx-cannot-take-over-f0-control' ([int]$third.HttpStatusCode -eq 409)
}finally{
    foreach($client in $clients){$client.Abort();$client.Dispose()};$timeout.Dispose()
    [ordered]@{time=[DateTimeOffset]::Now.ToString('o');scope='M1 dual shared control admission, no video or native down/text; not real input release or application isolation acceptance';checks=$checks.ToArray()}|
        ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $directory 'report.json') -Encoding utf8
    Write-Host "Dual control boundary evidence: $directory"
}
