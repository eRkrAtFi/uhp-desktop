# Next step: roadmap item 3 (RISC-V bare-metal / firmware slice)

With Steps A and B (vision/VISION.md's roadmap items 1 and 2 - the engine
interface and the Environment/Platform/Workload model with one schema-
driven editor) released as v0.4.0/v0.4.1/v0.4.2, the next step is roadmap
item 3 itself: **RISC-V bare-metal environment with a GDB stub** - a
Platform of RISC-V `virt`, a Workload that is a bare ELF file (no kernel,
no initramfs), run on riscv-emu or QEMU (one switch between them), a UART
console, and a GDB stub in riscv-emu for a debugger to attach to. The
rest of this file is what that step actually needs to build.

Decided while building Step B's Environment/Platform/Workload model and
its "New Environment" templates: **no "Bare-metal / Embedded" template
yet**, and no bare-metal-specific fields on `Workload` yet either - only
templates for things this app can actually run today ("Operating System"
and "Kernel / Bootloader") are shown.

What bare-metal needs, when roadmap item 3 happens:

- A workload shape for "just a raw binary/ELF, no kernel" - riscv-emu's
  own CLI already supports this (a bare positional `<raw-binary>` loaded
  at 0x80000000, or `--load <addr> <file> --entry <addr>` for placing one
  or more files at explicit addresses) - `RiscvEmuBoot.Build` only ever
  emits the `--kernel`/`--initrd` form today; it needs a second path for
  this.
- The "Bare-metal / Embedded" template itself, wired to that.
- A GDB stub in riscv-emu (VISION.md's own roadmap item 3 names this
  explicitly) - nothing in riscv-emu today exposes one.
- Likely a new `Workload.Kind` (or similar discriminator) once there are
  two genuinely different workload shapes (kernel+initramfs vs a single
  binary/ELF at an address) instead of one flat set of optional fields -
  decide the exact shape when the first real use case (an actual ELF to
  run) exists, not speculatively now.

Until then, `Workload` is written to leave room for this (see its own
class remarks in UHP.Core/Models/Workload.cs) without adding fields or
UI that do nothing yet.
