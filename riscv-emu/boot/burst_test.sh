#!/bin/bash
# Confirms a single, large (4096-byte), completely unpaced write into
# the guest's own console input is delivered byte-for-byte, under BOTH
# riscv-emu and real qemu-system-riscv64 (the latter paced via
# type_slowly - see guest_io.sh - since it is already confirmed,
# separately, via fs_compare.sh's own CI failures, to need that for much
# smaller command lines too; this is not re-litigating that, it is using
# QEMU as the reference implementation to prove THIS test itself is
# sound).
#
# Written to settle a question raised while first chasing that QEMU bug:
# riscv-emu's own leg of fs_compare.sh never lost a byte across any CI
# run despite sending whole commands unpaced - suggestive, but never a
# dedicated test of exactly that, and not proof by itself (fs_compare.sh's
# own commands are at most ~130 bytes; a real 16550's own RX FIFO is
# comparable in that range, so a bug specific to LARGER bursts would
# never have shown up there). This is that dedicated test.
#
# Confounds deliberately ruled out before trusting a mismatch as a real
# riscv-emu/UART bug:
#  - The guest's own tty line discipline, not the UART, silently
#    capping/splitting a canonical-mode line: the payload is
#    `yes`-generated, 58-byte lines, nowhere near Linux's own
#    N_TTY_BUF_SIZE (4096-byte) canonical-mode ceiling, so there is no
#    oversized line for canonical processing to even have an opinion
#    about.
#  - IXON/IXOFF software flow control: confirmed directly (raw bytes
#    inspected in a failed run's own preserved transcript, see
#    burst_test_last_run/) that this guest's console tty has IXON/IXOFF
#    enabled by default, and genuinely asserts it - emitting real XOFF
#    (0x13) and XON (0x11) bytes back out through the same UART TX line
#    that also carries the shell's own prompt/echo - once its own input
#    queue actually experiences real backpressure. That queue only ever
#    saw real backpressure once riscv-emu's own StdinRing stopped
#    silently dropping bytes under load (see emu.h/main.c) - genuinely
#    fixing the drop bug is what exposed this, not a regression it
#    caused. Those control bytes landing inside the captured transcript
#    (not actually lost or corrupted on the guest's own filesystem - dd's
#    own read() and the line discipline handle real XON/XOFF correctly,
#    this project's own test-side transcript parsing does not) broke
#    this script's own DD_RC=0 line matching. `stty raw -echo -ixon
#    -ixoff` right before the payload - waited for via send_and_wait
#    (a real prompt back, not a guess, so the payload is never sent
#    before the guest has actually finished applying it) - removes both
#    the input-echo noise and this flow-control byte injection at the
#    source. (A first, less specific attempt at this - plain `stty raw
#    -echo`, with no synchronization wait at all - raced the guest
#    instead of waiting for it, and was reverted as a result; the
#    version here fixes that by waiting properly, not by avoiding stty.)
#  - The test methodology itself: run the identical payload and command
#    against real QEMU (paced, since that is already confirmed necessary
#    there) too - if THAT doesn't reproduce as a mismatch, the test is
#    measuring what it claims to.
#
# dd (not `cat > file` + end-of-input) specifically: dd with a fixed
# bs/count reads exactly that many bytes and returns on its own, with no
# need to signal EOF (e.g. a literal Ctrl-D byte) through this script's
# own scripted delivery - one less moving part than cat would need here.
set -u
cd "$(dirname "$0")/.."
# shellcheck source=guest-markers.sh
. "$(dirname "$0")/guest-markers.sh"
# shellcheck source=guest_io.sh
. "$(dirname "$0")/guest_io.sh"
export PATH="../tools/w64devkit/bin:/c/Program Files/LLVM/bin:/c/Program Files/qemu:$PATH"
export MSYS2_ARG_CONV_EXCL="*"

WORKDIR=$(mktemp -d)
RISCV_OUT="$WORKDIR/riscv-emu.out"
RISCV_ERR="$WORKDIR/riscv-emu.err"
QEMU_OUT="$WORKDIR/qemu.out"
RISCV_DISK="$WORKDIR/riscv-emu-disk.img"
QEMU_DISK="$WORKDIR/qemu-disk.img"

