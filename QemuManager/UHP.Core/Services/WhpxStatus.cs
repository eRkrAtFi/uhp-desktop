using System;
using System.Runtime.InteropServices;

namespace QemuManager.Services;

/// <summary>Whether Windows Hypervisor Platform (QEMU's own -accel whpx)
/// is actually usable on this machine right now - the Host/Engines
/// pages' own "WHPX" status (Phase 6). Calls the same WinHvPlatform.dll
/// API (WHvGetCapability, WHvCapabilityCodeHypervisorPresent) QEMU's own
/// whpx accelerator checks before it will even try to create a
/// partition, rather than inferring it from the "Windows Hypervisor
/// Platform" optional feature's own install state - that can be
/// "enabled" and still not actually be usable (firmware-level
/// virtualization disabled, or some other conflicting hypervisor
/// state), so asking the same question QEMU itself would ask is the
/// only way to answer this that cannot disagree with what Power On
/// with Platform.Accelerator == "whpx" is actually about to find out.
/// No admin rights needed - this is a read-only capability query, not a
/// feature-state change.</summary>
public static class WhpxStatus
{
    private const int WHvCapabilityCodeHypervisorPresent = 0x00000000;

    [DllImport("WinHvPlatform.dll")]
    private static extern int WHvGetCapability(int capabilityCode, byte[] capabilityBuffer, uint bufferSizeInBytes, out uint writtenSizeInBytes);

    /// <summary>True if WHPX is present and usable. False covers both
    /// "not installed at all" (WinHvPlatform.dll itself fails to load -
    /// the optional feature was never turned on) and "installed but not
    /// currently usable" alike - QEMU's own -accel whpx would fail to
    /// start either way, and there is no reason to tell those two apart
    /// for the user here.</summary>
    public static bool IsEnabled()
    {
        try
        {
            var buffer = new byte[4];
            var hr = WHvGetCapability(WHvCapabilityCodeHypervisorPresent, buffer, (uint)buffer.Length, out _);
            return hr == 0 && BitConverter.ToInt32(buffer, 0) != 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (Exception) { return false; }
    }
}
