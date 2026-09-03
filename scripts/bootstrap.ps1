[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'environment.ps1')
$downloadDirectory = Join-Path $ToolRoot 'downloads'
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null

function Get-VerifiedArchive {
    param([string]$Url, [string]$Path, [string]$Algorithm, [string]$ExpectedHash)
    if (Test-Path -LiteralPath $Path) {
        if ((Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -eq $ExpectedHash) { return }
        throw "Existing archive failed checksum. Inspect and remove this exact incomplete archive before retrying: $Path"
    }
    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $ExpectedHash) {
        throw "Downloaded archive failed checksum: $Path"
    }
}

if (-not (Test-Path -LiteralPath $Dotnet)) {
    $sdkZip = Join-Path $downloadDirectory ('dotnet-' + $DependencyLock.dotnet.version + '.zip')
    Get-VerifiedArchive $DependencyLock.dotnet.url $sdkZip 'SHA512' $DependencyLock.dotnet.sha512
    New-Item -ItemType Directory -Path $env:DOTNET_ROOT -Force | Out-Null
    & tar.exe -xf $sdkZip -C $env:DOTNET_ROOT
    if ($LASTEXITCODE -ne 0) { throw 'SDK archive extraction failed.' }
}

if (-not (Test-Path -LiteralPath $Node)) {
    $nodeName = 'node-v' + $DependencyLock.node.version + '-win-x64.zip'
    $checksumText = (Invoke-WebRequest -Uri $DependencyLock.node.checksumsUrl).Content
    $checksumLine = ($checksumText -split "`n") | Where-Object { $_.TrimEnd().EndsWith('  ' + $nodeName) } | Select-Object -First 1
    if (-not $checksumLine) { throw 'Node archive is not listed in official checksums.' }
    $nodeHash = ($checksumLine.Trim() -split '\s+')[0]
    $nodeZip = Join-Path $downloadDirectory $nodeName
    Get-VerifiedArchive $DependencyLock.node.url $nodeZip 'SHA256' $nodeHash
    $nodeRoot = Join-Path $ToolRoot 'node'
    New-Item -ItemType Directory -Path $nodeRoot -Force | Out-Null
    & tar.exe -xf $nodeZip -C $nodeRoot
    if ($LASTEXITCODE -ne 0) { throw 'Node archive extraction failed.' }
}

& $Dotnet --version
if ($LASTEXITCODE -ne 0) { throw '.NET SDK validation failed.' }
& $Node --version
if ($LASTEXITCODE -ne 0) { throw 'Node validation failed.' }
Write-Host 'Project-local toolchain ready. Global SDKs and PATH have not been changed.'
