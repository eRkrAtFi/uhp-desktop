using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QemuManager.Services;

/// <summary>Finds QEMU's own binaries inside the user-configured install
/// directory. This app never bundles or installs QEMU itself - it is the
/// "virtualization core" this app drives, located at run time.</summary>
public static class QemuLocator
{
    private static readonly Regex SystemExeName =
        new(@"^qemu-system-(?<arch>.+?)(?<windowed>w)?\.exe$", RegexOptions.IgnoreCase);

    public static bool LooksLikeQemuDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        return Directory.EnumerateFiles(dir, "qemu-system-*.exe").Any();
    }

    /// <summary>Lists every architecture qemu-system-&lt;arch&gt;.exe offers in
    /// <paramref name="qemuDir"/>, e.g. "x86_64", "aarch64", "arm".</summary>
    public static List<string> ListArchitectures(string qemuDir)
    {
        var archs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(qemuDir)) return new List<string>();

        foreach (var file in Directory.EnumerateFiles(qemuDir, "qemu-system-*.exe"))
        {
            var match = SystemExeName.Match(Path.GetFileName(file));
            if (match.Success) archs.Add(match.Groups["arch"].Value);
        }

        // x86_64 first since it is what most users want; the rest alphabetically.
        return archs.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(a => a.Equals("x86_64", StringComparison.OrdinalIgnoreCase))
                    .ToList();
    }

    /// <summary>Full path to qemu-system-&lt;arch&gt;.exe. Prefers the "w"
    /// (windowed-subsystem) build so starting a VM does not flash a console
    /// window behind this app; falls back to the plain build if that is all
    /// that was shipped.</summary>
    public static string? SystemBinaryFor(string qemuDir, string architecture)
    {
        var windowed = Path.Combine(qemuDir, $"qemu-system-{architecture}w.exe");
        if (File.Exists(windowed)) return windowed;
        var plain = Path.Combine(qemuDir, $"qemu-system-{architecture}.exe");
        return File.Exists(plain) ? plain : null;
    }

    public static string? ImgBinary(string qemuDir)
    {
        var path = Path.Combine(qemuDir, "qemu-img.exe");
        return File.Exists(path) ? path : null;
    }
}
