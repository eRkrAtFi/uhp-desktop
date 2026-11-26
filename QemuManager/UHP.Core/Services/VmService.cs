using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>
/// The only place left that still knows "QEMU" and "riscv-emu" are two
/// different engines - everything above this (the WPF app today; a web API
/// or CLI tomorrow) talks to <see cref="IVmService"/> instead. See that
/// interface's own remarks for the overall shape. Also the only place that
/// still touches <see cref="EnvironmentStore"/> directly - the UI holds a
/// list of EnvironmentConfig it got from <see cref="LoadAll"/>, not an
/// EnvironmentStore of its own.
/// </summary>
public class VmService : IVmService, IDisposable
{
    private readonly EnvironmentStore _store = new();
    private readonly VmProcessManager _qemu = new();
    private readonly RiscvEmuProcessManager _riscv = new();

    // The one place a new engine gets registered (see
    // vision/VISION.md's roadmap item 5) - everything below looks an
    // engine up here by EnvironmentConfig instead of branching on
    // EnvironmentConfig.EngineId itself. _qemu/_riscv stay as typed fields
    // (not just entries in _engines) only because a couple of things
    // below are genuinely engine-specific rather than part of IEngine's
    // own common surface (riscv-emu's own StatsReceived/MIPS signal - no
    // other engine has anything like it, and IEngine does not need to
    // pretend otherwise).
    private readonly EngineRegistry _engines;

    // The configs this instance knows about - populated by LoadAll, kept in
    // sync by Save/Delete - so the stats timer below has something to poll
    // without the UI having to hand it a list every tick.
    private readonly List<EnvironmentConfig> _configs = new();
    private readonly object _configsLock = new();

    // riscv-emu's own "riscv-emu: stats ..." line can arrive many times a
    // second (tied to simulated instruction count, not wall-clock time) -
    // cached here rather than re-broadcast immediately, so StatsReceived
    // fires on one predictable ~2s cadence for every VM regardless of
    // engine. Cleared on process exit so a stopped VM's last-known MIPS
    // does not linger and get attached to a later, unrelated run.
    private readonly Dictionary<string, (ulong Instructions, double Mips)> _latestRiscvStats = new();

    private readonly Timer _statsTimer;
    private int _polling; // 0/1 guard against overlapping ticks if a QMP round trip runs long

    // "One console per VM" and "one in-flight action per VM" - both just a
    // HashSet.Add's own atomicity (true only for whoever adds the vmId
    // first), not anything WPF-specific: a ConsoleWindow still has to live
    // in the WPF project (it is one), but the yes/no decision of whether to
    // open a new one or just reuse what is already there belongs here,
    // where it can be tested without a window - see IVmService's own
    // remarks and QemuManager.SelfTest's dedicated test for this.
    private readonly HashSet<string> _activeConsoleSessions = new();
    private readonly object _consoleSessionLock = new();
    private readonly HashSet<string> _busyVmIds = new();
    private readonly object _busyLock = new();

    // Recent Tasks (Phase 4) - see ITaskHistory's own remarks. _lastKnownState
    // is this service's own copy of "what did GetState(vm) last report",
    // used only to detect a transition INTO Stopped nobody here asked for
    // (see DetectUnexpectedStop) - deliberately separate from VmListItem's
    // own State property (a WPF-layer concern this has no business
    // depending on).
    private readonly TaskHistoryService _history = new();
    private readonly Dictionary<string, VmState> _lastKnownState = new();
    private readonly object _lastKnownStateLock = new();

    // CPU history for the detail page's "last hour" graph (Phase 3) - kept
    // here, fed from the same PollStatsAsync tick StatsReceived already
    // uses, so it exists and keeps growing whether or not any window is
    // currently showing it (open the detail page an hour into a VM's
    // run and the graph is already full, not starting from empty).
    // 1800 entries at the ~2s polling cadence below = about an hour;
    // RingBuffer's own fixed capacity is what keeps a VM that runs for
    // days from growing this without bound.
    private const int CpuHistoryCapacity = 1800;
    private readonly Dictionary<string, RingBuffer<CpuSample>> _cpuHistory = new();
    private readonly object _cpuHistoryLock = new();

