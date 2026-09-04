. (Join-Path $PSScriptRoot 'environment.ps1')
$webSource=Join-Path $RepoRoot 'src/web'
$webOutput=Join-Path $RepoRoot ('artifacts/verification/web-dependencies-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $webOutput | Out-Null
foreach($name in @('package.json','package-lock.json','.npmrc')){Copy-Item -LiteralPath (Join-Path $webSource $name) -Destination (Join-Path $webOutput $name)}
$webChecks=[Collections.Generic.List[string]]::new()
$webFailure=$null
$actualNode=$null;$actualNpm=$null;$lockHash=$null;$assets=@()
Push-Location $webOutput
try {
    $actualNode=(& $Node --version).Trim()
    $actualNpm=(& $Npm --version).Trim()
    $package=Get-Content package.json -Raw | ConvertFrom-Json
    if($actualNode -ne ('v'+$package.engines.node) -or $actualNpm -ne $package.engines.npm){throw 'Actual project-local Node/npm differs from locked frontend toolchain.'}
    $lockHash=(Get-FileHash package-lock.json -Algorithm SHA256).Hash
    & $Npm ci --ignore-scripts --no-audit --cache (Join-Path $ToolRoot 'npm-cache')
    if($LASTEXITCODE -ne 0){throw 'npm ci failed.'}
    if((Get-FileHash package-lock.json -Algorithm SHA256).Hash -ne $lockHash){throw 'npm ci mutated the lockfile.'}
    $webChecks.Add('exact-node-npm-and-unchanged-lock-ci')
    @'
{"compilerOptions":{"target":"ES2022","lib":["ES2022","DOM","DOM.Iterable"],"module":"ESNext","moduleResolution":"Bundler","jsx":"react-jsx","strict":true,"noEmit":true,"types":["node"]},"files":["probe.tsx"]}
'@ | Set-Content tsconfig.json -Encoding utf8
    @'
import { createRoot } from 'react-dom/client';
function DependencyProbe({label}:{label:string}) { return <p>{label}</p>; }
const mount=document.getElementById('probe');
if(mount)createRoot(mount).render(<DependencyProbe label="Dependency build probe only; no remote control"/>);
'@ | Set-Content probe.tsx -Encoding utf8
    '<!doctype html><html><head><meta charset="utf-8"><title>Dependency probe only</title></head><body><div id="probe"></div><script type="module" src="/probe.tsx"></script></body></html>' | Set-Content index.html -Encoding utf8
    @'
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
export default defineConfig({plugins:[react()],base:'./',build:{outDir:'dist',emptyOutDir:false}});
'@ | Set-Content vite.config.mjs -Encoding utf8
    & $Node node_modules/typescript/bin/tsc --project tsconfig.json
    if($LASTEXITCODE -ne 0){throw 'Actual TypeScript typecheck failed.'}
    $webChecks.Add('typescript-tsx-dom-typecheck')
    @'
import assert from 'node:assert/strict';
import React from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
assert.equal(renderToStaticMarkup(React.createElement('span',null,'依赖验证')),'<span>依赖验证</span>');
'@ | Set-Content runtime.mjs -Encoding utf8
    & $Node runtime.mjs
    if($LASTEXITCODE -ne 0){throw 'React runtime compatibility failed.'}
    $webChecks.Add('react-dom-runtime-chinese-markup')
    & $Node node_modules/vite/bin/vite.js build
    if($LASTEXITCODE -ne 0 -or -not(Test-Path dist/index.html)){throw 'Vite production dependency build failed.'}
    $webChecks.Add('vite-react-plugin-production-build')
    & $Npm ls --all --json 1> dependency-tree.json
    if($LASTEXITCODE -ne 0){throw 'Dependency tree has missing or invalid packages.'}
    $webChecks.Add('resolved-dependency-tree-valid')
    $assets=@(Get-ChildItem dist -File -Recurse | ForEach-Object {[ordered]@{file=[IO.Path]::GetRelativePath($webOutput,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
} catch {$webFailure=$_.Exception.ToString();throw}
finally {
    [ordered]@{status=$(if($webFailure){'FAIL'}else{'PASS'});scope='M0 dependency installation/typecheck/runtime/build only; no product UI, browser, NX/TIA or release acceptance';at=[DateTimeOffset]::Now.ToString('o');node=$actualNode;npm=$actualNpm;lockSha256=$lockHash;checks=@($webChecks);assets=$assets;error=$webFailure} | ConvertTo-Json -Depth 6 | Set-Content report.json -Encoding utf8
    Pop-Location
    Write-Host "Web dependency evidence: $webOutput"
}
