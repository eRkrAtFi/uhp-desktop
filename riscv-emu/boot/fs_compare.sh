#!/bin/bash
# Phase 7 differential test: mkfs.ext2 + mount + write + md5sum + umount on
# the virtio-blk disk, run under both riscv-emu and real
# qemu-system-riscv64 against fresh, independent disk images, and diffs
# the two transcripts.
#
# Delivery: both legs send their commands through a pipe into the guest
# binary's own stdin, not a static file - confirmed directly that a
# static file does not work for QEMU's own -nographic stdio backend (the
# guest reached its shell prompt with every scripted command silently
# missing, not even echoed, let alone run).
#  - QEMU: `( eval "$CMDS" ) | qemu-system-riscv64 ... &`, a fixed-delay
#    scripted producer - a coproc's write fd went bad ("Bad file
#    descriptor") once left open and idle for a few seconds before the
#    first write, something riscv-emu.exe never did, and a named fifo
#    has its own, separately-documented problem with riscv-emu.exe (see
#    README's "Phase 5, finished").
#  - riscv-emu: a coproc, polling its own output for GUEST_PROMPT before
#    sending anything. Needed because riscv-emu's own boot-to-prompt time
#    is not as predictable as real QEMU's - a fixed pre-command delay
#    tuned generously enough to survive a slow run still occasionally
#    undershoots a slower one (confirmed directly: one real run's own
#    riscv-emu leg reached its prompt only after this script's scripted
#    pipe had already finished writing and closed, so every command was
#    silently lost - nothing left to read once the guest caught up).
#
# Every $VARIABLE meant for the GUEST to evaluate (its own $? above all)
# must reach the guest completely literally - confirmed directly this
# was NOT happening for QEMU's own leg: CMDS's embedded
# `printf "...echo MOUNT_RC=$?\n"` lines are DOUBLE-quoted, and CMDS
# itself gets re-parsed and executed (by `eval`) before anything reaches
# the guest - at that point bash evaluates $? for real, substituting
# ITS OWN last exit status (always 0, from the sleep right before each
# printf), not the guest's. The guest received an already-baked-in "0"
# no matter what its own mount/umount command actually did - a real
# correctness bug, not just a cosmetic transcript mismatch (a diff
# between riscv-emu's and QEMU's own transcripts could still "pass" even
# if a command genuinely failed in the guest, as long as both sides
# happened to send the same wrong, pre-baked "0"). Fixed by escaping
# ($?  -> \$?) every such reference inside CMDS, so it survives eval's
# own re-parse as a literal two-character "$?" the guest's own shell
# evaluates itself - the same reason riscv-emu's own commands (sent via
# individual single-quoted `printf '...$?...'` calls, never re-parsed by
# anything) never had this problem in the first place. A deliberately
# failing command (`false; echo FALSE_RC=$?`) is included in both legs'
# own sequence specifically so this script's own correctness-check step
# further down can catch a regression of this exact bug automatically,
# not just a human noticing a suspicious "0".
#
# Process identification: `$!` right after backgrounding a pipeline is
# the PID of the pipeline's LAST member - confirmed directly, including
# for the exact shape used below (a subshell producer piped into the
# real guest binary as the last command, `( eval "$CMDS" ) |
# qemu-system-riscv64 ... &`) - so $! here really is
# qemu-system-riscv64.exe's own PID, not some wrapper's. An earlier
# version of this script wrapped that whole pipeline in an extra
# `bash -c "..." &` layer "for quoting convenience" - $! then pointed at
# that wrapper instead, which is exactly the kind of mistake that risks
# a cleanup step killing the wrong thing; the wrapper is gone now, not
# worked around.
#
# Either way, `$!`/riscv_pid/qemu_pid are MSYS pids, not necessarily the
# real Windows PID a native tool like taskkill needs (confirmed directly:
# a minimal reproduction outside this script showed these differing for
# an ordinary backgrounded process). bash's own kill -0/wait operate
# correctly on the MSYS number directly (used throughout both legs for
# liveness polling), but win_pid() resolves the real Windows PID via
# /proc/<msys-pid>/winpid before every taskkill call, for both legs.
#
# Neither leg's own cleanup ever uses `taskkill //IM <name>` - that
# reaches every process with that name on the whole machine, not just
# the one this script started, which would force-stop an unrelated VM a
# user has running in UHP Desktop (or another instance of this very
# script) at the same time. Every taskkill call below is PID-targeted
# (via win_pid, see above) at the one specific process this run itself
# is responsible for - confirmed directly: started a second, independent
# qemu-system-riscv64.exe before running this script and watched it
# survive this script's own cleanup untouched, every time.
#
# Ending differs from how this script used to end, on BOTH sides: each
# leg's own scripted commands now send a real "poweroff" as their last
# step, and this script waits for the actual guest process to exit on
# its own (with a bounded timeout as a safety net, not as the normal way
# this ends) instead of always force-killing after a blind sleep. Found
# the hard way: a background `sleep N; taskkill` pair, written the usual
# way at the bottom of a leg, did not reliably fire at all when this
# whole script itself was run detached/backgrounded by the calling tool -
# the guest process (confirmed via its own real start time) sat alive
# for 10+ minutes past where the 90s timer should have killed it, on
# every single attempt, regardless of what else in this script changed.
# Waiting for a real exit removes the dependency on that timer firing at
# all for a normal passing run; the timeout only has to work for an
# actually-stuck run, not every run.
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
# shellcheck source=guest-markers.sh
. "$(dirname "$0")/guest-markers.sh"
export PATH="../tools/w64devkit/bin:/c/Program Files/LLVM/bin:/c/Program Files/qemu:$PATH"
# MSYS's automatic POSIX<->Win32 argument conversion occasionally mangles
# an argument that merely looks path-like but isn't one - this script's
# own sed address range below (`/mkfs\.ext2 -F/,$p`, which starts with a
# slash) tripped it once ("sed: unsupported command C") even though it is
# a sed script, not a path. The standard fix is turning the heuristic off
# entirely for this script.
export MSYS2_ARG_CONV_EXCL="*"

