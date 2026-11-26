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

    public static BuildResult Build(EnvironmentConfig vm, string riscvEmuDir)
    {
        if (!RiscvEmuLocator.LooksLikeRiscvEmuDir(riscvEmuDir))
            throw new InvalidOperationException($"riscv-emu.exe or its boot/firmware.bin was not found in '{riscvEmuDir}'.");

        var args = new List<string>
        {
            "--firmware", RiscvEmuLocator.FirmwarePath(riscvEmuDir),
            "--kernel", vm.Workload.KernelPath,
        };
        if (!string.IsNullOrWhiteSpace(vm.Workload.InitrdPath)) { args.Add("--initrd"); args.Add(vm.Workload.InitrdPath); }
        if (!string.IsNullOrWhiteSpace(vm.Workload.KernelCmdline)) { args.Add("--append"); args.Add(vm.Workload.KernelCmdline.Trim()); }
        if (!string.IsNullOrWhiteSpace(vm.Workload.DiskPath)) { args.Add("--disk"); args.Add(vm.Workload.DiskPath); }
        if (vm.Platform.NetworkMode == "user") args.Add("--net");
        if (vm.Platform.MemoryMB > 0) { args.Add("--mem"); args.Add(((ulong)vm.Platform.MemoryMB * 1024UL * 1024UL).ToString()); }
        // Run until the guest halts itself or the user stops it, not until
        // an arbitrary instruction cap - unlike a `make compare` test run,
        // there is no self-terminating guest program to expect here.
        args.Add("--max"); args.Add(ulong.MaxValue.ToString());

        return new BuildResult { ExePath = RiscvEmuLocator.ExePath(riscvEmuDir), Arguments = args };
    }
}
