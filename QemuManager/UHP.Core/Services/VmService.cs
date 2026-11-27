using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
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
    private readonly DiskRegistryStore _diskRegistry = new();

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
                _history.RecordInstant("Environment stopped", vm.Name, success: true, "Process exited.");
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
        // One-time, no-op after the first real run - see
        // DiskRegistryStore.MigrateIfNeeded's own remarks (PR #7
        // feedback: a regression the registry itself introduced,
        // disks already sitting in AppPaths.DisksDir before it existed
        // otherwise simply vanished from the Storage page).
        _diskRegistry.MigrateIfNeeded(loaded.Select(vm => vm.Workload.DiskPath));
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
            // happens, is DetectUnexpectedStop's own "Environment stopped" row, not
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

    public string? GetGdbEndpoint(EnvironmentConfig vm) => _engines.GetFor(vm).GetGdbEndpoint(vm.Id);

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

    // --- Phase 6: Host/Storage/Networking/Engines pages - see IVmService's
    // own remarks on each. ---

    private static string FormatBytes(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";

    private static string FormatFreeDiskSpace()
    {
        try
        {
            AppPaths.EnsureDirectories();
            var root = Path.GetPathRoot(AppPaths.AppDataDir);
            if (string.IsNullOrEmpty(root)) return "unknown";
            var drive = new DriveInfo(root);
            // DriveInfo.Name is "C:\" - the drive letter this free-space
            // figure is actually about (PR #7 feedback), not just a bare
            // number with no indication of which disk it is.
            return $"{FormatBytes(drive.AvailableFreeSpace)} free on {drive.Name}";
        }
        catch (Exception)
        {
            // A removable/mapped drive that disappeared, or a path
            // DriveInfo otherwise cannot resolve - "unknown" is honest;
            // this is not worth failing the whole Host page over.
            return "unknown";
        }
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); } catch (Exception) { return path; }
    }

    /// <summary>The real CPU model name (e.g. "AMD Ryzen 9 5900X 12-Core
    /// Processor"), from the same registry value Windows' own System
    /// Information/Task Manager read it from - PROCESSOR_IDENTIFIER (the
    /// env var this used before PR #7 feedback) only ever gives the
    /// family/model/stepping numbers, not this friendlier name.</summary>
    private static string GetCpuDescription()
    {
        // This app only ever runs on Windows (RuntimeIdentifier win-x64,
        // choco-installed Windows-only tools, WHPX) - CA1416 flags
        // Registry.GetValue as "Windows-only" because this project's own
        // TargetFramework is plain net8.0, not because there is any real
        // cross-platform risk here to fix.
#pragma warning disable CA1416
        var name = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
#pragma warning restore CA1416
        return $"{(string.IsNullOrWhiteSpace(name) ? "Unknown CPU" : name.Trim())}, {Environment.ProcessorCount} logical processors";
    }

    /// <summary>"Windows 11 Pro (build 26300)" - edition from the
    /// registry's own ProductName (e.g. "Windows 10 Pro"), but the
    /// "Windows 10"/"Windows 11" part recomputed from the real build
    /// number instead of trusted from that same string: an in-place
    /// upgrade past build 22000 (where Windows 11 actually starts) can
    /// leave ProductName still saying "Windows 10" even though the
    /// running OS already is Windows 11 in every way that matters here.</summary>
    private static string GetOsDescription()
    {
        var build = Environment.OSVersion.Version.Build;
        var windowsName = build >= 22000 ? "Windows 11" : "Windows 10";
#pragma warning disable CA1416 // this app only ever runs on Windows - see GetCpuDescription's own remarks
        var productName = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", null) as string ?? "";
#pragma warning restore CA1416
        var edition = Regex.Replace(productName, @"^Windows (10|11)\s*", "").Trim();
        return edition.Length > 0 ? $"{windowsName} {edition} (build {build})" : $"{windowsName} (build {build})";
    }

    public HostInfo GetHostInfo()
    {
        EnvironmentConfig[] snapshot;
        lock (_configsLock) snapshot = _configs.ToArray();

        var runningSnapshots = snapshot.Where(IsRunning).Select(GetSnapshot).ToList();

        return new HostInfo(
            GetOsDescription(),
            GetCpuDescription(),
            // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is total
            // installed physical RAM on a real machine (the standard
            // .NET technique for this since no direct cross-platform
            // "total RAM" API exists) - not this process's own usage.
            FormatBytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes),
            FormatFreeDiskSpace(),
            WhpxStatus.IsEnabled(),
            runningSnapshots.Count,
            snapshot.Length,
            runningSnapshots.Sum(s => s.CpuPercent),
            runningSnapshots.Sum(s => s.MemoryBytes));
    }

    public async Task<IReadOnlyList<EngineStatusInfo>> GetEngineStatusesAsync(AppSettings settings)
    {
        EnvironmentConfig[] snapshot;
        lock (_configsLock) snapshot = _configs.ToArray();

        var result = new List<EngineStatusInfo>();
        foreach (var engine in _engines.All)
        {
            var info = await engine.GetInstallStatusAsync(settings);
            var count = snapshot.Count(c => c.EngineId == engine.Id);
            result.Add(info with { EnvironmentCount = count });
        }
        return result;
    }

    public async Task<IReadOnlyList<DiskInfo>> GetDiskInventoryAsync(AppSettings settings)
    {
        EnvironmentConfig[] snapshot;
        lock (_configsLock) snapshot = _configs.ToArray();

        // Every Environment's own disk, grouped by normalized path so two
        // Environments that happen to point at the exact same file both
        // show up under one DiskInfo's own UsedByEnvironmentNames, not as
        // two separate rows for what is actually one file on disk.
        var usedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var rawPathByNormalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Track(string rawPath, string? environmentName)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return;
            var normalized = NormalizePath(rawPath);
            if (!usedBy.TryGetValue(normalized, out var names)) usedBy[normalized] = names = new List<string>();
            if (environmentName != null) names.Add(environmentName);
            rawPathByNormalized[normalized] = rawPath;
        }
        foreach (var vm in snapshot) Track(vm.Workload.DiskPath, vm.Name);

        // Every disk ever created via "Create disk" or added via "Add
        // existing disk" (_diskRegistry), regardless of where it
        // actually lives - not a scan of AppPaths.DisksDir, which could
        // never see a disk saved somewhere else and so silently lost
        // track of it (no longer "removable from the list" at all) the
        // moment its owning Environment was deleted. Stays visible here
        // whether or not any Environment currently references it.
        foreach (var path in _diskRegistry.LoadAll()) Track(path, null);

        var result = new List<DiskInfo>();
        foreach (var (normalized, rawPath) in rawPathByNormalized)
        {
            var exists = File.Exists(rawPath);
            string? format = null;
            long? virtualSize = null;
            if (exists && QemuLocator.LooksLikeQemuDir(settings.QemuDir))
                (format, virtualSize) = await DiskImageService.GetInfoAsync(settings.QemuDir, rawPath);

            // Size on disk is always the real file size (PR #7 feedback),
            // not qemu-img info's own "actual-size" - qemu-img is needed
            // for Format/VirtualSizeBytes (this app does not parse qcow2
            // itself), but "how many bytes does this file actually take"
            // has one correct answer regardless of whether QEMU happens
            // to be installed right now, and FileInfo.Length is always
            // that answer directly, for any format.
            long? actualSize = exists ? new FileInfo(rawPath).Length : null;

            result.Add(new DiskInfo(rawPath, exists, format, virtualSize, actualSize, usedBy[normalized]));
        }
        return result.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Both engines' NAT network is 10.0.2.0/24 - but with a different
    // degree of certainty each (PR #7 feedback): riscv-emu's net.c
    // hardcodes exactly these two addresses with no way to configure
    // anything else, so they are simply fact for it. QEMU's own user-mode
    // NAT only *defaults* to this network - it is QEMU's own long-
    // standing built-in default, but a real QEMU command line could
    // still override it (e.g. via Workload.ExtraArgs, which this app's
    // UI never inspects for that), so "(default)" is appended there
    // rather than stating it as a fact this app actually verified.
    private const string NatGuestAddress = "10.0.2.15";
    private const string NatGatewayAddress = "10.0.2.2";

    public IReadOnlyList<NetworkInfo> GetNetworkInventory()
    {
        EnvironmentConfig[] snapshot;
        lock (_configsLock) snapshot = _configs.ToArray();

        return snapshot.Select(vm =>
        {
            // Platform.NetworkMode's own convention (see its remarks):
            // "user" means NAT for either engine; anything else means no
            // NIC at all, including for riscv-emu, which has no other
            // backend choice of its own.
            var isNat = string.Equals(vm.Platform.NetworkMode, "user", StringComparison.OrdinalIgnoreCase);
            var isQemu = vm.EngineId == "Qemu";
            var suffix = isNat && isQemu ? " (default)" : "";
            return new NetworkInfo(
                vm.Name,
                _engines.GetFor(vm).DisplayName,
                isNat ? "NAT" : "none",
                isNat ? NatGuestAddress + suffix : null,
                isNat ? NatGatewayAddress + suffix : null);
        }).ToList();
    }

    /// <summary>Every Environment (by Name) whose Workload.DiskPath is
    /// this exact file - empty if none. Deliberately "used at all", not
    /// "used by a *running* one" - a disk attached to a stopped
    /// Environment could be started at any moment. Used both by
    /// RemoveDiskFromListAsync's own guard and by CreateDiskWindow's own
    /// "can this existing file ever be overwritten" check.</summary>
    public IReadOnlyList<string> GetEnvironmentsUsingDisk(string path)
    {
        var normalized = NormalizePath(path);
        EnvironmentConfig[] snap;
        lock (_configsLock) snap = _configs.ToArray();
        return snap.Where(c => string.Equals(NormalizePath(c.Workload.DiskPath ?? ""), normalized, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Name).ToList();
    }

    /// <summary>Un-registers path from the disk registry - it never
    /// touches the actual file (PR #7 feedback: an earlier version of
    /// this deleted it, which is a far more dangerous default than this
    /// page actually needs - "Open folder" is there for anyone who
    /// genuinely wants to delete the file themselves). Synchronous under
    /// the hood (plain JSON read/write, no engine or qemu-img call
    /// involved) - Task-returning only because IVmService.
    /// RemoveDiskFromListAsync is, matching every other potentially-slow
    /// action callers already await.</summary>
    public Task<string> RemoveDiskFromListAsync(string path)
    {
        string? Blocked()
        {
            var usedBy = GetEnvironmentsUsingDisk(path);
            return usedBy.Count > 0 ? $"Cannot remove: still used by {string.Join(", ", usedBy)}." : null;
        }

        var blocked = Blocked();
        if (blocked != null) return Task.FromResult(blocked);

        // Re-checked immediately before the actual removal, not only
        // once up front - closes the window for a real race against
        // another Save/StartAsync that attaches this exact disk to some
        // Environment in between the caller's own "is this safe to
        // offer" read (GetDiskInventoryAsync, well before whatever the
        // UI shows) and this call actually running.
        blocked = Blocked();
        if (blocked != null) return Task.FromResult(blocked);

        _diskRegistry.Remove(path);
        var message = $"Removed {path} from the list (file left untouched).";
        RecordTask("Remove Disk", path, true, message);
        return Task.FromResult(message);
    }

    /// <summary>Registers path in the disk registry - "Add existing
    /// disk" (Storage page) calls this directly for a disk the user
    /// already has; CreateDiskWindow also calls it right after a
    /// successful create, so "every disk created via Create disk is
    /// recorded regardless of location" holds without Create disk and
    /// Add existing disk needing two different code paths to get there.
    /// Refuses (returns a message, registers nothing) for a path that
    /// does not actually exist - there is nothing real to track yet.</summary>
    public Task<string> RegisterDiskAsync(string path)
    {
        if (!File.Exists(path))
        {
            var missing = $"{path} does not exist.";
            RecordTask("Add Disk", path, false, missing);
            return Task.FromResult(missing);
        }
        _diskRegistry.Add(path);
        var message = $"Added {path} to the disk list.";
        RecordTask("Add Disk", path, true, message);
        return Task.FromResult(message);
    }

    public void Dispose() => _statsTimer.Dispose();
}
