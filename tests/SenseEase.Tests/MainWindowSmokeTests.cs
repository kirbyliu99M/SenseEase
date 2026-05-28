using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SenseEase.Core.Models;
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
                // few timer ticks fire + the v0.9.3 cross-thread regression block has
                // a chance to run (~900 ms gives 150ms × 3 fires + buffer).
                var dispatcher = Dispatcher.CurrentDispatcher;
                var shutdownTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(900)
                };
                shutdownTimer.Tick += (_, _) =>
                {
                    shutdownTimer.Stop();
                    try { window.Close(); } catch { /* ignore */ }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                shutdownTimer.Start();

                window.Show();

                // v0.9.3 regression: deterministically fire OnStateChanged from
                // the thread pool (production trigger is WgcSensingProvider.OnTick).
                // Before the hotfix this hit Dispatcher.VerifyAccess on
                // PersistentBarWindow.SetTier + OverlayWindow.ApplyState. With the
                // marshal in place the dispatcher loop processes it cleanly. Three
                // fires (~150ms apart) ensures the handler runs multiple times and
                // exercises both internal paths. Captured into the same
                // `captured` field so a regression here fails this test.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(150);
                    try { window.OnStateChanged(null, new SenseState()); } catch (Exception ex) { captured ??= ex; }
                    await Task.Delay(150);
                    try { window.OnStateChanged(null, new SenseState { VimsTier = 4 }); } catch (Exception ex) { captured ??= ex; }
                    await Task.Delay(150);
                    try { window.OnStateChanged(null, new SenseState { VimsTier = 7 }); } catch (Exception ex) { captured ??= ex; }
                });

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
