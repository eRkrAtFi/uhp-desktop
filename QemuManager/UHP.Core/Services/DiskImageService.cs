using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace QemuManager.Services;

/// <summary>Wraps qemu-img.exe, QEMU's own disk image tool, to create new
/// virtual disks. This app never writes a disk image format itself.</summary>
public static class DiskImageService
{
    /// <summary>qemu-img info --output=json's own format/virtual-size for
    /// one disk image - the Storage page's own per-disk detail (Phase 6).
    /// Deliberately NOT its own "actual-size" (see
    /// Services.IVmService.GetDiskInventoryAsync's own remarks on why
    /// "size on disk" is always read straight from the file instead,
    /// never through qemu-img at all - PR #7 feedback). Null fields (not
    /// an empty record) when qemu-img itself is not available or path
    /// does not look like an image qemu-img can read at all - the page
    /// shows "unknown" for those rather than a wrong zero.</summary>
    public static async Task<(string? Format, long? VirtualSizeBytes)> GetInfoAsync(string qemuDir, string path)
    {
        var binary = QemuLocator.ImgBinary(qemuDir);
        if (binary == null || !File.Exists(path)) return (null, null);

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("info");
        psi.ArgumentList.Add("--output=json");
        psi.ArgumentList.Add(path);

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return (null, null);
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) return (null, null);

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            string? format = root.TryGetProperty("format", out var f) ? f.GetString() : null;
            long? virtualSize = root.TryGetProperty("virtual-size", out var v) ? v.GetInt64() : null;
            return (format, virtualSize);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>Whether path looks like a qcow2 image by its own
    /// extension - the same heuristic VmProcessManager's own GuessFormat
    /// already uses for QEMU's disk-attachment format= argument, reused
    /// here for riscv-emu's own Workload.DiskPath validation (PR #7
    /// feedback: riscv-emu only ever reads a disk as flat raw bytes, so
    /// a qcow2 file there would silently read back as corrupt garbage,
    /// not a clean error from riscv-emu itself - see
    /// RiscvEmuProcessManager's own Workload.DiskPath field).</summary>
    public static bool LooksLikeQcow2(string path) =>
        string.Equals(Path.GetExtension(path), ".qcow2", StringComparison.OrdinalIgnoreCase);

    /// <summary>Deletes a disk image file from disk - only ever called for
    /// one that Services.IVmService.GetDiskInventoryAsync already
    /// reported as used by no Environment (see its own remarks); this
    /// method itself has no opinion on that and will delete whatever
    /// path it is given, so every caller must have already checked.</summary>
    public static string DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return $"{path} no longer exists.";
            File.Delete(path);
            return $"Deleted {path}.";
        }
        catch (Exception ex)
        {
            return $"Could not delete {path}: {ex.Message}";
        }
    }

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

    /// <summary>A plain raw disk image of exactly sizeBytes, written
    /// directly by this app (FileStream.SetLength - the same technique
    /// qemu-img create -f raw itself uses internally) - no qemu-img
    /// involved at all, so this works even with no QEMU installed (PR #7
    /// feedback: riscv-emu's own Workload.DiskPath is always raw, never
    /// qcow2 - see EnvironmentEditWindow's own validation - and someone
    /// using only riscv-emu should not need QEMU present just to create
    /// one).</summary>
    public static string CreateRaw(string path, long sizeBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            fs.SetLength(sizeBytes);
            return $"Created {path} ({sizeBytes} bytes, raw).";
        }
        catch (Exception ex)
        {
            return $"Could not create {path}: {ex.Message}";
        }
    }

    /// <summary>Same as CreateQcow2Async, but a plain byte count rather
    /// than whole GiB - the Storage/editor "Create disk" dialog's own
    /// MB-or-GB size field (PR #7 feedback) needs finer granularity than
    /// that older int-GB overload ever did.</summary>
    public static async Task<string> CreateQcow2Async(string qemuDir, string path, long sizeBytes)
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
        // A bare byte count - qemu-img create's own SIZE argument accepts
        // one with no suffix at all, not just a K/M/G/T-suffixed value.
        psi.ArgumentList.Add(sizeBytes.ToString());

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("qemu-img did not start.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode == 0
            ? $"Created {path} ({sizeBytes} bytes, qcow2)."
            : $"qemu-img failed (exit {process.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}";
    }

    /// <summary>Creates a disk image in whichever format the caller
    /// chose - the Storage/editor "Create disk" dialog's own single
    /// entry point (PR #7 feedback), rather than the dialog itself
    /// knowing which of CreateRaw/CreateQcow2Async to call.</summary>
    public static async Task<string> CreateAsync(string qemuDir, string path, long sizeBytes, string format) =>
        string.Equals(format, "raw", StringComparison.OrdinalIgnoreCase)
            ? CreateRaw(path, sizeBytes)
            : await CreateQcow2Async(qemuDir, path, sizeBytes);
}
