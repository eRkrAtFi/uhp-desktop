# UHP Desktop + riscv-emu

> **Experimental, work in progress.** Built as a learning/reverse-
> engineering project, not a production hypervisor - see [Status](#status)
> below for what works and what doesn't yet, and
> [Reporting bugs](#reporting-bugs) if something breaks.

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

## Status

**Works:** creating/running/stopping both real QEMU VMs and riscv-emu
VMs; the riscv-emu console (typing, Ctrl+C, a real graceful Shut Down
that waits for the guest to exit on its own); live CPU/memory stats;
Recent Tasks; a portable, no-installer release build.

**Does not work yet:**
- The console window shows ANSI/VT100 escape sequences literally
  (colors, cursor movement) instead of interpreting them - plain text
  only for now.
- The **Storage** and **Networking** sections in the sidebar are
  placeholders with no functionality behind them yet (per-VM disk/
  network settings, in the VM's own Edit dialog, already work - these
  are for anything beyond that, e.g. a storage-pool or bridged-network
  view).

See [`QemuManager/ARCHITECTURE_AUDIT.md`](QemuManager/ARCHITECTURE_AUDIT.md)
for the full, honest list of known gaps and risks (storage/networking
depth, security posture, multi-node readiness) and what's actually
planned next.

## Vision

Where this project is headed: a platform/workload-centric lab for
running anything emulated or virtualized (operating systems, firmware,
kernels/bootloaders, bare-metal/embedded programs), with QEMU and
riscv-emu as two interchangeable engines behind one model rather than
two separate per-engine tools - see [`vision/VISION.md`](vision/VISION.md)
for the full architecture, data model, and the incremental roadmap
(one thin end-to-end slice before breadth, same as everything else in
this project).

## Requirements

- Windows 10/11 (x64).
- [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) to
  run UHP Desktop (the portable release bundle has no other
  prerequisite).
- A QEMU install if you want to run real QEMU VMs -
  `winget install --id SoftwareFreedomConservancy.QEMU -e`, or an
  installer from [qemu.org](https://www.qemu.org/download/#windows) /
  [qemu.weilnetz.de](https://qemu.weilnetz.de/w64/). Not needed at all
  for `riscv-emu` VMs - UHP Desktop works with only those and no QEMU
  install anywhere on the machine.
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

**A heads-up on the guest kernel:** this repo does not commit a riscv64
Linux kernel/initramfs to git (see [`THIRD_PARTY.md`](THIRD_PARTY.md) for
why - GPL source-availability obligations, and no confidently-known
source for the specific binaries used during this project's own early
development). It is fetched instead: run
`riscv-emu/scripts/fetch-guest-images.ps1` (or `.sh`) to download a real,
from-scratch, fully reproducible Buildroot build (Linux 6.18.64 - see
[`THIRD_PARTY.md`](THIRD_PARTY.md) for exactly what it targets and how it
is verified) from this repo's own
[`guest-images-v1`](../../releases/tag/guest-images-v1) release, then
point a new riscv-emu VM at the `boot/KernelImage` and
`boot/initramfs.cpio.gz` it extracts. The fetch itself needs nothing
extra - a plain HTTPS download, same as any other release asset; the
[GitHub CLI](https://cli.github.com/) (`gh`) is only used as a fallback
if that download fails for some reason (a proxy/firewall blocking
github.com's own release-asset host, for instance). (Real QEMU VMs need
neither - point UHP Desktop at an existing disk image/ISO the same way
any QEMU front end would.)

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

## Reporting bugs

Open an [issue](../../issues/new?template=bug_report.md) - there's a
template that asks for the version/commit, Windows version, and (if
UHP Desktop crashed or misbehaved) the matching file from its own
`logs` folder, which is usually what actually explains what happened.

## License

GPL-3.0 - see [`LICENSE`](LICENSE). Third-party components and their own
licenses are listed in [`THIRD_PARTY.md`](THIRD_PARTY.md).
