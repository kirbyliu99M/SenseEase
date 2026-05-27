using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SenseEase.App.Services;

namespace SenseEase.App.Views;

/// <summary>
/// Pop-up sprint Task 7 patch: in-app warning that appears when the app
/// detects it's running in validation mode (BarStub active, production UI
/// suppressed). Discovered during overlay-bar-redesign visual review when
/// the SENSEEASE_WGC_BAR_VALIDATION env var leaked between PowerShell
/// sessions, causing the user to see a cyan flashing bar + no tray icon
/// and start filing a phantom bug.
///
/// <para>UX shape (per user direction 2026-05-26):</para>
/// <list type="bullet">
///   <item>Bottom-right of primary monitor work area, 24 dip margin from edges</item>
///   <item>Semi-transparent dark background with amber border (matches the
///         visual language of the bar's red tier — "attention but not alarm")</item>
///   <item>Click-through (<c>IsHitTestVisible=false</c>) so it never blocks
///         the user's validation workflow</item>
///   <item>Fades in over 220 ms, holds 4 s, fades out over 600 ms, closes</item>
///   <item>Content comes from <see cref="ValidationModeDiagnostics.BuildDiagnosticText"/>
///         so the message has a single source of truth (testable separately
///         from this Window)</item>
/// </list>
///
/// <para>Permanent records (separate from this Window):</para>
/// <list type="bullet">
///   <item>Same diagnostic text appended to <c>%TEMP%/wgc_diag.txt</c>
///         (already used by WgcSensingProvider for validation runs)</item>
///   <item><see cref="System.Diagnostics.Debug.WriteLine"/> emission so IDE
///         debug consoles also surface the warning</item>
/// </list>
///
/// <para>This Window does NOT engage the WGC mask. It's intentionally a
/// short-lived diagnostic surface — masking it would over-engineer; its
/// 4-second total lifespan is below the WGC pipeline's median + EMA
/// filtering window, and the validation harness already discards OFI
/// during its own initialization tick anyway.</para>
/// </summary>
public partial class ValidationModeBanner : Window
{
    /// <summary>How long the banner holds at full opacity before fading.
    /// Per user spec 2026-05-26: 4 s.</summary>
    public TimeSpan HoldDuration { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Duration of the opacity 1→0 fade after the hold expires.</summary>
    public TimeSpan FadeDuration { get; set; } = TimeSpan.FromMilliseconds(600);

    private readonly DispatcherTimer _holdTimer;

    public ValidationModeBanner(string diagnosticText)
    {
        InitializeComponent();
        DiagnosticText.Text = diagnosticText;
        _holdTimer = new DispatcherTimer { Interval = HoldDuration };
        _holdTimer.Tick += OnHoldTimerTick;
        Loaded += OnLoaded;
        Closed += (_, _) => _holdTimer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Bottom-right of primary monitor work area, with margin so the
        // banner doesn't crowd the taskbar tray (where the validation user
        // is probably looking for the BarStub or wgc_diag.txt output).
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - 24;
        Top  = work.Bottom - ActualHeight - 24;

        // Fade-in: 0 → 1 over 220 ms. Matches the SpikePopupWindow and
        // NotificationCardWindow conventions; no snap, no flash.
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(220))));

        _holdTimer.Start();
    }

    private void OnHoldTimerTick(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        var fade = new DoubleAnimation(1.0, 0.0, new Duration(FadeDuration));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
