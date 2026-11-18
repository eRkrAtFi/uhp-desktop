using System;
using System.Diagnostics;

namespace QemuManager.Services;

/// <summary>Shared CPU%/memory sampling logic that VmProcessManager and
/// RiscvEmuProcessManager's GetResourceUsage both need: turning
/// Process.TotalProcessorTime (a monotonically increasing total) into a
/// live percent (0-100, already divided by core count) against the
/// previous sample, plus the process's current working-set memory. Each
/// caller still owns its own LastCpuTime/LastSampleTime storage
/// (RunningVm/RunningRiscvEmu) - this type has no per-VM state of its own,
/// it only does the arithmetic both of them were repeating identically.</summary>
internal static class ResourceUsageSampler
{
    /// <summary>Returns null if reading the process failed (e.g. it exited
    /// between the caller's own HasExited check and this call) - same
    /// "resource usage is simply unavailable right now" meaning both
    /// GetResourceUsage callers already give that to their own callers.</summary>
    public static (double cpuPercent, long memoryBytes, TimeSpan cpuTime, DateTime sampleTime)? Sample(
        Process process, TimeSpan lastCpuTime, DateTime lastSampleTime)
    {
        try
        {
            process.Refresh();
            var now = DateTime.UtcNow;
            var cpuTime = process.TotalProcessorTime;
            var wallElapsed = (now - lastSampleTime).TotalMilliseconds;
            var cpuElapsed = (cpuTime - lastCpuTime).TotalMilliseconds;
            var cpuPercent = wallElapsed > 0 ? cpuElapsed / wallElapsed / Environment.ProcessorCount * 100.0 : 0;
            return (Math.Max(0, cpuPercent), process.WorkingSet64, cpuTime, now);
        }
        catch
        {
            return null;
        }
    }
}
