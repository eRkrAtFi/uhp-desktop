namespace QemuManager.Models;

/// <summary>What clicking "Console" on a VM should actually do, decided by
/// the owning engine (riscv-emu is always Stream; QEMU depends on its own
/// Display setting) rather than by the UI asking "is this RiscvEmu?" - see
/// Services.IVmEngineService.GetConsoleAvailability.</summary>
public enum ConsoleAvailability
{
    /// <summary>VM is not running - nothing to show.</summary>
    NotRunning,
    /// <summary>A live text stream this app can render itself (riscv-emu's
    /// guest UART, or a QEMU VM with Display == "console").</summary>
    Stream,
    /// <summary>A QEMU VM with Display == "vnc" - no stream here, connect a
    /// VNC client to the endpoint instead.</summary>
    Vnc,
    /// <summary>A QEMU VM with Display == "gtk"/"sdl" - it owns its own
    /// window; there is nothing for this app to show.</summary>
    ExternalWindow,
}
