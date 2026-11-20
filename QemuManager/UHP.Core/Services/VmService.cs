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
/// still touches <see cref="VmStore"/> directly - the UI holds a list of
/// VmConfig it got from <see cref="LoadAll"/>, not a VmStore of its own.
/// </summary>
public class VmService : IVmService, IDisposable
{
    private readonly VmStore _store = new();
    private readonly VmProcessManager _qemu = new();
    private readonly RiscvEmuProcessManager _riscv = new();

    // The configs this instance knows about - populated by LoadAll, kept in
    // sync by Save/Delete - so the stats timer below has something to poll
    // without the UI having to hand it a list every tick.
    private readonly List<VmConfig> _configs = new();
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
        _qemu.OutputReceived += AppendToConsoleBuffer;
        _riscv.OutputReceived += AppendToConsoleBuffer;
        _statsTimer = new Timer(async _ => await PollStatsAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
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
        add { _qemu.StateChanged += value; _riscv.StateChanged += value; }
        remove { _qemu.StateChanged -= value; _riscv.StateChanged -= value; }
    }

    public event Action<string, byte[]>? OutputReceived
    {
        add { _qemu.OutputReceived += value; _riscv.OutputReceived += value; }
        remove { _qemu.OutputReceived -= value; _riscv.OutputReceived -= value; }
    }

    public event Action<string>? ProcessExited
    {
        add { _qemu.ProcessExited += value; _riscv.ProcessExited += value; }
        remove { _qemu.ProcessExited -= value; _riscv.ProcessExited -= value; }
    }

    public event Action<string, VmStats>? StatsReceived;

    private static bool IsRiscv(VmConfig vm) => vm.Kind == "RiscvEmu";

    public IReadOnlyList<VmConfig> LoadAll()
    {
        var loaded = _store.LoadAll();
        foreach (var vm in loaded) TryReattach(vm);
        lock (_configsLock) { _configs.Clear(); _configs.AddRange(loaded); }
        return loaded;
    }

    public void Save(VmConfig vm)
    {
        _store.Save(vm);
        lock (_configsLock) { if (!_configs.Any(c => c.Id == vm.Id)) _configs.Add(vm); }
    }

    public void Delete(VmConfig vm)
    {
        _store.Delete(vm);
        lock (_configsLock) { _configs.RemoveAll(c => c.Id == vm.Id); }
    }

    public VmConfig CreateDraft(string kind)
    {
        var vm = new VmConfig { Kind = kind };
        // VmConfig.MemoryMB's class default (2048) is sized for QEMU;
        // riscv-emu gets its own, smaller sensible default here instead of
        // silently inheriting that one.
        if (kind == "RiscvEmu") vm.MemoryMB = 512;
        return vm;
    }

    public VmState GetState(VmConfig vm)
    {
        if (IsRiscv(vm)) return _riscv.GetState(vm.Id) switch
        {
            RiscvEmuRunState.Running => VmState.Running,
            RiscvEmuRunState.Stopping => VmState.Stopping,
            _ => VmState.Stopped,
        };
        return _qemu.GetState(vm.Id) switch
        {
            VmRunState.Starting => VmState.Starting,
            VmRunState.Running => VmState.Running,
            VmRunState.Stopping => VmState.Stopping,
            _ => VmState.Stopped,
        };
    }

    public bool IsRunning(VmConfig vm) => IsRiscv(vm) ? _riscv.IsRunning(vm.Id) : _qemu.IsRunning(vm.Id);

    public VmSnapshot GetSnapshot(VmConfig vm)
    {
        var usage = IsRiscv(vm) ? _riscv.GetResourceUsage(vm.Id) : _qemu.GetResourceUsage(vm.Id);
        var uptime = IsRiscv(vm) ? _riscv.GetUptime(vm.Id) : _qemu.GetUptime(vm.Id);
        return new VmSnapshot(GetState(vm), usage?.cpuPercent ?? 0, usage?.memoryBytes ?? 0, uptime);
    }

    public async Task<string?> GetGuestDetailAsync(VmConfig vm)
    {
        // riscv-emu has no QMP-equivalent control channel to ask (see
        // RiscvEmuProcessManager's own class remarks) - this is the one
        // engine-specific gap the interface's doc comment calls out by
        // name, not a difference the caller has to know about itself.
        if (IsRiscv(vm)) return null;
        if (_qemu.GetState(vm.Id) != VmRunState.Running) return null;
        return await _qemu.QueryGuestStatusAsync(vm);
    }

