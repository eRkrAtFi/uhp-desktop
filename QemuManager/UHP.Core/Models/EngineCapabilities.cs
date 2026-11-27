namespace QemuManager.Models;

/// <summary>What an engine can actually do - vision/VISION.md's roadmap
/// item 1 ("capabilities"). Distinct from VmHardwareDescription's own
/// SupportsGuestDetail/SupportsSpeedMetric (display-ready facts about one
/// particular running Environment) - this is static, per-engine, and
/// read by EnvironmentEditWindow/NewEnvironmentWindow to decide which
/// templates and config-schema groups make sense for a given engine,
/// before any Environment exists yet to ask GetHardwareDescription about.</summary>
public record EngineCapabilities(
    /// <summary>Multiple display backends to choose from (QEMU: gtk/sdl/
    /// console/vnc) vs. a single, fixed console (riscv-emu).</summary>
    bool SupportsMultipleDisplayModes,
    bool SupportsDisk,
    bool SupportsCdrom,
    bool SupportsKernelBoot,
    bool SupportsMachineType,
    bool SupportsFirmware,
    bool SupportsNetwork,
    /// <summary>More than one CPU core - riscv-emu is fixed at one hart.</summary>
    bool SupportsSmp,
    bool SupportsGuestDetail,
    bool SupportsSpeedMetric,
    /// <summary>A bare ELF workload with no kernel/firmware (the
    /// "Bare-metal / Embedded" template) and a GDB stub to attach a
    /// debugger to it - roadmap item 3. True for both engines today.</summary>
    bool SupportsBareMetalElf);
