namespace QemuManager.Models;

/// <summary>Boot firmware - see vision/VISION.md's own data-model table
/// ("Firmware | Boot firmware and its state (flash image, NVRAM,
/// variables)"). Only a firmware image path exists so far; NVRAM/variable
/// state is roadmap item 4 (UEFI development on x86_64) - deliberately
/// not modeled yet, the same reason bare-metal workload fields are not
/// (see vision/NEXT.md) - there is no real use case to design their exact
/// shape from until that slice exists.</summary>
public class Firmware
{
    /// <summary>Passed as QEMU's -bios, e.g. an edk2 AAVMF build for
    /// aarch64 or an OpenSBI build for riscv64. Empty means none is passed -
    /// fine for x86_64 (QEMU's own SeaBIOS/OVMF default) or for booting a
    /// kernel directly (see Workload.KernelPath), but several machine
    /// types have no firmware of their own and need one to boot anything
    /// from disk/CD-ROM. Not used by riscv-emu (its own firmware stub,
    /// boot/firmware.bin, is fixed and not user-configurable).</summary>
    public string FirmwarePath { get; set; } = "";
}
