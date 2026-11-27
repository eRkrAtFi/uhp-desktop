using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QemuManager.Services;

/// <summary>Persists every disk path ever created or added via "Create
/// disk"/"Add existing disk" (Storage page, PR #7 feedback) - a small
/// versioned object (Version + Paths) at AppPaths.DiskRegistryFile,
/// read/written as a whole by LoadFile/SaveFile so MigrateIfNeeded's own
/// version stamp always travels with the actual path list.
/// IVmService.GetDiskInventoryAsync unions Paths with every
/// Environment's own Workload.DiskPath - replacing Phase 6's original
/// plain directory scan of AppPaths.DisksDir, which could not see a
/// disk saved anywhere else, and so silently lost track of one (no
/// longer "removable from the list" at all) the moment its owning
/// Environment was deleted.</summary>
public class DiskRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Bumped only when a migration step is added below -
    /// MigrateIfNeeded compares this against whatever Version the file
    /// on disk already has, not against "does the file exist at all"
    /// (PR #7 feedback: an intermediate build already created a
    /// version-less disks.json for some users before this versioning
    /// existed, which made "only if the file does not exist" never run
    /// for them at all - their pre-existing AppPaths.DisksDir disks
    /// stayed invisible forever, not just until the next real
    /// migration).</summary>
    private const int CurrentVersion = 1;

    private class RegistryFile
    {
        /// <summary>0 for a file saved before this versioning existed
        /// (including the bare-JSON-array shape LoadFile below detects
        /// and upgrades transparently) or no file at all - both mean
        /// "the version-1 migration (seed from AppPaths.DisksDir and
        /// every Environment's own Workload.DiskPath) has not run yet".</summary>
        public int Version { get; set; }
        public List<string> Paths { get; set; } = new();
    }

    public List<string> LoadAll() => LoadFile().Paths;

    /// <summary>No-op if path (by its normalized full path) is already
    /// registered - "every disk created via Create disk is recorded",
    /// not "recorded again on every single create/add".</summary>
    public void Add(string path)
    {
        var file = LoadFile();
        var normalized = Normalize(path);
        if (file.Paths.Any(p => string.Equals(Normalize(p), normalized, StringComparison.OrdinalIgnoreCase))) return;
        file.Paths.Add(path);
        SaveFile(file);
    }

    /// <summary>Removes path from the registry only - never touches the
    /// actual file (see Services.IVmService.RemoveDiskFromListAsync's
    /// own remarks on why "Remove from list" no longer deletes
    /// anything). No-op if it was never registered to begin with (e.g.
    /// a disk known only because some Environment references it).</summary>
    public void Remove(string path)
    {
        var file = LoadFile();
        var normalized = Normalize(path);
        if (file.Paths.RemoveAll(p => string.Equals(Normalize(p), normalized, StringComparison.OrdinalIgnoreCase)) == 0) return;
        SaveFile(file);
    }

    /// <summary>Migration (PR #7 feedback - a regression this registry
    /// itself caused, then a gap in the first fix for it): before this
    /// registry existed, GetDiskInventoryAsync found every "known but
    /// unreferenced" disk by scanning AppPaths.DisksDir directly; once
    /// the registry became the only source for that, anything already
    /// sitting in DisksDir from before this feature shipped simply
    /// vanished from the Storage page's own list. Gated on Version, not
    /// file existence, so it genuinely runs exactly once per real
    /// upgrade regardless of whether an intermediate build already
    /// created a version-less disks.json - and never again afterward:
    /// once stamped, a later "Remove from list" on any of these same
    /// disks stays removed, it is never silently re-added.
    ///
    /// Adds whatever is missing (every file already in AppPaths.
    /// DisksDir, unioned with every Environment's own current
    /// Workload.DiskPath in environmentDiskPaths) without duplicating
    /// or discarding anything already in Paths. Called from
    /// VmService.LoadAll, the first point every Environment's own
    /// DiskPath is actually known.</summary>
    public void MigrateIfNeeded(IEnumerable<string> environmentDiskPaths)
    {
        var file = LoadFile();
        if (file.Version >= CurrentVersion) return;

        var seen = new HashSet<string>(file.Paths.Select(Normalize), StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(AppPaths.DisksDir))
            foreach (var path in Directory.EnumerateFiles(AppPaths.DisksDir))
                if (seen.Add(Normalize(path))) file.Paths.Add(path);

        foreach (var path in environmentDiskPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (seen.Add(Normalize(path))) file.Paths.Add(path);
        }

        file.Version = CurrentVersion;
        SaveFile(file);
    }

    private static RegistryFile LoadFile()
    {
        if (!File.Exists(AppPaths.DiskRegistryFile)) return new RegistryFile();
        try
        {
            var text = File.ReadAllText(AppPaths.DiskRegistryFile);
            // Back-compat: disks.json before this versioning existed
            // was a bare JSON array of paths, with no Version field at
            // all - read as Version 0 (same as no file), its own paths
            // preserved rather than discarded.
            if (text.TrimStart().StartsWith("["))
                return new RegistryFile { Version = 0, Paths = JsonSerializer.Deserialize<List<string>>(text) ?? new List<string>() };
            return JsonSerializer.Deserialize<RegistryFile>(text) ?? new RegistryFile();
        }
        catch (Exception)
        {
            // A corrupt/partial file (e.g. the app was killed mid-write)
            // should read back as "nothing registered yet", not crash
            // app startup - same reasoning as RuntimeStateStore.Load.
            return new RegistryFile();
        }
    }

    private static void SaveFile(RegistryFile file)
    {
        AppPaths.EnsureDirectories();
        File.WriteAllText(AppPaths.DiskRegistryFile, JsonSerializer.Serialize(file, JsonOptions));
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); } catch (Exception) { return path; }
    }
}
