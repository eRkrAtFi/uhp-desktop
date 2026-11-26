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
