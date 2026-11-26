namespace QemuManager.Models;

/// <summary>What runs on the <see cref="Platform"/> - a disk/ISO (the
/// "Operating System" template), or a kernel + initramfs (the "Kernel /
/// Bootloader" template), today. See vision/VISION.md's own data-model
/// table.
///
/// Deliberately leaves room to grow without having added anything for it
/// yet: a bare-metal workload (a single ELF/binary with no kernel at all -
/// riscv-emu's own CLI already supports this, as a bare positional
/// argument or via --load/--entry) is roadmap item 3, and the "Bare-metal
/// / Embedded" template that would create one is intentionally not shown
/// yet - see vision/NEXT.md for exactly what is deferred and why. Adding
/// a Kind/discriminator now, before a real bare-metal use case exists to
/// decide its exact shape from, would be guessing; every field below is
/// one at least one of today's two working templates actually uses.</summary>
public class Workload
{
    /// <summary>Path to a disk image. Empty means none attached. For
    /// riscv-emu this becomes --disk (virtio-blk); for QEMU it is attached
    /// the way Platform.Architecture/MachineType call for.</summary>
    public string DiskPath { get; set; } = "";

    /// <summary>Path to an ISO mounted as a CD-ROM. Empty means none
    /// attached. Not used by riscv-emu.</summary>
    public string CdromPath { get; set; } = "";

    /// <summary>Path to a kernel to boot directly - riscv-emu's own
    /// riscv64 Linux "Image" file (always required - the "Kernel /
    /// Bootloader" template's own field, and the only way that engine
    /// boots anything), or QEMU's own -kernel (optional there - leave
    /// empty to boot normally from Disk image/CD-ROM/firmware instead).</summary>
    public string KernelPath { get; set; } = "";

    /// <summary>Path to an initramfs/initrd to go with KernelPath (e.g.
    /// initramfs.cpio.gz) - QEMU's -initrd, or riscv-emu's own --initrd.
    /// Empty means boot with no root filesystem at all.</summary>
    public string InitrdPath { get; set; } = "";

    /// <summary>Boot device order string passed to QEMU's -boot order=...,
    /// e.g. "cd" or "c". Not used by riscv-emu.</summary>
    public string BootOrder { get; set; } = "c";

    /// <summary>The kernel's own boot command line, when KernelPath is
    /// set - QEMU's -append, or riscv-emu's own --append (its own kernel
    /// cmdline always includes "console=ttyS0 earlycon=sbi" ahead of
    /// whatever is here - see RiscvEmuBoot). Was called ExtraArgs for
    /// riscv-emu before v0.4.1 - see EnvironmentMigration's own v1-to-v2
    /// step for existing Environments that already had one saved.</summary>
    public string KernelCmdline { get; set; } = "";

    /// <summary>Free-form extra QEMU command-line flags, appended
    /// verbatim - anything this app has no dedicated field for yet. Not
    /// used by riscv-emu (nothing beyond KernelCmdline applies to it).</summary>
    public string ExtraArgs { get; set; } = "";

    /// <summary>Only meaningful for riscv-emu today: the line typed into
    /// the guest's console to request a shutdown - see
    /// IEngine.ShutdownAsync's own remarks. Plain "poweroff" works for
    /// this project's own bundled Buildroot image; a guest with a
    /// different init system may need something else.</summary>
    public string ShutdownCommand { get; set; } = "poweroff";
}
