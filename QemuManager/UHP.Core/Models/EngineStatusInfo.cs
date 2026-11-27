using System.Collections.Generic;

namespace QemuManager.Models;

/// <summary>Whether an engine's own binary was actually found at its
/// configured path - the Engines page's own "status" column (Phase 6).</summary>
public enum EngineStatus
{
    Found,
    NotFound,
    /// <summary>Found on disk, but querying --version itself failed (the
    /// process would not start, or exited abnormally) - distinct from
    /// NotFound so the page can say *why* no version string is shown
    /// instead of implying the binary is simply missing.</summary>
    Error,
}

/// <summary>Display-ready facts about one registered engine's own
/// install - path, version, status, supported architectures, and how
/// many Environments currently use it. See
/// Services.IVmService.GetEngineStatuses.</summary>
public record EngineStatusInfo(
    string EngineId,
    string DisplayName,
    string Path,
    EngineStatus Status,
    /// <summary>null unless Status == Found and --version actually
    /// returned something.</summary>
    string? VersionText,
    IReadOnlyList<string> SupportedArchitectures,
    int EnvironmentCount);
