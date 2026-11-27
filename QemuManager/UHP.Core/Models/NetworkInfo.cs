namespace QemuManager.Models;

/// <summary>One Environment's own network configuration, read-only - the
/// Networking page's own list (Phase 6; no port forwarding yet - see
/// vision/NEXT.md). See Services.IVmService.GetNetworkInventory.</summary>
public record NetworkInfo(
    string EnvironmentName,
    string EngineDisplayName,
    /// <summary>"NAT" or "none" - Platform.NetworkMode's own values
    /// ("user"/anything else) translated to display-ready text, the same
    /// convention VmHardwareDescription already uses throughout.</summary>
    string Mode,
    /// <summary>The guest's own address under NAT - both engines' default
    /// user-mode network is 10.0.2.0/24 (QEMU's own long-standing
    /// built-in default; riscv-emu's net.c deliberately matches it, see
    /// its own NET_GUEST_IP) - null for Mode == "none".</summary>
    string? GuestAddress,
    /// <summary>The gateway/DNS address under NAT - null for Mode ==
    /// "none".</summary>
    string? GatewayAddress);
