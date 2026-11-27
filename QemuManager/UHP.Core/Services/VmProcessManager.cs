using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using QemuManager.Models;

namespace QemuManager.Services;

public enum VmRunState { Stopped, Starting, Running, Stopping }

/// <summary>One VM this process has launched: the OS process plus the QMP
/// channel used to talk to it.</summary>
public class RunningVm
{
    public required Process Process { get; init; }
    public required int QmpPort { get; init; }
    public VmRunState State { get; set; } = VmRunState.Starting;
    /// <summary>True when this VM was started with Display == "console" -
    /// stdio was redirected, so <see cref="VmProcessManager.SendInput"/> and
    /// the output pump loops are meaningful for it.</summary>
    public bool ConsoleMode { get; init; }

    /// <summary>Set when this VM was started with Display == "vnc" - QEMU
    /// was given "-display none -vnc 127.0.0.1:&lt;port - 5900&gt;" instead of
    /// opening its own gtk/sdl window, so closing a window can never stop
    /// it (there isn't one): connect any VNC client to this port to see
    /// its screen, same decoupling Display == "console" already gives a
    /// text console, just for a graphical one and without an in-app
    /// viewer yet.</summary>
    public int? VncPort { get; init; }

    /// <summary>Set when this VM was started with Debug.GdbEnabled -
    /// the actual resolved port (Debug.GdbPort's own value, or a freshly
    /// picked free one if that was 0) a GDB client can attach to on
    /// 127.0.0.1. See VmProcessManager.GetGdbEndpoint.</summary>
    public int? GdbPort { get; init; }

    /// <summary>Previous CPU-time sample, for turning Process.TotalProcessorTime
    /// (a monotonically increasing total) into a live percent - see
    /// VmProcessManager.GetResourceUsage.</summary>
    public TimeSpan LastCpuTime { get; set; }
    public DateTime LastSampleTime { get; set; } = DateTime.UtcNow;

    /// <summary>Serializes SendInput's own writes to this VM's stdin - see
    /// SendInput's own remarks for why this exists. Guarded by
    /// WriteChainLock, not accessed directly.</summary>
    public Task WriteChain = Task.CompletedTask;
    public readonly object WriteChainLock = new();
}

/// <summary>
/// Turns an <see cref="EnvironmentConfig"/> into a qemu-system-&lt;arch&gt;.exe
/// process and keeps track of every one this app instance started. QEMU is
/// the actual virtualization engine here; this class only ever builds its
/// command line, launches it, and talks to it over QMP (see
/// <see cref="QmpClient"/>) - it does not emulate anything itself.
/// </summary>
public class VmProcessManager : IEngine
{
    private readonly Dictionary<string, RunningVm> _running = new();

    public event Action? StateChanged;

    // --- IEngine (vision/VISION.md roadmap item 1) - additive: every
    // member below is new surface for VmService to reach this engine
    // through without branching on EnvironmentConfig.EngineId itself.
    // Nothing already public above changes signature or behavior. ---

    public string Id => "Qemu";
    public string DisplayName => "QEMU";

    public EngineCapabilities Capabilities { get; } = new(
        SupportsMultipleDisplayModes: true,
        SupportsDisk: true,
        SupportsCdrom: true,
        SupportsKernelBoot: false,
        SupportsMachineType: true,
        SupportsFirmware: true,
        SupportsNetwork: true,
        SupportsSmp: true,
        SupportsGuestDetail: true,
        SupportsSpeedMetric: false,
        SupportsBareMetalElf: true);

    public bool CanHandle(EnvironmentConfig env) => env.EngineId == Id;

    public EnvironmentConfig CreateDraft() => new() { EngineId = Id };

    public IReadOnlyList<string> ListArchitectures(AppSettings settings) => QemuLocator.ListArchitectures(settings.QemuDir);

    /// <summary>EnvironmentCount is always 0 here - VmService fills that
    /// in itself afterward (see IEngine.GetInstallStatusAsync's own
    /// remarks), since only it has the loaded Environment list this
    /// engine itself never sees.</summary>
    public async Task<EngineStatusInfo> GetInstallStatusAsync(AppSettings settings)
    {
        var archs = QemuLocator.ListArchitectures(settings.QemuDir);
        if (archs.Count == 0)
            return new EngineStatusInfo(Id, DisplayName, settings.QemuDir, EngineStatus.NotFound, null, archs, 0);

        var version = await QemuLocator.GetVersionAsync(settings.QemuDir);
        var status = version == null ? EngineStatus.Error : EngineStatus.Found;
        return new EngineStatusInfo(Id, DisplayName, settings.QemuDir, status, version, archs, 0);
    }

