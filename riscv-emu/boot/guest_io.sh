# Shared scripted-console-I/O helpers for fs_compare.sh and
# burst_test.sh - sourced, not run directly. Needs GUEST_PROMPT already
# set (source guest-markers.sh first) and bash (coproc, local).

# See the sourcing script's own top comment on process identification.
# Echoes nothing if the process is already gone or /proc/*/winpid is
# unavailable for some other reason - every call site treats an empty
# result as "nothing to kill", not an error.
win_pid() {
    cat "/proc/$1/winpid" 2>/dev/null
}

# Counts how many times $GUEST_PROMPT has appeared in a transcript so
# far, \r-stripped first - confirmed directly (a real CI run's own raw
# output, inspected byte-for-byte) that this guest's own tty really
# does emit \r\n, not just \n, same as any ordinary interactive Linux
# tty with its default ONLCR output translation. Some grep builds
# appear to tolerate this silently and some might not - not something
# to depend on either way, since it was never deliberately verified to
# hold rather than merely observed once.
prompt_count() {
    tr -d '\r' < "$1" 2>/dev/null | grep -cF "$GUEST_PROMPT"
}

# QEMU-leg only (see send_and_wait's own $6) - NOT used for riscv-emu,
# deliberately (see burst_test.sh). Writes $2 (plus a trailing newline)
# to fd $1 a few characters at a time, with a short real pause between
# chunks, instead of one single write() of the whole line. Needed even
# after send_and_wait already waits for a real prompt before sending
# anything (see its own comment) - confirmed directly (a real CI run,
# after that fix was already in place) that a single command's OWN
# characters can still be lost mid-line (`echo MOUNT_RC=$?` arrived as
# `echo MOUNT_R=`, with the rest and the trailing newline gone entirely
# - not a timing race with a different command, there was no next
# command sent yet). QEMU's own `-nographic`/stdio chardev on Windows
# appears to simply drop input once the guest cannot keep up reading it
# fast enough, rather than blocking the write or buffering it somewhere
# that survives - something no flag was found to change, and not
# something this project can fix (it is QEMU's own code, not ours).
# Spacing the writes out on the host side keeps the guest able to keep
# up. burst_test.sh confirms the SAME paced delivery, run against real
# QEMU with the identical payload fs_compare.sh/burst_test.sh's own
# riscv-emu leg sends unpaced, is what makes any of this a fair
# comparison rather than a test that only ever exercised one side.
type_slowly() {
    local fd="$1" cmd="$2" chunk=4 i=0 text len
    text="${cmd}"$'\n'
    len=${#text}
    while [ $i -lt $len ]; do
        printf '%s' "${text:i:chunk}" >&"$fd"
        i=$((i + chunk))
        sleep 0.05
    done
}

# Sends $3 to fd $2, then waits for a REAL fresh prompt to appear in $1,
# not the existing one $cmd's own echo lands on - the shell prints its
# prompt ONCE, with nothing after it until something is typed, and
# whatever is typed is echoed by the tty onto that SAME line (exactly
# like a real terminal: "$ " then typing "ls" shows "$ ls" on one line,
# not a second "$" of its own) - so sending one command produces exactly
# ONE new occurrence of GUEST_PROMPT (the next idle prompt, once the
# command's own output is done), not two (confirmed directly: an
# earlier version of this waited for +2 and never once saw it, since the
# count genuinely only ever grows by 1 per command). $4 (default 40s) is
# a timeout safety net, $5 a short label for diagnostics (e.g.
# "riscv-emu"/"QEMU").
#
# This prompt-wait pacing replaces fs_compare.sh's own earlier
# fixed-sleep pacing for BOTH legs - confirmed directly (a real CI run
# on GitHub Actions' own Windows runners, never reproduced locally
# before or after on the same machine) that a fixed per-command sleep
# was too tight there, a fixed sleep on either leg being exactly the
# same kind of guess about "probably long enough" on a host whose real
# timing cannot be controlled from here.
#
# $6, if non-empty, sends $cmd through type_slowly (byte-paced) instead
# of a single plain printf - QEMU's own leg passes this, riscv-emu's own
# leg deliberately does not: sending the whole command in one write is
# itself the point for riscv-emu (see burst_test.sh) - silently chunking
# its input here too would hide whatever that is actually testing
# rather than continue to prove it on every run.
# Writes raw bytes $2 to fd $1, as one single write (default, $3 unset
# or <= 0) or in $3-sized chunks with a short real pause between each
# (burst_test.sh's own QEMU leg) - unlike type_slowly, no trailing
# newline is added either way: this is for a raw payload a preceding
# command (already sent, with its own newline) is waiting to read an
# exact byte count of, not a shell command line of its own.
send_bytes() {
    local fd="$1" data="$2" chunk="${3:-0}" i=0 len
    len=${#data}
    if [ "$chunk" -le 0 ]; then
        printf '%s' "$data" >&"$fd"
        return
    fi
    while [ $i -lt $len ]; do
        printf '%s' "${data:i:chunk}" >&"$fd"
        i=$((i + chunk))
        sleep 0.05
    done
}

send_and_wait() {
    local out_file="$1" fd="$2" cmd="$3" timeout="${4:-40}" label="$5" paced="${6:-}" before tries
    before=$(prompt_count "$out_file")
    if [ -n "$paced" ]; then
        type_slowly "$fd" "$cmd"
    else
        printf '%s\n' "$cmd" >&"$fd"
    fi
    tries=0
    while [ "$(prompt_count "$out_file")" -lt "$((before + 1))" ]; do
        tries=$((tries + 1))
        if [ "$tries" -ge "$timeout" ]; then
            echo "guest_io: FAIL - $label did not respond within ${timeout}s to: $cmd" >&2
            echo "guest_io: last output:" >&2
            tail -n 20 "$out_file" >&2
            return 1
        fi
        sleep 1
    done
}
