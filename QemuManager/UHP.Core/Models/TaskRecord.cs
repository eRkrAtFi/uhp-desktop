using System;

namespace QemuManager.Models;

/// <summary>One row of Recent Tasks' own history (see ITaskHistory) - a
/// real action the app took (Power On, Shut Down, Force Stop, Create/Edit/
/// Delete VM, Create Disk) or detected (a VM's process exiting on its own),
/// not the old free-text "Recent Tasks" line it replaces.
///
/// Status is a plain string ("Running"/"Succeeded"/"Failed"), not a C#
/// enum - same convention EnvironmentConfig.EngineId already uses for anything that
/// crosses the JSON persistence boundary, deliberately: a plain string
/// round-trips exactly as written and reads back the same in the raw
/// tasks.json file, where a numeric enum ordinal would not.</summary>
public sealed class TaskRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>What kind of action this is - "Power On", "Shut Down",
    /// "Force Stop", "Create VM", "Edit VM", "Delete VM", "Create Disk",
    /// "Console", or "Environment stopped" for one this app itself did not
    /// initiate (see VmService's own unexpected-stop detector).</summary>
    public string Name { get; set; } = "";

    /// <summary>What the action was done to/for - almost always a VM's
    /// own Name, except Create Disk (the disk's own path) and Import
    /// (where the data came from).</summary>
    public string Target { get; set; } = "";

    public DateTime Started { get; set; }

    /// <summary>Null while Status is "Running" - the UI's own way to tell
    /// "still in progress" from "done", rather than a separate bool.</summary>
    public DateTime? Completed { get; set; }

    public string Status { get; set; } = "Running";

    /// <summary>Human-readable outcome - "Running..." while in progress
    /// (see TaskHistoryService.BeginTask), then whatever the operation
    /// itself reported, success or failure, once CompleteTask runs. Never
    /// cleared on failure - an error must stay exactly as readable here
    /// as a success would be, not replaced by a generic "Failed".</summary>
    public string Result { get; set; } = "";
}
