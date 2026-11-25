using System;
using System.Collections.Generic;
using System.Linq;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>See ITaskHistory's own remarks for the shape of this. Loads
/// whatever TaskHistoryStore already has on construction (so history
/// survives an app restart - part of the point), then keeps it in memory
/// (newest first) and persists the whole list again after every change -
/// the same "write immediately, no batching" choice VmStore already makes
/// for VM configs, reasonable here too since these actions are at most a
/// few per second, never a tight loop.</summary>
public class TaskHistoryService : ITaskHistory
{
    public const int MaxRecords = 200;

    private readonly List<TaskRecord> _records;
    private readonly object _lock = new();

    public TaskHistoryService()
    {
        _records = TaskHistoryStore.Load();
    }

    public IReadOnlyList<TaskRecord> GetRecent(int count = 200)
    {
        lock (_lock) return _records.Take(Math.Min(count, MaxRecords)).ToList();
    }

    public string BeginTask(string name, string target)
    {
        var record = new TaskRecord
        {
            Name = name,
            Target = target,
            Started = DateTime.Now,
            Status = "Running",
            Result = "Running...",
        };
        lock (_lock)
        {
            _records.Insert(0, record);
            Trim();
            TaskHistoryStore.Save(_records);
        }
        Changed?.Invoke();
        return record.Id;
    }

    public void CompleteTask(string id, bool success, string result)
    {
        lock (_lock)
        {
            var record = _records.FirstOrDefault(r => r.Id == id);
            if (record == null) return; // see ITaskHistory's own remarks - not expected in practice, never fatal if it somehow happened
            record.Completed = DateTime.Now;
            record.Status = success ? "Succeeded" : "Failed";
            record.Result = result;
            TaskHistoryStore.Save(_records);
        }
        Changed?.Invoke();
    }

    public void RecordInstant(string name, string target, bool success, string result)
    {
        var now = DateTime.Now;
        var record = new TaskRecord
        {
            Name = name,
            Target = target,
            Started = now,
            Completed = now,
            Status = success ? "Succeeded" : "Failed",
            Result = result,
        };
        lock (_lock)
        {
            _records.Insert(0, record);
            Trim();
            TaskHistoryStore.Save(_records);
        }
        Changed?.Invoke();
    }

    private void Trim()
    {
        if (_records.Count > MaxRecords)
            _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);
    }

    public event Action? Changed;
}
