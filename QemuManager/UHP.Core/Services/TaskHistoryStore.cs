using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>Persists Recent Tasks' own history as one JSON array at
/// AppPaths.TasksFile - see TaskHistoryService for the in-memory side
/// (capping, ordering, the actual begin/complete API) this is only ever
/// called from.</summary>
public static class TaskHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Save(IReadOnlyList<TaskRecord> records)
    {
        AppPaths.EnsureDirectories();
        File.WriteAllText(AppPaths.TasksFile, JsonSerializer.Serialize(records, JsonOptions));
    }

    public static List<TaskRecord> Load()
    {
        if (!File.Exists(AppPaths.TasksFile)) return new List<TaskRecord>();
        try
        {
            return JsonSerializer.Deserialize<List<TaskRecord>>(File.ReadAllText(AppPaths.TasksFile)) ?? new List<TaskRecord>();
        }
        catch (Exception)
        {
            // A corrupt/partial file (e.g. the app was killed mid-write)
            // should read back as "no history yet", not crash app startup -
            // same reasoning as RuntimeStateStore.Load.
            return new List<TaskRecord>();
        }
    }
}
