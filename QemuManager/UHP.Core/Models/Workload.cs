using System.Text.Json.Serialization;

namespace QemuManager.Models;

/// <summary>Which shape Workload's own fields are being used in right
/// now - set once, by whichever New Environment template created this
/// Environment, and never changed from within EnvironmentEditWindow
/// itself (there is no UI to change it - see ConfigField.VisibleWhen's
/// own remarks on why that is fine). Decides which Workload fields
/// GetConfigSchema's own VisibleWhen/RequiredWhen show and require - see
/// VmProcessManager/RiscvEmuProcessManager's own GetConfigSchema.</summary>
public enum WorkloadKind
{
    /// <summary>A kernel (+ optional initramfs/disk) booted the normal
    /// way - the "Operating System" and "Kernel / Bootloader" templates.</summary>
    LinuxKernel,

    /// <summary>A bare ELF with no kernel/firmware at all (+ optional
    /// disk) - the "Bare-metal / Embedded" template, roadmap item 3.</summary>
    BareMetalElf,
}

/// <summary>What runs on the <see cref="Platform"/> - a disk/ISO (the
/// "Operating System" template), a kernel + initramfs (the "Kernel /
/// Bootloader" template), or a bare ELF with no kernel/firmware at all
/// (the "Bare-metal / Embedded" template, roadmap item 3 - see
/// ElfPath). See vision/VISION.md's own data-model table.</summary>
public class Workload
{
    /// <summary>See <see cref="WorkloadKind"/>. Defaults to LinuxKernel -
    /// every Environment saved before this field existed loads as that,
    /// which matches what it actually was (the only shape that existed
    /// then).</summary>
    public WorkloadKind Kind { get; set; } = WorkloadKind.LinuxKernel;
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

    /// <summary>Path to a bare ELF64/EM_RISCV file to run directly, no
    /// kernel boot protocol and no firmware involved - the "Bare-metal /
    /// Embedded" template (roadmap item 3). riscv-emu loads it by its
    /// own PT_LOAD program headers and starts at e_entry in M-mode
    /// (--elf); QEMU does the same via its own ELF loader (-bios none
    /// -kernel &lt;this path&gt;, forcing no firmware regardless of
    /// Firmware.FirmwarePath - that field has no meaning for a workload
    /// this is set for). Mutually exclusive with KernelPath/InitrdPath -
    /// leave empty for every other template.</summary>
    public string ElfPath { get; set; } = "";

    /// <summary>False for Kind == BareMetalElf: a bare ELF has no OS/
    /// shell to type ShutdownCommand into (riscv-emu's own graceful
    /// shutdown) and no ACPI handler to react to a QMP power button
    /// event either (QEMU's own graceful shutdown) - "Shut Down" has
    /// nothing to actually ask the guest for in either case, only Force
    /// Stop makes sense. A computed property, not stored state - derived
    /// straight from Kind, so there is nothing here for a migration step
    /// to keep in sync.</summary>
    [JsonIgnore]
    public bool SupportsGracefulShutdown => Kind != WorkloadKind.BareMetalElf;
}
