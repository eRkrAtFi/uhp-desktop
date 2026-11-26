using System;
using System.ComponentModel;
using System.IO;
using QemuManager.Services;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;

namespace QemuManager.Models;

/// <summary>Wraps an <see cref="EnvironmentConfig"/> for the main window's
/// grid, adding the live status/resource-usage state that the config
/// itself has no business storing. This is a WPF view model, not a Core
/// type - it is the one place <see cref="VmState"/> (a plain Core enum, no
/// color baked in) gets turned into a <see cref="Brush"/> for the status
/// dot; Core itself never mentions WPF or any particular color. Likewise
/// Architecture/CpuCores/Accelerator below are no longer this class's own
/// EngineId == "RiscvEmu" ternaries - they read
/// IVmService.GetHardwareDescription, the same engine-aware hardware facts
/// Phase 3's "Hardware" card will bind to directly via the Hardware
/// property.</summary>
public class VmListItem : INotifyPropertyChanged
{
    private readonly IVmService _vmService;

    public EnvironmentConfig Config { get; }

    public VmListItem(EnvironmentConfig config, IVmService vmService)
    {
        Config = config;
        _vmService = vmService;
    }

    public string Name => Config.Name;
    /// <summary>Raw EnvironmentConfig.EngineId ("Qemu"/"RiscvEmu") - used
    /// for filtering and for deciding which engine's config schema to
    /// edit, never shown as-is; Engine below is the display label for the
    /// grid.</summary>
    public string Kind => Config.EngineId;
    public string Engine => Hardware.EngineDisplayName;

    /// <summary>Full hardware description (CPU, memory, disk, network,
    /// console, accelerator) - what Phase 3's Hardware card binds to.
    /// Architecture/CpuCores/Accelerator below just surface the three
    /// fields today's grid and the pre-Phase-2 card layout already showed,
    /// from this same source instead of their own copy of the logic.</summary>
    public VmHardwareDescription Hardware => _vmService.GetHardwareDescription(Config);

    public string Architecture => Hardware.Architecture;
    public string MemoryMB => Config.Platform.MemoryMB.ToString();
    public string CpuCores => Hardware.CpuDescription;
    public string Accelerator => Hardware.AcceleratorDescription;

    /// <summary>The "General" card's own two fields that are plain
    /// EnvironmentConfig facts, not anything IVmService needs to compute - local
    /// time, not UTC, matching how every other timestamp-ish thing in
    /// this app (Recent Tasks' own entries) is already displayed.</summary>
    public string CreatedText => Config.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>Where this VM's own config JSON lives on disk - VmStore's
    /// own file-naming convention (AppPaths.VmsDir\&lt;Id&gt;.json), surfaced
    /// here since VmStore itself does not expose it and the "General"
    /// card has nowhere else sensible to ask.</summary>
    public string ConfigFilePath => Path.Combine(AppPaths.VmsDir, Config.Id + ".json");

