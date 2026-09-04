[CmdletBinding()]
param([string]$VerifiedCopy=(Join-Path $PSScriptRoot '../artifacts/verification/nx-sample-20260904-102633-6240000/070-editable.prt'),[switch]$Jpeg,[switch]$FullHd)
$ErrorActionPreference='Stop'
Write-Host '同机双屏实验：NX 与浏览器分别放在两个屏幕；网页接管后 F12 退出。'
Write-Host '只启动服务，不自动接管实体键鼠、不打开或保存工程。'
& (Join-Path $PSScriptRoot 'start-nx-input-probe.ps1') -VerifiedCopy $VerifiedCopy -LocalConsole -Jpeg:$Jpeg -FullHd:$FullHd
