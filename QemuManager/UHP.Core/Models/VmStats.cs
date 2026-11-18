using System;

namespace QemuManager.Models;

/// <summary>One push of live data for one VM - see Services.IVmService's
/// StatsReceived event. CpuPercent/MemoryBytes/Uptime/GuestDetail apply to
/// either engine (GuestDetail is always null for riscv-emu - no QMP-
/// equivalent channel exists there); Instructions/Mips are only ever
/// non-null for a riscv-emu VM, straight from its own "riscv-emu: stats
/// ..." stderr line (see riscv-emu/src/main.c), not estimated.</summary>
public readonly record struct VmStats(
    VmState State,
    double CpuPercent,
    long MemoryBytes,
    TimeSpan? Uptime,
    string? GuestDetail,
    ulong? Instructions,
    double? Mips);
