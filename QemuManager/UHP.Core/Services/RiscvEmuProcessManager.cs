using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using QemuManager.Models;

namespace QemuManager.Services;

public enum RiscvEmuRunState { Stopped, Running, Stopping }

/// <summary>One "riscv-emu: stats insns=... secs=... mips=... pc=..." sample
/// - see src/main.c's periodic report. Pc is kept as the raw hex text (a
/// 64-bit guest address has no other use here than display).</summary>
public readonly record struct RiscvEmuStats(ulong Instructions, double Seconds, double Mips, string Pc);

/// <summary>One riscv-emu.exe this process has launched.</summary>
public class RunningRiscvEmu
{
    public required Process Process { get; init; }
    public RiscvEmuRunState State { get; set; } = RiscvEmuRunState.Running;

    /// <summary>Previous CPU-time sample - see RiscvEmuProcessManager.GetResourceUsage.</summary>
    public TimeSpan LastCpuTime { get; set; }
    public DateTime LastSampleTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Turns a <see cref="VmConfig"/> of kind RiscvEmu into a riscv-emu.exe
/// process. Unlike real QEMU there is no QMP control channel and no guest
/// ACPI power button to ask for a graceful shutdown - the guest's own
/// console is the only channel, so a graceful stop here means typing
/// VmConfig.ShutdownCommand at it and waiting for the guest to act on it
/// (see ShutdownGracefullyAsync). ForceStop remains the only *immediate*
/// way to stop one, for when a graceful wait is not what is wanted or
/// has already timed out.
/// </summary>
public class RiscvEmuProcessManager : IConsoleSource
{
    private readonly Dictionary<string, RunningRiscvEmu> _running = new();

    public event Action? StateChanged;

    /// <summary>Raised on this manager's own thread-pool read loop, not the
    /// UI thread - subscribers must marshal back via Dispatcher themselves.
    /// Guest UART bytes (riscv-emu.exe's stdout) plus riscv-emu's own
    /// genuine diagnostics from stderr (a failed --kernel/--disk path, an
    /// unimplemented instruction, ...) - see ReadStderrAsync for exactly
    /// what does and does not get forwarded from stderr. The routine
    /// per-200k-instruction progress/stats noise never reaches here, so
    /// this stays close to "what a person watching the VM needs to see,"
    /// not a raw stderr tee.</summary>
    public event Action<string, byte[]>? OutputReceived;
    /// <summary>Periodic instruction-count/MIPS samples, parsed from
    /// riscv-emu.exe's own "riscv-emu: stats ..." stderr lines (see
    /// src/main.c) - the separate, machine-readable channel a stats tile
    /// reads instead of screen-scraping the console.</summary>
    public event Action<string, RiscvEmuStats>? StatsReceived;
    public event Action<string>? ProcessExited;

    public RiscvEmuRunState GetState(string vmId) =>
        _running.TryGetValue(vmId, out var r) ? r.State : RiscvEmuRunState.Stopped;

    /// <summary>True for Running AND Stopping - the process is genuinely
    /// still alive during a graceful-shutdown wait (see
    /// ShutdownGracefullyAsync), same convention VmProcessManager's own
    /// IsRunning already uses for VmRunState.Stopping.</summary>
    public bool IsRunning(string vmId) => GetState(vmId) is RiscvEmuRunState.Running or RiscvEmuRunState.Stopping;

    public string Start(VmConfig vm, string riscvEmuDir)
    {
        if (IsRunning(vm.Id)) return "Already running.";

        RiscvEmuBoot.BuildResult build;
        try
        {
            build = RiscvEmuBoot.Build(vm, riscvEmuDir);
        }
        catch (Exception ex)
        {
            return $"Could not build the boot command line: {ex.Message}";
        }

        var psi = new ProcessStartInfo
        {
            FileName = build.ExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = riscvEmuDir,
        };
        foreach (var a in build.Arguments) psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            return $"Failed to start riscv-emu: {ex.Message}";
        }

        var running = new RunningRiscvEmu { Process = process, State = RiscvEmuRunState.Running };
        _running[vm.Id] = running;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(vm.Id);

        // So a later app restart can tell this VM is still running instead
        // of reporting it Stopped - see VmProcessManager.TryReattach, which
        // this mirrors (riscv-emu has no QMP port to remember).
        RuntimeStateStore.Save(vm.Id, new RuntimeState
        {
            Pid = process.Id,
            ProcessName = process.ProcessName,
        });

        // UART output (the guest console) goes to riscv-emu's own stdout,
        // read as raw bytes (not through a text StreamReader) so a text
        // reader's own decoding can't throw or substitute characters on
        // whatever non-UTF8 bytes a guest program happens to write. stderr
        // carries riscv-emu's own diagnostics and periodic stats, not any
        // guest output, and is read as text instead - see ReadStderrAsync.
        _ = PumpAsync(vm.Id, process.StandardOutput.BaseStream);
        _ = ReadStderrAsync(vm.Id, process.StandardError);

