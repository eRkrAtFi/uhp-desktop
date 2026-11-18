using System;
using System.IO;

namespace QemuManager.Services;

/// <summary>Writes an unhandled exception's full text (message, stack
/// trace, every inner exception - Exception.ToString() already includes
/// all of that) to its own timestamped file under AppPaths.AppDataDir's
/// own "logs" subfolder - %APPDATA%\QemuManager\logs by default, or next
/// to a portable install's own "data" folder when running portable, so a
/// portable install stays fully self-contained (copy the folder, logs
/// come with it) rather than leaving crash diagnostics behind in the
/// profile of whatever machine it happened to run on. This also means a
/// test/debug run that sets QEMUMANAGER_DATA_DIR (see AppPaths and
/// QemuManager.SelfTest) never touches the real %APPDATA% here either,
/// not even transiently - previously this path was hardcoded straight to
/// Environment.SpecialFolder.ApplicationData, bypassing that override
/// entirely, so SelfTest's own CrashLogger test was briefly writing into
/// (and then deleting from) the real profile on every run.</summary>
public static class CrashLogger
{
    public static string LogsDir { get; } = Path.Combine(AppPaths.AppDataDir, "logs");

    /// <summary>Returns the path written to, or "" if even this failed
    /// (e.g. no permission to write anywhere) - callers show the user a
    /// message either way; this never throws back at them.</summary>
    public static string LogException(Exception ex, string context)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            var path = Path.Combine(LogsDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(path, $"{DateTime.Now:O}  {context}{Environment.NewLine}{ex}{Environment.NewLine}");
            return path;
        }
        catch
        {
            return "";
        }
    }
}
