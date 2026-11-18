using System;

namespace QemuManager.Models;

/// <summary>One point-in-time read of a running VM's real, host-side
/// resource usage - see Services.IVmEngineService.GetSnapshot. All of this
/// is actual data from the OS process (CPU%/memory via
/// Services.ResourceUsageSampler, uptime via Process.StartTime), never
/// invented or estimated.</summary>
public readonly record struct VmSnapshot(VmState State, double CpuPercent, long MemoryBytes, TimeSpan? Uptime);
