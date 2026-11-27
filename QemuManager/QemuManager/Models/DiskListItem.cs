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
    /// <summary>Just the file name - the grid's own "Name" column (PR #7
    /// feedback); the full Path is a tooltip on that cell instead of its
    /// own always-visible column.</summary>
    public string FileName => System.IO.Path.GetFileName(Info.Path);
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

    /// <summary>Auto-picks KB/MB/GB so a small disk (a raw image a few
    /// MB in size, say) does not round down to a misleading "0 GB" -
    /// PR #7 feedback.</summary>
    private static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue) return "unknown";
        var b = bytes.Value;
        if (b >= 1024.0 * 1024 * 1024) return $"{b / (1024.0 * 1024 * 1024):0.##} GB";
        if (b >= 1024.0 * 1024) return $"{b / (1024.0 * 1024):0.##} MB";
        if (b >= 1024.0) return $"{b / 1024.0:0.##} KB";
        return $"{b} bytes";
    }
}
