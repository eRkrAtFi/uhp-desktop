# Third-party components

This project's own code (`riscv-emu/`, `QemuManager/`) is licensed under
GPL-3.0 (see [`LICENSE`](LICENSE)). This file lists everything else it
uses, builds with, or drives at runtime, and under what license.

## Runtime dependencies (used by the shipped app, not bundled)

| Component | License | How it's used |
|---|---|---|
| [.NET 8 runtime](https://dotnet.microsoft.com/) | MIT | UHP Desktop's own runtime. Not bundled in the portable zip - installed separately by the user. |
| [QEMU](https://www.qemu.org/) (`qemu-system-*.exe`, `qemu-img.exe`) | GPL-2.0 (mixed; QEMU itself states GPL-2.0-or-later for most of the tree) | A **separate, user-installed process** UHP Desktop launches and talks to over QMP (a local socket protocol) - never linked against, no QEMU source is included in this repo. The optional "fully portable" bundle (see `QemuManager/README.md`'s "Portable by design") copies a real QEMU install's own files verbatim next to the app; that copy carries QEMU's own license and source-availability obligations, which a redistributor of that specific bundle (not of this source repo) needs to honor - see QEMU's own `COPYING`/`COPYING.LIB` for exactly what that requires. |

## Build-time tools (not distributed, not bundled)

| Component | License | Role |
|---|---|---|
| [w64devkit](https://github.com/skeeto/w64devkit) (a packaged GCC/binutils) | Various (see its own repo) | Compiles `riscv-emu.exe`. Never shipped - a developer building riscv-emu needs it on `PATH`; the GitHub Actions CI workflow installs an equivalent toolchain fresh in the job, same as any developer would. |
| LLVM/clang, `llvm-objcopy` | Apache-2.0 with LLVM exceptions | Compiles/links the RISC-V guest test programs riscv-emu's own test suite runs (`tests/*.S`, `boot/firmware.S`) - build-time only, the output binaries are tiny and either ours (`firmware.bin`) or gitignored test artifacts, never a redistribution of LLVM itself. |

## Guest images (fetched separately, never committed to this repo)

See [`riscv-emu/scripts/fetch-guest-images.sh`](riscv-emu/scripts/fetch-guest-images.ps1)
for the actual download+verify logic.

| Image | License | Source |
|---|---|---|
| Alpine Linux riscv64 minirootfs, 3.20.10 | MIT (Alpine's own base) plus whatever each individual package in it carries - see [Alpine's own package licensing](https://pkgs.alpinelinux.org/) | `https://dl-cdn.alpinelinux.org/alpine/v3.20/releases/riscv64/alpine-minirootfs-3.20.10-riscv64.tar.gz`, sha256 `972e8257ffded92877ff9a09a8418c3eb7c6f0e49c9413461777d7279fdc4b2f` - verified directly against Alpine's own published `.sha256` for this exact file. |
| riscv64 Linux kernel Image (6.18.64) + busybox initramfs | GPL-2.0 (kernel), GPL-2.0 (busybox) | A from-scratch, fully reproducible Buildroot build - config committed to this repo under [`riscv-emu/buildroot/`](riscv-emu/buildroot/), built and `make legal-info`'d by a dedicated CI workflow ([`.github/workflows/guest-images.yml`](.github/workflows/guest-images.yml)), replacing the kernel+initramfs used during this project's own Phase 5/6 development, which had no independently-verifiable recorded source at all - see `riscv-emu/README.md`'s own history. Buildroot 2026.08.2, external Bootlin `riscv64-lp64d-musl` toolchain (not Buildroot building its own), targeting exactly the device set `riscv-emu/src/emu.h`/`dtb.c` implement (rv64gc, CLINT+Sstc, SiFive PLIC, 16550 UART, virtio-mmio blk+net, Goldfish RTC, syscon-poweroff) - nothing guessed, nothing aspirational; every optional RISC-V ISA extension the kernel's own `arch/riscv/Kconfig` defaults to on but riscv-emu does not implement is explicitly disabled, verified (not just assumed) to actually survive into the built kernel's own `.config` by [`riscv-emu/buildroot/scripts/verify-config-fragment.sh`](riscv-emu/buildroot/scripts/verify-config-fragment.sh), which fails the CI build if any of them silently was not. Published as release [`guest-images-v1`](https://github.com/eRkrAtFi/uhp-desktop/releases/tag/guest-images-v1) - `guest-images.tar.gz` (the kernel `Image` + `initramfs.cpio.gz` themselves, sha256 `0c18c2c01acaf48f621dcd7a06644efcdf49f1aa2eb326cd6aca99360af912d7`) and `guest-images-legal-info.tar.gz` (Buildroot's own `make legal-info` output - every package's source archive, license text, and manifest that went into the built image, satisfying GPL source-availability for the kernel and busybox). Fetched (not committed, for the same size/binary-blob reasons as Alpine's own tarball above) by [`riscv-emu/scripts/fetch-guest-images.sh`](riscv-emu/scripts/fetch-guest-images.sh)/`.ps1`, which verify the downloaded `guest-images.tar.gz` against that same sha256 before accepting it. |

## Explicitly not included

Reverse-engineering tools, text editors, or other software used *to
develop* this project (Ghidra, Node.js, this repo's own tooling
configuration, etc.) are not dependencies of the shipped app or its
source and are not listed here.
