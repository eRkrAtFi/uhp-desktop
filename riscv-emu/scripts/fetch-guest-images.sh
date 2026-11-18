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

# boot/KernelImage and boot/initramfs.cpio.gz: intentionally NOT fetched
# yet. The copies this project used during Phase 5/6 development have no
# recorded, independently-verifiable source (see README's own history),
# so neither their origin nor their license terms can be stated with
# confidence - the opposite of what this script exists to guarantee for
# everything else. They are being replaced with a Buildroot-built kernel
# + busybox initramfs (config committed to this repo, built and
# legal-info'd in CI, published as release assets) instead of guessing at
# a replacement download URL here. Until that lands, boot-test/
# interactive-test/fs-compare (which need --kernel) are skipped in CI and
# will fail locally with a clear message if run without a kernel present
# - see this script's own exit code/message below for exactly that.
if [ ! -f "boot/KernelImage" ] || [ ! -f "boot/initramfs.cpio.gz" ]; then
    echo ""
    echo "fetch-guest-images: boot/KernelImage and boot/initramfs.cpio.gz are"
    echo "  not fetched by this script yet (no verified source - see this"
    echo "  script's own comment). boot-test, interactive-test and"
    echo "  fs-compare need them and will fail until you provide your own,"
    echo "  built via Buildroot or otherwise, at those two paths."
fi