# See this script's own top comment on process identification. Echoes
# nothing if the process is already gone or /proc/*/winpid is unavailable
# for some other reason - every call site treats an empty result as
# "nothing to kill", not an error.
win_pid() {
    cat "/proc/$1/winpid" 2>/dev/null
}

WORKDIR=$(mktemp -d)
RISCV_OUT="$WORKDIR/riscv-emu.out"
RISCV_ERR="$WORKDIR/riscv-emu.err"
QEMU_OUT="$WORKDIR/qemu.out"
RISCV_DISK="$WORKDIR/riscv-emu-disk.img"
QEMU_DISK="$WORKDIR/qemu-disk.img"

# Set (to empty) before the trap is even registered, not after either
# guest is started - cleanup_guest_processes can fire on an exit well
# before that point (e.g. the disk-creation check below), and under
# `set -u` referencing either variable there would itself be a crash if
# it had never been assigned at all. Both are MSYS pids (see this
# script's own top comment on process identification), handled
# identically here.
riscv_pid=""
qemu_pid=""

# No matter how this script exits - a clean PASS, any of the FAIL paths
# below, or an unexpected error under `set -u` - make sure neither
# native guest process THIS SCRIPT STARTED is still running afterward. A
# trap is the one place this is guaranteed to actually run, unlike a
# cleanup step placed only at the bottom that a mid-script `exit 1`
# would skip.
cleanup_guest_processes() {
    local wpid
    if [ -n "$riscv_pid" ] && kill -0 "$riscv_pid" 2>/dev/null; then
        wpid=$(win_pid "$riscv_pid")
        echo "fs_compare: leftover riscv-emu process (MSYS pid $riscv_pid, Windows pid ${wpid:-unknown}) at exit - force-stopping" >&2
        [ -n "$wpid" ] && taskkill //PID "$wpid" //F >/dev/null 2>&1
    fi
    if [ -n "$qemu_pid" ] && kill -0 "$qemu_pid" 2>/dev/null; then
        wpid=$(win_pid "$qemu_pid")
        echo "fs_compare: leftover qemu-system-riscv64 process (MSYS pid $qemu_pid, Windows pid ${wpid:-unknown}) at exit - force-stopping" >&2
        [ -n "$wpid" ] && taskkill //PID "$wpid" //F >/dev/null 2>&1
    fi
}
trap cleanup_guest_processes EXIT

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

