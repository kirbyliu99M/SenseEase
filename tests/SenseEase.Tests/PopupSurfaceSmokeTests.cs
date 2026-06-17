using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UI-RESHAPE prep — smoke test for the new discreet surfaces. Mirrors
/// <c>MainWindowSmokeTests</c>: construct each window on an STA thread, run a real WPF
/// dispatcher loop briefly, and fail on any XAML binding Error captured via
/// <see cref="PresentationTraceSources.DataBindingSource"/>. Catches broken bindings
/// (silent traces, not exceptions) in the popup bar / chat panel / edge handle / NPS
/// survey before they reach the host wiring.
/// </summary>
public class PopupSurfaceSmokeTests
{
    private sealed class CapturingTraceListener : TraceListener
    {
        private readonly List<string> _errors = new();
        private readonly object _lock = new();
        public IReadOnlyList<string> Errors { get { lock (_lock) return _errors.AsReadOnly(); } }
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
        public override void TraceEvent(TraceEventCache? c, string s, TraceEventType t, int id, string? message)
        {
            if (t <= TraceEventType.Error && !string.IsNullOrEmpty(message))
                lock (_lock) { _errors.Add(message!); }
        }
        public override void TraceEvent(TraceEventCache? c, string s, TraceEventType t, int id, string? format, params object?[]? args)
        {
            if (t <= TraceEventType.Error && !string.IsNullOrEmpty(format))
            {
                string msg;
                try { msg = (args is { Length: > 0 }) ? string.Format(format!, args) : format!; }
                catch { msg = format!; }
                lock (_lock) { _errors.Add(msg); }
            }
        }
    }

    [Fact]
    public void NewSurfaces_Construct_And_FirstLayout_NoBindingErrors()
    {
        Exception? captured = null;
        List<string>? bindingErrors = null;
        var done = new ManualResetEventSlim();

        var staThread = new Thread(() =>
        {
            var listener = new CapturingTraceListener();
            var src = PresentationTraceSources.DataBindingSource;
            src.Listeners.Add(listener);
            var prevLevel = src.Switch.Level;
            src.Switch.Level = SourceLevels.Error;

            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                DispatcherUnhandledExceptionEventHandler onDispatch = (_, args) => { captured ??= args.Exception; args.Handled = true; };
                app.DispatcherUnhandledException += onDispatch;

                var windows = new List<Window>
                {
                    new EdgeSidebarWindow(),
                    new PopupBarWindow(),
                    new NpsSurveyWindow(),
                };
                foreach (var w in windows)
                {
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = -10000; w.Top = -10000;
                    w.ShowInTaskbar = false;
                }

                var dispatcher = Dispatcher.CurrentDispatcher;
                var shutdownTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(800) };
                shutdownTimer.Tick += (_, _) =>
                {
                    shutdownTimer.Stop();
                    foreach (var w in windows) { try { w.Close(); } catch { /* ignore */ } }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                shutdownTimer.Start();

                foreach (var w in windows) w.Show();

                Dispatcher.Run();
                app.DispatcherUnhandledException -= onDispatch;
            }
            catch (Exception ex) { captured ??= ex; }
            finally
            {
                try
                {
                    src.Switch.Level = prevLevel;
                    src.Listeners.Remove(listener);
                    listener.Flush();
                }
                catch { /* never mask the real assertion */ }
                bindingErrors = new List<string>(listener.Errors);
                listener.Dispose();
                done.Set();
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(20)), "Popup surface smoke test timed out");

        if (captured != null)
            throw new Xunit.Sdk.XunitException("A new surface threw during construction/layout: " + captured);

        Assert.True(
            bindingErrors == null || bindingErrors.Count == 0,
            "WPF DataBinding reported Error-level failures in the new surfaces:\n  " +
            string.Join("\n  ", bindingErrors ?? new List<string>()));
    }
}
