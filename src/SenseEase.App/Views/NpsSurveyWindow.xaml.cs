using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SenseEase.App.Accessibility;
using SenseEase.App.Interop;
using SenseEase.App.Services;
using SenseEase.App.Theme;
using SenseEase.App.ViewModels;

namespace SenseEase.App.Views;

/// <summary>
/// UI-RESHAPE prep — the subtle Teams-style NPS survey window: dark-glass, bottom-right,
/// never steals focus (<see cref="ClickThrough.EnableNoActivate"/>) + Win11 acrylic
/// (<see cref="AcrylicGlass"/>). A custom window per the locked decision (NOT a native
/// toast). The host injects the submit/dismiss callbacks (forwarding to
/// <see cref="NpsService"/>); the parameterless ctor (used by smoke tests + design-time)
/// constructs a no-op view-model. Implements <see cref="IShellSurface"/>.
/// </summary>
public partial class NpsSurveyWindow : Window, IShellSurface
{
    // Theme-aware panel acrylic tint (ABGR 0xAABBGGRR). After the 2026-06-18 theme remap, LIGHT is
    // the frosted light-blue glass and DARK is the slate:
    //   Light → Popup.PanelBg #D7E4FC (light-blue);  Dark → Popup.PanelBg #1D232E (slate).
    // The frost only governs what bleeds through the blur; the Popup.* token paints the fill
    // on top. Mirrors NotificationCardWindow.ApplyThemeAwareAcrylic.
    private const uint PanelAcrylicTintLight = 0xD1FCE4D7u; // ARGB #D1D7E4FC → ABGR (light-blue glass)
    private const uint PanelAcrylicTintDark  = 0xE62E231Du; // ARGB #E61D232E → ABGR (slate)

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_FORCE_TOPMOST = 0x0001 | 0x0002 | 0x0010 | 0x0200;

    public NpsSurveyWindow() : this(new NpsSurveyViewModel()) { }

    public NpsSurveyWindow(NpsSurveyViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opacity = 0;                       // fade in via OnEntrance (avoids an unsettled-position flash)
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => AnchorBottomRight();
        SizeChanged += (_, _) => AnchorBottomRight();
        // TASK #6: track Auto/Light/Dark so the glass adapts instead of being fixed
        // light-blue (fixes white-on-light-glass contrast in Windows dark mode).
        Loaded += OnThemeLoaded;
        Closed += OnThemeClosed;
        ContentRendered += OnEntrance;

        // #6: after a stretch with no response the card gently FADES out (mirroring the popup bar's idle
        // fade) instead of lingering — a calm recession, recorded as a dismissal so NpsService throttles
        // correctly. Any interaction (move / click / key over the card) restarts the idle window, and an
        // interaction mid-fade aborts it so a late answer is never lost.
        _idleDismiss = new DispatcherTimer { Interval = NpsIdleTimeout };
        _idleDismiss.Tick += OnIdleDismissTick;
        Loaded += (_, _) => _idleDismiss.Start();
        Closed += (_, _) => _idleDismiss.Stop();
        PreviewMouseMove += (_, _) => RestartIdle();
        PreviewMouseDown += (_, _) => RestartIdle();
        PreviewKeyDown   += (_, _) => RestartIdle();
    }

    private readonly DispatcherTimer _idleDismiss;
    private static readonly TimeSpan NpsIdleTimeout = TimeSpan.FromSeconds(15);
    private bool _idleFading;

    /// <summary>Restart the no-response idle window on any interaction. If a fade-out is already running,
    /// abort it and restore the card to full opacity so a late answer is never lost.</summary>
    private void RestartIdle()
    {
        if (_idleFading)
        {
            _idleFading = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }
        _idleDismiss.Stop();
        _idleDismiss.Start();
    }

