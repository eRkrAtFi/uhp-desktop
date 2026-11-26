namespace QemuManager.Models;

/// <summary>Consoles, GDB, and (later) tracing/logs - see
/// vision/VISION.md's own data-model table ("Debug configuration |
/// Consoles, UART, GDB, tracing, logs"). Tracing/logs are not modeled
/// until there is a real feature behind them to configure.</summary>
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

    /// <summary>Serve a GDB Remote Serial Protocol stub for this
    /// Environment - riscv-emu's own --gdb (src/gdbstub.c), or QEMU's own
    /// -gdb tcp::&lt;port&gt; - roadmap item 3. Loopback only on both
    /// engines; see VmService.GetGdbEndpoint for the actual "127.0.0.1:
    /// &lt;port&gt;" a running Environment is listening on.</summary>
    public bool GdbEnabled { get; set; }

    /// <summary>0 means "pick any free port" (the same mechanism
    /// Display == "vnc" already uses) - resolved once at Start time, not
    /// written back here, so this stays 0 across saves/reloads unless the
    /// user deliberately pins a specific port.</summary>
    public int GdbPort { get; set; }

    /// <summary>Stop before the first instruction (riscv-emu's own
    /// --gdb-wait, QEMU's own -S) instead of running immediately once a
    /// debugger attaches - see riscv-emu's own --gdb-wait remarks (main.c)
    /// for why a short program usually wants this.</summary>
    public bool GdbWaitForDebugger { get; set; }
}
