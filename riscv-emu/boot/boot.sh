#!/bin/sh
# Boots the Phase 5+ Linux image. A thin wrapper now, not an orchestrator:
# riscv-emu.exe's own --kernel/--initrd handling (src/main.c's
# setup_kernel_boot, src/dtb.c) does what this script used to do by hand -
# compute the kernel's load address from its own Image header, place the
# initramfs and an internally-generated DTB past it, and set pc/a0/a1/t3 the
# way firmware.S expects. This script's only remaining job is the disk
# image's own lifecycle (Phase 7's persistent virtio-blk backing file) and
# making sure firmware.bin actually reflects the current firmware.S.
set -e
cd "$(dirname "$0")/.."

# firmware.bin/.elf are prebuilt artifacts with no automatic build rule
# tied to running riscv-emu itself - make sure they actually reflect the
# current firmware.S before using them. See README's "Phase 5, finished"
# section for the debugging session this silent-staleness trap once cost.
make firmware

DISK=${RISCV_EMU_DISK:-boot/disk.img}

# Phase 7: a persistent raw disk image for the virtio-blk device - created
# once, zero-filled, and then left alone across runs so whatever a boot
# session writes to it is still there next time, the same way a real disk
# would be. RISCV_EMU_DISK overrides the path - used by boot/fs_compare.sh
# so its own scratch disk never collides with this persistent one.
if [ ! -f "$DISK" ]; then
    python3 -c "open('$DISK', 'wb').write(bytes(16*1024*1024))"
fi

exec ./riscv-emu.exe \
    --kernel boot/KernelImage \
    --initrd boot/initramfs.cpio.gz \
    --disk "$DISK" \
    --net \
    "$@"