    /// <summary>Architectures QEMU ships with no default machine type at
    /// all (qemu-system-* simply refuses to start with none given) and
    /// the "virt" machine type is the usual choice for - what the old,
    /// per-window VmEditWindow's own ArchHints dictionary used to offer
    /// by hand before the schema-driven EnvironmentEditWindow replaced it
    /// (see Platform.MachineType's own FieldDependency below, which is
    /// what restores this behavior generically through the schema
    /// instead).</summary>
    private static readonly Dictionary<string, (string MachineType, string Hint)> ArchMachineTypeHints =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["aarch64"] = ("virt", "ARM64 has no default machine type - \"virt\" is QEMU's standard one. You'll also need a firmware image (e.g. an edk2 AAVMF \"QEMU_EFI.fd\" build) unless booting a kernel directly via Workload's own Kernel Image field."),
            ["arm"] = ("virt", "32-bit ARM has no default machine type - \"virt\" is QEMU's standard one."),
            ["riscv64"] = ("virt", "RISC-V has no default machine type - \"virt\" is QEMU's standard one. You'll also need OpenSBI firmware unless booting a kernel directly via Workload's own Kernel Image field."),
            ["riscv32"] = ("virt", "RISC-V has no default machine type - \"virt\" is QEMU's standard one."),
        };

    private static bool IsLinuxKernel(EnvironmentConfig e) => e.Workload.Kind == WorkloadKind.LinuxKernel;
    private static bool IsBareMetalElf(EnvironmentConfig e) => e.Workload.Kind == WorkloadKind.BareMetalElf;

    public IReadOnlyList<ConfigField> GetConfigSchema() => new List<ConfigField>
    {
        new() { Key = "Platform.Architecture", Label = "Architecture", Type = FieldType.Choice, Group = "Platform",
            Get = e => e.Platform.Architecture, Set = (e, v) => e.Platform.Architecture = (string)v!, Required = true },
        new() { Key = "Platform.MachineType", Label = "Machine type", Type = FieldType.Text, Group = "Platform",
            Get = e => e.Platform.MachineType, Set = (e, v) => e.Platform.MachineType = (string)v!,
            Hint = "QEMU's -M/-machine value, e.g. \"virt\". Empty lets QEMU pick its own default - several architectures (aarch64/arm/riscv*) have none and need this set explicitly.",
            DependsOn = new FieldDependency
            {
                OnKey = "Platform.Architecture",
                SuggestedDefault = arch => ArchMachineTypeHints.TryGetValue(arch as string ?? "", out var h) ? h.MachineType : null,
                IsRequired = arch => ArchMachineTypeHints.ContainsKey(arch as string ?? ""),
                Hint = arch => ArchMachineTypeHints.TryGetValue(arch as string ?? "", out var h) ? h.Hint : null,
            } },
        new() { Key = "Platform.MemoryMB", Label = "Memory (MB)", Type = FieldType.Int, Group = "Platform",
            Get = e => e.Platform.MemoryMB, Set = (e, v) => e.Platform.MemoryMB = (int)v!, Required = true,
            Validate = v => (int)v! >= 16 ? null : "Memory must be a number of MB, at least 16." },
        new() { Key = "Platform.CpuCores", Label = "CPU cores", Type = FieldType.Int, Group = "Platform",
            Get = e => e.Platform.CpuCores, Set = (e, v) => e.Platform.CpuCores = (int)v!, Required = true,
            Validate = v => (int)v! >= 1 ? null : "CPU cores must be a positive whole number." },
        new() { Key = "Platform.Accelerator", Label = "Accelerator", Type = FieldType.Choice, Group = "Platform",
            Get = e => e.Platform.Accelerator, Set = (e, v) => e.Platform.Accelerator = (string)v!,
            Choices = new[] { "tcg", "whpx" },
            Hint = "tcg (software, always works) or whpx (hardware, needs Windows Hypervisor Platform)." },
        new() { Key = "Platform.NetworkMode", Label = "Network", Type = FieldType.Choice, Group = "Platform",
            Get = e => e.Platform.NetworkMode, Set = (e, v) => e.Platform.NetworkMode = (string)v!,
            Choices = new[] { "user", "none" } },
        new() { Key = "Firmware.FirmwarePath", Label = "Firmware / BIOS", Type = FieldType.FilePath, Group = "Firmware",
            Get = e => e.Firmware.FirmwarePath, Set = (e, v) => e.Firmware.FirmwarePath = (string)v!,
            FileFilter = "Firmware/BIOS images (*.fd;*.bin;*.rom;*.elf)|*.fd;*.bin;*.rom;*.elf|All files (*.*)|*.*" },
        new() { Key = "Workload.DiskPath", Label = "Disk image", Type = FieldType.FilePath, Group = "Workload",
            Get = e => e.Workload.DiskPath, Set = (e, v) => e.Workload.DiskPath = (string)v!,
            FileFilter = "Disk images (*.qcow2;*.img;*.raw;*.vmdk;*.vdi;*.vhdx)|*.qcow2;*.img;*.raw;*.vmdk;*.vdi;*.vhdx|All files (*.*)|*.*" },
        new() { Key = "Workload.CdromPath", Label = "CD-ROM / ISO", Type = FieldType.FilePath, Group = "Workload",
            Get = e => e.Workload.CdromPath, Set = (e, v) => e.Workload.CdromPath = (string)v!,
            FileFilter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*" },
        new() { Key = "Workload.BootOrder", Label = "Boot order", Type = FieldType.Text, Group = "Workload",
            Get = e => e.Workload.BootOrder, Set = (e, v) => e.Workload.BootOrder = (string)v!,
            Hint = "c = disk first, d = CD-ROM first, n = network" },
        new() { Key = "Workload.KernelPath", Label = "Kernel Image", Type = FieldType.FilePath, Group = "Workload",
            Get = e => e.Workload.KernelPath, Set = (e, v) => e.Workload.KernelPath = (string)v!,
            FileFilter = "Linux kernel image (*.bin;*.elf;vmlinuz*;*.img)|*.bin;*.elf;vmlinuz*;*.img|All files (*.*)|*.*",
            Hint = "Boots this kernel directly via -kernel, bypassing firmware/bootloader. Leave empty to boot normally from Disk image/CD-ROM instead.",
            VisibleWhen = IsLinuxKernel },
        new() { Key = "Workload.InitrdPath", Label = "Initrd / initramfs", Type = FieldType.FilePath, Group = "Workload",
            Get = e => e.Workload.InitrdPath, Set = (e, v) => e.Workload.InitrdPath = (string)v!,
            FileFilter = "Initramfs (*.cpio.gz;*.cpio;*.img;*.gz)|*.cpio.gz;*.cpio;*.img;*.gz|All files (*.*)|*.*",
            VisibleWhen = IsLinuxKernel },
        new() { Key = "Workload.KernelCmdline", Label = "Kernel command line", Type = FieldType.Text, Group = "Workload",
            Get = e => e.Workload.KernelCmdline, Set = (e, v) => e.Workload.KernelCmdline = (string)v!,
            Hint = "Passed to the kernel via -append, e.g. \"console=ttyS0\". Only used when Kernel Image is set.",
            VisibleWhen = IsLinuxKernel },
        new() { Key = "Workload.ExtraArgs", Label = "Extra arguments", Type = FieldType.MultilineText, Group = "Workload",
            Get = e => e.Workload.ExtraArgs, Set = (e, v) => e.Workload.ExtraArgs = (string)v! },
        new() { Key = "Workload.ElfPath", Label = "Bare-metal ELF", Type = FieldType.FilePath, Group = "Workload",
            Get = e => e.Workload.ElfPath, Set = (e, v) => e.Workload.ElfPath = (string)v!,
            FileFilter = "ELF files (*.elf)|*.elf|All files (*.*)|*.*",
            Hint = "Runs this ELF directly, no kernel/firmware/bootloader at all (-bios none -kernel).",
            VisibleWhen = IsBareMetalElf, RequiredWhen = IsBareMetalElf },
        new() { Key = "Debug.Display", Label = "Display backend", Type = FieldType.Choice, Group = "Debug",
            Get = e => e.Debug.Display, Set = (e, v) => e.Debug.Display = (string)v!,
            Choices = new[] { "gtk", "sdl", "console", "vnc" } },
        new() { Key = "Debug.EnterSends", Label = "Enter sends", Type = FieldType.Choice, Group = "Debug",
            Get = e => e.Debug.EnterSends, Set = (e, v) => e.Debug.EnterSends = (string)v!,
            Choices = new[] { "CR", "LF", "CRLF" },
            Hint = "Only matters with Display set to \"console\". Change this only if Enter in the console does not submit a line - most guests need no change." },
        new() { Key = "Debug.GdbEnabled", Label = "GDB stub", Type = FieldType.Bool, Group = "Debug",
            Get = e => e.Debug.GdbEnabled, Set = (e, v) => e.Debug.GdbEnabled = (bool)v!,
            Hint = "Serve a GDB Remote Serial Protocol stub on 127.0.0.1 (loopback only) - attach with lldb/gdb's own 'gdb-remote'/'target remote'." },
        new() { Key = "Debug.GdbPort", Label = "GDB port", Type = FieldType.Int, Group = "Debug",
            Get = e => e.Debug.GdbPort, Set = (e, v) => e.Debug.GdbPort = (int)v!,
            Hint = "0 picks any free port automatically - only set a specific one if something else needs to know it in advance." },
        new() { Key = "Debug.GdbWaitForDebugger", Label = "Wait for debugger", Type = FieldType.Bool, Group = "Debug",
            Get = e => e.Debug.GdbWaitForDebugger, Set = (e, v) => e.Debug.GdbWaitForDebugger = (bool)v!,
            Hint = "Stop before the first instruction (like QEMU's own -S) instead of running immediately once a debugger attaches - recommended for a short program, so a breakpoint can be set before it has already finished." },
    };

    public VmState GetVmState(string vmId) => GetState(vmId) switch
    {
        VmRunState.Starting => VmState.Starting,
        VmRunState.Running => VmState.Running,
        VmRunState.Stopping => VmState.Stopping,
        _ => VmState.Stopped,
    };

    public async Task<string> StartAsync(EnvironmentConfig env, AppSettings settings) =>
        await StartAsync(env, settings.QemuDir);

    public async Task<ShutdownResult> ShutdownAsync(EnvironmentConfig env, AppSettings settings) =>
        new ShutdownResult(await StopGracefullyAsync(env), false);

    /// <summary>riscv-emu has no QMP-equivalent control channel to ask -
    /// this is the one engine-specific gap IEngine's own doc comment
    /// calls out by name; here, QEMU's own answer.</summary>
    public async Task<string?> GetGuestDetailAsync(EnvironmentConfig env)
    {
        if (GetState(env.Id) != VmRunState.Running) return null;
        return await QueryGuestStatusAsync(env);
    }

    public (ConsoleAvailability kind, string? detail) GetConsoleAvailability(EnvironmentConfig env)
    {
        if (!IsRunning(env.Id)) return (ConsoleAvailability.NotRunning, null);
        if (env.Debug.Display == "vnc") return (ConsoleAvailability.Vnc, GetVncEndpoint(env.Id));
        if (env.Debug.Display == "console") return (ConsoleAvailability.Stream, null);
        return (ConsoleAvailability.ExternalWindow, null);
    }

    public VmHardwareDescription GetHardwareDescription(EnvironmentConfig env)
    {
        var disk = string.IsNullOrWhiteSpace(env.Workload.DiskPath) ? "None" : env.Workload.DiskPath;
        var network = env.Platform.NetworkMode == "user" ? "User-mode NAT" : "None";
        var console = env.Debug.Display switch
        {
            "console" => "Serial (text, in this app)",
            "vnc" => "VNC (headless)",
            _ => $"{env.Debug.Display} window (QEMU's own)",
        };
        return new VmHardwareDescription(
            EngineDisplayName: DisplayName,
            Architecture: env.Platform.Architecture,
            CpuDescription: $"{env.Platform.CpuCores} core{(env.Platform.CpuCores == 1 ? "" : "s")}",
            MemoryDescription: $"{env.Platform.MemoryMB} MB",
            DiskDescription: disk,
            NetworkDescription: network,
            ConsoleDescription: console,
            AcceleratorDescription: env.Platform.Accelerator,
            SupportsGuestDetail: true,
            SupportsSpeedMetric: false);
    }

    /// <summary>Raised on a background thread, not the UI thread - only
    /// fires for a VM started with Display == "console" (see
    /// <see cref="RunningVm.ConsoleMode"/>); a gtk/sdl VM has its own real
    /// window and nothing to pipe here.</summary>
    public event Action<string, byte[]>? OutputReceived;
    public event Action<string>? ProcessExited;

    public VmRunState GetState(string vmId) =>
        _running.TryGetValue(vmId, out var r) ? r.State : VmRunState.Stopped;

    public bool IsRunning(string vmId) => GetState(vmId) is VmRunState.Running or VmRunState.Starting or VmRunState.Stopping;

    public async Task<string> StartAsync(EnvironmentConfig vm, string qemuDir)
    {
        if (IsRunning(vm.Id)) return "VM is already running.";

        var binary = QemuLocator.SystemBinaryFor(qemuDir, vm.Platform.Architecture);
        if (binary == null)
            return $"No qemu-system-{vm.Platform.Architecture}.exe found in {qemuDir}.";

        var qmpPort = GetFreeTcpPort();
        var consoleMode = vm.Debug.Display == "console";
        // VNC display numbers are host-port-minus-5900 (QEMU's own
        // convention), not a port directly - picking a free TCP port first
        // and subtracting 5900 keeps this using the same no-collision
        // mechanism as qmpPort above instead of a second, different one.
        // GetFreeTcpPort's ephemeral-range ports (comfortably above 5900 on
        // Windows) keep that subtraction positive in practice.
        var vncPort = vm.Debug.Display == "vnc" ? GetFreeTcpPort() : (int?)null;
        // Debug.GdbPort == 0 means "pick any free port", same convention
        // as vncPort above - resolved once here, at Start time, never
        // written back into the saved Environment.
        var gdbPort = vm.Debug.GdbEnabled ? (vm.Debug.GdbPort != 0 ? vm.Debug.GdbPort : GetFreeTcpPort()) : (int?)null;
        var args = BuildArguments(vm, qmpPort, vncPort, gdbPort);

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = qemuDir,
            RedirectStandardOutput = consoleMode,
            RedirectStandardInput = consoleMode,
            RedirectStandardError = consoleMode,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            return $"Failed to start QEMU: {ex.Message}";
        }

        var running = new RunningVm { Process = process, QmpPort = qmpPort, State = VmRunState.Starting, ConsoleMode = consoleMode, VncPort = vncPort, GdbPort = gdbPort };
        _running[vm.Id] = running;
        process.Exited += (_, _) => OnProcessExited(vm.Id);
        process.EnableRaisingEvents = true;

        // So a later app restart can tell this VM is still running instead
        // of reporting it Stopped just because this app's own in-memory
        // _running dictionary was lost - see ARCHITECTURE_AUDIT.md's
        // Critical Risk #1 and TryReattach below.
        RuntimeStateStore.Save(vm.Id, new RuntimeState
        {
            Pid = process.Id,
            ProcessName = process.ProcessName,
            QmpPort = qmpPort,
            VncPort = vncPort,
            GdbPort = gdbPort,
        });

        if (consoleMode)
        {
            // QEMU's serial chardev ("-serial stdio") writes the guest's
            // virtual COM1 straight through - raw guest bytes, not
            // necessarily valid UTF-8, so these are read as a byte stream
            // rather than through a text StreamReader, same as
            // RiscvEmuProcessManager's pump.
            _ = PumpAsync(vm.Id, process.StandardOutput.BaseStream);
            _ = PumpAsync(vm.Id, process.StandardError.BaseStream);
        }

        // QEMU needs a brief moment to open its QMP listener after the
        // process starts; poll instead of guessing a fixed delay.
        var connected = await TryConnectQmpWithRetryAsync(qmpPort, process);
        // Running either way, whether or not QMP came up in time: QMP is a
        // control channel, not proof of guest health, so a guest that is
        // actually fine but merely slow to open its QMP port should not be
        // reported as anything other than Running (see the returned message
        // below for how that distinction is still surfaced to the caller).
        running.State = VmRunState.Running;
        StateChanged?.Invoke();

        var vncSuffix = vncPort.HasValue ? $" VNC: 127.0.0.1:{vncPort} - connect with any VNC client; closing that client (or never opening one) does not stop the VM." : "";
        return connected
            ? "Started." + vncSuffix
            : "Started, but QEMU's QMP control channel did not come up in time; Stop will fall back to force-terminate." + vncSuffix;
    }

    /// <summary>The "host:port" a running VM's VNC server is listening on,
    /// or null if it was not started with Display == "vnc" (or is not
    /// running at all). Lets the UI show this again on demand, not only
    /// in the one-shot Start message.</summary>
    public string? GetVncEndpoint(string vmId) =>
        _running.TryGetValue(vmId, out var running) && running.VncPort is { } port ? $"127.0.0.1:{port}" : null;

    /// <summary>The "host:port" a running Environment's GDB stub is
    /// listening on, or null if Debug.GdbEnabled was not set (or it is
    /// not running at all). Same role as GetVncEndpoint above.</summary>
    public string? GetGdbEndpoint(string vmId) =>
        _running.TryGetValue(vmId, out var running) && running.GdbPort is { } port ? $"127.0.0.1:{port}" : null;

    /// <summary>Graceful shutdown: asks the guest OS to power down over QMP,
    /// the same signal a real power button sends. May be ignored by the
    /// guest or simply take a while - this call only waits briefly before
    /// returning, it does not wait for the process to exit.</summary>
    public async Task<string> StopGracefullyAsync(EnvironmentConfig vm)
    {
        if (!_running.TryGetValue(vm.Id, out var running)) return "VM is not running.";

        running.State = VmRunState.Stopping;
        StateChanged?.Invoke();

        try
        {
            await using var qmp = new QmpClient();
            await qmp.ConnectAsync(running.QmpPort);
            await qmp.PowerdownAsync();

            // ACPI power-down is a request, not a guarantee: a guest with no
            // OS, or one that ignores the power button, never exits. Revert
            // the transient "Stopping..." label back to "Running" if the
            // process is still alive once a real shutdown would plausibly
            // have happened; the process-exited handler already covers the
            // case where it does shut down before then.
            _ = RevertStoppingIfStillRunningAsync(vm.Id, running);
            return "Sent ACPI power-down request; the guest decides when to shut down.";
        }
        catch (Exception ex)
        {
            running.State = VmRunState.Running;
            StateChanged?.Invoke();
            return $"Could not reach QEMU's QMP channel ({ex.Message}). Use Force Stop instead.";
        }
    }

    private async Task RevertStoppingIfStillRunningAsync(string vmId, RunningVm running)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (_running.TryGetValue(vmId, out var current) && current == running && current.State == VmRunState.Stopping)
        {
            current.State = VmRunState.Running;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Sends raw bytes to a console-mode VM's virtual serial port
    /// (QEMU's "-serial stdio" chardev). A no-op for a VM that was not
    /// started with Display == "console". Paced in small chunks rather
    /// than one bulk write - confirmed against a real guest that writing
    /// a whole typed line in one shot can silently drop bytes in the
    /// middle of it (a human typing the same line, one keystroke at a
    /// time, never hits this - it's specifically bursty writes that do).
    /// Fire-and-forget on a background task so a long paste can't freeze
    /// the UI thread for the whole pacing delay.
    ///
    /// Chained onto running.WriteChain, not fired independently - see
    /// RiscvEmuProcessManager.SendInput's own remarks for the exact bug
    /// this avoids (two overlapping, unordered writes to the same stream,
    /// now that Phase 5's xterm-based console calls this once per
    /// keystroke rather than once per whole typed line). This engine's
    /// own per-call byte pacing below made the overlap window for that
    /// race even wider than RiscvEmuProcessManager's single-write case,
    /// not narrower - a several-byte paced write already takes multiple
    /// times the pacing delay to finish, during which a fast typist's
    /// next keystroke was already calling SendInput again.</summary>
    public void SendInput(string vmId, byte[] data)
    {
        if (!_running.TryGetValue(vmId, out var running) || !running.ConsoleMode || running.Process.HasExited) return;
        var stream = running.Process.StandardInput.BaseStream;
        lock (running.WriteChainLock)
        {
            running.WriteChain = running.WriteChain.ContinueWith(_ => SendPacedAsync(stream, data)).Unwrap();
        }
    }

    /// <summary>Real host-side CPU percent (0-100, already divided by core
    /// count so one fully-pegged core on an 8-core host reads ~12%, not
    /// ~100%) and working-set memory for the actual qemu-system-*.exe
    /// process - not anything the guest self-reports, and not the VM's
    /// configured RAM allocation. Returns null for a VM that is not
    /// currently running.</summary>
    public (double cpuPercent, long memoryBytes)? GetResourceUsage(string vmId)
    {
        if (!_running.TryGetValue(vmId, out var running) || running.Process.HasExited) return null;
        var sample = ResourceUsageSampler.Sample(running.Process, running.LastCpuTime, running.LastSampleTime);
        if (sample == null) return null;
        running.LastCpuTime = sample.Value.cpuTime;
        running.LastSampleTime = sample.Value.sampleTime;
        return (sample.Value.cpuPercent, sample.Value.memoryBytes);
    }

    /// <summary>How long the OS process has actually been alive - real data
    /// from Process.StartTime, correct even for a VM reattached after an
    /// app restart (the process itself kept running the whole time; only
    /// this app's own tracking of it was gone). Null while not running.</summary>
    public TimeSpan? GetUptime(string vmId)
    {
        if (!_running.TryGetValue(vmId, out var running)) return null;
        try { return running.Process.HasExited ? null : DateTime.Now - running.Process.StartTime; }
        catch { return null; }
    }

    private static async Task SendPacedAsync(Stream stream, byte[] data)
    {
        const int chunkSize = 1;
        const int delayMs = 4;
        try
        {
            for (var i = 0; i < data.Length; i += chunkSize)
            {
                var n = Math.Min(chunkSize, data.Length - i);
                await stream.WriteAsync(data, i, n);
                await stream.FlushAsync();
                if (i + n < data.Length) await Task.Delay(delayMs);
            }
        }
        catch
        {
            // Not actionable for the user mid-console-session; the process-
            // exited handler is the source of truth for whether it's gone.
        }
    }

    private async Task PumpAsync(string vmId, Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (n <= 0) break;
                var chunk = new byte[n];
                Buffer.BlockCopy(buffer, 0, chunk, 0, n);
                OutputReceived?.Invoke(vmId, chunk);
            }
        }
        catch
        {
            // The pipe going away just means the process exited.
        }
    }

    /// <summary>Immediately ends the QEMU process. Equivalent to pulling the
    /// power cord: no guest-side shutdown happens.</summary>
    public string ForceStop(EnvironmentConfig vm)
    {
        if (!_running.TryGetValue(vm.Id, out var running)) return "VM is not running.";
        try
        {
            if (!running.Process.HasExited) running.Process.Kill(entireProcessTree: true);
            return "Terminated.";
        }
        catch (Exception ex)
        {
            return $"Failed to terminate: {ex.Message}";
        }
    }

    /// <summary>Live status straight from QEMU over QMP, not merely "the
    /// process is alive" - distinguishes running/paused/shutdown states.</summary>
    public async Task<string> QueryGuestStatusAsync(EnvironmentConfig vm)
    {
        if (!_running.TryGetValue(vm.Id, out var running)) return "stopped";
        try
        {
            await using var qmp = new QmpClient();
            await qmp.ConnectAsync(running.QmpPort);
            return await qmp.QueryStatusAsync();
        }
        catch
        {
            return running.Process.HasExited ? "stopped" : "unknown";
        }
    }

    private void OnProcessExited(string vmId)
    {
        if (_running.TryGetValue(vmId, out var running))
        {
            running.State = VmRunState.Stopped;
            _running.Remove(vmId);
        }
        RuntimeStateStore.Delete(vmId);
        StateChanged?.Invoke();
        ProcessExited?.Invoke(vmId);
    }

    /// <summary>Called once per VM at app startup (see MainWindow.LoadVms):
    /// if a runtime-state file says this VM's process was still running the
    /// last time any instance of this app saw it, and a process with that
    /// PID and binary name is still actually alive, re-adopt it so this VM
    /// reports Running instead of (incorrectly) Stopped. Returns true if the
    /// VM is now tracked as running, whether that is because it was just
    /// reattached or because it was already tracked.
    ///
    /// A reattached VM has no stdio pipes - those belonged to the OS
    /// process that originally launched it, which this app instance is
    /// not. Its ConsoleMode is therefore always false here even if the VM's
    /// own Display setting is "console": SendInput becomes a no-op and no
    /// console output will appear until the VM is stopped and started
    /// again by this app. Stop/Force Stop/live QMP status all still work,
    /// since those only need the process handle and (for Qemu) the QMP
    /// port, both of which this reattaches correctly.</summary>
    public bool TryReattach(EnvironmentConfig vm)
    {
        if (IsRunning(vm.Id)) return true;

        var state = RuntimeStateStore.Load(vm.Id);
        if (state == null) return false;

        Process process;
        try
        {
            process = Process.GetProcessById(state.Pid);
            if (!string.Equals(process.ProcessName, state.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                // Windows recycled this PID for an unrelated process since
                // this VM's own process exited - it really is gone.
                RuntimeStateStore.Delete(vm.Id);
                return false;
            }
        }
        catch (ArgumentException)
        {
            // No process with that PID exists any more.
            RuntimeStateStore.Delete(vm.Id);
            return false;
        }

        var running = new RunningVm
        {
            Process = process,
            QmpPort = state.QmpPort ?? 0,
            State = VmRunState.Running,
            ConsoleMode = false,
            VncPort = state.VncPort,
            GdbPort = state.GdbPort,
            // A freshly-started process has accumulated ~0 CPU time, so
            // LastCpuTime's own Zero default is correct for it - but this
            // process has already been running for a while, so GetResourceUsage's
            // first delta must start from its *current* total, not zero,
            // or it reports its entire CPU history as having happened in
            // the instant since reattach (confirmed: produced a nonsense
            // 90000%+ reading in QemuManager.SelfTest before this fix).
            LastCpuTime = process.TotalProcessorTime,
        };
        _running[vm.Id] = running;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(vm.Id);

        // Closes the narrow race where the process exited between
        // GetProcessById and EnableRaisingEvents - Exited would never fire
        // for it otherwise, leaving a dead entry in _running forever.
        if (process.HasExited)
        {
            OnProcessExited(vm.Id);
            return false;
        }

        StateChanged?.Invoke();
        return true;
    }

    private static async Task<bool> TryConnectQmpWithRetryAsync(int port, Process process)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (process.HasExited) return false;
            try
            {
                await using var probe = new QmpClient();
                await probe.ConnectAsync(port);
                return true;
            }
            catch
            {
                await Task.Delay(200);
            }
        }
        return false;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static List<string> BuildArguments(EnvironmentConfig vm, int qmpPort, int? vncPort, int? gdbPort)
    {
        var args = new List<string>
        {
            "-name", vm.Name,
            "-m", vm.Platform.MemoryMB.ToString(),
            "-smp", vm.Platform.CpuCores.ToString(),
            "-accel", vm.Platform.Accelerator,
            "-qmp", $"tcp:127.0.0.1:{qmpPort},server,nowait",
            "-boot", $"order={vm.Workload.BootOrder}",
        };

        // Several architectures have no usable default machine type/firmware
        // (aarch64/arm/riscv* in particular need -M explicitly to boot
        // anything) - see Platform.MachineType/Firmware.FirmwarePath and
        // ARCHITECTURE_AUDIT.md's recommendation #3. Both are opt-in and
        // empty for every VM saved before these fields existed, so this
        // changes nothing for an x86_64 VM relying on QEMU's own default.
        if (!string.IsNullOrWhiteSpace(vm.Platform.MachineType))
        {
            args.Add("-M");
            args.Add(vm.Platform.MachineType);
        }
        if (!string.IsNullOrWhiteSpace(vm.Workload.ElfPath))
        {
            // A bare-metal ELF has no firmware/bootloader at all -
            // Firmware.FirmwarePath has no meaning for this workload and
            // is ignored (never combined with Workload.ElfPath in the UI
            // either, but this is the actual enforcement point).
            args.Add("-bios");
            args.Add("none");
        }
        else if (!string.IsNullOrWhiteSpace(vm.Firmware.FirmwarePath))
        {
            args.Add("-bios");
            args.Add(vm.Firmware.FirmwarePath);
        }

        if (vm.Debug.Display == "console")
        {
            // No gtk/sdl window at all - the guest's serial port (COM1)
            // becomes this process's own stdio, which ConsoleWindow then
            // shows live. "-monitor none" keeps QEMU's human monitor from
            // also trying to multiplex onto the same stream; QMP (above)
            // is this app's only control channel either way.
            args.Add("-display"); args.Add("none");
            args.Add("-serial"); args.Add("stdio");
            args.Add("-monitor"); args.Add("none");
        }
        else if (vm.Debug.Display == "vnc" && vncPort.HasValue)
        {
            // No gtk/sdl window at all either - the guest's screen is only
            // reachable over VNC, on its own TCP port. Unlike gtk/sdl,
            // closing a VNC client can never stop this VM: there is no
            // window for QEMU to treat a close as a quit signal for in
            // the first place. See RunningVm.VncPort's own remarks.
            args.Add("-display"); args.Add("none");
            args.Add("-vnc"); args.Add($"127.0.0.1:{vncPort - 5900}");
        }
        else
        {
            args.Add("-display"); args.Add(vm.Debug.Display);
        }

        if (!string.IsNullOrWhiteSpace(vm.Workload.DiskPath))
        {
            args.Add("-drive");
            args.Add($"file={vm.Workload.DiskPath},format={GuessFormat(vm.Workload.DiskPath)},if=virtio");
        }

        if (!string.IsNullOrWhiteSpace(vm.Workload.CdromPath))
        {
            args.Add("-cdrom");
            args.Add(vm.Workload.CdromPath);
        }

        // Boots this kernel directly, bypassing firmware/bootloader -
        // same -kernel/-initrd/-append trio riscv-emu's own --kernel/
        // --initrd/--append always use, now first-class Workload fields
        // for QEMU too instead of needing them hand-typed into
        // ExtraArgs (see EnvironmentMigration's own v1-to-v2 step for
        // an Environment that already had them there).
        if (!string.IsNullOrWhiteSpace(vm.Workload.ElfPath))
        {
            // No -initrd/-append: a bare-metal ELF has no kernel boot
            // protocol for either of those to be part of. QEMU's own ELF
            // loader respects the file's own e_entry regardless of our
            // own riscv-emu's --elf flag even existing - same loader,
            // same entry point, on both engines.
            args.Add("-kernel");
            args.Add(vm.Workload.ElfPath);
        }
        else if (!string.IsNullOrWhiteSpace(vm.Workload.KernelPath))
        {
            args.Add("-kernel");
            args.Add(vm.Workload.KernelPath);
            if (!string.IsNullOrWhiteSpace(vm.Workload.InitrdPath)) { args.Add("-initrd"); args.Add(vm.Workload.InitrdPath); }
            if (!string.IsNullOrWhiteSpace(vm.Workload.KernelCmdline)) { args.Add("-append"); args.Add(vm.Workload.KernelCmdline); }
        }

        if (gdbPort.HasValue)
        {
            // tcp::<port> binds every interface by default in real QEMU;
            // 127.0.0.1:<port> is loopback only - deliberately, same as
            // riscv-emu's own --gdb (a debug stub that can read/write
            // arbitrary guest memory has no business listening on
            // anything else).
            args.Add("-gdb");
            args.Add($"tcp:127.0.0.1:{gdbPort}");
            if (vm.Debug.GdbWaitForDebugger) args.Add("-S");
        }

        if (vm.Platform.NetworkMode == "user")
        {
            args.Add("-netdev");
            args.Add("user,id=net0");
            args.Add("-device");
            args.Add("e1000,netdev=net0");
        }
        else
        {
            args.Add("-nic");
            args.Add("none");
        }

        if (!string.IsNullOrWhiteSpace(vm.Workload.ExtraArgs))
            args.AddRange(ArgumentTokenizer.Split(vm.Workload.ExtraArgs));

        return args;
    }

    private static string GuessFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".qcow2" => "qcow2",
        ".vmdk" => "vmdk",
        ".vdi" => "vdi",
        ".vhdx" => "vhdx",
        _ => "raw",
    };
}
