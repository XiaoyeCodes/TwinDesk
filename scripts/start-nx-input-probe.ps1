[CmdletBinding()]
param([Parameter(Mandatory)][string]$VerifiedCopy,[switch]$PrepareOnly,[switch]$Jpeg,[switch]$FullHd,[switch]$DualFixture,[switch]$LocalConsole)
. (Join-Path $PSScriptRoot 'environment.ps1')
$ErrorActionPreference='Stop'
Push-Location $RepoRoot
try {
    $copy=Get-Item -LiteralPath $VerifiedCopy
    $verification=[IO.Path]::GetFullPath((Join-Path $RepoRoot 'artifacts/verification'))+[IO.Path]::DirectorySeparatorChar
    if($copy.PSIsContainer -or -not $copy.FullName.StartsWith($verification,[StringComparison]::OrdinalIgnoreCase) -or
        $copy.Extension -ine '.prt' -or $copy.IsReadOnly -or $copy.Length -eq 0){throw 'Select a writable nonempty isolated .prt beneath artifacts/verification.'}
    $item=$copy
    while($null -ne $item){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse paths are not admitted.'}
        $item=if($item -is [IO.FileInfo]){$item.Directory}else{$item.Parent}
    }
    foreach($project in @('tools/Workbench.MediaProbe','tools/Workbench.Probe')){
        & $Dotnet restore $project --locked-mode -v quiet
        if($LASTEXITCODE -ne 0){throw 'Locked restore failed.'}
        & $Dotnet build $project --no-restore -v minimal
        if($LASTEXITCODE -ne 0){throw 'Build failed. Stop only the previous diagnostic server before rebuilding.'}
    }
    $probeDll=Join-Path $RepoRoot 'tools/Workbench.Probe/bin/Debug/net10.0-windows10.0.19041.0/Workbench.Probe.dll'
    $mediaDll=Join-Path $RepoRoot 'tools/Workbench.MediaProbe/bin/Debug/net10.0-windows10.0.19041.0/Workbench.MediaProbe.dll'
    $windows=& $Dotnet $probeDll --process ugraf --list
    if($LASTEXITCODE -ne 0){throw 'NX enumeration failed.'}
    $roots=@(($windows -join "`n" | ConvertFrom-Json) | Where-Object {
        $_.owner -eq 0 -and $_.visible -and -not $_.cloaked -and -not $_.minimized -and
        $_.title.StartsWith('NX ') -and ($_.title.EndsWith('['+$copy.Name+']') -or
            $_.title.EndsWith('['+$copy.Name+' （修改的） ]') -or $_.title.EndsWith('['+$copy.Name+'*]') -or $_.title.EndsWith('['+$copy.Name+']*'))
    })
    if($roots.Count -ne 1){throw 'Exactly one visible NX root showing the saved copy name is required. Verify the full opened path in native NX first; no target guessed.'}
    $target=$roots[0]
    $f0Target=$null
    if($DualFixture){
        $f0Candidates=@(foreach($name in @('dotnet','Workbench.DesktopFixture')){
            $f0Lines=& $Dotnet $probeDll --process $name --list
            if($LASTEXITCODE -ne 0){throw 'F0 enumeration failed.'}
            @($f0Lines -join "`n" | ConvertFrom-Json) | Where-Object title -eq 'TwinDesk F0 input fixture — NOT NX / TIA'
        })
        if($f0Candidates.Count -ne 1){throw 'Start exactly one DesktopFixture --interactive for the explicitly labelled F0 side.'}
        $f0Target=$f0Candidates[0]
    }
    $evidence=Join-Path $verification ('sc05-nx-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
    New-Item -ItemType Directory -Path $evidence | Out-Null
    [ordered]@{time=[DateTimeOffset]::Now.ToString('o');status='PREPARED_NOT_INPUT_VERIFIED';scope='Local NX copy admission; title is not proof of opened path; not workflow acceptance';target=$target;
        secondTarget=$f0Target;copy=$copy.FullName;copySha256=(Get-FileHash -LiteralPath $copy.FullName -Algorithm SHA256).Hash;
        binaries=@($mediaDll,(Join-Path (Split-Path $mediaDll) 'Workbench.Windows.dll')) | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'preparation.json') -Encoding utf8
    Write-Host "NX copy input preparation (NOT PASS): $evidence"
    Write-Host 'Prerequisite: native NX UI has confirmed this exact copy path. Keep NX foreground; no automatic activation. Scope guard is not file sandboxing.'
    if($PrepareOnly){return}
    $modeArguments=@();if($Jpeg){$modeArguments+='--jpeg'};if($FullHd){$modeArguments+='--1080p'}
    if($LocalConsole){$modeArguments+='--local-console'}
    if($DualFixture){$modeArguments+=@('--dual-fixture-process',$f0Target.processName,'--dual-fixture-window',"$($f0Target.handle)")}
    & $Dotnet $mediaDll --process ugraf --window "$($target.handle)" --owned --input-nx-copy $copy.FullName @modeArguments
    if($LASTEXITCODE -ne 0){throw 'NX input probe exited unsuccessfully.'}
} finally {Pop-Location}