riscv_pid=""
qemu_pid=""
cleanup_guest_processes() {
    local wpid
    if [ -n "$riscv_pid" ] && kill -0 "$riscv_pid" 2>/dev/null; then
        wpid=$(win_pid "$riscv_pid")
        echo "burst_test: leftover riscv-emu process (MSYS pid $riscv_pid, Windows pid ${wpid:-unknown}) at exit - force-stopping" >&2
        [ -n "$wpid" ] && taskkill //PID "$wpid" //F >/dev/null 2>&1
    fi
    if [ -n "$qemu_pid" ] && kill -0 "$qemu_pid" 2>/dev/null; then
        wpid=$(win_pid "$qemu_pid")
        echo "burst_test: leftover qemu-system-riscv64 process (MSYS pid $qemu_pid, Windows pid ${wpid:-unknown}) at exit - force-stopping" >&2
        [ -n "$wpid" ] && taskkill //PID "$wpid" //F >/dev/null 2>&1
    fi
}
trap cleanup_guest_processes EXIT

/usr/bin/dd if=/dev/zero of="$RISCV_DISK" bs=1M count=16 status=none
/usr/bin/dd if=/dev/zero of="$QEMU_DISK" bs=1M count=16 status=none
RISCV_DISK_WIN=$(cygpath -w "$RISCV_DISK")
QEMU_DISK_WIN=$(cygpath -w "$QEMU_DISK")

# A fixed, reproducible 4096-byte payload, not random data, so a
# mismatch is always reproducible and diffable and no random source is
# needed. `yes` repeats the line forever; `head -c` cuts it off exactly
# at 4096 bytes (mid-line is fine - raw byte fidelity is what is being
# tested, not line structure). Read back with `read -r -d ''` (reads to
# EOF since there is no NUL byte in it), not `$(cat ...)` - command
# substitution strips trailing newlines, which would silently shrink a
# payload that happens to end in one, corrupting the very thing being
# compared.
BURST_FILE="$WORKDIR/burst.bin"
yes 'the quick brown fox jumps over the lazy dog 0123456789' 2>/dev/null | head -c 4096 > "$BURST_FILE"
HOST_MD5=$(md5sum "$BURST_FILE" | awk '{print $1}')
BURST_CONTENT=""
IFS= read -r -d '' BURST_CONTENT < "$BURST_FILE" || true

# No `stty raw -echo` here - tried it, and it introduced its own real
# synchronization bug (the payload's earliest bytes sometimes arrived
# before the guest had actually finished executing `stty raw -echo` and
# forking dd, so the SHELL - not dd - read them, still in canonical
# mode) that was itself confusing enough to obscure rather than confirm
# anything about either emulator's UART. Canonical mode's own line-
# length ceiling (N_TTY_BUF_SIZE, 4096 bytes on Linux) is ruled out a
# simpler way instead: BURST_CONTENT's own lines (from `yes`, see above)
# are 58 bytes each, nowhere near that ceiling, so canonical processing
# never has an oversized line to (mis)handle in the first place.
STTY_CMD='stty raw -echo -ixon -ixoff'
# iflag=fullblock: without it, `dd bs=N count=1` is satisfied by a
# single read() call even if it returns fewer than N bytes (completely
# ordinary for a tty/character device, which returns whatever is
# immediately available rather than blocking until a full block is
# ready) - confirmed directly (base64-dumped the guest's own /tmp/x and
# cmp'd it byte-for-byte against the host's original payload) that this,
# not any byte loss in the ring/UART, was the actual cause of an
# earlier, perfectly reproducible "off by exactly 1 byte, always the
# last one, everything before it identical" mismatch: dd's own single
# short read, not a dropped byte. fullblock makes dd keep calling read()
# until it actually has the full block (or hits real EOF), which is the
# semantics this test needs.
DD_CMD='dd of=/tmp/x bs=4096 count=1 iflag=fullblock 2>/dev/null; echo DD_RC=$?'

