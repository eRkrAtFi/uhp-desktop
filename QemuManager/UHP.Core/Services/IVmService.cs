using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>ShutdownAsync's own result. TimedOut is what the caller uses
/// to decide whether to offer Force Stop - see ShutdownAsync's own
/// remarks for why it is never acted on automatically.</summary>
public readonly record struct ShutdownResult(string Message, bool TimedOut);

/// <summary>
/// The one thing a UI (or, eventually, a web API or CLI) needs to manage
/// and drive VMs of either engine - QEMU or riscv-emu - without ever
/// having to ask "which one is this?" itself, and without holding a
/// VmStore or either per-engine manager directly. Every place that
/// question used to get answered with an if/else in MainWindow.xaml.cs
/// now gets answered once, here, inside <see cref="VmService"/>.
///
/// Extends <see cref="IConsoleSource"/> so the same instance doubles as the
/// console source every <see cref="ConsoleWindow"/>-equivalent view needs
/// (SendInput/ForceStop/OutputReceived/ProcessExited), instead of the
/// caller having to pick which underlying per-engine manager owns a given
/// VM - both managers safely no-op for a VM ID they do not recognize
/// (TryGetValue-and-return), so forwarding a call to both unconditionally
/// is correct, not just convenient.
/// </summary>
public interface IVmService : IConsoleSource
{
    // --- VM list management (was VmStore, called directly by MainWindow) ---

    /// <summary>Every persisted VM, freshly loaded from disk, with any that
    /// were left running by a previous instance of this app re-adopted
    /// (see TryReattach) before being returned.</summary>
    IReadOnlyList<EnvironmentConfig> LoadAll();

    void Save(EnvironmentConfig vm);
    void Delete(EnvironmentConfig vm);

    /// <summary>A new EnvironmentConfig of the given Kind, with that engine's own
    /// sensible defaults already applied (e.g. riscv-emu's 512 MB default,
    /// which is sized for it, not inherited from EnvironmentConfig's own class
    /// default sized for QEMU).</summary>
    EnvironmentConfig CreateDraft(string kind);

    /// <summary>engineId's own configuration schema - see
    /// IEngine.GetConfigSchema's own remarks. What EnvironmentEditWindow
    /// renders instead of VmEditWindow/RiscvEmuEditWindow's own hand-laid-
    /// out, per-engine XAML.</summary>
    IReadOnlyList<Models.ConfigField> GetConfigSchema(string engineId);

    /// <summary>engineId's own architecture list (QEMU: whatever
    /// qemu-system-*.exe binaries settings.QemuDir actually has; riscv-emu:
    /// always exactly one, fixed).</summary>
    IReadOnlyList<string> ListArchitectures(string engineId, AppSettings settings);

    /// <summary>engineId's own static capabilities - see
    /// EngineCapabilities' own remarks.</summary>
    Models.EngineCapabilities GetCapabilities(string engineId);

    // --- Engine operations ---

    /// <summary>A VM's engine-agnostic run state right now.</summary>
    VmState GetState(EnvironmentConfig vm);
    bool IsRunning(EnvironmentConfig vm);

    /// <summary>Real host-side CPU%/memory/uptime for the VM's actual OS
    /// process. All zero/null fields when not running. Prefer subscribing
    /// to StatsReceived for live updates over polling this in a loop - see
    /// its own remarks for why.</summary>
    VmSnapshot GetSnapshot(EnvironmentConfig vm);

    /// <summary>Extra live detail beyond State that only QEMU can report
    /// (over QMP, e.g. "running" vs "paused") - null for a riscv-emu VM
    /// (no QMP-equivalent channel exists) and for a VM that is not
    /// currently Running.</summary>
    Task<string?> GetGuestDetailAsync(EnvironmentConfig vm);

    /// <summary>Display-ready hardware facts (CPU, memory, disk, network,
    /// console, accelerator) for this VM's Kind - see
    /// VmHardwareDescription's own remarks. Feeds Phase 3's "Hardware"
    /// card; today's grid/toolbar do not need it.</summary>
    VmHardwareDescription GetHardwareDescription(EnvironmentConfig vm);

    Task<string> StartAsync(EnvironmentConfig vm, AppSettings settings);

