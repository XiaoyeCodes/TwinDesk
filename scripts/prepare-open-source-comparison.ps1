$ErrorActionPreference = 'Stop'
$comparisonRepo = Split-Path -Parent $PSScriptRoot
$comparisonLockPath = Join-Path $comparisonRepo 'config/open-source-comparison.lock.json'
$comparisonLock = Get-Content -LiteralPath $comparisonLockPath -Raw | ConvertFrom-Json
$comparisonRoot = Join-Path $comparisonRepo 'artifacts/comparison'
$comparisonPointer = Join-Path $comparisonRoot 'latest.txt'
if (Test-Path -LiteralPath $comparisonPointer) {
    $previousRun = (Get-Content -LiteralPath $comparisonPointer -Raw).Trim()
    if (Test-Path -LiteralPath (Join-Path $previousRun 'processes.json')) { throw 'Stop the recorded comparison before preparing another environment.' }
}
$comparisonRuntimeRoot = Join-Path $env:LOCALAPPDATA 'TwinDeskComparison'
if ($comparisonRuntimeRoot -match '[^\x00-\x7F]') { throw 'Pinned Sunshine certificate initialization requires an ASCII runtime path; prepare a reviewed ASCII location before this comparison.' }
$comparisonCache = Join-Path $comparisonRepo '.tools/comparison-downloads'
New-Item -ItemType Directory -Force -Path $comparisonRoot,$comparisonCache,$comparisonRuntimeRoot | Out-Null
# Keep the vendor runtime in an ASCII user-local path; the repository may contain Chinese characters.
# On this pinned Sunshine release, its certificate initialization failed inside the Chinese project path.
$comparisonRun = Join-Path $comparisonRuntimeRoot (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff')
New-Item -ItemType Directory -Path $comparisonRun | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$comparisonPackages = @()
foreach ($package in $comparisonLock.packages) {
    $archive = Join-Path $comparisonCache ($package.name + '-' + $package.version + '.zip')
    if (-not (Test-Path -LiteralPath $archive)) {
        $partial = $archive + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
        Write-Host ('Downloading pinned ' + $package.name + ' ' + $package.version)
        Invoke-WebRequest -Uri $package.url -OutFile $partial -UseBasicParsing
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $package.sha256) { throw "Download hash mismatch: $partial" }
        Move-Item -LiteralPath $partial -Destination $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $package.sha256) { throw "Cached archive hash mismatch: $archive" }
    $destination = Join-Path $comparisonRun $package.name
    New-Item -ItemType Directory -Path $destination | Out-Null
    # Validate archive paths before extracting into a new isolated directory.
    $prefix = [IO.Path]::GetFullPath($destination).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $resolved = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
            if (-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escapes destination.' }
        }
    } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $archive -DestinationPath $destination
    Invoke-WebRequest -Uri $package.licenseUrl -OutFile (Join-Path $destination 'UPSTREAM-LICENSE.txt') -UseBasicParsing
    $comparisonPackages += [ordered]@{name=$package.name;version=$package.version;commit=$package.commit;archiveSha256=$package.sha256;directory=$destination;licenseSha256=(Get-FileHash -LiteralPath (Join-Path $destination 'UPSTREAM-LICENSE.txt')).Hash;executables=@(Get-ChildItem -LiteralPath $destination -Recurse -File -Filter *.exe | Get-FileHash | Select-Object Path,Hash)}
}
[ordered]@{time=(Get-Date).ToString('o');status='PREPARED_NOT_STARTED';scope=$comparisonLock.scope;lockSha256=(Get-FileHash -LiteralPath $comparisonLockPath).Hash;packages=$comparisonPackages} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $comparisonRun 'manifest.json') -Encoding utf8
$comparisonRun | Set-Content -LiteralPath (Join-Path $comparisonRoot 'latest.txt') -Encoding utf8
Write-Host "Prepared only; no services, drivers, firewall rules or streams started: $comparisonRun"
