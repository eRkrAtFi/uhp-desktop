using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace QemuManager.Services;

/// <summary>Wraps qemu-img.exe, QEMU's own disk image tool, to create new
/// virtual disks. This app never writes a disk image format itself.</summary>
public static class DiskImageService
{
    public static async Task<string> CreateQcow2Async(string qemuDir, string path, int sizeGB)
    {
        var binary = QemuLocator.ImgBinary(qemuDir);
        if (binary == null) return $"qemu-img.exe not found in {qemuDir}.";

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("create");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("qcow2");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add($"{sizeGB}G");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("qemu-img did not start.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode == 0
            ? $"Created {path} ({sizeGB} GiB, qcow2)."
            : $"qemu-img failed (exit {process.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}";
    }
}