    /// <summary>#6: the idle window elapsed with no response — fade the card out, then record it as a
    /// dismissal (DismissCommand → NpsService.RecordDismissed + close). ReducedMotion skips the fade.</summary>
    private void OnIdleDismissTick(object? sender, EventArgs e)
    {
        _idleDismiss.Stop();
        if (_idleFading) return;
        _idleFading = true;
        if (AccessibilityHelper.ReducedMotion())
        {
            (DataContext as NpsSurveyViewModel)?.DismissCommand.Execute(null);
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(400))) { EasingFunction = ease };
        fade.Completed += (_, _) => { if (_idleFading) (DataContext as NpsSurveyViewModel)?.DismissCommand.Execute(null); };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Native toast-style entrance: the card fades + rises into place once laid out
    /// (bottom-right, so it rises from the corner). ReducedMotion snaps to the resting state.</summary>
    private void OnEntrance(object? sender, EventArgs e)
    {
        if (AccessibilityHelper.ReducedMotion())
        {
            CardRise.BeginAnimation(TranslateTransform.YProperty, null);
            BeginAnimation(OpacityProperty, null);
            CardRise.Y = 0; Opacity = 1;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(220));
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        CardRise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, dur) { EasingFunction = ease });
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ClickThrough.EnableNoActivate(this);
        // TASK #6: pick the theme-matching tint now the HWND exists (re-applied on flips
        // via ApplyPopupTheme).
        bool dark;
        try { dark = ThemeManager.ShouldUseDark(); } catch { dark = false; }
        AcrylicGlass.EnableAcrylic(this, dark ? PanelAcrylicTintDark : PanelAcrylicTintLight);
    }

    // ── TASK #6: dark-variant theming for the popup glass ───────────────────────
    /// <summary>Pack URI for the dark popup-token override (Page-compiled pure
    /// ResourceDictionary, no x:Class — pack-URI-mergeable like Palette.Dark).</summary>
    internal const string DarkPopupTokensUri =
        "pack://application:,,,/SenseEase.App;component/Theme/PopupGlassTokens.Dark.xaml";

    private void OnThemeLoaded(object? sender, RoutedEventArgs e)
    {
        // Also track the GLOBAL dark palette so any borrowed SenseEaseTheme structural
        // brushes stay theme-correct, matching the rest of the app's windows.
        try { ThemeManager.TrackElement(this); } catch { /* no Application in headless tests */ }
        ApplyPopupTheme();
        try { Configuration.UserPreferences.Changed += OnThemePrefsChanged; } catch { }
        try { Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnThemeSystemChanged; } catch { }
    }

    private void OnThemeClosed(object? sender, EventArgs e)
    {
        try { ThemeManager.UntrackElement(this); } catch { }
        try { Configuration.UserPreferences.Changed -= OnThemePrefsChanged; } catch { }
        try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnThemeSystemChanged; } catch { }
    }

    private void OnThemePrefsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) ApplyPopupTheme();
        else Dispatcher.BeginInvoke(new Action(ApplyPopupTheme));
    }

    private void OnThemeSystemChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        if (Dispatcher.CheckAccess()) ApplyPopupTheme();
        else Dispatcher.BeginInvoke(new Action(ApplyPopupTheme));
    }

    /// <summary>Idempotently merge PopupGlassTokens.Dark.xaml ABOVE the light tokens when
    /// dark (add only if absent), remove it when light, and re-pick the acrylic tint —
    /// mirroring ThemeManager's merge. Never throws.</summary>
    private void ApplyPopupTheme()
    {
        bool dark;
        try { dark = ThemeManager.ShouldUseDark(); }
        catch { dark = false; }

        try
        {
            var merged = Resources.MergedDictionaries;
            ResourceDictionary? existing = null;
            foreach (ResourceDictionary rd in merged)
            {
                if (rd.Source is { } src &&
                    string.Equals(src.ToString(), DarkPopupTokensUri, StringComparison.OrdinalIgnoreCase))
                { existing = rd; break; }
            }

            if (dark)
            {
                if (existing is null)
                    merged.Add(new ResourceDictionary { Source = new Uri(DarkPopupTokensUri, UriKind.Absolute) });
            }
            else if (existing is not null)
            {
                merged.Remove(existing);
            }
        }
        catch { /* dictionary merge failed → leave on light default */ }

        AcrylicGlass.EnableAcrylic(this, dark ? PanelAcrylicTintDark : PanelAcrylicTintLight);
    }

    /// <summary>TOUCH: the on-screen keyboard needs an ACTIVE window. The survey is shown
    /// non-activating (ClickThrough.EnableNoActivate) so it never steals focus when it appears —
    /// but a deliberate tap on the (optional) comment field is explicit intent, so activate then
    /// so the touch keyboard can surface. Best-effort; the score-only path needs no keyboard.</summary>
    private void OnCommentFocus(object sender, RoutedEventArgs e)
    {
        if (!IsActive) Activate();
    }

    private void AnchorBottomRight()
    {
        var work = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        Left = work.Right - w - 16;
        Top = work.Bottom - h - 16;
    }

    public void ReassertTopmost()
    {
        if (!IsLoaded) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_FORCE_TOPMOST);
    }

    public (int Left, int Top, int Width, int Height) GetMonitorRectInPhysicalPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int left = (int)Math.Floor(Left * dpi.DpiScaleX);
        int top = (int)Math.Floor(Top * dpi.DpiScaleY);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        return (left, top, width, height);
    }
}
