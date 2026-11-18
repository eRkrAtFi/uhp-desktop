namespace QemuManager.Models;

/// <summary>A VM's run state, independent of which engine (QEMU or
/// riscv-emu) is actually running it - the UI's only concern is this enum;
/// how to get there is the engine's job (see Services.IVmEngineService).
/// Deliberately not a UI type: no color/Brush lives here (see
/// ARCHITECTURE_AUDIT-adjacent refactor notes) - a WPF view decides what
/// Stopped/Starting/Running/Stopping look like, Core only decides which one
/// is true.</summary>
public enum VmState { Stopped, Starting, Running, Stopping }
