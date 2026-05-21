using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SenseEase.App.Comfort;

namespace SenseEase.App;

public partial class App : Application
{
    private static LuxComfortController? _luxController;

    // Crash log: %LOCALAPPDATA%\SenseEase\crash.log — surface XAML binding errors,
    // background-thread exceptions, etc. without forcing the user to dig in Event Viewer.
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "crash.log");

    /// <summary>Called by MainViewModel.WireLuxComfort so SafeShutdownLux can reach
    /// the controller from any thread without touching WPF objects.</summary>
    public static void RegisterLuxController(LuxComfortController controller) =>
        _luxController = controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception, "DispatcherUnhandledException");
            SafeShutdownLux();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
            // Only restore when the runtime is terminating — non-terminating exceptions
            // on background threads must NOT shut the controller down or the display is
            // stuck at baseline while the app continues to run.
            if (args.IsTerminating) SafeShutdownLux();
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Unhandled Task exceptions: log but don't terminate (mark observed).
            LogCrash(args.Exception, "UnobservedTaskException");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeShutdownLux();
        SessionEnding += (_, _) => SafeShutdownLux();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SafeShutdownLux();
        base.OnExit(e);
    }

    /// <summary>Idempotent, thread-safe, never throws. The user's hard requirement
    /// "restore on close" routes through here on every exit path.</summary>
    private static void SafeShutdownLux()
    {
        try { SenseEase.Infrastructure.Real.WindowsActivityFilter.Instance.Shutdown(); } catch { }
        try { _luxController?.Shutdown(); }
        catch { /* never crash on shutdown */ }
    }

    /// <summary>Appends one timestamped record per crash. Best-effort: any IO failure
    /// here is silently swallowed — we are already in a crash path.</summary>
    private static void LogCrash(Exception? ex, string source)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] =====" + Environment.NewLine
                + ex.ToString() + Environment.NewLine + Environment.NewLine);
        }
        catch { /* never crash inside the crash handler */ }
    }
}
