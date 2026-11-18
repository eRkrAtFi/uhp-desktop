using System.IO;

namespace QemuManager.Services;

/// <summary>Finds riscv-emu's own binary and its prebuilt firmware stub
/// inside the user-configured install directory, the same role
/// <see cref="QemuLocator"/> plays for real QEMU.</summary>
public static class RiscvEmuLocator
{
    public static string ExePath(string riscvEmuDir) => Path.Combine(riscvEmuDir, "riscv-emu.exe");

    public static string FirmwarePath(string riscvEmuDir) => Path.Combine(riscvEmuDir, "boot", "firmware.bin");

    public static bool LooksLikeRiscvEmuDir(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(ExePath(dir)) && File.Exists(FirmwarePath(dir));
}
