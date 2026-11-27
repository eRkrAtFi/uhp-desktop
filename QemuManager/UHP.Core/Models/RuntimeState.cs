using System;

namespace QemuManager.Models;

/// <summary>
/// The minimum needed to re-discover a VM's actual OS process after this
/// app restarts (crash, update, manual kill) without it having seen that
/// process exit. Written by VmProcessManager/RiscvEmuProcessManager next to
/// the VM's own config (see Services/RuntimeStateStore), deliberately kept
/// out of EnvironmentConfig itself since it is runtime state, not part of the VM's
/// persisted definition.
/// </summary>
public class RuntimeState
{
    public int Pid { get; set; }

    /// <summary>The OS process name (Process.ProcessName, no ".exe") the PID
    /// is expected to belong to - e.g. "qemu-system-x86_64" or "riscv-emu".
    /// Checked on reattach because Windows recycles PIDs: a PID this file
    /// remembers could belong to an unrelated process by the time the app
    /// reads it back, if enough time and process churn happened in between.</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>QEMU's QMP port, so a reattached VM can still be asked for a
    /// graceful shutdown or live status. Null for a RiscvEmu VM - it has no
    /// QMP-equivalent control channel.</summary>
    public int? QmpPort { get; set; }

    /// <summary>QEMU's VNC port (see VmProcessManager.RunningVm.VncPort),
    /// so a reattached VM started with Display == "vnc" can still report
    /// where to connect. Null for every other Display value and for every
    /// RiscvEmu VM.</summary>
    public int? VncPort { get; set; }

    /// <summary>The resolved GDB stub port (see
    /// VmProcessManager/RiscvEmuProcessManager.RunningVm's own GdbPort),
    /// so a reattached Environment started with Debug.GdbEnabled can
    /// still report where to attach a debugger. Null unless
    /// Debug.GdbEnabled was set.</summary>
    public int? GdbPort { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
}