    // Console output, captured continuously for every VM regardless of
    // whether a ConsoleWindow is open - previously output only existed
    // for as long as *something* was subscribed to OutputReceived, so a
    // boot log that finished scrolling by before anyone thought to open
    // the console was gone for good. Cleared at the start of each
    // StartAsync, not kept across runs - "the boot log" means *this*
    // run's, not some stale one from hours ago. Same retention cap
    // ConsoleWindow's own local buffer already uses, applied here too
    // now that this is the buffer ConsoleWindow actually seeds itself
    // from (see GetConsoleBuffer).
    private const int MaxConsoleBufferChars = 500_000;
    private readonly Dictionary<string, StringBuilder> _consoleBuffers = new();
    private readonly object _consoleBuffersLock = new();

    public VmService()
    {
        _engines = new EngineRegistry(_qemu, _riscv);

        _riscv.StatsReceived += (vmId, s) => { lock (_latestRiscvStats) _latestRiscvStats[vmId] = (s.Instructions, s.Mips); };
        ProcessExited += vmId =>
        {
            lock (_latestRiscvStats) _latestRiscvStats.Remove(vmId);
            // A console session that outlives its own process (the window
            // is still open, showing the guest's last output) is fine and
            // deliberate - see ConsoleWindow's own remarks - so this does
            // NOT end the console session, only cleans up the stats cache.
            // The console *buffer* is left alone for the same reason -
            // the last thing the guest said (e.g. "Power down") needs to
            // stay readable after the process is already gone.
        };
        // Wired through _engines.All, not two separate per-engine
        // subscriptions - a third engine registered here later (see
        // EngineRegistry's own remarks) gets these for free.
        foreach (var engine in _engines.All)
        {
            engine.OutputReceived += AppendToConsoleBuffer;
            // Wired directly to each engine's own event, not through this
            // class's own StateChanged property below - that property has
            // no backing delegate of its own (its add/remove just forward
            // to each registered engine directly), so there is nothing for
            // this class itself to subscribe to there even though external
            // callers (MainWindow) can.
            engine.StateChanged += DetectUnexpectedStop;
        }
        _statsTimer = new Timer(async _ => await PollStatsAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>Recent Tasks' own record of a VM's process exiting without
    /// this app itself having asked it to - closing a QEMU gtk/sdl window
    /// directly, a crash, or a graceful ACPI shutdown (StartAsync/
    /// ShutdownAsync/ForceStop's own task-recording already ends the
    /// moment its own call returns, long before the guest's ACPI request
    /// actually finishes) completing well after the fact. Fires on every
    /// StateChanged, for either engine - the event itself carries no vmId,
    /// so every currently-known VM's own last-seen state has to be
    /// compared, the same thing MainWindow's own RefreshRunStatesFromExternalEvent
    /// already did before this moved here (Recent Tasks needing to be
    /// "história v jadre" - Core's own history, not the WPF layer's).</summary>
    private void DetectUnexpectedStop()
    {
        EnvironmentConfig[] snapshot;
        lock (_configsLock) snapshot = _configs.ToArray();
        foreach (var vm in snapshot)
        {
            var newState = GetState(vm);
            VmState oldState;
            lock (_lastKnownStateLock)
            {
                _lastKnownState.TryGetValue(vm.Id, out oldState); // default VmState.Stopped if never seen - a VM reattached already-Running at startup just records that as its first-known state, no false "it just stopped"
                _lastKnownState[vm.Id] = newState;
            }
            if (oldState != VmState.Stopped && newState == VmState.Stopped && !IsActionInProgress(vm.Id))
                _history.RecordInstant("VM stopped", vm.Name, success: true, "Process exited.");
        }
    }

    private void AppendToConsoleBuffer(string vmId, byte[] data)
    {
        // Raw bytes decoded 1:1 (Latin1), not UTF-8 - same choice
        // ConsoleWindow's own OnOutputReceived already makes, for the
        // same reason: an arbitrary byte from the guest must never throw
        // a decoding exception on this path.
        var text = Encoding.Latin1.GetString(data);
        lock (_consoleBuffersLock)
        {
            if (!_consoleBuffers.TryGetValue(vmId, out var sb))
                _consoleBuffers[vmId] = sb = new StringBuilder();
            sb.Append(text);
            if (sb.Length > MaxConsoleBufferChars)
                sb.Remove(0, sb.Length - MaxConsoleBufferChars);
        }
    }

    /// <summary>Everything captured for this VM since its current run
    /// started (or since the app started, if it was already running and
    /// got reattached) - what a newly-opened ConsoleWindow seeds its own
    /// view from, so a boot log that happened before the window existed
    /// is still there to read.</summary>
    public string GetConsoleBuffer(string vmId)
    {
        lock (_consoleBuffersLock)
            return _consoleBuffers.TryGetValue(vmId, out var sb) ? sb.ToString() : "";
    }

    /// <summary>The last few lines of GetConsoleBuffer, for the detail
    /// page's own console preview tile - trailing newline-split, empty
    /// lines included (a blank line in the real console output is still
    /// a real line), never more than <paramref name="lines"/> of them.</summary>
    public string GetConsolePreview(string vmId, int lines = 15)
    {
        var full = GetConsoleBuffer(vmId);
        if (full.Length == 0) return "";
        var parts = full.Split('\n');
        return string.Join('\n', parts.Skip(Math.Max(0, parts.Length - lines)));
    }

    /// <summary>Up to the last hour of host-CPU% samples for this VM,
    /// oldest first - what the detail page's "Performance" graph plots.
    /// Empty for a VM that has never been polled while running (never
    /// started this session, or not running long enough to have been
    /// polled even once).</summary>
    public IReadOnlyList<CpuSample> GetCpuHistory(string vmId)
    {
        lock (_cpuHistoryLock)
            return _cpuHistory.TryGetValue(vmId, out var buf) ? buf.ToList() : Array.Empty<CpuSample>();
    }

    /// <summary>Claims the one console session a VM may have open at a
    /// time. Returns true only for the first caller - every later call for
    /// the same vmId returns false until <see cref="EndConsoleSession"/>
    /// releases it, so the caller knows to activate whatever is already
    /// showing instead of opening a second one. Atomic (a lock around
    /// HashSet.Add, which itself reports whether the item was new) - safe
    /// even if two callers somehow raced to get here, not just correct in
    /// the single-threaded case a WPF dictionary check-then-assign already
    /// was.</summary>
    public bool TryBeginConsoleSession(string vmId)
    {
        lock (_consoleSessionLock) return _activeConsoleSessions.Add(vmId);
    }

    public void EndConsoleSession(string vmId)
    {
        lock (_consoleSessionLock) _activeConsoleSessions.Remove(vmId);
    }

    /// <summary>Same idea as console sessions, for "an action (Power On/
    /// Shut Down/Force Stop/Delete) is already running for this VM, ignore
    /// another click" - claimed at the start of StartAsync/ShutdownAsync/
    /// ForceStop/Delete and released when each finishes (see those
    /// methods). The UI also disables the corresponding button while this
    /// is true (see RefreshToolbarState), but this is the actual guard - a
    /// disabled button is a UI nicety on top of it, not a substitute for
    /// it.</summary>
    public bool TryBeginAction(string vmId)
    {
        lock (_busyLock) return _busyVmIds.Add(vmId);
    }

    public void EndAction(string vmId)
    {
        lock (_busyLock) _busyVmIds.Remove(vmId);
    }

    public bool IsActionInProgress(string vmId)
    {
        lock (_busyLock) return _busyVmIds.Contains(vmId);
    }

    public event Action? StateChanged
    {
        add { foreach (var e in _engines.All) e.StateChanged += value; }
        remove { foreach (var e in _engines.All) e.StateChanged -= value; }
    }

    public event Action<string, byte[]>? OutputReceived
    {
        add { foreach (var e in _engines.All) e.OutputReceived += value; }
        remove { foreach (var e in _engines.All) e.OutputReceived -= value; }
    }

    public event Action<string>? ProcessExited
    {
        add { foreach (var e in _engines.All) e.ProcessExited += value; }
        remove { foreach (var e in _engines.All) e.ProcessExited -= value; }
    }

    public event Action<string, VmStats>? StatsReceived;

    public IReadOnlyList<EnvironmentConfig> LoadAll()
    {
        var loaded = _store.LoadAll();
        foreach (var vm in loaded) TryReattach(vm);
        lock (_configsLock) { _configs.Clear(); _configs.AddRange(loaded); }
        return loaded;
    }

    public void Save(EnvironmentConfig vm)
    {
        _store.Save(vm);
        lock (_configsLock) { if (!_configs.Any(c => c.Id == vm.Id)) _configs.Add(vm); }
    }

    public void Delete(EnvironmentConfig vm)
    {
        _store.Delete(vm);
        lock (_configsLock) { _configs.RemoveAll(c => c.Id == vm.Id); }
    }

    public EnvironmentConfig CreateDraft(string kind) => _engines.Get(kind).CreateDraft();

    public IReadOnlyList<ConfigField> GetConfigSchema(string engineId) => _engines.Get(engineId).GetConfigSchema();

    public IReadOnlyList<string> ListArchitectures(string engineId, AppSettings settings) => _engines.Get(engineId).ListArchitectures(settings);

    public EngineCapabilities GetCapabilities(string engineId) => _engines.Get(engineId).Capabilities;

    public VmState GetState(EnvironmentConfig vm) => _engines.GetFor(vm).GetVmState(vm.Id);

    public bool IsRunning(EnvironmentConfig vm) => _engines.GetFor(vm).IsRunning(vm.Id);

    public VmSnapshot GetSnapshot(EnvironmentConfig vm)
    {
        var engine = _engines.GetFor(vm);
        var usage = engine.GetResourceUsage(vm.Id);
        var uptime = engine.GetUptime(vm.Id);
        return new VmSnapshot(GetState(vm), usage?.cpuPercent ?? 0, usage?.memoryBytes ?? 0, uptime);
    }

    public Task<string?> GetGuestDetailAsync(EnvironmentConfig vm) => _engines.GetFor(vm).GetGuestDetailAsync(vm);

    public VmHardwareDescription GetHardwareDescription(EnvironmentConfig vm) => _engines.GetFor(vm).GetHardwareDescription(vm);

    public async Task<string> StartAsync(EnvironmentConfig vm, AppSettings settings)
    {
        // One action at a time per VM, not just "don't let Power On race
        // itself": Power On/Shut Down/Force Stop/Delete all end up calling
        // into the same per-engine manager's _running dictionary for this
        // vmId, and two of those running concurrently (e.g. Shut Down
        // firing while Start has not finished yet) is exactly the kind of
        // race a double-click (on the same button or a different one)
        // could trigger - see IVmService's own remarks and
        // QemuManager.SelfTest's dedicated test for this.
        if (!TryBeginAction(vm.Id)) return "Another action is already in progress for this VM.";
        var taskId = _history.BeginTask("Power On", vm.Name);
        try
        {
            // A fresh run's own boot log, not whatever was left over from
            // a previous one hours ago - see GetConsoleBuffer's own
            // remarks on why this buffer is not cleared on exit (the
            // guest's last words stay readable), only here, on the next
            // actual start.
            lock (_consoleBuffersLock) _consoleBuffers.Remove(vm.Id);

            // PollStatsAsync only iterates _configs, not "whatever is
            // running right now" - normally every VM reaches here via
            // LoadAll or Save first (that's how the real app's "New VM"/
            // startup flow always works), but StartAsync should not
            // silently drop stats just because some caller started a VM it
            // never explicitly saved. The same dedup Save already does, so
            // starting an already-known VM twice is still a no-op here.
            lock (_configsLock) { if (!_configs.Any(c => c.Id == vm.Id)) _configs.Add(vm); }
            var message = await _engines.GetFor(vm).StartAsync(vm, settings);
            // Success is read from the VM's own resulting state, not from
            // parsing the message string - the actual source of truth for
            // whether this worked, and the same check regardless of which
            // engine's own wording produced the message.
            _history.CompleteTask(taskId, IsRunning(vm), message);
            return message;
        }
        finally { EndAction(vm.Id); }
    }

    public async Task<ShutdownResult> ShutdownAsync(EnvironmentConfig vm, AppSettings settings)
    {
        if (!TryBeginAction(vm.Id)) return new ShutdownResult("Another action is already in progress for this VM.", false);
        var taskId = _history.BeginTask("Shut Down", vm.Name);
        try
        {
            var result = await _engines.GetFor(vm).ShutdownAsync(vm, settings);
            // TimedOut is the real success/failure signal here (see
            // ShutdownResult's own remarks) - always false for QEMU, which
            // only ever reports "the ACPI request was sent", not whether
            // the guest actually powered off; that part, if/when it
            // happens, is DetectUnexpectedStop's own "VM stopped" row, not
            // this task completing late.
            _history.CompleteTask(taskId, !result.TimedOut, result.Message);
            return result;
        }
        finally { EndAction(vm.Id); }
    }

    public string ForceStop(EnvironmentConfig vm)
    {
        if (!TryBeginAction(vm.Id)) return "Another action is already in progress for this VM.";
        try
        {
            var message = _engines.GetFor(vm).ForceStop(vm);
            // No BeginTask/CompleteTask pair - Force Stop is near-instant
            // (Process.Kill, not an awaited guest-side negotiation), so
            // RecordInstant's "started and completed at once" fits better
            // than a Running row that would never actually be visible.
            _history.RecordInstant("Force Stop", vm.Name, !IsRunning(vm), message);
            return message;
        }
        finally { EndAction(vm.Id); }
    }

    public IReadOnlyList<Models.TaskRecord> GetRecentTasks(int count = 200) => _history.GetRecent(count);

    public void RecordTask(string name, string target, bool success, string result) =>
        _history.RecordInstant(name, target, success, result);

    public event Action? TasksChanged
    {
        add => _history.Changed += value;
        remove => _history.Changed -= value;
    }

    // A VM ID belongs to exactly one registered engine's own _running
    // dictionary; every other engine just finds nothing for that ID and
    // no-ops (see VmProcessManager/RiscvEmuProcessManager.SendInput).
    // Forwarding to every registered engine unconditionally is correct,
    // not a shortcut - it avoids this service needing its own vmId-to-
    // engine lookup table that would only ever duplicate what each
    // engine's own _running dictionary already is.
    public void SendInput(string vmId, byte[] data)
    {
        foreach (var engine in _engines.All) engine.SendInput(vmId, data);
    }

    public (ConsoleAvailability kind, string? detail) GetConsoleAvailability(EnvironmentConfig vm) =>
        _engines.GetFor(vm).GetConsoleAvailability(vm);

    public bool TryReattach(EnvironmentConfig vm) => _engines.GetFor(vm).TryReattach(vm);

    private async Task PollStatsAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return; // previous tick still running (e.g. a slow QMP round trip) - skip rather than overlap
        try
        {
            EnvironmentConfig[] snapshot;
            lock (_configsLock) snapshot = _configs.ToArray();

            foreach (var vm in snapshot)
            {
                if (!IsRunning(vm)) continue;

                var snap = GetSnapshot(vm);
                var detail = await GetGuestDetailAsync(vm);
                (ulong, double)? riscv;
                lock (_latestRiscvStats) riscv = _latestRiscvStats.TryGetValue(vm.Id, out var r) ? r : null;

                lock (_cpuHistoryLock)
                {
                    if (!_cpuHistory.TryGetValue(vm.Id, out var buf))
                        _cpuHistory[vm.Id] = buf = new RingBuffer<CpuSample>(CpuHistoryCapacity);
                    buf.Add(new CpuSample(DateTime.UtcNow, snap.CpuPercent));
                }

                StatsReceived?.Invoke(vm.Id, new VmStats(
                    snap.State, snap.CpuPercent, snap.MemoryBytes, snap.Uptime, detail,
                    riscv?.Item1, riscv?.Item2));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    public void Dispose() => _statsTimer.Dispose();
}
