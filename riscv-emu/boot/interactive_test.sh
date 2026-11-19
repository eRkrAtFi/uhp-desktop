#!/bin/bash
# Interactive test: types a command at the real shell prompt and checks
# that it actually got echoed and executed. Waits for the prompt to
# genuinely appear (polling the log) before writing anything, rather than
# guessing a fixed delay - a blind `sleep N` before sending input was
# tried first and is what produced some of the inconsistent results once
# attributed (incorrectly - see README's "Phase 5, finished" section) to a
# stdin ring buffer race; polling for the real prompt removes that
# specific source of unreliability from the test itself.
#
# Delivery mechanism: a bash coproc, not a named fifo. A fifo opened
# directly against riscv-emu.exe (a native Win32/MinGW binary, not an
# MSYS-runtime one) was tried first and is unreliable on this host - MSYS's
# named-pipe-to-native-stdin bridging would silently swallow writes (see
# README). A coproc's pipe is a plain OS anonymous pipe exactly like a
# shell `|`, which is the delivery method actually proven reliable here.
set -e
cd "$(dirname "$0")/.."
# shellcheck source=guest-markers.sh
. "$(dirname "$0")/guest-markers.sh"

# See boot_test.sh's own comment: a fixed RTC reading by default, so a
# byte-exact comparison stays available without having to remember this
# first.
: "${RISCV_EMU_RTC_FIXED:=1700000000}"
export RISCV_EMU_RTC_FIXED

LOG=$(mktemp)
ERRLOG=$(mktemp)

# stdout (the guest's own console text) and stderr (riscv-emu's own
# "N insns..." progress line, printed on a timer with no newline
# coordination with the guest's output) are kept in separate files, not
# merged with 2>&1 - merged, a progress line can land mid-line between two
# characters the guest wrote with no intervening newline, smashing e.g.
# "INTERACTIVE_TEST_OKriscv-emu: 240800000 insns..." onto one line and
# breaking the exact-line match below. $LOG is deliberately the only one
# this script's own checks ever read.
cleanup() { rm -f "$ERRLOG"; }
trap cleanup EXIT

coproc BOOT { sh boot/boot.sh --deterministic-time --max 1500000000 > "$LOG" 2> "$ERRLOG"; }
PID=$!

echo "interactive_test: waiting for the shell prompt (log: $LOG)"
i=0
while [ $i -lt 60 ]; do
    if grep -qF "$GUEST_PROMPT" "$LOG" 2>/dev/null; then
        break
    fi
    if ! kill -0 "$PID" 2>/dev/null; then
        echo "interactive_test: FAIL - boot exited before reaching a shell"
        echo "interactive_test: last real kernel output:"
        grep -v '^riscv-emu:' "$LOG" | tail -n 20
        echo "interactive_test: full log kept at $LOG"
        exit 1
    fi
    i=$((i + 1))
    sleep 1
done
if [ $i -ge 60 ]; then
    echo "interactive_test: FAIL - timed out waiting for the shell prompt"
    kill "$PID" 2>/dev/null || true
    echo "interactive_test: full log kept at $LOG"
    exit 1
fi

echo "interactive_test: shell is ready, sending a command"
printf 'echo INTERACTIVE_TEST_OK\n' >&"${BOOT[1]}"

i=0
while [ $i -lt 20 ]; do
    if grep -q 'INTERACTIVE_TEST_OK' "$LOG" 2>/dev/null; then
        # the echoed command line itself also contains the marker string,
        # so require it to appear on its OWN line (the command's actual
        # output), not just as a substring of the line that typed it
        if grep -qx 'INTERACTIVE_TEST_OK' "$LOG" 2>/dev/null; then
            echo "interactive_test: PASS - command was echoed and executed"
            kill "$PID" 2>/dev/null || true
            rm -f "$LOG"
            exit 0
        fi
    fi
    i=$((i + 1))
    sleep 1
done

echo "interactive_test: FAIL - command sent but never echoed back"
echo "interactive_test: last real kernel output:"
grep -v '^riscv-emu:' "$LOG" | tail -n 20
echo "interactive_test: full log kept at $LOG"
kill "$PID" 2>/dev/null || true
exit 1