        StateChanged?.Invoke();
        return "Started.";
    }

    /// <summary>Sends raw bytes to the guest's console input (riscv-emu's
    /// UART now has a real receive side - see riscv-emu/README.md's
    /// "Console input" section). One plain write, not byte-paced like
    /// VmProcessManager's own SendInput (real QEMU's console still needs
    /// that - see fs_compare.sh's own findings). riscv-emu's own
    /// StdinRing used to silently drop bytes once it filled up faster
    /// than the guest drained it, which per-byte pacing from here papered
    /// over - fixed properly instead (the ring now waits for room rather
    /// than dropping, see emu.h/main.c's own comments), proven lossless
    /// for a single large (4096-byte), completely unpaced write by
    /// riscv-emu/boot/burst_test.sh, so this no longer needs to pace
    /// anything itself.</summary>
    public void SendInput(string vmId, byte[] data)
    {
        if (!_running.TryGetValue(vmId, out var running) || running.Process.HasExited) return;
        var stream = running.Process.StandardInput.BaseStream;
        _ = SendAsync(stream, data);
    }

    private static async Task SendAsync(Stream stream, byte[] data)
    {
        try
        {
            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();
        }
        catch
        {
            // Not actionable for the user mid-console-session; the process-
            // exited handler is the source of truth for whether it's gone.
        }
    }

    /// <summary>Real host-side CPU percent (0-100, already divided by core
    /// count) and working-set memory for the riscv-emu.exe process itself -
    /// the interpreter's own usage, which is what actually costs the host
    /// something; the emulated guest's own notion of its resources (its
    /// fixed 256 MiB, one hart) is a separate, much less useful number.
    /// Returns null for a VM that is not currently running.</summary>
    public (double cpuPercent, long memoryBytes)? GetResourceUsage(string vmId)
    {
        if (!_running.TryGetValue(vmId, out var running) || running.Process.HasExited) return null;
        var sample = ResourceUsageSampler.Sample(running.Process, running.LastCpuTime, running.LastSampleTime);
        if (sample == null) return null;
        running.LastCpuTime = sample.Value.cpuTime;
        running.LastSampleTime = sample.Value.sampleTime;
        return (sample.Value.cpuPercent, sample.Value.memoryBytes);
    }

    /// <summary>See VmProcessManager.GetUptime - same reasoning, same
    /// Process.StartTime source.</summary>
    public TimeSpan? GetUptime(string vmId)
    {
        if (!_running.TryGetValue(vmId, out var running)) return null;
        try { return running.Process.HasExited ? null : DateTime.Now - running.Process.StartTime; }
        catch { return null; }
    }

