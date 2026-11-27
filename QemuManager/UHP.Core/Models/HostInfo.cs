namespace QemuManager.Models;

/// <summary>Display-ready facts about the host machine itself, not any
/// one Environment - the Host page's own data (Phase 6), including the
/// running/total Environment summary that used to sit above the
/// navigator tree. See Services.IVmService.GetHostInfo.</summary>
public record HostInfo(
    string OperatingSystem,
    string CpuDescription,
    string MemoryDescription,
    /// <summary>Free space where this app keeps its own data
    /// (AppPaths.AppDataDir) - not necessarily where any one disk image
    /// or kernel actually lives, the same way Settings' own paths are
    /// each wherever the user pointed them, independent of this.</summary>
    string FreeDiskSpaceDescription,
    bool WhpxEnabled,
    int RunningEnvironments,
    int TotalEnvironments,
    double TotalCpuPercent,
    long TotalMemoryBytes);
