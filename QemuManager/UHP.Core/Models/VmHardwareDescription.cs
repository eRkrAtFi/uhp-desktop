namespace QemuManager.Models;

/// <summary>The "Hardware" facts about one VM, in display-ready text, with
/// whatever each engine's real capabilities are already baked in - e.g.
/// riscv-emu's CPU is always "1 hart (fixed)" because that engine has no
/// SMP yet, not because the UI decided to say so. See
/// Services.IVmService.GetHardwareDescription. Deliberately plain strings,
/// not raw numbers/enums for the UI to reformat - every engine difference
/// belongs here, not in a WPF ternary on Kind.</summary>
public record VmHardwareDescription(
    string EngineDisplayName,
    string Architecture,
    string CpuDescription,
    string MemoryDescription,
    string DiskDescription,
    string NetworkDescription,
    string ConsoleDescription,
    string AcceleratorDescription);
