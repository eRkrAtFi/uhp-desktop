using System;
using System.IO;
using System.Text.Json;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>Persists one <see cref="RuntimeState"/> per currently-running VM
/// under %APPDATA%\QemuManager\run\&lt;Id&gt;.json - a sidecar to VmStore's own
/// VM-definition files, not merged into them, since this is transient
/// runtime state (deleted the moment the process exits) rather than part of
/// the VM's persisted configuration. This is what lets the app tell "this
/// VM is still running" apart from "this VM is Stopped" after a restart,
/// instead of only ever trusting its own in-memory process-handle dictionary
/// (see ARCHITECTURE_AUDIT.md, Critical Risk #1).</summary>
public static class RuntimeStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string PathFor(string vmId) => Path.Combine(AppPaths.RuntimeDir, vmId + ".json");

    public static void Save(string vmId, RuntimeState state)
    {
        AppPaths.EnsureDirectories();
        File.WriteAllText(PathFor(vmId), JsonSerializer.Serialize(state, JsonOptions));
    }

    public static RuntimeState? Load(string vmId)
    {
        var path = PathFor(vmId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            // A corrupt/partial runtime-state file (e.g. the app was killed
            // mid-write) should read back as "nothing to reattach to", not
            // crash app startup.
            return null;
        }
    }

    public static void Delete(string vmId)
    {
        try { File.Delete(PathFor(vmId)); } catch (Exception) { /* best effort */ }
    }
}
