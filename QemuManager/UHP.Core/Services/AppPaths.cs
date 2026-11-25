using System;
using System.IO;
using System.Linq;

namespace QemuManager.Services;

/// <summary>Centralizes where this app keeps its own state, separate from
/// QEMU's. Two modes, chosen explicitly rather than inferred from where the
/// .exe happens to be running from:
///
/// - Portable: a "data" folder next to the .exe (found via
///   AppContext.BaseDirectory, which - unlike Assembly.Location - resolves
///   correctly for a PublishSingleFile build too). Copying QemuManager.exe
///   plus its "data" folder to a USB stick or a different machine carries
///   every VM config and setting with it. Opted into by a "portable.flag"
///   marker file next to the .exe, or by a "data" folder already existing
///   there (so an existing portable install upgrading to this check still
///   works without anyone having to retroactively drop a flag file next to
///   it) - package-portable.ps1 writes the marker into every bundle it
///   builds.
/// - Default (no marker): %APPDATA%\QemuManager, the one real location for
///   every normal run of every build variant (publish/, bin/Debug,
///   bin/Release) that was not explicitly set up as a portable bundle.
///
/// Before this existed, the deciding factor was simply "does a 'data'
/// folder exist next to whichever .exe just ran" - which meant every
/// freshly-built .exe (a new bin/Debug output, a throwaway test build, a
/// differently-configured publish/ output, ...) silently cloned whatever
/// was in %APPDATA%\QemuManager into its own local "data" folder the first
/// time it ran and found none there yet, one independent, instantly-
/// diverging copy per build folder - which is how a VM created for a
/// one-off repro in a Debug build ended up looking like it was "in the
/// user's list": that Debug .exe had, on its very first run, already
/// cloned the real list into a folder that then diverged from it.</summary>
public static class AppPaths
{
    public static string AppDataDir { get; } = ResolveAppDataDir();

    public static string VmsDir { get; } = Path.Combine(AppDataDir, "vms");

    /// <summary>Sidecar runtime-state files (see RuntimeStateStore) - one per
    /// currently-running VM, separate from the VM's own persisted config.</summary>
    public static string RuntimeDir { get; } = Path.Combine(AppDataDir, "run");

    public static string SettingsFile { get; } = Path.Combine(AppDataDir, "settings.json");

    /// <summary>Recent Tasks' own persisted history (see TaskHistoryStore) -
    /// one flat JSON array, not one file per record like VmStore's own
    /// vms/*.json: this is a single ordered list with a fixed cap
    /// (TaskHistoryService.MaxRecords), not a keyed collection something
    /// else looks records up in by id.</summary>
    public static string TasksFile { get; } = Path.Combine(AppDataDir, "tasks.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(VmsDir);
        Directory.CreateDirectory(RuntimeDir);
    }

    private static string ResolveAppDataDir() => ResolveAppDataDir(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("QEMUMANAGER_DATA_DIR"),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>The actual decision, as a pure-enough function of explicit
    /// inputs rather than reading AppContext/Environment directly, so
    /// QemuManager.SelfTest can exercise every branch (override set, marker
    /// present via flag file, marker present via existing "data" folder,
    /// no marker at all) against temp directories it controls - without
    /// that, the real AppDataDir property's result is cached in a static
    /// field the first time anything touches it, and there would be no way
    /// to re-decide it a second time with different inputs inside the same
    /// test process.
    ///
    /// overrideDir (QEMUMANAGER_DATA_DIR) wins outright when set - no
    /// marker check, no %APPDATA% migration, just that exact directory.
    /// This is how QemuManager.SelfTest (and any debug/automation run
    /// launched for development, not by the real user) stays completely
    /// isolated from both the real %APPDATA% location and from a real
    /// portable bundle's own "data" folder.</summary>
    public static string ResolveAppDataDir(string baseDirectory, string? overrideDir, string roamingAppDataDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;

        var portableDir = Path.Combine(baseDirectory, "data");
        var isPortable = File.Exists(Path.Combine(baseDirectory, "portable.flag")) || Directory.Exists(portableDir);
        return isPortable ? portableDir : Path.Combine(roamingAppDataDir, "QemuManager");
    }

    /// <summary>True exactly when there is something worth asking about: in
    /// portable mode, the portable "data" folder does not exist yet, and
    /// %APPDATA%\QemuManager (an earlier, non-portable install's own data)
    /// has at least one saved VM. The caller - MainWindow, before its first
    /// LoadVms() - is expected to ask the user Import/Start empty on this,
    /// not act on it silently.
    ///
    /// This used to be a silent, automatic copy done inside
    /// ResolveAppDataDir itself, including every disk image - which meant
    /// opening a portable build on a machine that already had a real
    /// %APPDATA%\QemuManager silently duplicated potentially
    /// multi-gigabyte disk images into the portable folder with no
    /// warning and no way to decline. Splitting "detect" from "import"
    /// like this, and having import copy only VM configs (see
    /// ImportLegacyConfigs), fixes both: nothing happens without the user
    /// choosing it, and what does happen is small and fast regardless of
    /// how large any attached disk images are - their VmConfig.DiskPath
    /// is an absolute path already pointing at the original files in
    /// their old location, untouched by copying only the JSON that
    /// contains it.</summary>
    public static bool HasUnimportedLegacyData(string baseDirectory, string? overrideDir, string roamingAppDataDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir)) return false;

        var portableDir = Path.Combine(baseDirectory, "data");
        var isPortable = File.Exists(Path.Combine(baseDirectory, "portable.flag")) || Directory.Exists(portableDir);
        if (!isPortable || Directory.Exists(portableDir)) return false;

        var legacyVmsDir = Path.Combine(roamingAppDataDir, "QemuManager", "vms");
        return Directory.Exists(legacyVmsDir) && Directory.EnumerateFiles(legacyVmsDir, "*.json").Any();
    }

    public static bool HasUnimportedLegacyData() => HasUnimportedLegacyData(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("QEMUMANAGER_DATA_DIR"),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>Copies VM configs (vms/*.json) and settings.json from
    /// %APPDATA%\QemuManager into the portable "data" folder - deliberately
    /// never disks/, logs/, or run/ (runtime/process state that only ever
    /// makes sense for the install that wrote it). Returns how many VM
    /// configs were copied. Each copied VmConfig's own DiskPath is left
    /// exactly as saved - still an absolute path into the *old* location -
    /// so the imported VM keeps working without this needing to move or
    /// even touch the disk image itself.</summary>
    public static int ImportLegacyConfigs(string baseDirectory, string roamingAppDataDir)
    {
        var portableDir = Path.Combine(baseDirectory, "data");
        var legacyDir = Path.Combine(roamingAppDataDir, "QemuManager");

        var portableVmsDir = Path.Combine(portableDir, "vms");
        Directory.CreateDirectory(portableVmsDir);

        var count = 0;
        var legacyVmsDir = Path.Combine(legacyDir, "vms");
        if (Directory.Exists(legacyVmsDir))
        {
            foreach (var file in Directory.EnumerateFiles(legacyVmsDir, "*.json"))
            {
                File.Copy(file, Path.Combine(portableVmsDir, Path.GetFileName(file)), overwrite: false);
                count++;
            }
        }

        var legacySettings = Path.Combine(legacyDir, "settings.json");
        if (File.Exists(legacySettings))
            File.Copy(legacySettings, Path.Combine(portableDir, "settings.json"), overwrite: false);

        return count;
    }
}
