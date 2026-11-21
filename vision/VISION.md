# UHP Vision

> **UHP is a universal lab for running anything that can be emulated or virtualized.**
> Pick a platform, add what you want to run, choose an engine, press **Run**.

UHP is not meant to be another front end for virtual machines. Its focus is the
*platform* and the *workload* running on it, not only operating systems:

- operating systems: Windows, Linux, BSD, custom distributions,
- firmware: BIOS, UEFI, coreboot, U-Boot, OpenFirmware,
- kernels and bootloaders: Linux, custom kernels, GRUB, UEFI applications,
- bare-metal and embedded programs: RISC-V or ARM ELF files, MCU firmware,
- experimental platforms and custom CPU or hardware emulators.

The user should not need to know which engine runs the workload, or how to
assemble a QEMU command line, attach firmware, or wire up virtual hardware.

## Architecture

```
UHP Desktop / Web / CLI   management, configuration, consoles, debugger, monitoring
UHP Core                  lifecycle, environments, platforms, processes, tasks
Platform layer            CPU, chipset, RAM, peripherals, boot firmware
Workload layer            OS, firmware, kernel, bare-metal program
Execution engines         QEMU, riscv-emu, Hyper-V, future engines
```

## Data model

The single `VmConfig` class is replaced step by step with:

| Object | Purpose |
| --- | --- |
| Environment | The whole runnable setup the user creates, opens, and runs |
| Platform | The emulated hardware: architecture, machine, CPU, memory, devices |
| Firmware | Boot firmware and its state (flash image, NVRAM, variables) |
| Workload | What runs on the platform: disk, ISO, kernel and initramfs, ELF |
| Engine | What executes it, with its capabilities and configuration schema |
| Debug configuration | Consoles, UART, GDB, tracing, logs |

"New VM" becomes **New Environment** with templates: Operating System,
Firmware, Kernel / Bootloader, Bare-metal / Embedded, Custom.

## Roadmap

1. **Engine interface in UHP Core.** `IEngine` with metadata, capabilities,
   a configuration schema, and operations (start, shut down, console, stats).
   QEMU and riscv-emu become two implementations of it.
2. **Environment / Platform / Workload model**, and one schema-driven editor
   instead of separate per-engine windows (the same schemas serve the web UI
   and CLI later).
3. **First vertical slice: RISC-V bare-metal / firmware environment.**
   Platform RISC-V `virt`, workload an ELF file, engine riscv-emu or QEMU
   (one switch), UART console, and a **GDB stub in riscv-emu**. Bonus: run the
   same program on both engines and compare the output.
4. **Second slice: UEFI development on x86_64** (QEMU, Q35, OVMF, NVRAM
   variables, GDB). Validates the firmware and NVRAM parts of the model.
5. **A third engine** (Hyper-V or VirtualBox) to prove the engine interface.
6. **Plugins** only after that, if needed, preferably out-of-process
   (in the style of libvirt drivers or Terraform providers).

Later ideas: an optional build step per environment (run a build command
before Run), more platforms (ARM boards, embedded), web UI, CLI, Linux host.

## Principles

- One thin end-to-end slice before breadth.
- Every engine is verified against a reference (as riscv-emu is against QEMU).
- Logic lives in UHP Core; the desktop, web, and CLI are thin clients.
- Prior art worth studying: Renode (platform descriptions, embedded debugging).