    /// <summary>Graceful shutdown. For QEMU: ACPI power-down over QMP,
    /// fire-and-forget (QEMU itself reports no further progress on it
    /// over any channel this app reads, so there is nothing to wait on
    /// here beyond the request being sent). For riscv-emu: types
    /// Workload.ShutdownCommand at the guest's own console and genuinely
    /// waits (up to settings.RiscvShutdownTimeoutSeconds) for the real
    /// process to exit on its own - see
    /// RiscvEmuProcessManager.ShutdownGracefullyAsync's own remarks for
    /// why this never auto-escalates to Force Stop by itself.
    /// ShutdownResult.TimedOut is false for QEMU always (sending the
    /// ACPI request either succeeded or it did not - "the guest is still
    /// deciding" is not a timeout in the same sense) and is what the UI
    /// uses to decide whether to offer Force Stop for a riscv-emu VM that
    /// did not respond in time.</summary>
    Task<ShutdownResult> ShutdownAsync(EnvironmentConfig vm, AppSettings settings);

    /// <summary>What clicking "Console" on this VM should do right now -
    /// see <see cref="ConsoleAvailability"/>. The second value is the VNC
    /// endpoint ("host:port") when the first is Vnc, otherwise null.</summary>
    (ConsoleAvailability kind, string? detail) GetConsoleAvailability(EnvironmentConfig vm);

    /// <summary>The "host:port" a running VM's GDB stub (Debug.GdbEnabled)
    /// is listening on, or null if that was not set or it is not running.</summary>
    string? GetGdbEndpoint(EnvironmentConfig vm);

    /// <summary>Re-adopts this VM's OS process if a previous run of this
    /// app (or this one, after a crash) left it running - see
    /// VmProcessManager/RiscvEmuProcessManager.TryReattach for how each
    /// engine does this. Returns true if the VM is now tracked as running,
    /// whether that is because it was just reattached or already was.</summary>
    bool TryReattach(EnvironmentConfig vm);

    // --- Idempotency guards (bug: a double-click on Console, Power On,
    // Shut Down, or Force Stop could race before the UI alone noticed -
    // see VmService's own remarks on why each guard lives here rather than
    // in MainWindow's own dictionary/flag) ---

    /// <summary>Claims the one console session a VM may have open at a
    /// time - returns true only for the first caller for a given vmId;
    /// every later call returns false until <see cref="EndConsoleSession"/>
    /// releases it. The caller (MainWindow.OpenConsoleWindow) uses this to
    /// decide "create a new ConsoleWindow" vs. "just activate the existing
    /// one" - the window itself has to live in the WPF project, but this
    /// decision does not, and is tested without one.</summary>
    bool TryBeginConsoleSession(string vmId);
    void EndConsoleSession(string vmId);

    /// <summary>True while a Power On/Shut Down/Force Stop/Delete is
    /// already running for this VM - StartAsync/ShutdownAsync/ForceStop
    /// all claim and release this internally, so a second call for the
    /// same VM while one is in flight gets turned away with a message
    /// instead of running concurrently. The UI also disables the
    /// corresponding buttons while this is true (see
    /// MainWindow.RefreshToolbarState), but this is the real guard, not
    /// just a UI nicety on top of it.</summary>
    bool IsActionInProgress(string vmId);

    // --- History (Phase 3's Performance/Console-preview cards) - kept
    // here, fed continuously regardless of whether any window is open
    // to look at it, not recomputed/re-captured only once a window
    // happens to ask - see VmService's own remarks on each. ---

    /// <summary>Up to the last hour of host-CPU% samples for this VM,
    /// oldest first.</summary>
    IReadOnlyList<CpuSample> GetCpuHistory(string vmId);

    /// <summary>Everything captured on this VM's console since its
    /// current run started.</summary>
    string GetConsoleBuffer(string vmId);

    /// <summary>The last few lines of <see cref="GetConsoleBuffer"/>.</summary>
    string GetConsolePreview(string vmId, int lines = 15);

    // --- Recent Tasks (Phase 4) - a real, persisted history of actions,
    // not the old free-text line-per-action feed. StartAsync/ShutdownAsync/
    // ForceStop already record their own task internally (begin on entry,
    // complete once the result is known) - RecordTask is for the caller's
    // own one-shot actions (Create/Edit/Delete VM, Create Disk) that have
    // no meaningful "in progress" phase of their own, and for anything
    // else worth a row (e.g. a blocked Edit/Delete because the VM is still
    // running). A VM's process exiting on its own is recorded internally
    // too (see VmService's own unexpected-stop detector) - no caller
    // action needed for that one. ---

    /// <summary>Most recent first, capped at 200 regardless of what is
    /// asked for.</summary>
    IReadOnlyList<Models.TaskRecord> GetRecentTasks(int count = 200);

