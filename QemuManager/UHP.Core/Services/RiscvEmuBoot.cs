using System;
using System.Collections.Generic;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>
/// Builds the riscv-emu.exe command line for one boot attempt. riscv-emu
/// itself now does the work this class used to do by hand - reading the
/// kernel Image header's text_offset, placing the initramfs and an
/// internally-generated DTB past it with margin, and setting pc/a0/a1/t3
/// the way boot/firmware.S expects (see src/main.c's setup_kernel_boot and
/// src/dtb.c) - so this is a thin argument-list builder now, the same role
/// boot/boot.sh plays for a terminal user, not a C# port of that logic.
/// </summary>
public static class RiscvEmuBoot
{
    public class BuildResult
    {
        public required string ExePath { get; init; }
        public required List<string> Arguments { get; init; }
    }

    /// <summary>gdbPort is the already-resolved port (Debug.GdbPort's own
    /// value, or a freshly picked free one if that was 0) - null when
    /// Debug.GdbEnabled is not set. Resolving "any free port" is a host
    /// concern (TcpListener), not something this pure argument-list
    /// builder does itself - see RiscvEmuProcessManager.Start.</summary>
    public static BuildResult Build(EnvironmentConfig vm, string riscvEmuDir, int? gdbPort = null)
    {
        if (!RiscvEmuLocator.LooksLikeRiscvEmuDir(riscvEmuDir))
            throw new InvalidOperationException($"riscv-emu.exe or its boot/firmware.bin was not found in '{riscvEmuDir}'.");

        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(vm.Workload.ElfPath))
        {
            // No --firmware/--kernel/--initrd/--append at all: a
            // bare-metal ELF starts directly at its own e_entry in
            // M-mode, no kernel boot protocol involved (see --elf in
            // riscv-emu's own src/main.c).
            args.Add("--elf");
            args.Add(vm.Workload.ElfPath);
        }
        else
        {
            args.Add("--firmware"); args.Add(RiscvEmuLocator.FirmwarePath(riscvEmuDir));
            args.Add("--kernel"); args.Add(vm.Workload.KernelPath);
            if (!string.IsNullOrWhiteSpace(vm.Workload.InitrdPath)) { args.Add("--initrd"); args.Add(vm.Workload.InitrdPath); }
            if (!string.IsNullOrWhiteSpace(vm.Workload.KernelCmdline)) { args.Add("--append"); args.Add(vm.Workload.KernelCmdline.Trim()); }
        }
        if (!string.IsNullOrWhiteSpace(vm.Workload.DiskPath)) { args.Add("--disk"); args.Add(vm.Workload.DiskPath); }
        if (vm.Platform.NetworkMode == "user") args.Add("--net");
        if (vm.Platform.MemoryMB > 0) { args.Add("--mem"); args.Add(((ulong)vm.Platform.MemoryMB * 1024UL * 1024UL).ToString()); }
        if (gdbPort.HasValue)
        {
            args.Add("--gdb"); args.Add(gdbPort.Value.ToString());
            if (vm.Debug.GdbWaitForDebugger) args.Add("--gdb-wait");
        }
        // Run until the guest halts itself or the user stops it, not until
        // an arbitrary instruction cap - unlike a `make compare` test run,
        // there is no self-terminating guest program to expect here.
        args.Add("--max"); args.Add(ulong.MaxValue.ToString());

        return new BuildResult { ExePath = RiscvEmuLocator.ExePath(riscvEmuDir), Arguments = args };
    }
}
