# Deferred to roadmap item 3 (RISC-V bare-metal / firmware slice)

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
