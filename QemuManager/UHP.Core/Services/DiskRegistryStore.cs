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
