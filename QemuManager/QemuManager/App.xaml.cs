using System;
using System.Configuration;
using System.Data;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using QemuManager.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace QemuManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>Every unhandled-exception channel .NET/WPF has, wired to
    /// the same CrashLogger (see UHP.Core) - a crash on any one of these
    /// used to mean the user's only information was the window silently
    /// disappearing, with every running VM's status gone from view along
    /// with it even though the VMs themselves kept running untouched.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Exceptions that escape a UI-thread event handler (a Button.Click
        // handler, for instance) - the one a double-click-triggered race
        // would actually surface through. Handled = true tells WPF the
        // crash was dealt with and the app may keep running; set it only
        // after logging, and only because by this point the exception has
        // already unwound out of whatever it was doing - nothing further
        // down that call stack still expects to run.
        DispatcherUnhandledException += (_, args) =>
        {
            var path = CrashLogger.LogException(args.Exception, "DispatcherUnhandledException");
            ShowCrashMessage(args.Exception, path);
            args.Handled = true;
        };

        // Exceptions on any other thread (a background Task that was not
        // awaited, a Timer callback, ...) - .NET terminates the process for
        // these regardless of anything a handler does; this exists only so
        // the log file still gets written before that happens.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLogger.LogException(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown non-Exception object"), "AppDomain.UnhandledException");

        // A Task whose exception nobody ever observed (no one awaited it
        // or checked .Exception) - finalized long after the fact, on the
        // finalizer thread. Marking it observed here keeps that (separate,
        // older runtime behavior) finalizer-thread crash from ever
        // happening at all, not just from going unlogged.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLogger.LogException(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };
    }

    private static void ShowCrashMessage(Exception ex, string logPath)
    {
        var where = string.IsNullOrEmpty(logPath) ? "" : $"\n\nDetails were logged to:\n{logPath}";
        MessageBox.Show(
            $"Something went wrong: {ex.Message}\n\nThe application will try to keep running - your VMs are unaffected.{where}",
            "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
