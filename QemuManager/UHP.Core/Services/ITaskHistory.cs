using System;
using System.Collections.Generic;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>Recent Tasks as a real, persisted history of actions - every
/// Power On/Shut Down/Force Stop, VM create/edit/delete, disk create, and
/// a VM's process exiting on its own - instead of the free-text line-per-
/// action feed it replaces. Implemented by TaskHistoryService; IVmService
/// exposes the same surface (GetRecentTasks/RecordTask/TasksChanged) so a
/// caller never needs a second object just for this.</summary>
public interface ITaskHistory
{
    /// <summary>Most recent first, capped at <paramref name="count"/> (and
    /// at TaskHistoryService.MaxRecords regardless, even if a caller asks
    /// for more).</summary>
    IReadOnlyList<TaskRecord> GetRecent(int count = 200);

    /// <summary>Starts a task that has a real "in progress" phase worth
    /// showing (Power On, Shut Down) - the returned id is what
    /// <see cref="CompleteTask"/> needs to fill in the same row once the
    /// operation finishes, rather than appending a second, separate one.</summary>
    string BeginTask(string name, string target);

    /// <summary>Finishes a task <see cref="BeginTask"/> started - sets
    /// Completed/Status/Result on that same row. A no-op if id does not
    /// match any current record (e.g. it aged out of MaxRecords in the
    /// meantime, which cannot really happen in practice given how briefly
    /// a task stays "Running", but silently doing nothing is still the
    /// right behavior if it ever did).</summary>
    void CompleteTask(string id, bool success, string result);

    /// <summary>One-shot actions with no meaningful "in progress" phase to
    /// show (Force Stop, Create/Edit/Delete VM, Create Disk) or events this
    /// app detected rather than initiated (a VM's process exiting on its
    /// own) - records Started and Completed as the same instant.</summary>
    void RecordInstant(string name, string target, bool success, string result);

    /// <summary>Fires after every BeginTask/CompleteTask/RecordInstant -
    /// the UI's own cue to re-read GetRecent and rebind, rather than
    /// trying to patch one row of an already-bound collection in place.</summary>
    event Action? Changed;
}
