using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SenseEase.App.Views;

/// <summary>
/// Pop-up sprint Task 4 + overlay-bar-redesign D1-D4: the expanded
/// notification card. Shown when the user clicks the bar or tray icon
/// (user-initiated) or, with Toggle A = ON, auto-expanded on a tier-up.
///
/// <para>overlay-bar-redesign updates:</para>
/// <list type="bullet">
///   <item>D1: light theme (white surface, dark text). All foreground/background
///         colour decisions now live in the XAML — this file only sets
///         <c>TitleText.Text</c> and <c>BodyText.Text</c>.</item>
///   <item>D2: numeric title readout — <c>SetContent</c> now takes
///         <c>(band, tier, pressure)</c> and formats the title as
///         <c>"VIMS Tier {tier} · Pressure {p}/100"</c>. The body shows the
///         tier-band description (the text that USED to be the title-suffix
///         in the v1 zh-only build).</item>
///   <item>D3: Power Save toggle + Settings gear. <see cref="IsPowerSaveActive"/>
///         flips the card into a "sensing paused" message; toggling the
///         CheckBox raises <see cref="PowerSaveToggled"/> for the host to
///         pause WGC + mark the tray. The gear button opens
///         <see cref="SettingsWindow"/> using the host-provided
///         <see cref="HasVestibularDiagnosis"/> lookup.</item>
///   <item>D4: deep-blue Open Dashboard button with stacked zh/en label.
///         Style moved to <c>PrimaryDeepBlueButtonStyle</c> in
///         Window.Resources.</item>
/// </list>
/// </summary>
public partial class NotificationCardWindow : Window
{
    /// <summary>How long the card sits idle (no MouseMove over the
    /// CardBorder) before the fade-collapse begins. 60 s per spec; tests
    /// shrink this for determinism.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Duration of the opacity 1→0 fade after idle timeout. 5 s
    /// per spec. The fade is reversible — MouseMove during fade snaps
    /// opacity back to 1 and resets the idle timer.</summary>
    public TimeSpan FadeDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Raised on "啟動保護" click. Host runs the actual engage
    /// action; the card hides immediately on click (does NOT wait for the
    /// handler to complete).</summary>
    public event EventHandler? EngageClicked;

    /// <summary>Raised on "6 小時內不再提示" click. Host calls
    /// <c>NotificationCardController.Mute(band)</c> in response. The card
    /// hides immediately on click.</summary>
    public event EventHandler? MuteClicked;

    /// <summary>overlay-bar-redesign D3: raised when the Power Save toggle
    /// flips. Host receives <c>true</c> for ON (pause sensing) and
    /// <c>false</c> for OFF (resume sensing).</summary>
    public event EventHandler<bool>? PowerSaveToggled;

    /// <summary>overlay-bar-redesign D3: provided by the host so the gear
    /// button can construct a <see cref="SettingsWindow"/> with the
    /// same vestibular-diagnosis lookup the host uses elsewhere. Falls
    /// back to <c>() => false</c> when the host doesn't wire it.</summary>
    public Func<bool>? HasVestibularDiagnosis { get; set; }

    /// <summary>Path Z bug-fix 7: invoked when the user changes a preference
    /// in the SettingsWindow opened from the gear icon. Host wires this to
    /// repaint the bar/tray immediately (matches the tray-menu Settings path
    /// in <see cref="MainWindow.ShowSettingsWindow"/>). No-op if unwired.</summary>
    public Action? OnPreferencesChanged { get; set; }

    private readonly DispatcherTimer _idleTimer;
    private Storyboard? _activeFadeStoryboard;

    // overlay-bar-redesign D2/D3: cache the last content the card was set to
    // so an IsPowerSaveActive flip can re-render without callers passing
    // values again. Default band is Grey for the rare path where the toggle
    // is set before any SetContent — the card simply shows the power-save
    // message anyway, so the cached band is irrelevant in that state.
    private TierColorPalette.TierBand _lastBand = TierColorPalette.TierBand.Grey;
    private int _lastTier = 1;
    private double _lastPressure;
    private bool _hasContent;
    private bool _isPowerSaveActive;
    private bool _suppressToggleEvent; // re-entrancy guard when host sets IsPowerSaveActive

    /// <summary>overlay-bar-redesign D3: when true, the card shows the
    /// "Power Save active — sensing paused" message instead of the numeric
    /// tier/pressure readout. Setting this property re-renders the card
    /// immediately so the surface stays in sync with the host's WGC state.
    /// The setter also syncs the in-card CheckBox without re-raising the
    /// <see cref="PowerSaveToggled"/> event (the host is the source of
    /// truth for this state).</summary>
    public bool IsPowerSaveActive
    {
        get => _isPowerSaveActive;
        set
        {
            if (_isPowerSaveActive == value) return;
            _isPowerSaveActive = value;
            // Keep the in-card CheckBox visually in sync without re-raising
            // PowerSaveToggled (otherwise the host setting this property in
            // response to its OWN handler would loop).
            _suppressToggleEvent = true;
            try { PowerSaveToggle.IsChecked = value; }
            finally { _suppressToggleEvent = false; }
            ApplyContentToUi();
        }
    }

