using System;
using System.IO;

namespace QemuManager.Services;

/// <summary>Finds lldb.exe (LLVM's own debugger, what the detail page's
/// "Copy lldb command" button builds a ready-made attach command for -
/// see VmListItem.GdbAttachCommand) - not a VM engine the way
/// QemuLocator/RiscvEmuLocator are, but the same "where is this actually
/// installed" concern, so the same shape.</summary>
public static class LldbLocator
{
    /// <summary>"lldb" if it is already reachable via PATH (the shortest,
    /// most portable command - correct even if LLVM is installed
    /// somewhere other than its own installer's default location);
    /// otherwise the full path to LLVM's own installer default
    /// (C:\Program Files\LLVM\bin\lldb.exe) if that file actually
    /// exists, quoted for a shell; otherwise still just "lldb" (nothing
    /// to point at instead - the copied command may not work, but there
    /// is no better guess to make).</summary>
    public static string ExePathForCommandLine()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "lldb.exe")))
                return "lldb";
        }
        const string defaultPath = @"C:\Program Files\LLVM\bin\lldb.exe";
        return File.Exists(defaultPath) ? $"\"{defaultPath}\"" : "lldb";
    }
}
