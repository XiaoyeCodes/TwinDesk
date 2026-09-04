[CmdletBinding()]
param([switch]$Jpeg,[switch]$DelayEncoded)
. (Join-Path $PSScriptRoot 'environment.ps1')
$ErrorActionPreference='Stop'
Push-Location $RepoRoot
try {
    foreach($project in @('tools/Workbench.MediaProbe','tools/Workbench.Probe')){
        & $Dotnet restore $project --locked-mode -v quiet
        if($LASTEXITCODE -ne 0){throw 'Locked restore failed.'}
        & $Dotnet build $project --no-restore -v quiet
        if($LASTEXITCODE -ne 0){throw 'Build failed.'}
    }
    $candidates=@(foreach($processName in @('dotnet','Workbench.DesktopFixture')){
        $lines=& $Dotnet tools/Workbench.Probe/bin/Debug/net10.0-windows10.0.19041.0/Workbench.Probe.dll --process $processName --list
        if($LASTEXITCODE -ne 0){throw 'Window enumeration failed.'}
        ($lines -join "`n" | ConvertFrom-Json) | Where-Object title -eq 'TwinDesk SC03 media scene fixture — NOT NX / TIA'
    })
    if($candidates.Count -ne 1){throw 'Start exactly one DesktopFixture --media-scenes in another terminal; no NX/TIA target is selected.'}
    $target=$candidates[0]
    $codecArguments=@();if($Jpeg){$codecArguments=@('--jpeg')}
    if($DelayEncoded){if($Jpeg){throw 'Encoded output delay requires H264.'};$codecArguments+=@('--delay-scene-output')}
    Write-Host 'SC03 read-only native fixture only, 127.0.0.1:8091. Choose 10 seconds and enable the 3-second callback delay. No input endpoint.'
    & $Dotnet tools/Workbench.MediaProbe/bin/Debug/net10.0-windows10.0.19041.0/Workbench.MediaProbe.dll --process $target.processName --window "$($target.handle)" --owned @codecArguments
} finally {Pop-Location}