    public NotificationCardWindow()
    {
        InitializeComponent();
        _idleTimer = new DispatcherTimer { Interval = IdleTimeout };
        _idleTimer.Tick += OnIdleTimerTick;
        Loaded += OnLoaded;
        Closed += (_, _) => _idleTimer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position just below the bar: top-centre, 12 dip below the bar's
        // bottom edge. Bar is 4 dip tall at y=0 → card top at y=16.
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2.0;
        Top  = work.Top + 16;

        // Smooth fade-in (~220 ms) so the card doesn't snap on screen.
        Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)));
        BeginAnimation(OpacityProperty, fadeIn);

        // Start the idle countdown. MouseMove on CardBorder restarts it.
        RestartIdleTimer();
    }

    /// <summary>overlay-bar-redesign D2: sets card content for a VIMS tier
    /// + pressure readout. The numeric title is rounded to a whole number;
    /// the body carries the tier-band description (what the v1 title
    /// suffix communicated).
    /// <para>When <see cref="IsPowerSaveActive"/> is true, the inputs are
    /// cached but the rendered title/body is the power-save override
    /// message — so the card flips correctly on subsequent
    /// <c>IsPowerSaveActive = false</c> without callers re-passing
    /// values.</para></summary>
    public void SetContent(TierColorPalette.TierBand band, int tier, double pressure)
    {
        _lastBand = band;
        _lastTier = tier;
        _lastPressure = pressure;
        _hasContent = true;
        ApplyContentToUi();
    }

    private void ApplyContentToUi()
    {
        if (_isPowerSaveActive)
        {
            TitleText.Text = "VIMS · 省電模式中 / Power Save Active";
            BodyText.Text  = "感測已暫停。按下「省電模式」可恢復。/ Sensing paused. Toggle Power Save off to resume.";
            return;
        }

        // Default normal-state body: derive from the tier band so callers
        // that haven't called SetContent yet still get a sensible placeholder.
        string body = DescriptionForBand(_lastBand);
        if (_hasContent)
        {
            TitleText.Text = $"VIMS Tier {_lastTier} · Pressure {Math.Round(_lastPressure)}/100";
        }
        else
        {
            // No SetContent yet — keep the title empty (matches the v1
            // "shown but unpopulated" state).
            TitleText.Text = string.Empty;
        }
        BodyText.Text = body;
    }

    private static string DescriptionForBand(TierColorPalette.TierBand band) => band switch
    {
        TierColorPalette.TierBand.Grey  => "目前視覺壓力低。如有疑問請開啟設定面板。",
        TierColorPalette.TierBand.Amber => "偵測到中度視覺壓力。建議短暫休息或暗化螢幕。",
        TierColorPalette.TierBand.Red   => "偵測到高度視覺壓力。建議立即啟動保護或暫停當前活動。",
        _                                => string.Empty,
    };

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        // Any movement inside the CardBorder counts as user interaction.
        // Reset both the idle timer AND cancel any in-progress fade (the
        // "last-chance pull-back" path).
        if (_activeFadeStoryboard is not null)
        {
            _activeFadeStoryboard.Stop(this);
            _activeFadeStoryboard = null;
            Opacity = 1.0;
        }
        RestartIdleTimer();
    }

    private void RestartIdleTimer()
    {
        _idleTimer.Stop();
        _idleTimer.Interval = IdleTimeout;
        _idleTimer.Start();
    }

    private void OnIdleTimerTick(object? sender, EventArgs e)
    {
        _idleTimer.Stop();
        // Start the fade. Reversible — MouseMove during the fade pulls
        // opacity back to 1 via OnCardMouseMove above.
        var fade = new DoubleAnimation(1.0, 0.0, new Duration(FadeDuration));
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            // Only hide if the fade actually finished (not interrupted by
            // mouse-move). When interrupted, _activeFadeStoryboard is set
            // to null in OnCardMouseMove BEFORE Stop completes, so this
            // event fires with _activeFadeStoryboard == null → skip hide.
            if (_activeFadeStoryboard is null) return;
            _activeFadeStoryboard = null;
            Hide();
        };
        _activeFadeStoryboard = sb;
        sb.Begin(this, isControllable: true);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Hide();

    private void OnEngageClicked(object sender, RoutedEventArgs e)
    {
        EngageClicked?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    private void OnMuteClicked(object sender, RoutedEventArgs e)
    {
        MuteClicked?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    private void OnPowerSaveToggled(object sender, RoutedEventArgs e)
    {
        // Guard against the re-entrant path where the host sets
        // IsPowerSaveActive — that setter assigns CheckBox.IsChecked which
        // re-fires this handler. _suppressToggleEvent stays true for the
        // duration of the property setter so we silently swallow this pass.
        if (_suppressToggleEvent) return;

        bool nowOn = PowerSaveToggle.IsChecked == true;
        // Update internal state and re-render so the title/body switch
        // immediately on user interaction even if the host doesn't echo
        // the value back through IsPowerSaveActive.
        if (_isPowerSaveActive != nowOn)
        {
            _isPowerSaveActive = nowOn;
            ApplyContentToUi();
        }
        PowerSaveToggled?.Invoke(this, nowOn);
    }

    /// <summary>overlay-bar-redesign D3: opens the Settings window using the
    /// host-provided vestibular-diagnosis lookup (or a no-diagnosis fallback
    /// when the host hasn't wired it). Modal-feeling — uses
    /// <c>ShowDialog</c> with the card as owner so the settings stay on top
    /// while open.</summary>
    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        // Path Z bug-fix 3: stop the card's idle-fade timer and cancel any
        // in-progress fade so the card doesn't disappear behind the modal
        // Settings dialog after 60 s of inactivity in Settings.
        _idleTimer.Stop();
        if (_activeFadeStoryboard is not null)
        {
            _activeFadeStoryboard.Stop(this);
            _activeFadeStoryboard = null;
            Opacity = 1.0;
        }

        try
        {
            var diagFn = HasVestibularDiagnosis ?? (() => false);
            var win = new SettingsWindow(diagFn) { Owner = this };
            // Path Z bug-fix 7: forward PreferencesChanged to the host so the
            // bar/tray repaint immediately on Toggle B change — matching the
            // tray-menu Settings path.
            if (OnPreferencesChanged is not null)
                win.PreferencesChanged += (_, _) => OnPreferencesChanged();
            win.ShowDialog();
        }
        finally
        {
            // Restart the idle countdown when Settings closes so the card
            // resumes its normal auto-collapse behaviour.
            RestartIdleTimer();
        }
    }

    /// <summary>Patch 9: primary action — surface the dashboard window. User
    /// flagged this as the most-important sub-feature of the bar/card
    /// optimization sprint: a card-launched route back to the main app
    /// without hunting in the taskbar.
    /// <para>Implementation notes:</para>
    /// <list type="bullet">
    ///   <item>Restore from Minimized state if needed (TaskbarIcon click +
    ///         Show alone wouldn't un-minimize).</item>
    ///   <item>Topmost true→false dance bumps the dashboard above
    ///         PersistentBarWindow (which is also Topmost) without permanently
    ///         pinning it there. Standard WPF pattern for "raise above
    ///         siblings without becoming sticky."</item>
    ///   <item>Card closes after activation so the user gets a single-action
    ///         flow — one click, dashboard up, card gone.</item>
    ///   <item>Null guard: if Application.Current.MainWindow is null (e.g.
    ///         the dashboard was already closed and the app is running tray-
    ///         only), the button silently no-ops rather than crashing. A
    ///         future polish could re-instantiate MainWindow in that case;
    ///         out of scope for Patch 9.</item>
    /// </list></summary>
    private void OnOpenDashboardClicked(object sender, RoutedEventArgs e)
    {
        var mainWindow = System.Windows.Application.Current?.MainWindow;
        if (mainWindow is null) { Close(); return; }

        if (mainWindow.WindowState == WindowState.Minimized)
            mainWindow.WindowState = WindowState.Normal;

        mainWindow.Show();
        mainWindow.Activate();
        // Topmost dance: bump above other Topmost siblings (PersistentBarWindow,
        // NotificationCardWindow itself) without permanently pinning the
        // dashboard. WPF resolves this synchronously on the dispatcher.
        mainWindow.Topmost = true;
        mainWindow.Topmost = false;

        Close();
    }

    /// <summary>Monitor-coords physical-pixel rect for the WGC exclude mask
    /// (Option II: always-on mask for any visible UI). Same DPI maths as
    /// PersistentBarWindow.</summary>
    public (int Left, int Top, int Width, int Height) GetMonitorRectInPhysicalPixels()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX;
        double sy = dpi.DpiScaleY;
        int left   = (int)Math.Floor(Left * sx);
        int top    = (int)Math.Floor(Top  * sy);
        int width  = (int)Math.Ceiling(ActualWidth  * sx);
        int height = (int)Math.Ceiling(ActualHeight * sy);
        return (left, top, width, height);
    }
}
