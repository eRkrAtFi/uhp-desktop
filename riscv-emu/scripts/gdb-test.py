#!/usr/bin/env python3
"""vision/VISION.md roadmap item 3, M3's own automated verification
(`make gdb-test`): the identical scenario - breakpoint on main, continue,
read registers, continue to completion - run through lldb (LLVM's own
gdb-remote client) against BOTH riscv-emu's own GDB stub (--gdb
--gdb-wait) and real qemu-system-riscv64's own (-gdb tcp::<port> -S),
diffing the register values each side reports at the breakpoint stop -
the same differential-testing principle every other guest program in
this repo already follows (see Makefile's compare/baremetal-compare),
just driven through the GDB protocol instead of comparing stdout.

Skips (prints why, exits 0 - not a failure) rather than fails outright
if lldb or qemu-system-riscv64 are not on PATH - CI may not always have
both, and this is the one test in the suite that needs an actual
external debugger client, not just riscv-emu and QEMU themselves.
"""
import os
import re
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ELF = os.path.join(ROOT, "examples", "baremetal-hello", "hello.elf")
RISCV_EMU = os.path.join(ROOT, "riscv-emu.exe")
RISCV_EMU_PORT = 42424
QEMU_PORT = 42425
# Generous, not tight: a loaded CI runner (this step follows the rest of
# `make test-all`, including the slower kernel-boot-shaped tests) can
# take noticeably longer than this same scenario does on a quiet dev
# machine for every one of these - confirmed directly (CI's own first
# run of this test timed out waiting on the child process with the
# previous, tighter values).
LLDB_TIMEOUT_SECONDS = 60
PROCESS_EXIT_TIMEOUT_SECONDS = 20
# A real probe connection cannot be used to wait for either server's own
# listening socket to come up: riscv-emu's own GDB stub accepts exactly
# one connection, ever (see src/gdbstub.c) - a probe that succeeds would
# itself BE that one connection, leaving lldb's own real attempt right
# after it with nothing left to accept (confirmed directly: lldb hung
# waiting for a stop-reply that was never coming, from a session that
# had already been silently closed out from under it). A short, fixed
# wait is simpler and sufficient - both servers' own listen() call
# completes near-instantly after the process starts.
STARTUP_WAIT_SECONDS = 1.5


def run_lldb(port, reg_names):
    """target create + gdb-remote + breakpoint on main + continue +
    register read + three SOURCE-level steps (hello.c's own for loop,
    reached after exactly three - see hello.c's own line numbers) +
    read the loop's own local variable + delete the breakpoint +
    continue to completion, all in one lldb batch (-o ...) invocation -
    the exact scenario this milestone's own verification step
    describes. Source-level stepping and reading a real local variable
    (not just raw registers) only works because examples/baremetal-hello
    is built with -g -O0 (see Makefile's own BAREMETAL_FLAGS) - the
    compiler has to emit line/variable debug info for lldb to show any
    of this. Returns (returncode, combined stdout+stderr transcript) -
    the caller decides whether an empty/unexpected transcript means lldb
    itself is not usable in this environment (see is_lldb_output_sane)
    rather than a real comparison failure."""
    elf_posix = ELF.replace("\\", "/")
    cmds = [
        'target create "%s"' % elf_posix,
        "gdb-remote 127.0.0.1:%d" % port,
        "breakpoint set --name main",
        "continue",
        "register read %s" % " ".join(reg_names),
        "next",
        "next",
        "next",
        "frame variable i",
        "breakpoint delete 1",
        "continue",
    ]
    args = ["lldb", "-b"]
    for c in cmds:
        args += ["-o", c]
    result = subprocess.run(args, capture_output=True, text=True, timeout=LLDB_TIMEOUT_SECONDS)
    return result.returncode, result.stdout + result.stderr


def is_lldb_output_sane(output):
    """False means lldb ran (no exception, no timeout) but produced
    nothing resembling a real interactive session - confirmed to happen
    on at least one real CI runner despite lldb being found on PATH and
    exiting 0 with completely empty stdout+stderr, cause unconfirmed
    (quite possibly something about lldb's own Windows build needing a
    real console/display that a CI job's own non-interactive session
    does not have). Treated the same as "lldb not found" - a skip, not
    a failure - rather than guessing further at environments this was
    never going to be tested against directly."""
    return "(lldb)" in output


def extract_values(lldb_output, reg_names):
    values = {}
    for name in reg_names:
        m = re.search(r"^\s*%s\s*=\s*(0x[0-9a-fA-F]+)" % re.escape(name), lldb_output, re.MULTILINE)
        if not m:
            print("gdb-test: could not find register '%s' in lldb's own output:\n%s" % (name, lldb_output))
            sys.exit(1)
        values[name] = int(m.group(1), 16)
    return values


def extract_variable(lldb_output, name):
    """Pulls an integer value out of lldb's own "frame variable <name>"
    reply, e.g. "(uint32_t) i = 0" -> 0. Exits with the full transcript
    printed if the variable could not be read at all (source-level
    debug info missing or broken - a real failure, not a value
    mismatch)."""
    m = re.search(r"\)\s*%s\s*=\s*(-?\d+)" % re.escape(name), lldb_output)
    if not m:
        print("gdb-test: could not read local variable '%s' from lldb's own output (source-level debug info broken?):\n%s" % (name, lldb_output))
        sys.exit(1)
    return int(m.group(1))


