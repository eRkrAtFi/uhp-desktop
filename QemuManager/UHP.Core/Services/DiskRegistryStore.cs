using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QemuManager.Services;

/// <summary>Persists every disk path ever created or added via "Create
/// disk"/"Add existing disk" (Storage page, PR #7 feedback) - one flat
/// JSON array of paths at AppPaths.DiskRegistryFile, the same single-
/// file convention TaskHistoryStore already uses for Recent Tasks.
/// IVmService.GetDiskInventoryAsync unions this with every Environment's
/// own Workload.DiskPath - replacing Phase 6's original plain directory
/// scan of AppPaths.DisksDir, which could not see a disk saved anywhere
/// else, and so silently lost track of one (no longer "removable from
/// the list" at all) the moment its owning Environment was deleted.</summary>
public class DiskRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<string> LoadAll()
    {
        if (!File.Exists(AppPaths.DiskRegistryFile)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(AppPaths.DiskRegistryFile)) ?? new List<string>();
        }
        catch (Exception)
        {
            // A corrupt/partial file (e.g. the app was killed mid-write)
            // should read back as "nothing registered yet", not crash
            // app startup - same reasoning as RuntimeStateStore.Load.
            return new List<string>();
        }
    }

    /// <summary>No-op if path (by its normalized full path) is already
    /// registered - "every disk created via Create disk is recorded",
    /// not "recorded again on every single create/add".</summary>
    public void Add(string path)
    {
        var all = LoadAll();
        var normalized = Normalize(path);
        if (all.Any(p => string.Equals(Normalize(p), normalized, StringComparison.OrdinalIgnoreCase))) return;
        all.Add(path);
        Save(all);
    }

    /// <summary>One-time migration (PR #7 feedback - a regression this
    /// registry itself caused): before it existed, GetDiskInventoryAsync
    /// found every "known but unreferenced" disk by scanning
    /// AppPaths.DisksDir directly; once the registry became the only
    /// source for that, anything already sitting in DisksDir from
    /// before this feature shipped simply vanished from the Storage
    /// page's own list, with no way to get it back short of "Add
    /// existing disk" one at a time. Runs only if the registry file
    /// does not exist at all yet, so it can never re-run or clobber
    /// anything a real session has already registered since - seeds it
    /// with every file already in AppPaths.DisksDir, unioned with every
    /// Environment's own current Workload.DiskPath (environmentDiskPaths -
    /// a disk outside DisksDir some Environment already references is
    /// tracked too, the same thing GetDiskInventoryAsync's own Track()
    /// does for the live inventory). Called from VmService.LoadAll, the
    /// first point _configs (and so every Environment's own DiskPath)
    /// is actually known.</summary>
    public void MigrateIfNeeded(IEnumerable<string> environmentDiskPaths)
    {
        if (File.Exists(AppPaths.DiskRegistryFile)) return;

        var seed = new List<string>();
        if (Directory.Exists(AppPaths.DisksDir))
            seed.AddRange(Directory.EnumerateFiles(AppPaths.DisksDir));
        seed.AddRange(environmentDiskPaths.Where(p => !string.IsNullOrWhiteSpace(p)));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in seed)
            if (seen.Add(Normalize(path))) result.Add(path);

        Save(result);
    }

    /// <summary>Removes path from the registry only - never touches the
    /// actual file (see Services.IVmService.RemoveDiskFromListAsync's
    /// own remarks on why "Remove from list" no longer deletes
    /// anything). No-op if it was never registered to begin with (e.g.
    /// a disk known only because some Environment references it).</summary>
    public void Remove(string path)
    {
        var all = LoadAll();
        var normalized = Normalize(path);
        if (all.RemoveAll(p => string.Equals(Normalize(p), normalized, StringComparison.OrdinalIgnoreCase)) == 0) return;
        Save(all);
    }

    private static void Save(List<string> all)
    {
        AppPaths.EnsureDirectories();
        File.WriteAllText(AppPaths.DiskRegistryFile, JsonSerializer.Serialize(all, JsonOptions));
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); } catch (Exception) { return path; }
    }
}
