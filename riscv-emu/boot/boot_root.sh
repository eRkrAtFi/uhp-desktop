#!/bin/sh
# Phase 7 extension: boot from a real root filesystem on the virtio-blk
# disk (root=/dev/vda) instead of the fixed initramfs every phase before
# this used. Does NOT load an initrd at all - if one is present, the
# kernel always runs it as the true early userspace and ignores root=
# entirely, so getting an actual root-device boot means leaving it out,
# not just adding root= alongside it.
#
# boot/disk.img has to already contain a real root filesystem (the same
# content the initramfs has - busybox, /etc/inittab, /etc/init.d/rcS,
# etc. - copied onto it via mkfs.ext2 + mount + cp -a from a normal
# initramfs boot first; see README's Phase 7 section). This script does
# not create that content itself, only boots from whatever is already
# there, the same way real hardware's root= doesn't know or care how its
# filesystem got populated.
set -e
cd "$(dirname "$0")/.."

make firmware

KERNEL=boot/KernelImage
DTB=boot/virt.dtb
DISK=${RISCV_EMU_DISK:-boot/disk.img}

if [ ! -f "$DISK" ]; then
    echo "boot_root.sh: $DISK does not exist - run boot.sh once and" >&2
    echo "boot_root.sh: populate it with a real root filesystem first" >&2
    echo "boot_root.sh: (see README's Phase 7 section)" >&2
    exit 1
fi

RAM_BASE=0x80000000
TEXT_OFFSET=$(python3 -c "
import struct
with open('$KERNEL', 'rb') as f:
    f.seek(8); print(hex(struct.unpack('<Q', f.read(8))[0]))
")
KERNEL_ADDR=$((RAM_BASE + TEXT_OFFSET))
DTB_ADDR=0x83000000

# No real initrd this time - initrd-start/-end both 0, which the kernel
# treats as "no initrd present" (zero size), same as real hardware/real
# QEMU leaving -initrd off entirely.
python3 boot/make_dtb.py "$DTB" "$RAM_BASE" 0x10000000 0 0 \
    "console=ttyS0 earlycon=sbi root=/dev/vda rootfstype=ext2 rw init=/init loglevel=7"

exec ./riscv-emu.exe \
    --entry "$RAM_BASE" \
    --load "$RAM_BASE" boot/firmware.bin \
    --load "$(printf '0x%x' "$KERNEL_ADDR")" "$KERNEL" \
    --load "$DTB_ADDR" "$DTB" \
    --disk "$DISK" \
    --net \
    --reg a0 0 \
    --reg a1 "$DTB_ADDR" \
    --reg t3 "$(printf '0x%x' "$KERNEL_ADDR")" \
    "$@"
