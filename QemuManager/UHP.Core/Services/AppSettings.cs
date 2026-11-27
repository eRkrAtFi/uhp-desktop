using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace QemuManager.Services;

/// <summary>App-wide settings, independent of any one VM. Persisted as a single
/// JSON file so the user only has to point at their QEMU install once -
/// unless a bundled copy sits right next to the .exe already (see
/// DefaultQemuDir/DefaultRiscvEmuDir), in which case there is nothing to
/// point at all: see "Fully portable (QEMU bundled)" in README.md.</summary>
public class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Directory containing qemu-system-*.exe and qemu-img.exe.</summary>
    public string QemuDir { get; set; } = DefaultQemuDir();

    /// <summary>Directory containing riscv-emu.exe and its boot\firmware.bin -
    /// i.e. the riscv-emu project's own root folder.</summary>
    public string RiscvEmuDir { get; set; } = DefaultRiscvEmuDir();

    /// <summary>How long VmService.ShutdownAsync waits, after sending a
    /// riscv-emu VM's own Workload.ShutdownCommand to its console, for the
    /// guest to actually power itself off before giving up and reporting a
    /// timeout - see VmService.ShutdownAsync's own remarks for why this
    /// never auto-escalates to Force Stop on its own. 60s is generous for
    /// a guest that is genuinely going to shut down (the Buildroot-built
    /// image this project ships with does, in well under that), while
    /// still being a bounded wait rather than an indefinite one for a
    /// guest that is not going to respond at all.</summary>
    public int RiscvShutdownTimeoutSeconds { get; set; } = 60;

    /// <summary>Remembered DataGrid column widths, keyed "GridName.
    /// ColumnHeader" (e.g. "RecentTasksList.Result") - every resizable
    /// grid's own columns (Environments, Storage, Networking, Recent
    /// Tasks) persist their width here on close and restore it on the
    /// next launch (PR #7 feedback), instead of resetting to this app's
    /// own hardcoded default ratios every time.</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new();

    /// <summary>A "qemu" folder bundled next to the .exe (see
    /// package-portable.ps1) wins, then anywhere qemu-system-*.exe is
    /// already reachable via PATH (the installer's own "add to PATH"
    /// option, or a portable QEMU someone unpacked and added themselves),
    /// then the Program Files default as a last resort - this is only ever
    /// the *default value* for a brand-new settings.json; once one exists,
    /// whatever the user saved there is authoritative and this is never
    /// consulted again.</summary>
    private static string DefaultQemuDir()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "qemu");
        if (QemuLocator.LooksLikeQemuDir(bundled)) return bundled;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && QemuLocator.LooksLikeQemuDir(dir))
                return dir;
        }

        return @"C:\Program Files\qemu";
    }

    /// <summary>Same idea as DefaultQemuDir, for a "riscv-emu" folder
    /// bundled next to the .exe.</summary>
    private static string DefaultRiscvEmuDir()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "riscv-emu");
        return RiscvEmuLocator.LooksLikeRiscvEmuDir(bundled) ? bundled : "";
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception)
        {
            // Fall through to defaults; a corrupt settings file should not
            // prevent the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        AppPaths.EnsureDirectories();
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(AppPaths.SettingsFile, json);
    }
}
