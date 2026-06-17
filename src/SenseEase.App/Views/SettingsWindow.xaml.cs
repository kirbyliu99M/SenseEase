using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
        Loaded += (_, _) => SettingsGlassTheme.Track(this);
        Closed += (_, _) => SettingsGlassTheme.Untrack(this);

        // UI-reshape Task 9 (2026-06-17): frosted Win11 acrylic behind the rounded Surface card,
        // applied once the HWND exists (mirrors NotificationCardWindow). The tint is THEME-AWARE —
        // a dark slate scrim in Dark mode, a soft near-white scrim in Light — so the frameless
        // glass holds together in either theme. ABGR order (0xAABBGGRR).
        SourceInitialized += (_, _) => ApplyThemeAwareAcrylic();
    }

    /// <summary>UI-reshape Task 9: pick an acrylic scrim that suits the current theme so the
    /// frameless glass reads correctly in Light and Dark (mirrors
    /// <c>NotificationCardWindow.ApplyThemeAwareAcrylic</c>). The Surface token paints the actual
    /// card fill on top of this frost; the scrim only governs what bleeds through the blur.</summary>
    private void ApplyThemeAwareAcrylic()
    {
        // GLASS BRIDGE (2026-06-18): the frost must match the popup glass. The old near-white light
        // scrim (0xD9FFFFFF) was exactly why the card read WHITE. Slate by default, Game-Bar near-black
        // on dark — same hue as the popup PanelBg the card paints on top. ABGR (0xAABBGGRR).
        uint tint = ThemeManager.ShouldUseDark()
            ? 0xD9221F1Fu   // Game-Bar near-black (ARGB #1F1F22)
            : 0xD92E231Du;  // slate (ARGB #1D232E)
        AcrylicGlass.EnableAcrylic(this, tint);
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
    /// group (vestibular "regret" toggle + moved Reset). Forward to the hosted
    /// <see cref="SettingsContent"/>. Set by the host AFTER construction (before Show) when
    /// it has the MainViewModel; left unset on paths that don't (the group then stays hidden).
    /// Constructor signature is preserved verbatim so existing call sites are unaffected.</summary>
    public Func<bool>? GetVestibularProtection
    {
        get => ContentHost.GetVestibularProtection;
        set => ContentHost.GetVestibularProtection = value;
    }
    public Action<bool>? SetVestibularProtection
    {
        get => ContentHost.SetVestibularProtection;
        set => ContentHost.SetVestibularProtection = value;
    }
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

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>C2 (audit): Escape closes the settings dialog — the standard
    /// cancel/back affordance Apple HIG and WCAG require on a modal surface,
    /// mirroring the "關閉 / Close" button.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
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
