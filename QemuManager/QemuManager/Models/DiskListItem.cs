namespace QemuManager.Models;

/// <summary>Wraps one Services.DiskInfo for the Storage page's own
/// DataGrid (Phase 6) - plain display-text properties, the same
/// "logic in UHP.Core, this just renders it" convention VmListItem
/// already follows for the Environment grid.</summary>
public class DiskListItem
{
    public DiskInfo Info { get; }

    public DiskListItem(DiskInfo info) => Info = info;

    public string Path => Info.Path;
    public string Format => Info.Format ?? "unknown";
    public string VirtualSizeText => FormatBytes(Info.VirtualSizeBytes);
    public string ActualSizeText => FormatBytes(Info.ActualSizeBytes);
    public string UsedByText => Info.UsedByEnvironmentNames.Count > 0
        ? string.Join(", ", Info.UsedByEnvironmentNames)
        : "(none)";
    public string ExistsText => Info.Exists ? "Yes" : "No";

    /// <summary>Remove from list only ever means "delete this orphan
    /// file" (see Services.IVmService.DeleteDiskFileAsync's own
    /// remarks) - there is no separate catalog entry to remove while
    /// leaving the file alone, so this is disabled whenever any
    /// Environment still references it.</summary>
    public bool CanRemove => Info.UsedByEnvironmentNames.Count == 0;

    private static string FormatBytes(long? bytes) =>
        bytes.HasValue ? $"{bytes.Value / (1024.0 * 1024 * 1024):0.##} GB" : "unknown";
}
