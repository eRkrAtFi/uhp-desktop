using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>Loads and saves <see cref="EnvironmentConfig"/> as one JSON
/// file per Environment - the same role VmStore played, same folder
/// (AppPaths.VmsDir), same one-file-per-config convention. Was called
/// VmStore; renamed alongside the model it persists.
///
/// LoadAll transparently migrates any file still in the pre-Environment
/// VmConfig shape (see EnvironmentMigration) - the file's original bytes
/// are backed up to AppPaths.VmsMigrationBackupDir first, then the
/// migrated EnvironmentConfig is written back over the original file
/// (same Id, same filename), so every future load sees the new shape
/// directly and migration genuinely only ever happens once per file.</summary>
public class EnvironmentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<EnvironmentConfig> LoadAll()
    {
        AppPaths.EnsureDirectories();
        var result = new List<EnvironmentConfig>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.VmsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var env = LoadOne(file, json);
                if (env != null) result.Add(env);
            }
            catch (Exception)
            {
                // Skip a file that does not parse rather than failing the
                // whole list; one bad config should not hide the others.
            }
        }
        return result.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static EnvironmentConfig? LoadOne(string file, string json)
    {
        int schemaVersion;
        using (var doc = JsonDocument.Parse(json))
        {
            if (!EnvironmentMigration.NeedsMigration(doc.RootElement))
                return JsonSerializer.Deserialize<EnvironmentConfig>(json);

            schemaVersion = doc.RootElement.TryGetProperty(nameof(EnvironmentConfig.SchemaVersion), out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : 0;
        }

        // Back up the exact original bytes before this file is ever
        // overwritten - same filename, a separate directory, so a
        // migration bug is recoverable from rather than only ever
        // trusted to have been lossless.
        Directory.CreateDirectory(AppPaths.VmsMigrationBackupDir);
        var backupPath = Path.Combine(AppPaths.VmsMigrationBackupDir, Path.GetFileName(file));
        if (!File.Exists(backupPath)) File.Copy(file, backupPath);

        // schemaVersion 0 (no property at all - the original flat
        // VmConfig shape) needs the full legacy conversion first;
        // MigrateFromJson already carries that all the way up to
        // CurrentSchemaVersion. Anything already Environment-shaped
        // (schemaVersion >= 1, e.g. every file saved under v0.4.0) only
        // needs whatever later version steps (MigrateV1ToV2, ...) it is
        // still behind on.
        var migrated = schemaVersion == 0
            ? EnvironmentMigration.MigrateFromJson(json)
            : EnvironmentMigration.MigrateV1ToV2(JsonSerializer.Deserialize<EnvironmentConfig>(json)!);
        File.WriteAllText(file, JsonSerializer.Serialize(migrated, JsonOptions));
        return migrated;
    }

    public void Save(EnvironmentConfig env)
    {
        AppPaths.EnsureDirectories();
        var path = PathFor(env.Id);
        var json = JsonSerializer.Serialize(env, JsonOptions);
        File.WriteAllText(path, json);
    }

    public void Delete(EnvironmentConfig env)
    {
        var path = PathFor(env.Id);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string PathFor(string id) => Path.Combine(AppPaths.VmsDir, id + ".json");
}
