using System;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>
/// Whatever a <see cref="ConsoleWindow"/> needs from a process manager to
/// show one VM's text console live and let the user force-stop it - shared
/// by <see cref="RiscvEmuProcessManager"/> (always console-only - there is
/// no other display) and <see cref="VmProcessManager"/> (only when a VM's
/// own Display setting is "console" rather than a real gtk/sdl window).
/// </summary>
public interface IConsoleSource
{
    /// <summary>Raised on a background thread, not the UI thread - callers
    /// must marshal back via Dispatcher themselves.</summary>
    event Action<string, byte[]>? OutputReceived;

    /// <summary>Raised (vmId) once that VM's process has actually exited -
    /// whether the user force-stopped it, or the guest shut itself down on
    /// its own (e.g. typing "exit"/"poweroff" at its own shell, or a QEMU
    /// VM's ACPI powerdown completing). Also a background-thread event.</summary>
    event Action<string>? ProcessExited;

    void SendInput(string vmId, byte[] data);

    string ForceStop(VmConfig vm);
}
