#!/bin/bash
# Phase 7 differential test: mkfs.ext2 + mount + write + md5sum + umount on
# the virtio-blk disk, run under both riscv-emu and real
# qemu-system-riscv64 against fresh, independent disk images, and diffs
# the two transcripts.
#
# Delivery is a single direct pipe with built-in delays
# (`(sleep; printf; ...) | guest-binary`), not a coproc or a named fifo -
# both were tried for the QEMU side specifically and misbehaved: a bash
# coproc's write fd went bad ("Bad file descriptor") once left open and
# idle for a few seconds before the first write, something riscv-emu.exe
# never did; a named fifo has its own, separately-documented problem with
# riscv-emu.exe (see README's "Phase 5, finished"). A plain pipe scripted
# up front is the one method proven reliable against both binaries.
#
# What is NOT compared byte-for-byte: the raw disk images, or the kernel's
# own "mounted/unmounting filesystem <uuid>" dmesg lines. mkfs.ext2 draws
# its filesystem UUID from the kernel's entropy pool, which is real
# randomness under real QEMU and this project's own fixed rng-seed (see
# make_dtb.py) under riscv-emu - guaranteed to differ, by design, same as
# every other reference to "rng-seed" in this project. What IS compared:
# mkfs.ext2's own report (purely a function of the (identical) disk size,
# no randomness in it at all), and - the actual correctness proof - the
# md5sum of a file written through the real VFS/ext2/virtio-blk stack and
# read back, which must be identical if the whole pipeline behaves the
# same on both sides.
set -u
cd "$(dirname "$0")/.."
export PATH="../tools/w64devkit/bin:/c/Program Files/LLVM/bin:/c/Program Files/qemu:$PATH"
# MSYS's automatic POSIX<->Win32 argument conversion occasionally mangles
# an argument that merely looks path-like but isn't one - this script's
# own sed address range below (`/mkfs\.ext2 -F/,$p`, which starts with a
# slash) tripped it once ("sed: unsupported command C") even though it is
# a sed script, not a path. The standard fix is turning the heuristic off
# entirely for this script.
export MSYS2_ARG_CONV_EXCL="*"

WORKDIR=$(mktemp -d)
RISCV_OUT="$WORKDIR/riscv-emu.out"
RISCV_ERR="$WORKDIR/riscv-emu.err"
QEMU_OUT="$WORKDIR/qemu.out"
RISCV_DISK="$WORKDIR/riscv-emu-disk.img"
QEMU_DISK="$WORKDIR/qemu-disk.img"

# dd, not python3: python3 and the two guest binaries below are native
# Windows builds with no idea what MSYS's /tmp means (a path under
# $(mktemp -d) resolves, from their side, against the current drive's own
# root instead and fails) - dd is an MSYS binary and resolves it the same
# way the rest of this script does. Called as /usr/bin/dd specifically,
# not bare `dd` - w64devkit (prepended onto PATH below, for the RISC-V
# toolchain and QEMU) ships its own dd.exe that shadows MSYS's and does
# not understand /dev/zero the same way (fails with "can't open
# '/Device/Null'"), so the bare name is not safe to rely on once that PATH
# line has run.
/usr/bin/dd if=/dev/zero of="$RISCV_DISK" bs=1M count=16 status=none
/usr/bin/dd if=/dev/zero of="$QEMU_DISK" bs=1M count=16 status=none
RISCV_DISK_WIN=$(cygpath -w "$RISCV_DISK")
QEMU_DISK_WIN=$(cygpath -w "$QEMU_DISK")
if [ ! -f "$RISCV_DISK" ] || [ ! -f "$QEMU_DISK" ]; then
    echo "fs_compare: FAIL - could not create one or both scratch disk images in $WORKDIR" >&2
    ls -la "$WORKDIR" >&2
    exit 1
fi

# Same command sequence, same delays, sent identically to both sides.
CMDS='
sleep 8
printf "mkfs.ext2 -F /dev/vda 2>&1\n"
sleep 3
printf "mkdir -p /mnt/disk; mount -t ext2 /dev/vda /mnt/disk 2>&1; echo MOUNT_RC=$?\n"
sleep 3
printf "echo \"hello from ext2 on virtio-blk\" > /mnt/disk/testfile.txt; sync; md5sum /mnt/disk/testfile.txt; cat /mnt/disk/testfile.txt\n"
sleep 3
printf "umount /mnt/disk; echo UMOUNT_RC=$?\n"
sleep 3
'

