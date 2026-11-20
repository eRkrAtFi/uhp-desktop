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

# boot/KernelImage and boot/initramfs.cpio.gz - see fetch-guest-images.sh's
# own comment for the full reasoning (reproducible Buildroot build,
# published as a GitHub Release asset). Fetched via `gh release download`,
# not Invoke-WebRequest - this repo is private, so the asset's own
# browser_download_url 404s for anyone not authenticated (confirmed
# directly). `gh` resolves credentials from GH_TOKEN/GITHUB_TOKEN if set,
# otherwise from a local `gh auth login`.
function Fetch-GuestImagesRelease([string]$ReleaseTag, [string]$AssetName, [string]$Destination, [string]$ExpectedSha256) {
    if (Test-Path $Destination) {
        $got = (Get-FileHash -Path $Destination -Algorithm SHA256).Hash.ToLower()
        if ($got -eq $ExpectedSha256.ToLower()) {
            Write-Host "fetch-guest-images: $Destination already present and verified - skipping download"
            return
        }
    }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Error "fetch-guest-images: FAIL - 'gh' (GitHub CLI) not found. This repo is private, so downloading a release asset needs it - install from https://cli.github.com/ and run 'gh auth login', or set GH_TOKEN/GITHUB_TOKEN (CI already does this for this exact step)."
        exit 1
    }
    $dir = Split-Path -Parent $Destination
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Write-Host "fetch-guest-images: downloading $AssetName from release $ReleaseTag (private repo - via gh, authenticated)"
    gh release download $ReleaseTag --repo eRkrAtFi/uhp-desktop --pattern $AssetName --output $Destination --clobber
    Verify-Sha256 $Destination $ExpectedSha256
}

$GuestImagesRelease = "guest-images-v1"
$GuestImagesSha256 = "0c18c2c01acaf48f621dcd7a06644efcdf49f1aa2eb326cd6aca99360af912d7"
Fetch-GuestImagesRelease $GuestImagesRelease "guest-images.tar.gz" "boot/guest-images.tar.gz" $GuestImagesSha256
tar xzf "boot/guest-images.tar.gz" -C boot KernelImage initramfs.cpio.gz
Write-Host "fetch-guest-images: extracted boot/KernelImage and boot/initramfs.cpio.gz from $GuestImagesRelease"
