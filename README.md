# UHP Desktop + riscv-emu

A Windows desktop VM manager (**UHP Desktop**, .NET 8/WPF) paired with a
hand-written RISC-V system emulator (**riscv-emu**) built from scratch in
C and verified at every step against real QEMU.

- **UHP Desktop** (`QemuManager/`) manages virtual machines two ways: real
  QEMU VMs (it drives `qemu-system-*.exe` over QMP, the same way
  virt-manager or GNOME Boxes do) and `riscv-emu` VMs (its own process,
  driven directly - no QEMU involved at runtime for those).
- **riscv-emu** (`riscv-emu/`) is an RV64IMAFDC(Zicsr,Sstc) interpreter
  that boots a real, unmodified riscv64 Linux kernel to an interactive
  shell, with virtio-blk/virtio-net and real-time guest clock semantics -
  see [`riscv-emu/README.md`](riscv-emu/README.md) for the full phase-by-phase
  build log and every bug found along the way.

![UHP Desktop's main window, with one VM running](docs/screenshot.png)

## Requirements

- Windows 10/11 (x64).
- [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) to
  run UHP Desktop (the portable release bundle has no other
  prerequisite).
- A QEMU install (e.g. [qemu.weilnetz.de](https://qemu.weilnetz.de/w64/))
  if you want to run real QEMU VMs - UHP Desktop also works with only
  `riscv-emu` VMs and no QEMU at all.
- To **build** riscv-emu yourself (not required to just run it): a
  portable GCC such as [w64devkit](https://github.com/skeeto/w64devkit)
  for the interpreter itself, and LLVM/clang (for the guest test
  programs riscv-emu's own test suite compiles and runs).

## Quick start - using it

1. Download the latest release's `UHP-Desktop-portable.zip` from this
   repo's [Releases](../../releases) page and extract it anywhere.
2. Run `QemuManager.exe`. No installer, no admin rights needed.
3. Click **+ New VM**, choose **riscv-emu**, point it at a kernel Image
   (and optionally an initramfs) - see the note below on where to get one
   - and click **Power On**.
4. The console window that opens is the guest's own UART - type into it
   like a terminal (`Ctrl+C` works, arrow keys work, `Send Ctrl+C` button
   if you'd rather click).

**A heads-up on the guest kernel:** this repo does not ship a riscv64
Linux kernel/initramfs (see [`THIRD_PARTY.md`](THIRD_PARTY.md) for why -
GPL source-availability obligations, and no confidently-known source for
the specific binaries used during development). A Buildroot-based,
from-scratch, fully-reproducible kernel+initramfs build (config committed
to this repo, built in CI, published as release assets) is in progress -
until it lands, you'll need your own riscv64 kernel Image to try
`riscv-emu`, or stick to real QEMU VMs in the meantime, which need no
kernel of their own (point UHP Desktop at an existing disk image/ISO the
same way any QEMU front end would).

## Quick start - building it

```sh
# riscv-emu (needs w64devkit's gcc + LLVM's clang on PATH - see SETUP
# notes in riscv-emu/README.md if you don't have these yet)
cd riscv-emu
make                 # builds riscv-emu.exe
make test            # unit test, this emulator only
make compare         # differential: byte-for-byte against real qemu-system-riscv64
make test-all         # the fast suite: unit + differential + integration

# UHP Desktop (needs the .NET 8 SDK)
cd ../QemuManager
dotnet build QemuManager.SelfTest/QemuManager.SelfTest.csproj -c Release
dotnet QemuManager.SelfTest/bin/Release/net8.0/QemuManager.SelfTest.dll \
    "C:\Program Files\qemu" "..\riscv-emu"   # exercises both engines, no window needed
dotnet build QemuManager/QemuManager.csproj -c Release   # the WPF app itself
```

See [`riscv-emu/README.md`](riscv-emu/README.md) and
[`QemuManager/README.md`](QemuManager/README.md) for everything else -
architecture, every bug found and fixed, and why each design decision was
made. [`QemuManager/ARCHITECTURE_AUDIT.md`](QemuManager/ARCHITECTURE_AUDIT.md)
tracks known risks and planned work.

## License

GPL-3.0 - see [`LICENSE`](LICENSE). Third-party components and their own
licenses are listed in [`THIRD_PARTY.md`](THIRD_PARTY.md).