echo "fs_compare: booting riscv-emu..."
# stdout (the guest's own console text, all of it - kernel printk and
# shell output alike share one UART/one byte stream, same as a real
# machine) and stderr (riscv-emu's own "N insns..." progress line) are
# kept apart, not merged with 2>&1 - merged, a progress line with no
# newline coordination of its own can land mid-line between two guest
# console bytes, corrupting the transcript the comparison below depends
# on (boot/interactive_test.sh hit this same bug once already - see
# README's "Phase 5, finished").
RISCV_EMU_DISK="$RISCV_DISK_WIN" bash -c "($CMDS) | sh boot/boot.sh --deterministic-time --max 2000000000" > "$RISCV_OUT" 2> "$RISCV_ERR" &
# Neither side's scripted commands shut the guest down (the point is the
# filesystem round trip, not a clean poweroff), so both just keep running
# afterward - out to the full --max/forever otherwise. `kill` on the bash
# wrapper PID does not reach the actual riscv-emu.exe/qemu-system-riscv64.exe
# underneath on this host (a native binary reached via `exec`, not a direct
# child from bash's own point of view - the same class of issue this
# project's README documents for MSYS-vs-native process control elsewhere),
# so both are stopped by image name once the scripted sequence has clearly
# had time to finish instead.
sleep 35
taskkill //IM riscv-emu.exe //F >/dev/null 2>&1

echo "fs_compare: booting real qemu-system-riscv64..."
KERNEL=boot/KernelImage
INITRD=boot/initramfs.cpio.gz
bash -c "($CMDS) | qemu-system-riscv64 -M virt -smp 1 -m 256M -bios default \
    -kernel '$KERNEL' -initrd '$INITRD' \
    -append 'console=ttyS0 earlycon=sbi rdinit=/init loglevel=7' \
    -drive if=none,file='$QEMU_DISK_WIN',format=raw,id=d0 \
    -device virtio-blk-device,drive=d0 -global virtio-mmio.force-legacy=false \
    -nographic -no-reboot" > "$QEMU_OUT" 2>&1 &
sleep 35
taskkill //IM qemu-system-riscv64.exe //F >/dev/null 2>&1
sleep 1

# Normalize before diffing:
#  - everything before the mkfs.ext2 command itself is dropped - the
#    kernel boot banner legitimately differs (real OpenSBI + this
#    kernel's own printk timing vs this project's own firmware.S, neither
#    pretending to be the other - see README's "Phase 5, finished" for why
#    this project never tries to match that), so only the interactive
#    session the test actually cares about is compared
#  - bracketed kernel-timestamp lines within that session (both sides
#    print these, but at different real/virtual rates - never meant to
#    match)
#  - the line naming the random filesystem UUID (see this script's own
#    top comment on why that is expected to differ, not a bug)
normalize() {
    sed -n '/mkfs\.ext2 -F/,$p' "$1" \
        | grep -v '^\[' \
        | grep -v 'filesystem.*-.*-.*-.*-'
}
normalize "$RISCV_OUT" > "$WORKDIR/riscv-emu.norm"
normalize "$QEMU_OUT" > "$WORKDIR/qemu.norm"

# A diff of two empty files "passes" trivially - guard against that
# masking a real failure (e.g. the mkfs.ext2 command's own echo never
# appearing at all, so sed's address range never opens and everything
# after it is silently dropped).
if [ ! -s "$WORKDIR/riscv-emu.norm" ] || [ ! -s "$WORKDIR/qemu.norm" ]; then
    echo "fs_compare: FAIL - one or both normalized transcripts are empty (the"
    echo "fs_compare: mkfs.ext2 command itself was never found in the raw log -"
    echo "fs_compare: see $WORKDIR for the raw riscv-emu.out/qemu.out)"
    exit 1
fi

echo
echo "fs_compare: normalized transcripts (dropping kernel timestamps and the"
echo "fs_compare: random filesystem UUID line) -"
if diff -u "$WORKDIR/riscv-emu.norm" "$WORKDIR/qemu.norm"; then
    echo "fs_compare: PASS - riscv-emu matches QEMU (mkfs.ext2 report, mount/"
    echo "fs_compare: umount exit codes, and the written file's md5sum/content"
    echo "fs_compare: all identical)"
    # Best-effort: an orphaned qemu-system-riscv64.exe/riscv-emu.exe (the
    # taskkill above does not always reach it immediately - same class of
    # MSYS-vs-native-process issue noted above) can still be holding one
    # of the disk images open on Windows, which turns this into a harmless
    # "Permission denied" rather than an actual failure; the exit code
    # below is what the caller sees either way.
    rm -rf "$WORKDIR" 2>/dev/null
    exit 0
else
    echo "fs_compare: FAIL - see diff above. Raw logs kept at $WORKDIR"
    exit 1
fi
