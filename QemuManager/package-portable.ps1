<#
.SYNOPSIS
  Builds the fully self-contained, "unzip and go" portable distribution:
  publishes QemuManager.exe, then bundles a QEMU install and riscv-emu
  next to it so Settings never needs touching on a fresh machine (see
  AppSettings.DefaultQemuDir/DefaultRiscvEmuDir, which look for exactly
  these two folder names next to the .exe before falling back to
  C:\Program Files\qemu / unconfigured).

.PARAMETER QemuDir
  An existing QEMU install to bundle. Copied wholesale (robocopy /MIR) -
  every qemu-system-*.exe ships with shared data files (BIOS/EFI images,
  keymaps, DLLs) some of them depend on, so this does not try to guess
  which subset is "needed".

.PARAMETER RiscvEmuDir
  An existing riscv-emu checkout/build. Only riscv-emu.exe and
  boot\firmware.bin are copied - that is the entire runtime surface
  RiscvEmuLocator looks for; the rest of that project (source, build
  tooling, test logs) has no reason to be in a distributed copy of this
  app.

.PARAMETER Configuration
  Build configuration to publish. Default: Release.

.EXAMPLE
  .\package-portable.ps1
  .\package-portable.ps1 -QemuDir "D:\qemu" -RiscvEmuDir "D:\riscv-emu"
#>
param(
    [string]$QemuDir = "C:\Program Files\qemu",
    [string]$RiscvEmuDir = (Join-Path $PSScriptRoot "..\riscv-emu"),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$csproj = Join-Path $PSScriptRoot "QemuManager\QemuManager.csproj"
# -o puts the published output directly at the repo root's publish\ folder
# instead of dotnet's own default bin\Release\net8.0-windows\win-x64\publish\
# - much easier to find than four levels deep, and the one place this
# script, .gitignore (see /publish/) and README.md all agree on.
$publishDir = Join-Path $PSScriptRoot "publish"
$dataDir = Join-Path $publishDir "data"

# publish\data is the user's real portable VM configs/settings/disks (see
# AppPaths.ResolveAppDataDir) - `dotnet publish` has never been observed to
# touch anything outside its own known build-output files, but this
# actually checks that every time instead of continuing to trust it
# implicitly.
function Get-DataSnapshot {
    if (Test-Path $dataDir) {
        Get-ChildItem $dataDir -Recurse -File | Sort-Object FullName | ForEach-Object {
            "$($_.FullName)|$((Get-FileHash $_.FullName -Algorithm MD5).Hash)"
        }
    }
}
$dataBefore = Get-DataSnapshot

Write-Host "Publishing QemuManager ($Configuration) to '$publishDir'..." -ForegroundColor Cyan
dotnet publish $csproj -c $Configuration -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$dataAfter = Get-DataSnapshot
if (($dataBefore -join "`n") -ne ($dataAfter -join "`n")) {
    throw "'$dataDir' changed during 'dotnet publish' - this should never happen. Refusing to continue; check what changed before re-running."
}
Write-Host "Verified: '$dataDir' is unchanged by the publish step ($($dataBefore.Count) file(s) checksummed before and after)." -ForegroundColor Green

if (-not (Test-Path (Join-Path $publishDir "QemuManager.exe"))) {
    throw "Expected QemuManager.exe not found under $publishDir - publish layout may have changed."
}

# Marks this folder as a portable bundle (see AppPaths.ResolveAppDataDir) -
# without it, QemuManager.exe defaults to %APPDATA%\QemuManager instead of
# a "data" folder next to itself. A plain `dotnet publish` (no this script)
# does not write this, so a bare bin\...\publish\ output stays on
# %APPDATA% by default, same as a Debug build - only a bundle actually
# built for portable distribution opts in.
$portableFlag = Join-Path $publishDir "portable.flag"
if (-not (Test-Path $portableFlag)) {
    Set-Content -Path $portableFlag -Value "This file marks $publishDir as a portable QemuManager bundle - see AppPaths.ResolveAppDataDir. Do not delete it if you want this folder's own data\ subfolder to keep being used."
}

if (-not (Test-Path $QemuDir)) {
    Write-Warning "QEMU dir '$QemuDir' does not exist - skipping QEMU bundling. Pass -QemuDir to point at a real install."
}
else {
    $destQemu = Join-Path $publishDir "qemu"
    Write-Host "Bundling QEMU from '$QemuDir' into '$destQemu' (robocopy /MIR, this is the slow part - it's a real install, usually 1GB+)..." -ForegroundColor Cyan
    robocopy $QemuDir $destQemu /MIR /NFL /NDL /NJH /NJS /MT:8 | Out-Null
    # robocopy's own exit codes: 0-7 are all "success" variants (8+ means a
    # real failure); unlike every other tool used in this repo, treating
    # anything nonzero as failure here would be wrong. Reset it immediately
    # so it doesn't linger as this whole script's own exit code - a 1
    # ("files copied") would otherwise look like this script failed.
    $robocopyExitCode = $LASTEXITCODE
    if ($robocopyExitCode -ge 8) { throw "robocopy failed copying QEMU (exit code $robocopyExitCode)" }
    $global:LASTEXITCODE = 0
}

$riscvEmuExe = Join-Path $RiscvEmuDir "riscv-emu.exe"
$riscvFirmware = Join-Path $RiscvEmuDir "boot\firmware.bin"
if (-not (Test-Path $riscvEmuExe) -or -not (Test-Path $riscvFirmware)) {
    Write-Warning "riscv-emu.exe or boot\firmware.bin not found under '$RiscvEmuDir' - skipping riscv-emu bundling. Pass -RiscvEmuDir to point at a real build."
}
else {
    $destRiscv = Join-Path $publishDir "riscv-emu"
    Write-Host "Bundling riscv-emu from '$RiscvEmuDir' into '$destRiscv'..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path (Join-Path $destRiscv "boot") | Out-Null
    Copy-Item $riscvEmuExe (Join-Path $destRiscv "riscv-emu.exe") -Force
    Copy-Item $riscvFirmware (Join-Path $destRiscv "boot\firmware.bin") -Force
}

Write-Host "`nDone. Portable folder: $publishDir" -ForegroundColor Green
Write-Host "Copy that whole folder anywhere and run QemuManager.exe - no Settings configuration needed if QEMU/riscv-emu were bundled above."
Get-ChildItem $publishDir | Select-Object Name, @{N = "Size"; E = {
        if ($_.PSIsContainer) {
            "{0:N0} MB" -f ((Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
        }
        else {
            "{0:N1} MB" -f ($_.Length / 1MB)
        }
    } } | Format-Table -AutoSize

exit 0