echo "fs_compare: booting riscv-emu..."
# stdout (the guest's own console text, all of it - kernel printk and
# shell output alike share one UART/one byte stream, same as a real
# machine) and stderr (riscv-emu's own "N insns..." progress line) are
# kept apart, not merged with 2>&1 - merged, a progress line with no
# newline coordination of its own can land mid-line between two guest
# console bytes, corrupting the transcript the comparison below depends
# on (boot/interactive_test.sh hit this same bug once already - see
# README's "Phase 5, finished"). --max is this leg's own secondary
# safety net: an absolute instruction ceiling under riscv-emu's own
# control, independent of the wall-clock timeouts below.
coproc RISCV { RISCV_EMU_DISK="$RISCV_DISK_WIN" sh boot/boot.sh --deterministic-time --max 2000000000 > "$RISCV_OUT" 2> "$RISCV_ERR"; }
# Deliberately riscv_pid, not RISCV_PID - `coproc NAME { ...; }` itself
# auto-creates both a NAME array (fds) and a NAME_PID scalar (see bash's
# own "help coproc"/man page, Coprocesses section), and bash unsets BOTH
# once the coprocess exits and gets reaped. This script's first version
# stored $! into a variable it happened to also call RISCV_PID - not a
# prefix collision, that line WAS the exact same auto-created variable
# bash itself manages, so it went unbound right on schedule once riscv-
# emu exited, crashing the whole script under set -u. Capturing $! into
# a distinctly-named variable immediately, and never referencing
# RISCV/RISCV_PID directly again, avoids this entirely.
riscv_pid=$!

i=0
while [ $i -lt 90 ]; do
    if grep -qF "$GUEST_PROMPT" "$RISCV_OUT" 2>/dev/null; then
        break
    fi
    if ! kill -0 "$riscv_pid" 2>/dev/null; then
        echo "fs_compare: FAIL - riscv-emu exited before reaching a shell" >&2
        echo "fs_compare: last output:" >&2
        tail -n 20 "$RISCV_OUT" >&2
        exit 1
    fi
    i=$((i + 1))
    sleep 1
done
if [ $i -ge 90 ]; then
    echo "fs_compare: FAIL - timed out waiting for riscv-emu's shell prompt" >&2
    echo "fs_compare: last output:" >&2
    tail -n 20 "$RISCV_OUT" >&2
    exit 1
fi

# Same commands CMDS (below, QEMU's own leg) sends, just delivered one at
# a time through the coproc's own write fd instead of pre-scripted into a
# pipe - see this script's own top comment for why the two legs differ.
# Every printf here is single-quoted, so $? is never evaluated by this
# script itself - it reaches the guest completely literally, for the
# guest's own shell to evaluate (see this script's own top comment on
# why that matters).
printf 'mkfs.ext2 -F /dev/vda 2>&1\n' >&"${RISCV[1]}"
sleep 3
printf 'mkdir -p /mnt/disk\n' >&"${RISCV[1]}"
sleep 1
printf 'mount -t ext2 /dev/vda /mnt/disk 2>&1; echo MOUNT_RC=$?\n' >&"${RISCV[1]}"
sleep 3
printf 'false; echo FALSE_RC=$?\n' >&"${RISCV[1]}"
sleep 1
printf 'echo "hello from ext2 on virtio-blk" > /mnt/disk/testfile.txt; sync; md5sum /mnt/disk/testfile.txt; cat /mnt/disk/testfile.txt\n' >&"${RISCV[1]}"
sleep 3
printf 'umount /mnt/disk; echo UMOUNT_RC=$?\n' >&"${RISCV[1]}"
sleep 3

