# Next step: roadmap item 4 (UEFI development on x86_64)

Roadmap item 3 (RISC-V bare-metal environment with a GDB stub) is done:
`examples/baremetal-hello` (a tiny freestanding C program), `--elf` in
riscv-emu (loads a real ELF64/EM_RISCV file by its own PT_LOAD program
headers, starts at e_entry in M-mode), a GDB Remote Serial Protocol stub
(`--gdb`/`--gdb-wait`, src/gdbstub.c) verified against real QEMU's own
`-gdb`/`-S` through lldb (`make gdb-test`), and a "Bare-metal / Embedded"
template in UHP wiring `Workload.ElfPath`/`Debug.GdbEnabled` through
`IEngine`/the schema for both engines, no `EngineId` branching.

Next, per vision/VISION.md's own roadmap: item 4, **UEFI development on
x86_64** (QEMU, Q35, OVMF, NVRAM variables, GDB) - validates the firmware
and NVRAM parts of the Environment/Platform/Workload model the same way
item 3 validated the bare-metal/debugging parts.

## Known issue: `make gdb-test` skips on CI (lldb crashes there)

CI's own choco-installed lldb.exe exits with an access violation
(0xC0000005 - exit code 3221225781, no output at all) every time this
has run on GitHub's windows-latest runner so far - `is_lldb_output_sane`
in scripts/gdb-test.py already catches this and skips cleanly (not a
failure), but the actual register/variable/alignment verification this
test exists for never runs there, only locally. Worth fixing properly
rather than leaving it permanently skipped - options, not yet tried:

- A different/newer lldb build (the one choco's `llvm` package installs
  may simply be broken on that specific runner image - worth pinning a
  specific version or trying a different package/installer).
- Real `gdb` from MSYS2 instead of lldb - a completely different
  client, sidesteps whatever is wrong with this particular lldb.exe
  build entirely, but would need its own command syntax (gdb's own
  batch-mode `-x`/`-batch`, not lldb's `-o`) alongside or instead of the
  existing lldb-based script.
- A minimal custom RSP client (Python, using the exact raw-socket
  framing already prototyped by hand while building src/gdbstub.c -
  send/recv $...#XX packets directly) instead of depending on a real
  debugger binary at all - the most CI-robust option (nothing external
  to crash), at the cost of not being a real end-to-end test of lldb/gdb
  interoperability specifically.
