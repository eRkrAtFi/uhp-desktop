using System.Reflection;

namespace QemuManager.Services;

/// <summary>Reads the version string QemuManager.csproj's own
/// SetSourceRevisionId target bakes in at build time ("0.1.0+&lt;short
/// commit hash&gt;[-dirty].built-&lt;UTC timestamp&gt;") - the same string a
/// published .exe's own "Product Version" file property shows (right
/// click > Properties > Details in Explorer, or
/// (Get-Item QemuManager.exe).VersionInfo.ProductVersion from a script,
/// which is exactly what finish.ps1's own last line does). One place
/// reads it so SettingsWindow and CrashLogger never drift apart on the
/// format.</summary>
public static class AppVersion
{
    public static string DisplayString { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
