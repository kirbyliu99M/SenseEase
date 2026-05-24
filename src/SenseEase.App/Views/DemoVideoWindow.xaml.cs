using System;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace SenseEase.App.Views;

/// <summary>
/// Plays a vection-inducing video clip during a SenseEase demo run. The video
/// is displayed in a normal window on the primary monitor, so the existing
/// <c>WgcSensingProvider</c> (which captures the whole primary monitor)
/// naturally picks up the on-screen motion and drives the real pressure /
/// mask / chatbot pipeline. The point of this window is purely to put motion
/// on the screen — all reactive UI happens in MainWindow.
/// </summary>
public partial class DemoVideoWindow : Window
{
    /// <summary>Raised when playback ends naturally (MediaEnded) OR fails OR
    /// the user closes the window. MainViewModel listens for this to restore
    /// the prior sensing mode and emit a demo-summary log entry.</summary>
    public event EventHandler? DemoFinished;

    private bool _finishedRaised;

    public DemoVideoWindow(string videoPath)
    {
        InitializeComponent();

        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
        {
            ErrorText.Text = $"Demo video not found:\n{videoPath}";
            ErrorText.Visibility = Visibility.Visible;
            // Auto-close after a moment so the demo flow doesn't dead-end.
            Loaded += (_, _) =>
            {
                var t = new System.Windows.Threading.DispatcherTimer
                            { Interval = TimeSpan.FromSeconds(2) };
                t.Tick += (_, _) => { t.Stop(); Close(); };
                t.Start();
            };
            return;
        }

        Player.Source = new Uri(videoPath, UriKind.Absolute);
        Loaded += (_, _) => Player.Play();

        // Natural end → close (Closed handler raises DemoFinished once).
        Player.MediaEnded += (_, _) => Close();

        // Codec / file failure → show the underlying message, then close.
        Player.MediaFailed += (_, e) =>
        {
            ErrorText.Text = $"Playback failed: {e.ErrorException?.Message ?? "unknown error"}";
            ErrorText.Visibility = Visibility.Visible;
        };

        // ESC stops the demo.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected override void OnClosed(EventArgs e)
    {
        // Stop the player before we tear down so the Media Foundation pipeline
        // releases its handles cleanly (avoids the COM finalizer-thread warning
        // on app shutdown when a Demo is still running).
        try { Player.Stop(); Player.Close(); } catch { }

        if (!_finishedRaised)
        {
            _finishedRaised = true;
            DemoFinished?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosed(e);
    }
}
