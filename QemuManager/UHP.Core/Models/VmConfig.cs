using System;

namespace QemuManager.Models;

/// <summary>
/// Everything needed to build a qemu-system-&lt;arch&gt;.exe command line for one
/// virtual machine. Persisted as one JSON file per VM under
/// %APPDATA%\QemuManager\vms\&lt;Id&gt;.json by <see cref="Services.VmStore"/>.
/// </summary>
public class VmConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New VM";

    /// <summary>"Qemu" (default, backward-compatible with every VM saved
    /// before this field existed) or "RiscvEmu" - selects the engine, and
    /// which of the fields below actually apply. Not an enum so an older
    /// save file missing this property still deserializes to the right
    /// default instead of failing to parse.</summary>
    public string Kind { get; set; } = "Qemu";

    /// <summary>The architecture suffix of qemu-system-&lt;Architecture&gt;.exe, e.g. "x86_64".
    /// Only meaningful when Kind is "Qemu".</summary>
    public string Architecture { get; set; } = "x86_64";

    /// <summary>For "RiscvEmu" this becomes --mem (bytes = MemoryMB * 1024 *
    /// 1024); riscv-emu.exe's own default (see src/emu.h's MEM_SIZE) is
    /// 256 MB.</summary>
    public int MemoryMB { get; set; } = 2048;
    public int CpuCores { get; set; } = 2;

    /// <summary>Path to a disk image. Empty means no disk is attached.
    /// For "RiscvEmu" this becomes --disk (virtio-blk); for "Qemu" it is
    /// attached the way Architecture/MachineType call for.</summary>
    public string DiskPath { get; set; } = "";

    /// <summary>Path to an ISO mounted as a CD-ROM. Empty means none attached.</summary>
    public string CdromPath { get; set; } = "";

    /// <summary>"tcg" (software, always available) or "whpx" (Windows Hypervisor
    /// Platform; needs that Windows feature enabled, much faster when it works).</summary>
    public string Accelerator { get; set; } = "tcg";

    /// <summary>"user" for QEMU's built-in NAT, or "none" to attach no NIC.
    /// For "RiscvEmu" there is no backend choice (--net is the only one),
    /// so "user" means --net and anything else means no NIC.</summary>
    public string NetworkMode { get; set; } = "user";

    /// <summary>"gtk" or "sdl" display backend.</summary>
    public string Display { get; set; } = "gtk";

    /// <summary>Boot device order string passed to -boot order=..., e.g. "cd" or "c".</summary>
    public string BootOrder { get; set; } = "c";

    /// <summary>QEMU's -M/-machine value, e.g. "virt" for aarch64/arm/riscv.
    /// Empty (the default, and every VM saved before this field existed)
    /// means QEMU picks its own default machine type for Architecture -
    /// fine for x86_64, but several other architectures have no usable
    /// default and will not boot at all without one. See
    /// VmEditWindow's architecture-hint text and ARCHITECTURE_AUDIT.md's
    /// recommendation #3.</summary>
    public string MachineType { get; set; } = "";

    /// <summary>Path to a firmware/BIOS image passed as QEMU's -bios, e.g. an
    /// edk2 AAVMF build for aarch64 or an OpenSBI build for riscv64. Empty
    /// means none is passed - fine for x86_64 (QEMU's own SeaBIOS/OVMF
    /// default) or for booting a kernel directly via -kernel (see
    /// ExtraArgs), but several machine types have no firmware of their own
    /// and need one to boot anything from disk/CD-ROM.</summary>
    public string FirmwarePath { get; set; } = "";

    /// <summary>Free-form extra arguments. For "Qemu" these are appended
    /// verbatim to the command line; for "RiscvEmu" they are appended to
    /// the kernel boot command line (the DTB's /chosen/bootargs), e.g.
    /// "loglevel=8" - not the riscv-emu.exe command line itself, which this
    /// app always builds the same way (see RiscvEmuBoot).</summary>
    public string ExtraArgs { get; set; } = "";

    /// <summary>Only meaningful when Kind is "RiscvEmu": path to a riscv64
    /// Linux "Image" file.</summary>
    public string KernelPath { get; set; } = "";

    /// <summary>Only meaningful when Kind is "RiscvEmu": path to an
    /// initramfs (e.g. initramfs.cpio.gz). Empty means boot with no root
    /// filesystem at all.</summary>
    public string InitrdPath { get; set; } = "";

    /// <summary>Only meaningful when Kind is "RiscvEmu": the line typed into
    /// the guest's console to request a shutdown (see Phase 4's "Shutdown"
    /// action). There is no QMP/ACPI equivalent for this engine - console
    /// text is the only channel, and it only does anything if a shell
    /// prompt is actually sitting there waiting for input, which is why the
    /// app still falls back to Force Stop after a timeout regardless of
    /// what this is set to.
    ///
    /// Plain "poweroff" - verified directly against the Buildroot-built
    /// image (Linux 6.18.64) that replaced this project's own earlier
    /// undocumented-provenance one: its real busybox init (board/riscv-
    /// emu/rootfs-overlay/etc/inittab) does act on a plain poweroff, and
    /// its kernel fragment's CONFIG_POWER_RESET_SYSCON_POWEROFF carries
    /// the request the rest of the way, under both riscv-emu and real
    /// qemu-system-riscv64 identically (fs_compare.sh's own poweroff step,
    /// and this app's own Shut Down button, both confirmed). The OLD
    /// image's init never acted on a plain poweroff signal at all (needed
    /// "sync; poweroff -f" to bypass init and call reboot() directly) -
    /// that workaround does not apply here and should not come back if a
    /// future guest image's own init turns out to need it again; verify
    /// against the real image first, the way this one was, rather than
    /// changing this default on the strength of a guess.</summary>
    public string ShutdownCommand { get; set; } = "poweroff";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
