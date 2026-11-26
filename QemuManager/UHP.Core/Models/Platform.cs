namespace QemuManager.Models;

/// <summary>The emulated hardware - architecture, machine type, CPU,
/// memory, network backend - independent of what runs on it (see
/// <see cref="Workload"/>) or what boots it (see <see cref="Firmware"/>).
/// See vision/VISION.md's own data-model table. Not every engine uses
/// every field (riscv-emu has a fixed Architecture/MachineType/CpuCores
/// it does not read from here - see IEngine.GetConfigSchema for which
/// fields an engine's own editor actually shows), the same way
/// VmConfig's own fields used to apply to only one Kind or the other.</summary>
public class Platform
{
    public string Architecture { get; set; } = "x86_64";

    /// <summary>QEMU's -M/-machine value, e.g. "virt" for aarch64/arm/riscv.
    /// Empty means QEMU picks its own default machine type - fine for
    /// x86_64, but several other architectures have no usable default at
    /// all. Not used by riscv-emu (always "virt"-equivalent, fixed).</summary>
    public string MachineType { get; set; } = "";

    public int CpuCores { get; set; } = 2;

    /// <summary>For riscv-emu this becomes --mem (bytes = MemoryMB * 1024 *
    /// 1024); riscv-emu.exe's own default (see src/emu.h's MEM_SIZE) is
    /// 256 MB, but IEngine.CreateDraft fills in 512 MB here instead - see
    /// its own remarks.</summary>
    public int MemoryMB { get; set; } = 2048;

    /// <summary>"tcg" (software, always available) or "whpx" (Windows
    /// Hypervisor Platform). Not used by riscv-emu (there is only ever
    /// one way it executes - its own interpreter).</summary>
    public string Accelerator { get; set; } = "tcg";

    /// <summary>"user" for built-in NAT, or "none" for no NIC at all. For
    /// riscv-emu there is no backend choice (--net is the only one), so
    /// "user" means --net and anything else means no NIC.</summary>
    public string NetworkMode { get; set; } = "user";
}
