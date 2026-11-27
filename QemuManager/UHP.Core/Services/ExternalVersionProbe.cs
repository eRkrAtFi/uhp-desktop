using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace QemuManager.Services;

/// <summary>Runs an external engine binary with --version and returns its
/// first non-blank stdout line - shared between QemuLocator and
/// RiscvEmuLocator (the Engines page needs the same thing from both:
/// qemu-system-*'s own "QEMU emulator version X.Y.Z" banner, and
/// riscv-emu's own "riscv-emu vX.Y.Z (commit abc123)" line - see
/// src/main.c's own --version handling). Null on any failure (binary
/// missing, would not start, exited abnormally, printed nothing) - the
/// caller reports that as EngineStatus.Error, not as a crash.</summary>
internal static class ExternalVersionProbe
{
    public static async Task<string?> GetFirstLineAsync(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--version");

            using var process = Process.Start(psi);
            if (process == null) return null;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