run_leg() {
    local label="$1" out_file="$2"
    local md5
    if ! tr -d '\r' < "$out_file" | grep -qx 'DD_RC=0'; then
        echo "burst_test: FAIL - $label's own dd did not report success (DD_RC=0)" >&2
        echo "burst_test: last output:" >&2
        tail -n 20 "$out_file" >&2
        return 1
    fi
    md5=$(tr -d '\r' < "$out_file" | grep -oE '^[0-9a-f]{32}  /tmp/x$' | awk '{print $1}' | tail -1)
    if [ -z "$md5" ]; then
        echo "burst_test: FAIL - could not find md5sum output for /tmp/x in $label's own transcript" >&2
        echo "burst_test: last output:" >&2
        tail -n 20 "$out_file" >&2
        return 1
    fi
    if [ "$md5" != "$HOST_MD5" ]; then
        echo "burst_test: FAIL - $label md5sum mismatch (host $HOST_MD5, guest $md5) - a single 4096-byte burst lost or corrupted bytes" >&2
        return 1
    fi
    echo "burst_test: PASS - $label absorbed a single 4096-byte burst with no byte loss (md5sum $HOST_MD5)"
}

echo "burst_test: booting riscv-emu..."
coproc RISCV { RISCV_EMU_DISK="$RISCV_DISK_WIN" sh boot/boot.sh --deterministic-time --max 2000000000 > "$RISCV_OUT" 2> "$RISCV_ERR"; }
# Deliberately riscv_pid, not RISCV_PID - see fs_compare.sh's own
# comment on why (coproc's own auto-created, auto-unset variable).
riscv_pid=$!

i=0
while [ "$(prompt_count "$RISCV_OUT")" -lt 1 ]; do
    if ! kill -0 "$riscv_pid" 2>/dev/null; then
        echo "burst_test: FAIL - riscv-emu exited before reaching a shell" >&2
        echo "burst_test: last output:" >&2
        tail -n 20 "$RISCV_OUT" >&2
        exit 1
    fi
    i=$((i + 1))
    if [ $i -ge 90 ]; then
        echo "burst_test: FAIL - timed out waiting for riscv-emu's shell prompt" >&2
        echo "burst_test: last output:" >&2
        tail -n 20 "$RISCV_OUT" >&2
        exit 1
    fi
    sleep 1
done

send_and_wait "$RISCV_OUT" "${RISCV[1]}" "$STTY_CMD" 20 "riscv-emu" || exit 1

# The whole point for this leg: the dd command line AND its entire
# 4096-byte payload go out as two plain writes (one per printf, no
# chunking, no pacing at all) - deliberately the opposite of what
# QEMU's own leg, further down, needs.
before=$(prompt_count "$RISCV_OUT")
{ printf '%s\n' "$DD_CMD"; printf '%s' "$BURST_CONTENT"; } >&"${RISCV[1]}"
i=0
while [ "$(prompt_count "$RISCV_OUT")" -lt "$((before + 1))" ]; do
    i=$((i + 1))
    if [ $i -ge 40 ]; then
        echo "burst_test: FAIL - riscv-emu did not respond to the dd command within 40s" >&2
        echo "burst_test: last output:" >&2
        tail -n 20 "$RISCV_OUT" >&2
        exit 1
    fi
    sleep 1
done

send_and_wait "$RISCV_OUT" "${RISCV[1]}" 'md5sum /tmp/x' 20 "riscv-emu" || exit 1
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
    echo "burst_test: FAIL - riscv-emu did not exit within 45s of poweroff" >&2
    wpid=$(win_pid "$riscv_pid")
    [ -n "$wpid" ] && taskkill //PID "$wpid" //F >/dev/null 2>&1
    exit 1
fi
riscv_pid=""

