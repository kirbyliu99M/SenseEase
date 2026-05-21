using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Smoke test: instantiate MainWindow on an STA thread, run a real WPF dispatcher loop
/// for a short window, and fail if any XAML binding error / TypeInitializer / unhandled
/// exception fires during construction or first layout. Catches regressions like the
/// `VimsTier` TwoWay-binding-to-read-only-property bug that killed the UI thread silently.
/// </summary>
public class MainWindowSmokeTests
{
    [Fact]
    public void MainWindow_Constructs_And_FirstLayout_DoesNotThrow()
    {
        Exception? captured = null;
        var done = new ManualResetEventSlim();

        var staThread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };

                DispatcherUnhandledExceptionEventHandler onDispatch = (_, args) =>
                {
                    captured ??= args.Exception;
                    args.Handled = true;
                };
                app.DispatcherUnhandledException += onDispatch;

                var window = new SenseEase.App.MainWindow();
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000; window.Top = -10000;
                window.ShowInTaskbar = false;

                // Schedule shutdown after the window has had a chance to render + first
                // few timer ticks fire (~600 ms is enough to surface most binding errors).
                var dispatcher = Dispatcher.CurrentDispatcher;
                var shutdownTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(700)
                };
                shutdownTimer.Tick += (_, _) =>
                {
                    shutdownTimer.Stop();
                    try { window.Close(); } catch { /* ignore */ }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                shutdownTimer.Start();

                window.Show();
                Dispatcher.Run();

                app.DispatcherUnhandledException -= onDispatch;
            }
            catch (Exception ex)
            {
                captured ??= ex;
            }
            finally
            {
                done.Set();
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(20)), "MainWindow smoke test timed out");
        if (captured != null)
            throw new Xunit.Sdk.XunitException(
                "MainWindow construction or first layout threw: " + captured);
    }
}
