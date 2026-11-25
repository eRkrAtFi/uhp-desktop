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
/// Turns a <see cref="VmConfig"/> into a qemu-system-&lt;arch&gt;.exe process and
/// keeps track of every VM this app instance started. QEMU is the actual
/// virtualization engine here; this class only ever builds its command line,
/// launches it, and talks to it over QMP (see <see cref="QmpClient"/>) -
/// it does not emulate anything itself.
/// </summary>
public class VmProcessManager : IConsoleSource
{
    private readonly Dictionary<string, RunningVm> _running = new();

    public event Action? StateChanged;

    /// <summary>Raised on a background thread, not the UI thread - only
    /// fires for a VM started with Display == "console" (see
    /// <see cref="RunningVm.ConsoleMode"/>); a gtk/sdl VM has its own real
    /// window and nothing to pipe here.</summary>
    public event Action<string, byte[]>? OutputReceived;
    public event Action<string>? ProcessExited;

    public VmRunState GetState(string vmId) =>
        _running.TryGetValue(vmId, out var r) ? r.State : VmRunState.Stopped;

    public bool IsRunning(string vmId) => GetState(vmId) is VmRunState.Running or VmRunState.Starting or VmRunState.Stopping;

    public async Task<string> StartAsync(VmConfig vm, string qemuDir)
    {
        if (IsRunning(vm.Id)) return "VM is already running.";

        var binary = QemuLocator.SystemBinaryFor(qemuDir, vm.Architecture);
        if (binary == null)
            return $"No qemu-system-{vm.Architecture}.exe found in {qemuDir}.";

        var qmpPort = GetFreeTcpPort();
        var consoleMode = vm.Display == "console";
        // VNC display numbers are host-port-minus-5900 (QEMU's own
        // convention), not a port directly - picking a free TCP port first
        // and subtracting 5900 keeps this using the same no-collision
        // mechanism as qmpPort above instead of a second, different one.
        // GetFreeTcpPort's ephemeral-range ports (comfortably above 5900 on
        // Windows) keep that subtraction positive in practice.
        var vncPort = vm.Display == "vnc" ? GetFreeTcpPort() : (int?)null;
        var args = BuildArguments(vm, qmpPort, vncPort);

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

        var running = new RunningVm { Process = process, QmpPort = qmpPort, State = VmRunState.Starting, ConsoleMode = consoleMode, VncPort = vncPort };
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

    /// <summary>Graceful shutdown: asks the guest OS to power down over QMP,
    /// the same signal a real power button sends. May be ignored by the
    /// guest or simply take a while - this call only waits briefly before
    /// returning, it does not wait for the process to exit.</summary>
    public async Task<string> StopGracefullyAsync(VmConfig vm)
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
    public string ForceStop(VmConfig vm)
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
    public async Task<string> QueryGuestStatusAsync(VmConfig vm)
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
    public bool TryReattach(VmConfig vm)
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

    private static List<string> BuildArguments(VmConfig vm, int qmpPort, int? vncPort)
    {
        var args = new List<string>
        {
            "-name", vm.Name,
            "-m", vm.MemoryMB.ToString(),
            "-smp", vm.CpuCores.ToString(),
            "-accel", vm.Accelerator,
            "-qmp", $"tcp:127.0.0.1:{qmpPort},server,nowait",
            "-boot", $"order={vm.BootOrder}",
        };

        // Several architectures have no usable default machine type/firmware
        // (aarch64/arm/riscv* in particular need -M explicitly to boot
        // anything) - see VmConfig.MachineType/FirmwarePath and
        // ARCHITECTURE_AUDIT.md's recommendation #3. Both are opt-in and
        // empty for every VM saved before these fields existed, so this
        // changes nothing for an x86_64 VM relying on QEMU's own default.
        if (!string.IsNullOrWhiteSpace(vm.MachineType))
        {
            args.Add("-M");
            args.Add(vm.MachineType);
        }
        if (!string.IsNullOrWhiteSpace(vm.FirmwarePath))
        {
            args.Add("-bios");
            args.Add(vm.FirmwarePath);
        }

        if (vm.Display == "console")
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
        else if (vm.Display == "vnc" && vncPort.HasValue)
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
            args.Add("-display"); args.Add(vm.Display);
        }

        if (!string.IsNullOrWhiteSpace(vm.DiskPath))
        {
            args.Add("-drive");
            args.Add($"file={vm.DiskPath},format={GuessFormat(vm.DiskPath)},if=virtio");
        }

        if (!string.IsNullOrWhiteSpace(vm.CdromPath))
        {
            args.Add("-cdrom");
            args.Add(vm.CdromPath);
        }

        if (vm.NetworkMode == "user")
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

        if (!string.IsNullOrWhiteSpace(vm.ExtraArgs))
            args.AddRange(SplitArguments(vm.ExtraArgs));

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

    /// <summary>Splits a free-form extra-arguments string on whitespace,
    /// honoring double quotes - enough for the common case of a quoted path
    /// containing spaces, without pulling in a full shell parser.</summary>
    private static IEnumerable<string> SplitArguments(string input)
    {
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in input)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
