<#
.SYNOPSIS
  Run this after every change to QemuManager/UHP.Core, no exceptions:
  build, the full SelfTest suite, publish-safely.sh, and finally the
  version actually baked into publish\QemuManager.exe - printed on its
  own last line so a summary can end with it instead of a claim that
  might not be true. Written after forgetting to publish twice in a row
  made that claim unverifiable from the chat alone.

.PARAMETER QemuDir
  Passed through to SelfTest. Default matches AppSettings' own fallback.

.PARAMETER RiscvEmuDir
  Passed through to SelfTest. Default: the riscv-emu checkout next to
  this one.
#>
param(
    [string]$QemuDir = "C:\Program Files\qemu",
    [string]$RiscvEmuDir = (Join-Path $PSScriptRoot "..\riscv-emu")
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "`n=== Build ===" -ForegroundColor Cyan
dotnet build QemuManager.SelfTest/QemuManager.SelfTest.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "SelfTest build failed" }
dotnet build QemuManager/QemuManager.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "QemuManager build failed" }

Write-Host "`n=== SelfTest ===" -ForegroundColor Cyan
dotnet QemuManager.SelfTest/bin/Release/net8.0/QemuManager.SelfTest.dll $QemuDir $RiscvEmuDir
if ($LASTEXITCODE -ne 0) { throw "SelfTest itself exited non-zero" }
# SelfTest has no single pass/fail exit code of its own today (it prints
# True/False per assertion rather than failing the process) - its full
# output above is what actually needs reading, this just confirms the
# process ran to completion rather than crashing partway through.

Write-Host "`n=== Publish ===" -ForegroundColor Cyan
# Explicit Git Bash, not whatever plain "bash" resolves to - on this
# machine that is WSL's own bash (a separate Linux environment with no
# access to Windows PATH entries like dotnet.exe), confirmed the hard
# way: publish-safely.sh failed with "dotnet: command not found" under
# plain `bash`, despite dotnet working fine everywhere else in this same
# script.
$gitBash = "C:\Program Files\Git\bin\bash.exe"
if (-not (Test-Path $gitBash)) { throw "Git Bash not found at '$gitBash' - publish-safely.sh needs it" }
& $gitBash publish-safely.sh
if ($LASTEXITCODE -ne 0) { throw "publish-safely.sh failed - publish/ may not reflect this change" }

Write-Host "`n=== Published version ===" -ForegroundColor Cyan
$publishedExe = Join-Path $PSScriptRoot "publish\QemuManager.exe"
if (-not (Test-Path $publishedExe)) { throw "publish\QemuManager.exe does not exist after publish-safely.sh" }
$version = (Get-Item $publishedExe).VersionInfo.ProductVersion
Write-Host "publish\QemuManager.exe version: $version" -ForegroundColor Green
