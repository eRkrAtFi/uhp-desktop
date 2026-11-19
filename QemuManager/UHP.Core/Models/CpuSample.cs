using System;

namespace QemuManager.Models;

/// <summary>One point in a VM's CPU-history graph (see
/// Services.IVmService.GetCpuHistory) - host CPU% at a point in time,
/// the same number VmStats.CpuPercent already reports live, just kept
/// around long enough to plot.</summary>
public record CpuSample(DateTime TimestampUtc, double CpuPercent);