# Point 1 of the fix: a real "poweroff", waited for, not a blind
# sleep-then-kill. riscv-emu already exits on its own in response to
# this (the same path VmConfig.ShutdownCommand/StopGracefullyAsync rely
# on for the app's own graceful-shutdown feature, and what this exact
# verification was also meant to confirm for the new image). boot.sh's
# own last step is `exec ./riscv-emu.exe ...`, which replaces the
# process image in place rather than forking - so riscv_pid (captured
# back when boot.sh had only just started) IS riscv-emu.exe's real PID
# by this point, and polling it with `kill -0` genuinely tracks the
# native process, not just the shell that launched it.
printf 'poweroff\n' >&"${RISCV[1]}"
i=0
while [ $i -lt 45 ]; do
    if ! kill -0 "$riscv_pid" 2>/dev/null; then
        break
    fi
    i=$((i + 1))
    sleep 1
done
if [ $i -ge 45 ]; then
    echo "fs_compare: FAIL - riscv-emu did not exit within 45s of poweroff" >&2
    echo "fs_compare: last output:" >&2
    tail -n 20 "$RISCV_OUT" >&2
    wpid=$(win_pid "$riscv_pid")
    [ -n "$wpid" ] && taskkill //PID "$wpid" //F >/dev/null 2>&1
    exit 1
fi
echo "fs_compare: riscv-emu exited cleanly after poweroff"

echo "fs_compare: booting real qemu-system-riscv64..."
KERNEL=boot/KernelImage
INITRD=boot/initramfs.cpio.gz
# Same command sequence, same delays, sent identically every run - see
# this script's own top comment for why QEMU's own leg still uses a
# fixed-delay scripted pipe rather than the coproc/poll approach above.
# The initial delay (was 8s, tuned for the OLD undocumented-provenance
# image) had to grow a lot for the Buildroot-built image now replacing
# it: that image boot-time network init blocks the console for roughly
# 45s attempting DHCP on eth0 before giving up (the attempt itself is
# harmless - a manual check after boot shows eth0 correctly configured
# either way, udhcpc succeeds in the background regardless of what the
# foreground wait_iface script reports - but the console is genuinely
# busy printing its own "...timeout!" output during that window).
#
# Every $? here is escaped (\$?) - see this script's own top comment for
# why an unescaped one gets evaluated by this script's own shell (via
# `eval`, below) instead of staying literal for the guest. No
# apostrophes in this comment block either way - CMDS below is single-
# quoted, and one did exactly this (broke the quoting, corrupted the
# rest of the script) the first time this was written.
CMDS='
sleep 60
printf "mkfs.ext2 -F /dev/vda 2>&1\n"
sleep 3
printf "mkdir -p /mnt/disk\n"
sleep 1
printf "mount -t ext2 /dev/vda /mnt/disk 2>&1; echo MOUNT_RC=\$?\n"
sleep 3
printf "false; echo FALSE_RC=\$?\n"
sleep 1
printf "echo \"hello from ext2 on virtio-blk\" > /mnt/disk/testfile.txt; sync; md5sum /mnt/disk/testfile.txt; cat /mnt/disk/testfile.txt\n"
sleep 3
printf "umount /mnt/disk; echo UMOUNT_RC=\$?\n"
sleep 3
printf "poweroff\n"
'

# Point 1 of the fix: -no-reboot makes qemu-system-riscv64 exit on its
# own once the guest genuinely powers off, instead of resetting. No
# `bash -c "..." &` wrapper around this pipeline (an earlier version of
# this script had one, "for quoting convenience") - see this script's
# own top comment on process identification for why that broke $!.
( eval "$CMDS" ) | qemu-system-riscv64 -M virt -smp 1 -m 256M -bios default \
    -kernel "$KERNEL" -initrd "$INITRD" \
    -append 'console=ttyS0 earlycon=sbi rdinit=/init loglevel=7' \
    -drive if=none,file="$QEMU_DISK_WIN",format=raw,id=d0 \
    -device virtio-blk-device,drive=d0 -global virtio-mmio.force-legacy=false \
    -nographic -no-reboot > "$QEMU_OUT" 2>&1 &
qemu_pid=$!

# Point 2 of the fix: a bounded wait, not a blind sleep - the normal way
# this ends (the vast majority of runs), with the timeout underneath it
# as a safety net for a run that genuinely does not end that way.
i=0
while [ $i -lt 150 ]; do
    if ! kill -0 "$qemu_pid" 2>/dev/null; then
        break
    fi
    i=$((i + 1))
    sleep 1
