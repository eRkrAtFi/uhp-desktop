using System.IO;
using System.Threading.Tasks;

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

    /// <summary>riscv-emu --version's own output (src/main.c: "riscv-emu
    /// vX.Y.Z (commit abc123)") - the Engines page's own version column
    /// (Phase 6). Null if riscvEmuDir does not look like a real install.</summary>
    public static async Task<string?> GetVersionAsync(string riscvEmuDir) =>
        LooksLikeRiscvEmuDir(riscvEmuDir) ? await ExternalVersionProbe.GetFirstLineAsync(ExePath(riscvEmuDir)) : null;
}
