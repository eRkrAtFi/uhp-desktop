# Downloads third-party guest images this project needs but never commits
# to git - see fetch-guest-images.sh's own top comment for the full
# reasoning (GPL licensing for the kernel/initramfs, plain size for
# Alpine's minirootfs, and why the kernel/initramfs are not fetched here
# yet at all). This is the same script, for a plain PowerShell/Windows
# run with no sh/curl required.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

function Verify-Sha256([string]$Path, [string]$Expected) {
    $got = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLower()
    if ($got -ne $Expected.ToLower()) {
        Write-Error "fetch-guest-images: FAIL - $Path does not match the expected sha256`n  expected: $Expected`n  got:      $got"
        Remove-Item -Path $Path -Force -ErrorAction SilentlyContinue
        exit 1
    }
    Write-Host "fetch-guest-images: OK - $Path ($Expected)"
}

function Fetch([string]$Url, [string]$Destination, [string]$ExpectedSha256) {
    if (Test-Path $Destination) {
        $got = (Get-FileHash -Path $Destination -Algorithm SHA256).Hash.ToLower()
        if ($got -eq $ExpectedSha256.ToLower()) {
            Write-Host "fetch-guest-images: $Destination already present and verified - skipping download"
            return
        }
    }
    $dir = Split-Path -Parent $Destination
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Write-Host "fetch-guest-images: downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Destination
    Verify-Sha256 $Destination $ExpectedSha256
}

# Alpine's own official riscv64 minirootfs - version/URL/hash confirmed
# directly against Alpine's own published
# alpine-minirootfs-3.20.10-riscv64.tar.gz.sha256 at dl-cdn.alpinelinux.org.
$AlpineVersion = "3.20.10"
$AlpineUrl = "https://dl-cdn.alpinelinux.org/alpine/v3.20/releases/riscv64/alpine-minirootfs-$AlpineVersion-riscv64.tar.gz"
$AlpineSha256 = "972e8257ffded92877ff9a09a8418c3eb7c6f0e49c9413461777d7279fdc4b2f"
Fetch $AlpineUrl "tests/alpine-minirootfs.tar.gz" $AlpineSha256

if (-not (Test-Path "boot/KernelImage") -or -not (Test-Path "boot/initramfs.cpio.gz")) {
    Write-Host ""
    Write-Host "fetch-guest-images: boot/KernelImage and boot/initramfs.cpio.gz are"
    Write-Host "  not fetched by this script yet (no verified source - see"
    Write-Host "  fetch-guest-images.sh's own comment). boot-test, interactive-test"
    Write-Host "  and fs-compare need them and will fail until you provide your own,"
    Write-Host "  built via Buildroot or otherwise, at those two paths."
}