    /// <summary>Types VmConfig.ShutdownCommand at the guest's own console
    /// and waits (up to <paramref name="timeout"/>) for the real
    /// riscv-emu.exe process to exit on its own in response - a real
    /// shutdown, not a kill. Does NOT force-stop anything itself, on
    /// success or on timeout: a VM that does not respond within the
    /// timeout is reported back as TimedOut=true and left exactly as it
    /// was (still Running) for the caller to decide what to do (the UI
    /// offers Force Stop as a separate, explicit action - see
    /// MainWindow.StopButton_Click). Auto-force-stopping here would risk
    /// corrupting a filesystem on an attached disk the guest had not
    /// actually finished unmounting - the entire reason this method, and
    /// not ForceStop, exists for a Shut Down click in the first place.
    ///
    /// State is Stopping for the duration of the wait (mirrors
    /// VmProcessManager.StopGracefullyAsync's own VmRunState.Stopping for
    /// QEMU) - IsRunning stays true throughout, so Force Stop (and
    /// anything else gated on "is this VM running") keeps working
    /// normally while this is in progress.</summary>
    public async Task<ShutdownResult> ShutdownGracefullyAsync(VmConfig vm, TimeSpan timeout)
    {
        if (!_running.TryGetValue(vm.Id, out var running) || running.Process.HasExited)
            return new ShutdownResult("Not running.", false);

        running.State = RiscvEmuRunState.Stopping;
        StateChanged?.Invoke();

        SendInput(vm.Id, Encoding.UTF8.GetBytes(vm.ShutdownCommand + "\n"));

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(timeout);
            // OnProcessExited (wired to Process.Exited in Start()) still
            // fires normally and does the real state/_running cleanup;
            // this just waits for that to happen and reports success.
            await running.Process.WaitForExitAsync(cts.Token);
            return new ShutdownResult("Guest shut down.", false);
        }
        catch (OperationCanceledException)
        {
            // Still running - revert Stopping back to Running (not
            // Stopped: nothing died) rather than leaving a transient
            // label stuck forever. Guarded on `current == running` the
            // same way VmProcessManager.RevertStoppingIfStillRunningAsync
            // is, in case this VM was somehow stopped and started again
            // during the wait.
            if (_running.TryGetValue(vm.Id, out var current) && current == running && current.State == RiscvEmuRunState.Stopping)
            {
                current.State = RiscvEmuRunState.Running;
                StateChanged?.Invoke();
            }
            return new ShutdownResult($"Guest did not shut down within {timeout.TotalSeconds:0}s of \"{vm.ShutdownCommand}\".", true);
        }
    }

    /// <summary>Immediately kills the riscv-emu.exe process - no console
    /// command, no wait. For a graceful stop that gives the guest a real
    /// chance to shut down on its own first, see ShutdownGracefullyAsync;
    /// this is what that offers as a fallback once its own wait times
    /// out, and what it stays otherwise unless explicitly called.</summary>
    public string ForceStop(VmConfig vm)
    {
        if (!_running.TryGetValue(vm.Id, out var running)) return "Not running.";
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

    private void OnProcessExited(string vmId)
    {
        if (_running.TryGetValue(vmId, out var running))
        {
            running.State = RiscvEmuRunState.Stopped;
            _running.Remove(vmId);
        }
        RuntimeStateStore.Delete(vmId);
        StateChanged?.Invoke();
        ProcessExited?.Invoke(vmId);
    }

    /// <summary>Called once per VM at app startup (see MainWindow.LoadVms) -
    /// mirrors VmProcessManager.TryReattach for the riscv-emu engine. Its
    /// console has no live output or input either, for the same reason
    /// VmProcessManager's reattached VMs don't: the original process's
    /// stdio pipes belonged to the app instance that started it, not this
    /// one.</summary>
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
                RuntimeStateStore.Delete(vm.Id);
                return false;
            }
        }
        catch (ArgumentException)
        {
            RuntimeStateStore.Delete(vm.Id);
            return false;
        }

        // LastCpuTime must start from the process's *current* accumulated
        // total, not the Zero a freshly-started process correctly gets -
        // see VmProcessManager.TryReattach's own remarks (same bug,
        // confirmed first here via QemuManager.SelfTest's RiscvEmu
        // reattach test: a nonsense 90000%+ CPU reading).
        var running = new RunningRiscvEmu
        {
            Process = process,
            State = RiscvEmuRunState.Running,
            LastCpuTime = process.TotalProcessorTime,
        };
        _running[vm.Id] = running;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(vm.Id);

        if (process.HasExited)
        {
            OnProcessExited(vm.Id);
            return false;
        }

        StateChanged?.Invoke();
        return true;
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
            // The pipe going away just means the process exited; OnProcessExited
            // (wired to Process.Exited) is the source of truth for that, not this.
        }
    }

    /// <summary>stderr carries only riscv-emu's own text (never guest UART
    /// bytes - see Start's own remarks), so this reads it as text, not raw
    /// bytes. Three kinds of line come through here: the machine-readable
    /// "riscv-emu: stats ..." sample (src/main.c), which is parsed and
    /// raised as StatsReceived, not shown; the older, purely human-
    /// readable "riscv-emu: NNN insns, ... pc=..." progress line printed
    /// right next to it (same numbers, different format - see src/main.c's
    /// own comment on why both exist), which is redundant with the one
    /// above and is dropped; and everything else - "loaded ... at 0x...",
    /// "unknown option", a bad --kernel/--disk path's bare perror() line,
    /// the instruction-limit-reached notice - which is a genuine diagnostic
    /// nothing else currently surfaces, so it is forwarded to OutputReceived
    /// like any other console text rather than silently dropped. A guest
    /// that never produces any of its own stdout (a kernel Image that
    /// fails to even start) would otherwise leave the person looking at a
    /// blank console with no explanation at all.</summary>
    private async Task ReadStderrAsync(string vmId, StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var stats = TryParseStats(line);
                if (stats is { } s) { StatsReceived?.Invoke(vmId, s); continue; }
                if (IsNoisyProgressLine(line)) continue;
                OutputReceived?.Invoke(vmId, Encoding.UTF8.GetBytes(line + "\n"));
            }
        }
        catch
        {
            // Same reasoning as PumpAsync's own catch - OnProcessExited is
            // the source of truth for the process having gone away.
        }
    }

    private static bool IsNoisyProgressLine(string line) =>
        line.StartsWith("riscv-emu: ", StringComparison.Ordinal) && line.Contains(" insns, ", StringComparison.Ordinal);

    private static RiscvEmuStats? TryParseStats(string line)
    {
        const string prefix = "riscv-emu: stats ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;

        ulong insns = 0; double secs = 0, mips = 0; string pc = "";
        foreach (var field in line.AsSpan(prefix.Length).ToString().Split(' '))
        {
            var eq = field.IndexOf('=');
            if (eq < 0) continue;
            var key = field[..eq];
            var value = field[(eq + 1)..];
            switch (key)
            {
                case "insns": ulong.TryParse(value, out insns); break;
                case "secs": double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out secs); break;
                case "mips": double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out mips); break;
                case "pc": pc = value; break;
            }
        }
        return new RiscvEmuStats(insns, secs, mips, pc);
    }
}