    /// <summary>Records a one-shot action with no "in progress" phase
    /// worth showing - see this section's own remarks for which ones.</summary>
    void RecordTask(string name, string target, bool success, string result);

    /// <summary>Fires after any task is recorded or completed, including
    /// StartAsync/ShutdownAsync/ForceStop's own internal recording and the
    /// unexpected-stop detector - the UI's cue to re-read GetRecentTasks
    /// and rebind, same as ITaskHistory.Changed (which this forwards).</summary>
    event Action? TasksChanged;

    // --- Events ---

    /// <summary>Raised whenever any VM's run state changes, for either
    /// engine - the UI re-reads GetState/IsRunning for whatever it cares
    /// about rather than this event carrying a payload.</summary>
    event Action? StateChanged;

    /// <summary>Pushed roughly every 2 seconds for every currently-running
    /// VM (both engines, one unified cadence) - CPU%/memory/uptime/guest
    /// detail for all of them, plus the latest MIPS/instruction-count
    /// sample for a riscv-emu VM if one has arrived since the last push.
    /// riscv-emu's own stats line can arrive far more often than 2s (it is
    /// tied to simulated instruction count, not wall-clock time) - this
    /// event deliberately does not re-broadcast every single one of those;
    /// it caches the latest and folds it into the next regular tick, so a
    /// subscriber marshaling onto a UI thread never gets flooded.</summary>
    event Action<string, VmStats>? StatsReceived;

    // --- Phase 6: Host/Storage/Networking/Engines pages - UI only ever
    // renders what these return; every fact behind them (host OS/CPU/RAM/
    // disk, WHPX, qemu-img info, --version, which Environment uses which
    // disk) is gathered here, not recomputed in XAML/code-behind. ---

    /// <summary>Host machine facts, independent of any one Environment -
    /// the Host page's data, including the running/total summary that
    /// used to sit above the navigator tree. See HostInfo's own
    /// remarks.</summary>
    Models.HostInfo GetHostInfo();

    /// <summary>Both registered engines' own install status (path,
    /// found/not-found/error, version, architectures) - the Engines
    /// page's data. EnvironmentCount is filled in here (the per-engine
    /// count each IEngine.GetInstallStatusAsync itself leaves at 0 - see
    /// its own remarks), from the same loaded list GetHostInfo uses. A
    /// fresh read every call, not cached - this page's whole point is
    /// "what is actually there right now."</summary>
    Task<IReadOnlyList<Models.EngineStatusInfo>> GetEngineStatusesAsync(AppSettings settings);

    /// <summary>Every disk image this app knows about - every
    /// Environment's own Workload.DiskPath, plus everything in the disk
    /// registry (DiskRegistryStore - every disk ever created via
    /// "Create disk" or added via "Add existing disk", regardless of
    /// where it actually lives) - the Storage page's data. Queries
    /// qemu-img info for format/size when QEMU is available; Format/
    /// sizes are null otherwise, not a wrong zero.</summary>
    Task<IReadOnlyList<Models.DiskInfo>> GetDiskInventoryAsync(AppSettings settings);

    /// <summary>Every Environment's own network configuration, read-only -
    /// the Networking page's data (no port forwarding yet).</summary>
    IReadOnlyList<Models.NetworkInfo> GetNetworkInventory();

    /// <summary>Every Environment (by Name) whose Workload.DiskPath is
    /// this exact file - empty if none. "Create disk" uses this to
    /// decide whether overwriting an existing file can ever be offered
    /// at all (never, if anything is actually using it).</summary>
    IReadOnlyList<string> GetEnvironmentsUsingDisk(string path);

    /// <summary>Un-registers a disk from the list - never deletes the
    /// actual file (PR #7 feedback - "Open folder" is there for anyone
    /// who genuinely wants to delete it themselves). Refuses (returns a
    /// message, removes nothing) unless it is currently used by no
    /// Environment at all, checked twice (once up front, once
    /// immediately before the actual removal) to close a real race
    /// against another Save/StartAsync attaching this exact disk to
    /// some Environment in between.</summary>
    Task<string> RemoveDiskFromListAsync(string path);

    /// <summary>Registers an existing disk file in the list ("Add
    /// existing disk", Storage page) - also what CreateDiskWindow calls
    /// right after a successful create, so every disk made that way is
    /// recorded the same way regardless of where it was saved. Refuses
    /// (registers nothing) for a path that does not actually exist.</summary>
    Task<string> RegisterDiskAsync(string path);
}
