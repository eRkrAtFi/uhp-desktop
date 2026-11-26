namespace QemuManager.Models;

/// <summary>Consoles, and (later) UART/GDB/tracing/logs - see
/// vision/VISION.md's own data-model table ("Debug configuration |
/// Consoles, UART, GDB, tracing, logs"). Only the console settings exist
/// so far (what this app's own terminal needs); a GDB stub is riscv-emu
/// work that does not exist yet (roadmap item 3, alongside the bare-metal
/// workload - see vision/NEXT.md) and tracing/logs are not modeled until
/// there is a real feature behind them to configure.</summary>
public class DebugConfiguration
{
    /// <summary>QEMU only: "gtk"/"sdl" (QEMU's own window), "console"
    /// (text console in this app), or "vnc" (headless). riscv-emu has
    /// only ever had one display - its own guest UART, always a console -
    /// so this does not apply to it.</summary>
    public string Display { get; set; } = "gtk";

    /// <summary>What the terminal's own Enter key sends down this
    /// Environment's console: "CR" (default, matching a real terminal),
    /// "LF", or "CRLF" - see the console TerminalControl's own remarks on
    /// why this needs to be configurable per guest at all.</summary>
    public string EnterSends { get; set; } = "CR";
}