    private VmState _state = VmState.Stopped;
    /// <summary>Set from IVmService.GetState - the engine-agnostic enum,
    /// not a display string. StatusText/StatusBrush below derive their
    /// display from this (plus GuestDetail), rather than this class
    /// storing a pre-formatted label the way it used to.</summary>
    public VmState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Raise(nameof(State));
            Raise(nameof(IsRunning));
            Raise(nameof(StatusBrush));
            Raise(nameof(StatusText));
            Raise(nameof(GdbEndpoint));
            Raise(nameof(HasGdbEndpoint));
            Raise(nameof(GdbAttachCommand));
        }
    }

    /// <summary>"127.0.0.1:&lt;port&gt;" while running with
    /// Debug.GdbEnabled, else null - see IVmService.GetGdbEndpoint. A
    /// computed property, not a polled one like GuestDetail/stats below:
    /// the resolved port lives in the engine's own in-memory running-VM
    /// table for as long as this Environment is running, so there is
    /// nothing to refresh beyond re-reading it whenever State changes.</summary>
    public string? GdbEndpoint => State == VmState.Running ? _vmService.GetGdbEndpoint(Config) : null;

    /// <summary>For Visibility bindings (BoolToVis) - WPF has no built-in
    /// "non-null string" converter, and GdbEndpoint itself needs to stay
    /// a plain nullable string for GdbAttachCommand/the TextBlock binding
    /// below to use directly.</summary>
    public bool HasGdbEndpoint => GdbEndpoint != null;

    /// <summary>The ready-made command GdbEndpoint's own copy button
    /// places on the clipboard - lldb's own gdb-remote, the way this
    /// project's own riscv-emu/scripts/gdb-test.py and manual testing
    /// both already use it.</summary>
    public string? GdbAttachCommand => GdbEndpoint is { } ep ? $"lldb -o \"gdb-remote {ep}\"" : null;

    private string? _guestDetail;
    /// <summary>Extra live detail QEMU alone can report over QMP (e.g.
    /// "paused") - see IVmService.GetGuestDetailAsync/StatsReceived. Always
    /// null for riscv-emu and for a VM that is not Running.</summary>
    public string? GuestDetail
    {
        get => _guestDetail;
        set
        {
            if (_guestDetail == value) return;
            _guestDetail = value;
            Raise(nameof(GuestDetail));
            Raise(nameof(StatusText));
        }
    }

    /// <summary>True for any state other than Stopped - drives whether
    /// live resource usage applies at all.</summary>
    public bool IsRunning => _state != VmState.Stopped;

    // Just the four engine-agnostic states, never QMP's own extra detail
    // (e.g. "running" itself, or "paused") - that produced the confusing
    // "Running (running)" the grid used to show. GuestDetail is kept as
    // its own property precisely so the VM detail page (Phase 3) can
    // still surface that QMP-specific text somewhere it actually adds
    // information, without the grid repeating the state it's already
    // showing via the status dot and this same word.
    public string StatusText => _state switch
    {
        VmState.Starting => "Starting...",
        VmState.Stopping => "Stopping...",
        VmState.Running => "Running",
        _ => "Stopped",
    };

    /// <summary>Status-dot color: green while running, amber mid-transition
    /// (Starting/Stopping), gray when stopped - the same at-a-glance
    /// convention ESXi's VM inventory list uses. The only place a color
    /// gets attached to a VmState at all - see this class's own remarks.</summary>
    public Brush StatusBrush => _state switch
    {
        VmState.Stopped => new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
        VmState.Starting or VmState.Stopping => new SolidColorBrush(Color.FromRgb(0xE0, 0xA5, 0x30)),
        _ => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)), // Running
    };

    private double _cpuPercent;
    /// <summary>Host CPU percent the process is actually consuming right
    /// now (0-100, already divided by core count) - real numbers from
    /// <see cref="System.Diagnostics.Process"/>, not guest-reported ones;
    /// see IVmService.StatsReceived.</summary>
    public double CpuPercent
    {
        get => _cpuPercent;
        set
        {
            if (_cpuPercent == value) return;
            _cpuPercent = value;
            Raise(nameof(CpuPercent));
            Raise(nameof(CpuPercentText));
        }
    }
    public string CpuPercentText => IsRunning ? $"{_cpuPercent:0.#}%" : "-";

    private long _memoryBytes;
    /// <summary>Host working-set memory (bytes) the process is actually
    /// using right now, not the VM's configured RAM allocation.</summary>
    public long MemoryBytes
    {
        get => _memoryBytes;
        set
        {
            if (_memoryBytes == value) return;
            _memoryBytes = value;
            Raise(nameof(MemoryBytes));
            Raise(nameof(MemoryUsageText));
        }
    }
    public string MemoryUsageText => IsRunning ? $"{_memoryBytes / (1024.0 * 1024.0):0} MB" : "-";

    private TimeSpan? _uptime;
    /// <summary>Wall-clock time the actual OS process has been alive - see
    /// IVmService.StatsReceived. Null while not running, which UptimeText
    /// shows as "-" rather than a stale last value.</summary>
    public TimeSpan? Uptime
    {
        get => _uptime;
        set
        {
            if (_uptime == value) return;
            _uptime = value;
            Raise(nameof(Uptime));
            Raise(nameof(UptimeText));
        }
    }
    public string UptimeText => _uptime is { } u
        ? (u.TotalHours >= 1 ? $"{(int)u.TotalHours}h {u.Minutes}m" : $"{u.Minutes}m {u.Seconds}s")
        : "-";

    private double? _mips;
    /// <summary>riscv-emu only - straight from its own "riscv-emu: stats
    /// ..." stderr line (see IVmService.StatsReceived), never estimated.
    /// Always null for a QEMU VM. Not bound anywhere yet - Phase 3's
    /// "Speed (MIPS)" summary tile is what will read this.</summary>
    public double? Mips
    {
        get => _mips;
        set
        {
            if (_mips == value) return;
            _mips = value;
            Raise(nameof(Mips));
            Raise(nameof(MipsText));
        }
    }
    public string MipsText => _mips is { } m ? $"{m:0.#} MIPS" : "-";

    private ulong? _instructions;
    public ulong? Instructions
    {
        get => _instructions;
        set
        {
            if (_instructions == value) return;
            _instructions = value;
            Raise(nameof(Instructions));
        }
    }

    public void RefreshFromConfig()
    {
        Raise(nameof(Name));
        Raise(nameof(Kind));
        Raise(nameof(Hardware));
        Raise(nameof(Architecture));
        Raise(nameof(MemoryMB));
        Raise(nameof(CpuCores));
        Raise(nameof(Accelerator));
    }

    public void ClearResourceUsage()
    {
        CpuPercent = 0;
        MemoryBytes = 0;
        Uptime = null;
        GuestDetail = null;
        Mips = null;
        Instructions = null;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