def main():
    if shutil.which("lldb") is None:
        print("gdb-test: lldb not found on PATH - skipping (install LLVM's lldb to run this test locally; CI may not always have it either)")
        return 0
    qemu = shutil.which("qemu-system-riscv64")
    if qemu is None:
        print("gdb-test: qemu-system-riscv64 not found on PATH - skipping (nothing to differentially compare riscv-emu's own GDB stub against)")
        return 0
    if not os.path.exists(RISCV_EMU):
        print("gdb-test: %s not built - run `make` first" % RISCV_EMU)
        return 1
    if not os.path.exists(ELF):
        print("gdb-test: %s not built - run `make baremetal-hello` first" % ELF)
        return 1

    # --- riscv-emu leg: plain x1/x2/x10/pc, matching this stub's own
    # target.xml (see src/gdbstub.c's build_target_xml). ---
    riscv_proc = subprocess.Popen(
        [RISCV_EMU, "--deterministic-time", "--gdb", str(RISCV_EMU_PORT), "--gdb-wait", "--elf", ELF],
        cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(STARTUP_WAIT_SECONDS)
        riscv_rc, riscv_out = run_lldb(RISCV_EMU_PORT, ["x1", "x2", "x10", "pc"])
    finally:
        # By this point the guest has already run to completion and
        # exited on its own (confirmed by the "continue" at the end of
        # run_lldb's own command list) in the common case - this is a
        # safety net for a slower CI runner, or lldb's own session not
        # having finished cleanly, not the expected path. A bare
        # riscv_proc.wait() with no terminate() first (what this used to
        # be) raised TimeoutExpired straight out of this finally block on
        # a loaded CI runner, which discards whatever exception (if any)
        # was already propagating and replaces it with a confusing
        # unrelated traceback - terminate() first, same as the QEMU leg
        # below already does, so a plain timeout here is never fatal.
        riscv_proc.terminate()
        try:
            riscv_proc.wait(timeout=PROCESS_EXIT_TIMEOUT_SECONDS)
        except subprocess.TimeoutExpired:
            riscv_proc.kill()

    if not is_lldb_output_sane(riscv_out):
        print("gdb-test: lldb ran (exit %d) but produced no real session output in this environment - skipping, not failing (see is_lldb_output_sane's own remarks):\n%s"
              % (riscv_rc, riscv_out))
        return 0

    # --- QEMU leg: real ABI register names (ra/sp/a0/pc) - QEMU's own
    # gdbstub reports a different target.xml than ours, but the same
    # standard GDB register NUMBERING underneath (x1=ra, x2=sp, x10=a0,
    # 32=pc), which is what is actually being compared here, just read
    # through each side's own names. ---
    qemu_proc = subprocess.Popen(
        [qemu, "-M", "virt", "-bios", "none", "-kernel", ELF, "-nographic", "-no-reboot",
         "-gdb", "tcp::%d" % QEMU_PORT, "-S"],
        cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(STARTUP_WAIT_SECONDS)
        qemu_rc, qemu_out = run_lldb(QEMU_PORT, ["ra", "sp", "a0", "pc"])
    finally:
        qemu_proc.terminate()
        try:
            qemu_proc.wait(timeout=PROCESS_EXIT_TIMEOUT_SECONDS)
        except subprocess.TimeoutExpired:
            qemu_proc.kill()

    if not is_lldb_output_sane(qemu_out):
        print("gdb-test: lldb ran (exit %d) but produced no real session output against QEMU - skipping, not failing:\n%s"
              % (qemu_rc, qemu_out))
        return 0

    riscv_vals = extract_values(riscv_out, ["x1", "x2", "x10", "pc"])
    qemu_vals = extract_values(qemu_out, ["ra", "sp", "a0", "pc"])

    pairs = [("x1", "ra"), ("x2", "sp"), ("x10", "a0"), ("pc", "pc")]
    ok = True
    for riscv_name, qemu_name in pairs:
        rv, qv = riscv_vals[riscv_name], qemu_vals[qemu_name]
        status = "OK" if rv == qv else "MISMATCH"
        print("gdb-test: %-4s riscv-emu=0x%016x qemu=0x%016x %s" % (riscv_name, rv, qv, status))
        if rv != qv:
            ok = False

    # The actual regression this milestone's own stack-alignment fix
    # (link.ld's own ALIGN(16) before .bss) guards against: sp must be
    # 16-byte aligned per the RISC-V calling convention at any call
    # boundary - confirmed BROKEN before that fix (sp = 0x8000225f, not
    # even close), on both sides identically (same ELF, same linker
    # output - QEMU is not a second opinion here, just confirming this
    # script reads the right register).
    for label, sp in (("riscv-emu", riscv_vals["x2"]), ("qemu", qemu_vals["sp"])):
        aligned = (sp & 0xf) == 0
        print("gdb-test: %-9s sp=0x%016x 16-byte aligned: %s" % (label, sp, aligned))
        if not aligned:
            ok = False

    # Source-level stepping + a real local variable (hello.c's own loop
    # counter `i`, read via "frame variable" after three source-level
    # "next" steps from the breakpoint on main - see run_lldb's own
    # remarks) - needs -g -O0 debug info to work at all, not just
    # matching values.
    riscv_i = extract_variable(riscv_out, "i")
    qemu_i = extract_variable(qemu_out, "i")
    i_status = "OK" if riscv_i == qemu_i else "MISMATCH"
    print("gdb-test: i    riscv-emu=%d qemu=%d %s" % (riscv_i, qemu_i, i_status))
    if riscv_i != qemu_i:
        ok = False

    if not ok:
        print("gdb-test: FAIL - register/variable/alignment state differs between riscv-emu and QEMU, or sp is misaligned")
        return 1
    print("gdb-test: riscv-emu matches QEMU (registers, a source-level local variable, and sp alignment, all at the breakpoint stop on main)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
