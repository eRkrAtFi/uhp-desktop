#!/bin/sh
# Boot test: automates what this project's own debugging sessions did by
# hand for a long time - run boot.sh, watch for the real Linux shell
# prompt actually appearing, and give a real pass/fail exit code instead of
# a human reading scrollback. Does not type anything at the prompt once it
# appears - that is interactive_test.sh's job, kept separate because its
# own success is not yet reliable (see README's "Phase 5, finished"
# section) and should never make a plain boot regression look like a
# failure too.
set -e
cd "$(dirname "$0")/.."

# A fixed RTC reading, not real wall-clock time, by default for every
# comparison/regression script - not just this one - so byte-exact
# comparisons (the kind "Performance" in the README relies on to confirm
# an optimization changed nothing observable) stay available by default
# rather than something to remember only once an MD5 mismatch shows up
# with no obvious cause. Override by exporting RISCV_EMU_RTC_FIXED
# yourself before calling this (or unset it) for a run that actually
# wants real time, e.g. the Alpine/apk work in README's "Phase 10".
: "${RISCV_EMU_RTC_FIXED:=1700000000}"
export RISCV_EMU_RTC_FIXED

MAX_INSNS=${1:-600000000}
LOG=$(mktemp)
# Only ever removed on a PASS (see below) - a FAIL keeps the log around and
# tells the caller where it is, since the last 20 raw lines are often just
# "riscv-emu: NNN insns..." progress with no kernel text in them at all
# (the guest can go quiet for a long stretch before the instruction limit
# is hit), and a human chasing the failure needs the real kernel log, not
# that.
report_fail() {
    echo "boot_test: FAIL - $1"
    echo "boot_test: last real kernel output:"
    grep -v '^riscv-emu:' "$LOG" | tail -n 20
    echo "boot_test: full log kept at $LOG"
}

echo "boot_test: running boot.sh --max $MAX_INSNS (log: $LOG)"
# --deterministic-time, not real time: this script's own instruction-count
# cap and polling loop were tuned against the original instruction-tied
# mtime - a guest that's idle-waiting on something will reach a given
# instruction count in the same number of instructions either way, but a
# future script/human reusing MAX_INSNS as a rough "how far did it get"
# yardstick should not have that yardstick quietly mean something
# different depending on real-world timing jitter from one run to the next.
sh boot/boot.sh --deterministic-time --max "$MAX_INSNS" < /dev/null > "$LOG" 2>&1 &
PID=$!

# Poll the log rather than waiting on $PID directly: boot.sh's own `exec`
# replaces the shell, and this harness has previously (this session)
# mis-observed a background job as "done" well before the real riscv-emu
# process actually finished writing output - polling the file's own
# content for the markers we care about sidesteps that class of mistake
# entirely, rather than trusting any process-exit signal's timing.
i=0
while [ $i -lt 120 ]; do
    if grep -q "Welcome to you Linux RISC-V" "$LOG" 2>/dev/null; then
        echo "boot_test: PASS - reached the real shell"
        wait "$PID" 2>/dev/null || true
        rm -f "$LOG"
        exit 0
    fi
    if grep -q "Kernel panic" "$LOG" 2>/dev/null; then
        report_fail "kernel panic"
        kill "$PID" 2>/dev/null || true
        exit 1
    fi
    if grep -q "unimplemented instruction\|mtrap_unexpected" "$LOG" 2>/dev/null; then
        report_fail "emulator or firmware hit something it doesn't handle"
        kill "$PID" 2>/dev/null || true
        exit 1
    fi
    if ! kill -0 "$PID" 2>/dev/null; then
        # process really has exited (not just "notification says so") -
        # one last check in case the success line landed between polls
        if grep -q "Welcome to you Linux RISC-V" "$LOG" 2>/dev/null; then
            echo "boot_test: PASS - reached the real shell"
            rm -f "$LOG"
            exit 0
        fi
        report_fail "process exited without reaching a shell"
        exit 1
    fi
    i=$((i + 1))
    sleep 1
done

report_fail "timed out after ${i}s without reaching a shell"
kill "$PID" 2>/dev/null || true
exit 1
