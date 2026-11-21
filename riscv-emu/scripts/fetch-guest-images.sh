#!/bin/sh
# Downloads third-party guest images this project needs but never commits
# to git - either because they are GPL-licensed (the Linux kernel,
# busybox-based initramfs) or just large binary blobs (Alpine's own
# minirootfs). Each download is checked against a known-good sha256
# before being accepted, so a corrupted or tampered download fails loudly
# instead of silently landing where a test expects a real image.
set -e
cd "$(dirname "$0")/.."

verify() {
    # $1 = file, $2 = expected sha256
    got=$(sha256sum "$1" | cut -d' ' -f1)
    if [ "$got" != "$2" ]; then
        echo "fetch-guest-images: FAIL - $1 does not match the expected sha256" >&2
        echo "  expected: $2" >&2
        echo "  got:      $got" >&2
        rm -f "$1"
        exit 1
    fi
    echo "fetch-guest-images: OK - $1 ($2)"
}

fetch() {
    # $1 = URL, $2 = destination, $3 = expected sha256
    if [ -f "$2" ]; then
        got=$(sha256sum "$2" | cut -d' ' -f1)
        if [ "$got" = "$3" ]; then
            echo "fetch-guest-images: $2 already present and verified - skipping download"
            return
        fi
    fi
    mkdir -p "$(dirname "$2")"
    echo "fetch-guest-images: downloading $1"
    curl -fL -o "$2" "$1"
    verify "$2" "$3"
}

# Alpine's own official riscv64 minirootfs - see README's "Phase 10" for
# what it is used for (a real distro rootfs on a persistent virtio-blk
# disk, not another bespoke busybox test image). Version/URL/hash
# confirmed directly against Alpine's own published
# alpine-minirootfs-3.20.10-riscv64.tar.gz.sha256 at dl-cdn.alpinelinux.org.
ALPINE_VERSION="3.20.10"
ALPINE_URL="https://dl-cdn.alpinelinux.org/alpine/v3.20/releases/riscv64/alpine-minirootfs-${ALPINE_VERSION}-riscv64.tar.gz"
ALPINE_SHA256="972e8257ffded92877ff9a09a8418c3eb7c6f0e49c9413461777d7279fdc4b2f"
fetch "$ALPINE_URL" "tests/alpine-minirootfs.tar.gz" "$ALPINE_SHA256"

# boot/KernelImage and boot/initramfs.cpio.gz: a from-scratch,
# reproducible Buildroot build (config committed to this repo under
# riscv-emu/buildroot/, built and legal-info'd in CI - see
# .github/workflows/guest-images.yml) - replaces the kernel+initramfs
# this project used during Phase 5/6 development, which had no
# independently-verifiable recorded source at all (see THIRD_PARTY.md).
# Published as a GitHub Release asset rather than committed to git (same
# reasoning as Alpine's own tarball above, plus GPL source-availability
# for the kernel/busybox themselves - satisfied by
# guest-images-legal-info.tar.gz, attached to that same release, not
# fetched by this script since nothing here needs it at runtime).
#
# A plain, unauthenticated HTTPS download of the release asset's own
# browser_download_url - same as every other fetch in this script - is
# tried first. `gh release download` is only a fallback for that
# failing for some other reason (a proxy/firewall blocking github.com's
# own release-asset host, a renamed/deleted asset this script's own
# pinned URL hasn't caught up with, etc.) - `gh` already resolves
# credentials the same way everywhere: GH_TOKEN/GITHUB_TOKEN if set (CI
# sets this explicitly for this step - see ci.yml), otherwise whatever
# `gh auth login` already set up locally.
fetch_guest_images_release() {
    # $1 = release tag, $2 = asset name, $3 = destination, $4 = expected sha256
    if [ -f "$3" ]; then
        got=$(sha256sum "$3" | cut -d' ' -f1)
        if [ "$got" = "$4" ]; then
            echo "fetch-guest-images: $3 already present and verified - skipping download"
            return
        fi
    fi
    mkdir -p "$(dirname "$3")"
    url="https://github.com/eRkrAtFi/uhp-desktop/releases/download/$1/$2"
    echo "fetch-guest-images: downloading $2 from release $1"
    if curl -fL -o "$3" "$url"; then
        verify "$3" "$4"
        return
    fi
    echo "fetch-guest-images: plain download failed - falling back to 'gh release download'" >&2
    if ! command -v gh >/dev/null 2>&1; then
        echo "fetch-guest-images: FAIL - 'gh' (GitHub CLI) not found either. Install" >&2
        echo "  it from https://cli.github.com/ and run 'gh auth login', or set" >&2
        echo "  GH_TOKEN/GITHUB_TOKEN, then re-run this script." >&2
        exit 1
    fi
    gh release download "$1" --repo eRkrAtFi/uhp-desktop --pattern "$2" --output "$3" --clobber
    verify "$3" "$4"
}

GUEST_IMAGES_RELEASE="guest-images-v1"
GUEST_IMAGES_SHA256="0c18c2c01acaf48f621dcd7a06644efcdf49f1aa2eb326cd6aca99360af912d7"
fetch_guest_images_release "$GUEST_IMAGES_RELEASE" "guest-images.tar.gz" "boot/guest-images.tar.gz" "$GUEST_IMAGES_SHA256"
tar xzf "boot/guest-images.tar.gz" -C boot KernelImage initramfs.cpio.gz
echo "fetch-guest-images: extracted boot/KernelImage and boot/initramfs.cpio.gz from $GUEST_IMAGES_RELEASE"