    public VmHardwareDescription GetHardwareDescription(VmConfig vm)
    {
        var disk = string.IsNullOrWhiteSpace(vm.DiskPath) ? "None" : vm.DiskPath;
        var network = vm.NetworkMode == "user"
            ? (IsRiscv(vm) ? "virtio-net (user-mode NAT)" : "User-mode NAT")
            : "None";

        if (IsRiscv(vm))
        {
            return new VmHardwareDescription(
                EngineDisplayName: "riscv-emu",
                Architecture: "riscv64",
                CpuDescription: "1 hart (fixed - no SMP yet)",
                MemoryDescription: $"{vm.MemoryMB} MB",
                DiskDescription: disk == "None" ? disk : $"{disk} (virtio-blk)",
                NetworkDescription: network,
                ConsoleDescription: "Serial (UART)",
                AcceleratorDescription: "Interpreter (this project's own CPU emulation)",
                SupportsGuestDetail: false,
                SupportsSpeedMetric: true);
        }

        var console = vm.Display switch
        {
            "console" => "Serial (text, in this app)",
            "vnc" => "VNC (headless)",
            _ => $"{vm.Display} window (QEMU's own)",
        };
        return new VmHardwareDescription(
            EngineDisplayName: "QEMU",
            Architecture: vm.Architecture,
            CpuDescription: $"{vm.CpuCores} core{(vm.CpuCores == 1 ? "" : "s")}",
            MemoryDescription: $"{vm.MemoryMB} MB",
            DiskDescription: disk,
            NetworkDescription: network,
            ConsoleDescription: console,
            AcceleratorDescription: vm.Accelerator,
            SupportsGuestDetail: true,
            SupportsSpeedMetric: false);
    }

    public async Task<string> StartAsync(VmConfig vm, AppSettings settings)
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
            return IsRiscv(vm) ? _riscv.Start(vm, settings.RiscvEmuDir) : await _qemu.StartAsync(vm, settings.QemuDir);
        }
        finally { EndAction(vm.Id); }
    }

    public async Task<ShutdownResult> ShutdownAsync(VmConfig vm, AppSettings settings)
    {
        if (!TryBeginAction(vm.Id)) return new ShutdownResult("Another action is already in progress for this VM.", false);
        try
        {
            if (IsRiscv(vm))
            {
                var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.RiscvShutdownTimeoutSeconds));
                return await _riscv.ShutdownGracefullyAsync(vm, timeout);
            }
            return new ShutdownResult(await _qemu.StopGracefullyAsync(vm), false);
        }
        finally { EndAction(vm.Id); }
    }

    public string ForceStop(VmConfig vm)
    {
        if (!TryBeginAction(vm.Id)) return "Another action is already in progress for this VM.";
        try { return IsRiscv(vm) ? _riscv.ForceStop(vm) : _qemu.ForceStop(vm); }
        finally { EndAction(vm.Id); }
    }

    // A VM ID belongs to exactly one engine's _running dictionary; the
    // other engine's manager just finds nothing for that ID and no-ops
    // (see VmProcessManager/RiscvEmuProcessManager.SendInput). Forwarding
    // to both unconditionally is correct, not a shortcut - it avoids this
    // service needing its own vmId-to-engine lookup table that would only
    // ever duplicate what each manager's own _running dictionary already is.
    public void SendInput(string vmId, byte[] data)
    {
        _qemu.SendInput(vmId, data);
        _riscv.SendInput(vmId, data);
    }

    public (ConsoleAvailability kind, string? detail) GetConsoleAvailability(VmConfig vm)
    {
        if (IsRiscv(vm))
            return _riscv.IsRunning(vm.Id) ? (ConsoleAvailability.Stream, null) : (ConsoleAvailability.NotRunning, null);

        if (!_qemu.IsRunning(vm.Id)) return (ConsoleAvailability.NotRunning, null);
        if (vm.Display == "vnc") return (ConsoleAvailability.Vnc, _qemu.GetVncEndpoint(vm.Id));
        if (vm.Display == "console") return (ConsoleAvailability.Stream, null);
        return (ConsoleAvailability.ExternalWindow, null);
    }

    public bool TryReattach(VmConfig vm) => IsRiscv(vm) ? _riscv.TryReattach(vm) : _qemu.TryReattach(vm);

    private async Task PollStatsAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return; // previous tick still running (e.g. a slow QMP round trip) - skip rather than overlap
        try
        {
            VmConfig[] snapshot;
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
