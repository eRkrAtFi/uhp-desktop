using System;
using System.IO;

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
        if (!isPortable) return Path.Combine(roamingAppDataDir, "QemuManager");

        // Portable mode: one-time, best-effort migration from this app's
        // pre-portable location (%APPDATA%\QemuManager) if the portable
        // folder doesn't exist yet - copies, not moves, so existing VM
        // configs and settings do not appear to vanish just because this
        // .exe is now somewhere else, and the old location is left intact
        // in case this .exe is one of several copies.
        var legacyDir = Path.Combine(roamingAppDataDir, "QemuManager");
        if (!Directory.Exists(portableDir) && Directory.Exists(legacyDir))
        {
            try
            {
                CopyDirectory(legacyDir, portableDir);
            }
            catch
            {
                // Best effort - e.g. no permission to read the old location.
                // The app still works, just starting empty rather than
                // losing anything that was never copied.
            }
        }

        return portableDir;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var destPath = Path.Combine(destDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            if (!File.Exists(destPath)) File.Copy(file, destPath);
        }
    }
}
