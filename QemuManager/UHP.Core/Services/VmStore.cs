using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>Loads and saves <see cref="VmConfig"/> as one JSON file per VM.
/// This is the whole persistence layer: there is no database, just a folder.</summary>
public class VmStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<VmConfig> LoadAll()
    {
        AppPaths.EnsureDirectories();
        var result = new List<VmConfig>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.VmsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var vm = JsonSerializer.Deserialize<VmConfig>(json);
                if (vm != null)
                {
                    // VmConfig.MemoryMB's class default (2048) is sized for
                    // QEMU - a riscv-emu VM saved before this field existed
                    // deserializes with that QEMU default silently filled
                    // in instead of riscv-emu's own 512 (CreateDraft's own
                    // default for a brand-new VM, but that path is never
                    // consulted again once a file already exists on disk).
                    // Checked against the raw JSON, not "is MemoryMB still
                    // 2048" - a real riscv-emu VM that was deliberately
                    // saved with 2048 MB must not get silently downgraded
                    // on every future load.
                    if (vm.Kind == "RiscvEmu")
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (!doc.RootElement.TryGetProperty(nameof(VmConfig.MemoryMB), out _))
                            vm.MemoryMB = 512;
                    }
                    result.Add(vm);
                }
            }
            catch (Exception)
            {
                // Skip a file that does not parse rather than failing the
                // whole list; one bad VM config should not hide the others.
            }
        }
        return result.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Save(VmConfig vm)
    {
        AppPaths.EnsureDirectories();
        var path = PathFor(vm.Id);
        var json = JsonSerializer.Serialize(vm, JsonOptions);
        File.WriteAllText(path, json);
    }

    public void Delete(VmConfig vm)
    {
        var path = PathFor(vm.Id);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string PathFor(string id) => Path.Combine(AppPaths.VmsDir, id + ".json");
}
