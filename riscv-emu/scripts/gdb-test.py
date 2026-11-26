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
LLDB_TIMEOUT_SECONDS = 30
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
    register read + delete the breakpoint + continue to completion, all
    in one lldb batch (-o ...) invocation - the exact scenario this
    milestone's own verification step describes. Returns the combined
    stdout+stderr transcript for extract_values to pull register values
    out of."""
    elf_posix = ELF.replace("\\", "/")
    cmds = [
        'target create "%s"' % elf_posix,
        "gdb-remote 127.0.0.1:%d" % port,
        "breakpoint set --name main",
        "continue",
        "register read %s" % " ".join(reg_names),
        "breakpoint delete 1",
        "continue",
    ]
    args = ["lldb", "-b"]
    for c in cmds:
        args += ["-o", c]
    result = subprocess.run(args, capture_output=True, text=True, timeout=LLDB_TIMEOUT_SECONDS)
    return result.stdout + result.stderr


def extract_values(lldb_output, reg_names):
    values = {}
    for name in reg_names:
        m = re.search(r"^\s*%s\s*=\s*(0x[0-9a-fA-F]+)" % re.escape(name), lldb_output, re.MULTILINE)
        if not m:
            print("gdb-test: could not find register '%s' in lldb's own output:\n%s" % (name, lldb_output))
            sys.exit(1)
        values[name] = int(m.group(1), 16)
    return values


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
        riscv_out = run_lldb(RISCV_EMU_PORT, ["x1", "x2", "x10", "pc"])
    finally:
        riscv_proc.wait(timeout=10)

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
        qemu_out = run_lldb(QEMU_PORT, ["ra", "sp", "a0", "pc"])
    finally:
        qemu_proc.terminate()
        try:
            qemu_proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            qemu_proc.kill()

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

    if not ok:
        print("gdb-test: FAIL - register state at the breakpoint stop on main differs between riscv-emu and QEMU")
        return 1
    print("gdb-test: riscv-emu matches QEMU (registers at the breakpoint stop on main)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
