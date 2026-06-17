using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SenseEase.App.ViewModels;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Smoke test: instantiate MainWindow on an STA thread, run a real WPF dispatcher loop
/// for a short window, and fail on any XAML binding Error captured via
/// <see cref="PresentationTraceSources.DataBindingSource"/> at
/// <see cref="SourceLevels.Error"/>. Also asserts a small set of cheap post-layout
/// state invariants. Catches regressions like the <c>VimsTier</c>
/// TwoWay-binding-to-read-only-property bug that killed the UI thread silently.
///
/// <para><b>Binding-error detection note.</b>  WPF binding failures are silent in
/// the sense that they do not throw — they write a trace message at Error level to
/// <see cref="PresentationTraceSources.DataBindingSource"/>. We attach a
/// <see cref="TraceListener"/> scoped to that source at <see cref="SourceLevels.Error"/>
/// during the smoke run so any such message fails the test. The listener is removed and
/// flushed in the same <c>finally</c> block so it cannot leak into subsequent tests.</para>
/// </summary>
public class MainWindowSmokeTests
{
    private sealed class CapturingTraceListener : TraceListener
    {
        private readonly List<string> _errors = new();
        private readonly object _lock = new();

        public IReadOnlyList<string> Errors { get { lock (_lock) return _errors.AsReadOnly(); } }

        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }

        public override void TraceEvent(TraceEventCache? eventCache, string source,
            TraceEventType eventType, int id, string? message)
        {
            if (eventType <= TraceEventType.Error && !string.IsNullOrEmpty(message))
                lock (_lock) { _errors.Add(message!); }
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source,
            TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            if (eventType <= TraceEventType.Error && !string.IsNullOrEmpty(format))
            {
                string msg;
                try { msg = (args is { Length: > 0 }) ? string.Format(format!, args) : format!; }
                catch { msg = format!; }
                lock (_lock) { _errors.Add(msg); }
            }
        }
    }

    [Fact]
    public void MainWindow_Constructs_And_FirstLayout_DoesNotThrow()
    {
        Exception? captured = null;
        List<string>? bindingErrors = null;
        var done = new ManualResetEventSlim();

        // Post-layout state assertions written into the STA thread and read
        // back via these locals after Join.
        LivenessState? initialLiveness = null;
        bool? persistentOverlayDefault = null;

        var staThread = new Thread(() =>
        {
            var bindingListener = new CapturingTraceListener();

            // Attach listener to DataBindingSource at Error level BEFORE the
            // window is constructed so any binding error during layout is captured.
            var dataBindingSource = PresentationTraceSources.DataBindingSource;
            dataBindingSource.Listeners.Add(bindingListener);
            var prevLevel = dataBindingSource.Switch.Level;
            dataBindingSource.Switch.Level = SourceLevels.Error;

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

                    // ── Post-layout state assertions + deterministic teardown ──
                    // Captured on the UI thread where the VM is live; read by the
                    // outer thread after Join. Keep these cheap (property reads only).
                    // MainWindow.DataContext is the MainViewModel instance.
                    try
                    {
                        if (window.DataContext is MainViewModel vm)
                        {
                            initialLiveness          = vm.LivenessState;
                            persistentOverlayDefault = vm.PersistentOverlayEnabled;

                            // Deterministic teardown of the live WGC provider's timers.
                            // MainWindow.OnClosing intercepts the window.Close() below and
                            // cancels it (minimize-to-tray is the default pref), so the
                            // production Closed→DisposeServices teardown never fires here —
                            // the WgcSensingProvider's capture + liveness-watchdog timers
                            // would leak. A leaked OnTick that fires AFTER this dispatcher
                            // shuts down marshals a health-event log append back onto the
                            // now-dead dispatcher (MainViewModel.cs:1020) → TaskCanceledException
                            // on a ThreadPool thread → aborts the whole test-host process
                            // mid-run (only in a full run that outlives the provider's 2 s
                            // warmup; the smoke test alone exits before then). Disposing the
                            // VM's services now — on the live dispatcher thread, before
                            // BeginInvokeShutdown — stops + disposes both provider timers so
                            // nothing can fire post-shutdown. Fail-soft + idempotent; mirrors
                            // the production app-exit (tray Quit) teardown.
                            vm.DisposeServices();
                        }
                    }
                    catch { /* best-effort: a null DataContext is a separate failure path */ }

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
                // ── Tear down + flush binding listener ─────────────────────
                // Must be removed before the test method returns so the listener
                // cannot leak into later tests (or accumulate entries from them).
                try
                {
                    dataBindingSource.Switch.Level = prevLevel;
                    dataBindingSource.Listeners.Remove(bindingListener);
                    bindingListener.Flush();
                }
                catch { /* never let cleanup failure mask the real assertion */ }

                bindingErrors = new List<string>(bindingListener.Errors);
                bindingListener.Dispose();
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

        // ── Binding-error gate ────────────────────────────────────────────────
        // WPF binding failures are silent traces, not exceptions; the listener
        // above makes them visible. Any Error-level binding trace means a
        // property path is broken and the UI is silently wrong.
        Assert.True(
            bindingErrors == null || bindingErrors.Count == 0,
            "WPF DataBinding reported Error-level failures during smoke run " +
            "(these are silent traces that hide broken bindings):\n  " +
            string.Join("\n  ", bindingErrors ?? new List<string>()));

        // ── Post-layout state assertions ──────────────────────────────────────
        // LivenessState initial: the bare new-VM path must land on SteadyWatching
        // so nothing new lights up at startup (matches LivenessContractTests §2).
        Assert.Equal(LivenessState.SteadyWatching, initialLiveness);

        // PersistentOverlayEnabled default must be false (2026-06-06 Kirby override,
        // D-12 cross-ref — see D6DayOneProtectionTests for the full rationale).
        Assert.False(persistentOverlayDefault,
            "PersistentOverlayEnabled must default false at first launch (D-12 cross-ref).");
    }
}