done
if [ $i -ge 150 ]; then
    echo "fs_compare: FAIL - qemu-system-riscv64 did not exit within 150s of poweroff" >&2
    echo "fs_compare: last output:" >&2
    tail -n 20 "$QEMU_OUT" >&2
    wpid=$(win_pid "$qemu_pid")
    [ -n "$wpid" ] && taskkill //PID "$wpid" //F >/dev/null 2>&1
    exit 1
fi
echo "fs_compare: qemu-system-riscv64 exited cleanly after poweroff"

# Copied to a fixed, known path (gitignored) right after both legs
# finish producing output, unconditionally - not just in a FAIL branch.
# $WORKDIR itself is a fresh mktemp -d every run, so without this a CI
# failure's own raw transcripts are gone the moment the job's runner is
# torn down, leaving only whatever this script's own FAIL messages
# happened to `tail -n 20` into the log - not enough to diagnose a
# timing-sensitive mismatch that only reproduces on a slower/different
# machine than whoever is reading the log used locally.
LAST_RUN_DIR="$(dirname "$0")/fs_compare_last_run"
rm -rf "$LAST_RUN_DIR" 2>/dev/null
mkdir -p "$LAST_RUN_DIR"
cp "$RISCV_OUT" "$LAST_RUN_DIR/riscv-emu.out"
cp "$QEMU_OUT" "$LAST_RUN_DIR/qemu.out"

# Confirms the $?-escaping fix (see this script's own top comment) is
# actually working, on both sides independently, before trusting
# anything else about this run - a transcript diff alone cannot catch
# "both sides agree, and are both wrong" (e.g. if $? were silently
# un-escaped again on one side, or both). A real command failure
# (`false`) must show up as FALSE_RC=1, not FALSE_RC=0 or missing
# entirely.
for label_file in "riscv-emu:$RISCV_OUT" "QEMU:$QEMU_OUT"; do
    label="${label_file%%:*}"
    file="${label_file#*:}"
    if ! grep -qx 'FALSE_RC=1' "$file"; then
        echo "fs_compare: FAIL - $label never reported FALSE_RC=1 for the deliberately" >&2
        echo "fs_compare: failing command - \$? is not reaching the guest literally." >&2
        echo "fs_compare: last output:" >&2
        tail -n 20 "$file" >&2
        exit 1
    fi
done
echo "fs_compare: both sides correctly reported FALSE_RC=1 for the deliberately failing command"

# Normalize before diffing:
#  - everything before the mkfs.ext2 command itself is dropped - the
#    kernel boot banner legitimately differs (real OpenSBI + this
#    kernel's own printk timing vs this project's own firmware.S, neither
#    pretending to be the other - see README's "Phase 5, finished" for why
#    this project never tries to match that), so only the interactive
#    session the test actually cares about is compared
#  - everything after the real (evaluated, line-start) UMOUNT_RC= output
#    is dropped too - busybox's own poweroff sequence that follows is not
#    what this test exists to verify, and riscv-emu's own transcript of
#    it is confirmed incomplete (its last buffered UART output appears to
#    be lost when the guest's poweroff MMIO write makes riscv-emu.exe
#    exit immediately, before that output is flushed - a real, if minor,
#    riscv-emu behavior worth fixing separately, not a reason to fail
#    this specific test). The anchor is anchored to the START of the
#    line specifically (^UMOUNT_RC=) so it matches only the evaluated
#    output, not the "echo UMOUNT_RC=$?" input line that precedes it and
#    also contains that same substring.
#  - bracketed kernel-timestamp lines within that session (both sides
#    print these, but at different real/virtual rates - never meant to
#    match)
#  - the line naming the random filesystem UUID (see this script's own
#    top comment on why that is expected to differ, not a bug)
normalize() {
    sed -n '/mkfs\.ext2 -F/,/^UMOUNT_RC=/p' "$1" \
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
    echo "fs_compare: umount exit codes, the deliberately-failing command's own"
    echo "fs_compare: exit code, and the written file's md5sum/content all"
    echo "fs_compare: identical)"
    rm -rf "$WORKDIR" 2>/dev/null
    exit 0
else
    echo "fs_compare: FAIL - see diff above. Raw logs kept at $WORKDIR"
    exit 1
fi
