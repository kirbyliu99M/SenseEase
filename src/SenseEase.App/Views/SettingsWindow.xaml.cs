using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SenseEase.App.Interop;
using SenseEase.App.Localization;
using SenseEase.App.Theme;
using SenseEase.Infrastructure.Real;

namespace SenseEase.App.Views;

/// <summary>
/// Topmost settings dialog opened from the tray menu and the Pop-up Card's
/// gear icon. As of UI Consolidation Sprint M-Phase-1 (2026-05-30) this is
/// thin chrome around the shared <see cref="SettingsContent"/> UserControl —
/// all toggle UI + handlers + persistence logic live in the UserControl, so
/// this Window and the main console's "設定" tab share state automatically
/// through the static <see cref="Configuration.UserPreferences.Current"/> +
/// <c>AppConfig.*</c> singletons.
///
/// <para>Cross-instance visual sync is handled by
/// <see cref="Configuration.UserPreferences.Changed"/> — when this Window is
/// open simultaneously with the main console's Settings tab, a toggle
/// flipped on one surface re-hydrates the other automatically.</para>
///
/// <para>The Constructor signature (<see cref="Func{T}"/>) is preserved
/// verbatim so the existing call sites (<c>MainWindow.ShowSettingsWindow</c>,
/// <c>NotificationCardWindow.OnSettingsClicked</c>) continue to work without
/// modification.</para>
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Forwards <see cref="SettingsContent.PreferencesChanged"/> from
    /// the hosted UserControl. The host subscribers (bar/tray repaint in
    /// MainWindow, host-callback in NotificationCardWindow) consume this
    /// event verbatim.</summary>
    public event EventHandler? PreferencesChanged;

    public SettingsWindow(Func<bool> hasVestibularDiagnosis)
    {
        InitializeComponent();

        // UX-16 (2026-06-11): the window title was the zh-only literal
        // "SenseEase 設定". Bind it live to the language singleton — the same
        // {loc:T} markup-extension contract used everywhere in XAML — so it
        // shows the matching half and re-picks the instant the user flips
        // language, no restart. (Done in code-behind because the title lives
        // on this Window's chrome, which the host owns.)
        SetBinding(TitleProperty, new Binding(nameof(LocalizationManager.Language))
        {
            Source = LocalizationManager.Current,
            Mode = BindingMode.OneWay,
            Converter = new SettingsTitleConverter(),
        });

        ContentHost.HasVestibularDiagnosis  = hasVestibularDiagnosis ?? (() => false);
        ContentHost.PreferencesChanged     += (s, e) => PreferencesChanged?.Invoke(this, e);

        // Dark-mode fix (2026-06-09): keep this window's OWN chrome (Surface card +
        // Accent Close button, declared with theme tokens in XAML) light/dark-correct,
        // including a live OS theme flip while the dialog is open. Mirrors EventLogWindow;
        // the hosted SettingsContent tracks itself separately.
        // GLASS BRIDGE (2026-06-18): match the popup panel — the chrome + hosted SettingsContent
        // wear the glass palette (slate default; SettingsGlassTheme flips the Game-Bar dark variant).
        // Replaces ThemeManager.TrackElement (which merged the grey Palette.Dark and fought the glass).
        // R3 #7: light-dismiss — close when the user clicks ANOTHER window or the desktop. The earlier
        // Deactivated-event approach was unreliable for this frameless Topmost window (Deactivated does
        // not always fire, and its foreground check raced the focus change). Mirror the PROVEN pattern
        // used by NotificationCardWindow / the popup bar: poll GetForegroundWindow on a timer and close
        // once the foreground is some OTHER top-level window. An in-window dropdown (foreground stays
        // us) or an owned child dialog (reset confirm / hotkey picker) keeps it open.
        _dismissPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _dismissPoll.Tick += OnDismissPollTick;
        // 閃退 ROOT-CAUSE FIX (Item 2, redesigned): the close decision now keys on PROCESS OWNERSHIP, not on
        // timing (no arm/settle/Activated heuristics). The window closes only when the foreground belongs to
        // ANOTHER process; every one of our own windows (incl. the open-transition churn onto the edge handle)
        // is the same process and can never dismiss us. See OnDismissPollTick.
        Loaded += (_, _) => { SettingsGlassTheme.Track(this); PlayEntrance(); _dismissPoll.Start(); };
        Closed += (_, _) => { SettingsGlassTheme.Untrack(this); _dismissPoll.Stop(); };

        // GREY-CORNERS FIX (2026-06-19): NO window-wide acrylic. ACCENT_ENABLE_ACRYLICBLURBEHIND frosts
        // the SQUARE HWND, and clipping RootGrid (OnRootSizeChanged) cannot clip that backdrop — so the
        // acrylic showed as greyish squares behind the rounded card's corners. The popup bar avoids
        // window-wide acrylic for exactly this reason. The Surface card (~95% opaque) is the fill; the
        // area outside its rounded corners is transparent (the desktop), so the corners read clean.
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private readonly DispatcherTimer _dismissPoll;
    private int  _foreignTicks;   // consecutive polls where ANOTHER PROCESS's window held the foreground
    private const int ForeignTicksToClose = 3; // 3 × 200 ms = 600 ms of sustained other-app foreground before closing
    private bool _closing;        // re-entrancy guard: a 2nd close trigger during the breath-out is a no-op

    /// <summary>Light-dismiss: close the Settings window once the user genuinely switches to ANOTHER APP or
    /// the desktop. Cheap 200 ms GetForegroundWindow poll. The close decision keys on a STABLE fact —
    /// PROCESS OWNERSHIP — not on timing: it closes only when the foreground window belongs to a DIFFERENT
    /// process. Every one of OUR OWN windows (the popup bar, the edge handle, the overlay, the export
    /// SaveFileDialog, the Event Log, an in-window combo/dropdown) is the same process, so none of them — nor
    /// the open-transition churn that bounces the foreground onto the edge handle — ever counts as a dismiss.
    /// <para>閃退 ROOT-CAUSE FIX: earlier versions treated the foreground going to any window we did not OWN
    /// as "foreign", but the launching churn briefly hands the foreground to the app's OWN edge handle /
    /// popup — which the old code mis-read as a click-away and rug-pulled the window the instant it appeared
    /// (and timer-based settles only traded that for "never closes" on slow machines). Keying on process
    /// ownership removes the timing dependency entirely: a real switch to another process/desktop still
    /// dismisses (debounced by <see cref="ForeignTicksToClose"/> against a transient cross-process blip),
    /// while nothing inside our own app can. An interop hiccup is treated as "wait" (fail-OPEN), never as a
    /// reason to close.</para></summary>
    private void OnDismissPollTick(object? sender, EventArgs e)
    {
        if (!IsVisible) { _dismissPoll.Stop(); return; }
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) { _foreignTicks = 0; return; }                 // transient null — wait, don't count it
            GetWindowThreadProcessId(fg, out uint fgPid);
            if (fgPid == 0 || fgPid == (uint)System.Environment.ProcessId)
            { _foreignTicks = 0; return; }                                        // one of OUR windows (incl. the open churn) — stay open
            // Foreground belongs to ANOTHER process (another app) or the desktop — a genuine switch away.
            if (++_foreignTicks < ForeignTicksToClose) return;                    // small debounce vs a transient cross-process blip
        }
        catch { _foreignTicks = 0; return; }   // interop hiccup — wait; NEVER fail-closed (a transient error must not yank the window)
        _dismissPoll.Stop();
        CloseWithBreath();
    }

    /// <summary>One-piece glass redesign (2026-06-18): a calm fade + subtle scale settle on OPEN,
    /// mirroring the popup card reveal (230ms, QuinticEase.EaseOut, 0.975→1) so the dialog feels
    /// like a native Windows sheet rather than a hard pop. Animates the INNER SlabBorder transform
    /// — never RootGrid, whose Clip (set in OnRootSizeChanged in local coords) would scale with it.
    /// ReducedMotion snaps to the resting state (a motion-sickness app must never animate for a
    /// user who feels unwell).</summary>
    private void PlayEntrance()
    {
        if (SenseEase.App.Accessibility.AccessibilityHelper.ReducedMotion())
        {
            SlabBorder.Opacity = 1;
            SlabScale.ScaleX = SlabScale.ScaleY = 1;
            return;
        }
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var dur  = new Duration(TimeSpan.FromMilliseconds(230));
        SlabBorder.Opacity = 0;
        SlabBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        var settle = new DoubleAnimation(0.975, 1, dur) { EasingFunction = ease };
        SlabScale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
        SlabScale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
    }

    /// <summary>Item 1 — calm-breath CLOSE: the inverse of <see cref="PlayEntrance"/>. Fades the inner
    /// SlabBorder out + settles the scale to 0.975 over ~140ms (QuinticEase.EaseIn), then calls the real
    /// <see cref="Window.Close"/> in the fade's Completed — so the panel leaves with the same motion family
    /// as the popup card. Animates ONLY Opacity + a RenderTransform on the inner Border (composited,
    /// ClearType-safe, no relayout, no Effect added), so the AllowsTransparency software-render contract
    /// holds. Guarded against a 2nd trigger (Escape during the fade) and stops the dismiss poll so a late
    /// tick can't re-enter. ReducedMotion closes instantly. ALL THREE close paths (X, Escape, light-dismiss
    /// poll) route through here so a click-away and the button close with the same motion.</summary>
    private void CloseWithBreath()
    {
        if (_closing) return;
        _closing = true;
        _dismissPoll.Stop();   // a late poll tick must not re-enter mid-fade
        if (SenseEase.App.Accessibility.AccessibilityHelper.ReducedMotion()) { Close(); return; }
        var ease = new QuinticEase { EasingMode = EasingMode.EaseIn };
        var dur  = new Duration(TimeSpan.FromMilliseconds(140));
        var shrink = new DoubleAnimation(1.0, 0.975, dur) { EasingFunction = ease };
        SlabScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        SlabScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var fade = new DoubleAnimation(SlabBorder.Opacity, 0.0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) => { try { Close(); } catch { } };
        SlabBorder.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>UI-reshape Task 9: clip the window root to a rounded rect on every resize so the
    /// acrylic never shows a square translucent peek behind the rounded card corners — the glass
    /// reads as one clean rounded slab (same idea as <c>PopupBarWindow.OnRootSizeChanged</c>).</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        => RootGrid.Clip = new RectangleGeometry(
               new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16, 16);

    /// <summary>UI-reshape Task 9: the slim title row is the drag handle — a left-press on the bare
    /// row (the title text / empty space, not the round close button which handles its own click)
    /// moves the frameless window. DragMove throws if the button was already released (fast click);
    /// that is harmless, so it is swallowed.</summary>
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* DragMove throws outside a left-button-down context — ignore */ }
    }

    // TOUCH: DragMove() needs a held mouse button and is unreliable under touch, so move the
    // frameless dialog explicitly on touch. Absolute screen coords (PointToScreen) stay stable as
    // the window moves; e.Handled suppresses the unreliable mouse promotion of the same gesture.
    private TouchDevice? _moveTouch;
    private Point _moveStartScreenPx;
    private double _moveStartLeft, _moveStartTop;

    private void OnTitleBarTouchDown(object sender, TouchEventArgs e)
    {
        // Re-entrancy guard (audit 2026-06-20): a 2nd finger landing on the title bar mid-drag would
        // overwrite _moveTouch + reset the drag baseline, jumping the window. Let the first touch own it.
        if (_moveTouch is not null) { e.Handled = true; return; }
        _moveTouch = e.TouchDevice;
        _moveStartScreenPx = PointToScreen(e.GetTouchPoint(this).Position);
        _moveStartLeft = Left; _moveStartTop = Top;
        try { ((UIElement)sender).CaptureTouch(e.TouchDevice); } catch { }
        e.Handled = true;
    }

    private void OnTitleBarTouchMove(object sender, TouchEventArgs e)
    {
        if (_moveTouch is null || e.TouchDevice != _moveTouch) return;
        var nowPx = PointToScreen(e.GetTouchPoint(this).Position);
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _moveStartLeft + (nowPx.X - _moveStartScreenPx.X) / dpi.DpiScaleX;
        Top  = _moveStartTop  + (nowPx.Y - _moveStartScreenPx.Y) / dpi.DpiScaleY;
        e.Handled = true;
    }

    private void OnTitleBarTouchUp(object sender, TouchEventArgs e)
    {
        if (_moveTouch is null || e.TouchDevice != _moveTouch) return;
        try { ((UIElement)sender).ReleaseTouchCapture(e.TouchDevice); } catch { }
        _moveTouch = null;
        e.Handled = true;
    }

    /// <summary>Alpha-2 addendum: optional host wiring for the auto-profiling Settings
    /// group (the moved Reset). Forward to the hosted <see cref="SettingsContent"/>. Set by
    /// the host AFTER construction (before Show) when it has the MainViewModel; left unset on
    /// paths that don't (the group then stays hidden). Constructor signature is preserved
    /// verbatim so existing call sites are unaffected.
    /// (Vestibular-removal: the Get/SetVestibularProtection pass-throughs were removed.)</summary>
    public Action? ResetRequested
    {
        get => ContentHost.ResetRequested;
        set => ContentHost.ResetRequested = value;
    }
    /// <summary>D-1 (CW-1 step 3, 2026-06-11): pass-through for the 漸進適應 / Graded exposure
    /// toggle. Wired by the host (MainWindow) to MainViewModel.GradedExposureEnabled so this
    /// tray/gear window flips the engine pause in-session, matching the console 設定 tab. Left
    /// unset on paths without a MainViewModel (the toggle then hydrates from / persists the
    /// pref directly via the UserControl's null-fallback).</summary>
    public Func<bool>? GetGradedExposure
    {
        get => ContentHost.GetGradedExposure;
        set => ContentHost.GetGradedExposure = value;
    }
    public Action<bool>? SetGradedExposure
    {
        get => ContentHost.SetGradedExposure;
        set => ContentHost.SetGradedExposure = value;
    }
    /// <summary>#7: forwards the reveal-hotkey picker delegates to the hosted SettingsContent.</summary>
    public Func<string>? GetRevealHotkey
    {
        get => ContentHost.GetRevealHotkey;
        set => ContentHost.GetRevealHotkey = value;
    }
    public Action<string>? SetRevealHotkey
    {
        get => ContentHost.SetRevealHotkey;
        set => ContentHost.SetRevealHotkey = value;
    }
    /// <summary>Pass-through for the "open Event Log window" action (2026-06-07).
    /// Wired by the host that owns the MainViewModel (MainWindow); left unset on
    /// paths that don't (the Event Log card then stays hidden).</summary>
    public Action? OpenEventLog
    {
        get => ContentHost.OpenEventLog;
        set => ContentHost.OpenEventLog = value;
    }
    /// <summary>FEAT-08 (CW-10, B1b UI half): pass-through for the live head-tracking
    /// health read (the one-line status caption in the 頭部追蹤與隱私 group). Wired by
    /// the host (MainWindow) to the MainViewModel's <see cref="SidecarHeadPoseReader"/>
    /// — the same both-hosts delegate pattern as GradedExposure. Left unset on paths
    /// without a reader (the notification-card popup), where the status row stays
    /// hidden because there is no honest health to show.</summary>
    public Func<HeadTrackingHealth>? GetHeadTrackingHealth
    {
        get => ContentHost.GetHeadTrackingHealth;
        set => ContentHost.GetHeadTrackingHealth = value;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => CloseWithBreath();

    /// <summary>C2 (audit): Escape closes the settings dialog — the standard
    /// cancel/back affordance Apple HIG and WCAG require on a modal surface,
    /// mirroring the "關閉 / Close" button.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWithBreath();
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>UX-16: picks the localized window-title half for the bound
    /// <see cref="UILanguage"/>. Mirrors the private PickConverter inside the
    /// <c>{loc:T}</c> markup extension; declared here because the Title binding
    /// is set in code-behind (the title is host-owned Window chrome).</summary>
    private sealed class SettingsTitleConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter,
            System.Globalization.CultureInfo culture)
            => value is UILanguage lang && lang == UILanguage.English
                ? "SenseEase Settings"
                : "SenseEase 設定";

        public object ConvertBack(object? value, Type targetType, object? parameter,
            System.Globalization.CultureInfo culture)
            => Binding.DoNothing;
    }
}
