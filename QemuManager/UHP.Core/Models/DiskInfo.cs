using System.Collections.Generic;

namespace QemuManager.Models;

/// <summary>One disk image file - the Storage page's own list (Phase 6),
/// gathered from every Environment's own Workload.DiskPath plus
/// whatever else sits in AppPaths.DisksDir (a disk created but never
/// attached to anything yet, or one left behind after its own
/// Environment was deleted). See
/// Services.IVmService.GetDiskInventoryAsync.</summary>
public record DiskInfo(
    string Path,
    bool Exists,
    /// <summary>From qemu-img info, e.g. "qcow2"/"raw" - null if QEMU is
    /// not available to ask, or the file does not exist.</summary>
    string? Format,
    long? VirtualSizeBytes,
    long? ActualSizeBytes,
    /// <summary>Every Environment (by Name) whose Workload.DiskPath is
    /// this exact file - empty for an orphan file with nothing
    /// referencing it any more, which is the only case "Remove from
    /// list" is allowed to actually delete (see
    /// Services.IVmService.DeleteDiskFileAsync's own remarks).</summary>
    IReadOnlyList<string> UsedByEnvironmentNames);
