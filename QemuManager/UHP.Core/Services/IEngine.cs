using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>
/// vision/VISION.md's roadmap item 1: what QEMU and riscv-emu have in
/// common, so VmService (and later a web API/CLI) can drive either one
/// without ever asking "which engine is this Environment?" itself - the
/// question <see cref="EngineRegistry.GetFor"/> answers once, in the one
/// place a third engine gets added later (see VISION.md's roadmap item 5).
///
/// <see cref="VmProcessManager"/> (QEMU) and <see cref="RiscvEmuProcessManager"/>
/// (riscv-emu) both implement this directly, alongside their own existing
/// public surface (StartAsync(vm, qemuDir), GetVncEndpoint, etc.) - nothing
/// about either engine's own behavior changes here; this only gives
/// VmService a common way to reach it instead of branching on
/// EnvironmentConfig.EngineId. Extends <see cref="IConsoleSource"/>
/// (OutputReceived/ProcessExited/SendInput/ForceStop), which both already
/// implemented for an unrelated reason (ConsoleWindow/TerminalControl)
/// before this existed.
/// </summary>
public interface IEngine : IConsoleSource
{
    /// <summary>Matches EnvironmentConfig.EngineId exactly ("Qemu"/
    /// "RiscvEmu") - the identity <see cref="EngineRegistry"/> looks
    /// engines up by.</summary>
    string Id { get; }

    /// <summary>"QEMU" / "riscv-emu" - what VmHardwareDescription.EngineDisplayName
    /// already showed per engine, now owned by the engine itself.</summary>
    string DisplayName { get; }

    /// <summary>What this engine can actually do - see EngineCapabilities'
    /// own remarks. Static per engine, unlike GetHardwareDescription
    /// (which needs a real EnvironmentConfig to describe).</summary>
    EngineCapabilities Capabilities { get; }

    /// <summary>Raised whenever any Environment this engine is tracking
    /// changes run state - no payload (same convention as
    /// IVmService.StateChanged); the caller re-reads GetVmState for
    /// whatever Environment it cares about.</summary>
    event Action? StateChanged;

    /// <summary>True if this engine is the one that owns env (today: an
    /// exact EnvironmentConfig.EngineId match) - what EngineRegistry.GetFor
    /// asks each registered engine, instead of VmService hardcoding
    /// "EngineId == RiscvEmu means _riscv".</summary>
    bool CanHandle(EnvironmentConfig env);

    /// <summary>A new EnvironmentConfig for this engine's own Id, with
    /// whatever defaults make sense for it already applied (e.g.
    /// riscv-emu's own 512 MB default, sized for it rather than inherited
    /// from Platform's class default, which is sized for QEMU).</summary>
    EnvironmentConfig CreateDraft();

    /// <summary>Every architecture this engine can currently target -
    /// QEMU's own list is whatever qemu-system-*.exe binaries settings.
    /// QemuDir actually has (see QemuLocator.ListArchitectures); riscv-emu
    /// is always exactly one, fixed.</summary>
    IReadOnlyList<string> ListArchitectures(AppSettings settings);

    /// <summary>This engine's own configuration schema (vision/VISION.md's
    /// roadmap item 1: "fields, types, defaults, validation") -
    /// EnvironmentEditWindow renders one row per field instead of
    /// VmEditWindow/RiscvEmuEditWindow's own hand-laid-out, per-engine
    /// XAML. Each field's own Get/Set read and write directly on an
    /// EnvironmentConfig instance.</summary>
    IReadOnlyList<ConfigField> GetConfigSchema();

    /// <summary>This Environment's run state, in the engine-agnostic
    /// VmState enum - each engine's own internal run-state enum
    /// (VmRunState/RiscvEmuRunState) never leaves its own class.</summary>
    VmState GetVmState(string vmId);

    bool IsRunning(string vmId);

    /// <summary>Real host-side CPU%/memory for the actual OS process. Null
    /// when not running.</summary>
    (double cpuPercent, long memoryBytes)? GetResourceUsage(string vmId);

    /// <summary>How long the OS process has been alive. Null when not
    /// running.</summary>
    TimeSpan? GetUptime(string vmId);

    Task<string> StartAsync(EnvironmentConfig env, AppSettings settings);

    /// <summary>Graceful shutdown - see IVmService.ShutdownAsync's own
    /// remarks for what "graceful" means per engine and why
    /// ShutdownResult.TimedOut is never acted on automatically here.</summary>
    Task<ShutdownResult> ShutdownAsync(EnvironmentConfig env, AppSettings settings);

    /// <summary>Re-adopts this Environment's OS process if a previous run
    /// of this app left it running. True if it is now tracked as running,
    /// whether that is because it was just reattached or already was.</summary>
    bool TryReattach(EnvironmentConfig env);

    /// <summary>Extra live detail beyond GetVmState that only some engines
    /// can report (QEMU: QMP's query-status). Null if this engine has no
    /// such channel, or the Environment is not currently running.</summary>
    Task<string?> GetGuestDetailAsync(EnvironmentConfig env);

    /// <summary>What clicking "Console" on this Environment should do
    /// right now - see ConsoleAvailability's own remarks.</summary>
    (ConsoleAvailability kind, string? detail) GetConsoleAvailability(EnvironmentConfig env);

    /// <summary>Display-ready hardware facts for this Environment, with
    /// this engine's own real capabilities already baked in - see
    /// VmHardwareDescription's own remarks.</summary>
    VmHardwareDescription GetHardwareDescription(EnvironmentConfig env);
}