echo "burst_test: booting real qemu-system-riscv64..."
KERNEL=boot/KernelImage
INITRD=boot/initramfs.cpio.gz
qemu_producer() {
    local tries=0
    while [ "$(prompt_count "$QEMU_OUT")" -lt 1 ]; do
        tries=$((tries + 1))
        if [ "$tries" -ge 90 ]; then
            echo "burst_test: FAIL - QEMU did not reach a shell prompt within 90s" >&2
            tail -n 20 "$QEMU_OUT" >&2
            return 1
        fi
        sleep 1
    done
    # Paced on both the command line and the raw payload - see this
    # script's own top comment and guest_io.sh's own type_slowly/
    # send_bytes comments for why QEMU's own leg needs this at all.
    local before tries2
    send_and_wait "$QEMU_OUT" 1 "$STTY_CMD" 20 "QEMU" 1 || return 1
    before=$(prompt_count "$QEMU_OUT")
    type_slowly 1 "$DD_CMD"
    send_bytes 1 "$BURST_CONTENT" 4
    tries2=0
    while [ "$(prompt_count "$QEMU_OUT")" -lt "$((before + 1))" ]; do
        tries2=$((tries2 + 1))
        if [ "$tries2" -ge 40 ]; then
            echo "burst_test: FAIL - QEMU did not respond to the dd command within 40s" >&2
            tail -n 20 "$QEMU_OUT" >&2
            return 1
        fi
        sleep 1
    done
    send_and_wait "$QEMU_OUT" 1 'md5sum /tmp/x' 20 "QEMU" 1 || return 1
    type_slowly 1 'poweroff'
}
qemu_producer | qemu-system-riscv64 -M virt -smp 1 -m 256M -bios default \
    -kernel "$KERNEL" -initrd "$INITRD" \
    -append 'console=ttyS0 earlycon=sbi rdinit=/init loglevel=7' \
    -drive if=none,file="$QEMU_DISK_WIN",format=raw,id=d0 \
    -device virtio-blk-device,drive=d0 -global virtio-mmio.force-legacy=false \
    -nographic -no-reboot > "$QEMU_OUT" 2>&1 &
qemu_pid=$!

i=0
while [ $i -lt 150 ]; do
    if ! kill -0 "$qemu_pid" 2>/dev/null; then
        break
    fi
    i=$((i + 1))
    sleep 1
done
if [ $i -ge 150 ]; then
    echo "burst_test: FAIL - qemu-system-riscv64 did not exit within 150s of poweroff" >&2
    echo "burst_test: last output:" >&2
    tail -n 20 "$QEMU_OUT" >&2
    wpid=$(win_pid "$qemu_pid")
    [ -n "$wpid" ] && taskkill //PID "$wpid" //F >/dev/null 2>&1
    exit 1
fi
qemu_pid=""

# Copied to a fixed, known path (gitignored) unconditionally, same as
# fs_compare.sh's own fs_compare_last_run - $WORKDIR is a fresh
# mktemp -d every run, gone once this script (or its CI job) exits, so
# without this a failure's own raw transcripts would not survive long
# enough to diagnose past whatever a FAIL message's own tail happened
# to catch.
LAST_RUN_DIR="$(dirname "$0")/burst_test_last_run"
rm -rf "$LAST_RUN_DIR" 2>/dev/null
mkdir -p "$LAST_RUN_DIR"
cp "$RISCV_OUT" "$LAST_RUN_DIR/riscv-emu.out"
cp "$QEMU_OUT" "$LAST_RUN_DIR/qemu.out"

qemu_ok=1
riscv_ok=1
run_leg "riscv-emu" "$RISCV_OUT" || riscv_ok=0
run_leg "QEMU" "$QEMU_OUT" || qemu_ok=0

if [ "$qemu_ok" -eq 0 ]; then
    echo "burst_test: the QEMU leg itself failed this same test - see above before treating a riscv-emu result as conclusive (either this test is not measuring what it claims to, or a shared assumption about the payload/delivery is wrong)." >&2
fi
if [ "$riscv_ok" -eq 0 ] || [ "$qemu_ok" -eq 0 ]; then
    exit 1
fi
rm -rf "$WORKDIR" 2>/dev/null
exit 0
