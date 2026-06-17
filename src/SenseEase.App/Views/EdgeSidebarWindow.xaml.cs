using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SenseEase.App.Accessibility;
using SenseEase.App.Converters;
using SenseEase.App.Interop;
using SenseEase.App.Services;
using SenseEase.App.Theme;

namespace SenseEase.App.Views;

/// <summary>
/// UI-RESHAPE prep — the discreet right-edge handle that reveals the popup bar. Reuses
/// the proven <see cref="PersistentBarWindow"/> pattern (a 60 Hz Win32 cursor-proximity
/// poller rather than WPF MouseMove, since the resting strip is only ~4 dip wide), plus
/// Win32 topmost re-assertion and the WGC-exclude rect contract via
/// <see cref="IShellSurface"/>. The future <c>ShellController</c> subscribes to
/// <see cref="Clicked"/> to open the popup bar and calls <see cref="SetTier"/> to tint
/// the handle by the live status.
/// </summary>
public partial class EdgeSidebarWindow : Window, IShellSurface
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_FORCE_TOPMOST = SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;

    /// <summary>Distance from the window rect (dip) inside which the handle reveals.</summary>
    public double HoverProximityDip { get; set; } = 140.0;
    /// <summary>Hysteresis: once revealed, the handle only re-hides past this LARGER distance, so a
    /// cursor sitting right at the enter threshold can't flip-flop the reveal animation (the tremble).</summary>
    public double HoverExitDip { get; set; } = 172.0;
    /// <summary>Resting horizontal scale of the trapezoid (tucked toward the edge).</summary>
    public double RestScaleX { get; set; } = 0.55;
    /// <summary>Resting opacity — discreet but findable (a touch user can't hover to discover it,
    /// so it must be perceptible enough to tap at rest).</summary>
    public double RestOpacity { get; set; } = 0.28;
    /// <summary>Revealed opacity — the dark-grey handle brightens on mouse approach.</summary>
    public double RevealedOpacity { get; set; } = 0.52;

    private readonly DispatcherTimer _proximityTimer;
    private bool _isHovered;
    private bool _revealFired;
    private int _topmostTickCounter;

    /// <summary>Raised on left-click while revealed — the host opens the popup bar.</summary>
    public event EventHandler? Clicked;

    /// <summary>Raised when the cursor reaches the screen's right edge within the handle's
    /// vertical band — a deliberate "throw to the edge" gesture the host treats as a reveal
    /// (the two-stage flow: edge → condensed bar). Fires once per edge entry.</summary>
    public event EventHandler? RevealRequested;

    public EdgeSidebarWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
        // TOUCH FIX: the handle is ALWAYS hit-test-visible (it used to be click-through until a
        // mouse HOVER set it true via the proximity poller — but a touch user has no hovering
        // cursor, so the handle never became tappable and the bar could never be opened). Now a
        // direct tap/click always reveals it; the proximity poller only adds the mouse hover-brighten
        // + edge-throw niceties on top. Trade-off: it claims a thin right-edge strip from mouse
        // edge-clicks (a standard edge-launcher cost); kept narrow + short + ReassertTopmost.

        _proximityTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _proximityTimer.Tick += OnProximityTick;

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Mirror PersistentBarWindow's click model: pass-through-when-far is handled by
        // toggling IsHitTestVisible (a layered transparent window), NOT WS_EX_TRANSPARENT
        // — which would permanently eat the reveal click. NoActivate keeps the handle from
        // stealing focus from the user's app when clicked.
        ClickThrough.EnableNoActivate(this);
        RepositionForWorkArea();
        _proximityTimer.Start();

        // TASK #6: track Auto/Light/Dark so the popup glass tokens this window merges
        // stay theme-correct (the neutral #3A3A3A handle itself reads on both themes, so
        // there is no acrylic tint to flip here — see ApplyPopupTheme). Listen to BOTH
        // theme signals exactly as ThemeManager does.
        try { ThemeManager.TrackElement(this); } catch { /* no Application in headless tests */ }
        ApplyPopupTheme();
        try { Configuration.UserPreferences.Changed += OnThemePrefsChanged; } catch { }
        try { Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnThemeSystemChanged; } catch { }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _proximityTimer.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        try { ThemeManager.UntrackElement(this); } catch { }
        try { Configuration.UserPreferences.Changed -= OnThemePrefsChanged; } catch { }
        try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnThemeSystemChanged; } catch { }
    }

    // ── TASK #6: dark-variant theming for the popup glass tokens ────────────────
    /// <summary>Pack URI for the dark popup-token override (Page-compiled pure
    /// ResourceDictionary, no x:Class — pack-URI-mergeable like Palette.Dark).</summary>
    internal const string DarkPopupTokensUri =
        "pack://application:,,,/SenseEase.App;component/Theme/PopupGlassTokens.Dark.xaml";

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
    /// dark (add only if absent) and remove it when light, mirroring ThemeManager's merge.
    /// The handle has no acrylic, so there is no tint to flip here. Never throws.</summary>
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
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => { RepositionForWorkArea(); ReassertTopmost(); }));

    private void RepositionForWorkArea()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width;                       // flush to the right edge
        // #4: align the handle's CENTRE with the popup's remembered centre (where the user left the
        // bar), so the handle and the bar move together; default = vertically centred.
        double centreY;
        try { centreY = Configuration.UserPreferences.Current.PopupBarCenterY ?? (work.Top + (work.Height / 2.0)); }
        catch { centreY = work.Top + (work.Height / 2.0); }
        Top = Math.Max(work.Top, Math.Min(work.Bottom - Height, centreY - (Height / 2.0)));
    }

    /// <summary>#4: re-read the popup's remembered centre and re-anchor (called by the host when the
    /// handle is re-shown after the bar collapses, so they stay aligned). Never throws.</summary>
    public void ReanchorToSavedCentre()
    {
        if (Dispatcher.CheckAccess()) { try { RepositionForWorkArea(); ReassertTopmost(); } catch { } }
        else Dispatcher.BeginInvoke(new Action(ReanchorToSavedCentre));
    }

    public void ReassertTopmost()
    {
        if (!IsLoaded) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_FORCE_TOPMOST);
    }

    /// <summary>UI-RESHAPE iter: the edge handle is now a NEUTRAL discreet trapezoid (no tier
    /// tint) — kept as a no-op for the host's StateChanged fan-out contract.</summary>
    public void SetTier(int tier) { /* neutral handle — intentionally no tier colour */ }

    private void OnProximityTick(object? sender, EventArgs e)
    {
        // A tick already queued when the window closes still runs once after the timer is
        // stopped; touching a closed window (GetDpi / SetWindowPos) then throws on the
        // dispatcher. No-op once unloaded so teardown (incl. smoke tests) stays clean.
        if (!IsLoaded) return;

        if (++_topmostTickCounter >= 12) { _topmostTickCounter = 0; ReassertTopmost(); }

        if (!GetCursorPos(out POINT physical)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = physical.X / dpi.DpiScaleX;
        double cy = physical.Y / dpi.DpiScaleY;

        double dx = Math.Max(0, Math.Max(Left - cx, cx - (Left + ActualWidth)));
        double dy = Math.Max(0, Math.Max(Top - cy, cy - (Top + ActualHeight)));
        double dist = Math.Sqrt((dx * dx) + (dy * dy));

        // Deliberate "throw cursor to the right edge" opens the bar (in addition to a click on
        // the visible handle). Gate to the handle's vertical band so hitting the edge elsewhere
        // (a maximized app's close button, the top-right corner) does not summon it. Fire once
        // per edge entry; rearm only once the cursor has clearly left the handle.
        bool atEdge = cx >= (Left + ActualWidth - 2.0) && cy >= Top && cy <= (Top + ActualHeight);
        if (atEdge)
        {
            if (!_revealFired) { _revealFired = true; RevealRequested?.Invoke(this, EventArgs.Empty); }
        }
        else if (dist > 4.0) _revealFired = false;

        // Hysteresis: enter at HoverProximityDip, only exit past the larger HoverExitDip — no
        // flip-flop tremble when the cursor lingers at the boundary.
        bool nowHovered = _isHovered ? dist <= HoverExitDip : dist <= HoverProximityDip;
        if (nowHovered == _isHovered) return;
        _isHovered = nowHovered;
        // (IsHitTestVisible stays TRUE always now — see ctor; the poller only drives the visual
        // hover-brighten for mouse users.)
        AnimateReveal(nowHovered);
    }

    private void AnimateReveal(bool reveal)
    {
        double targetOpacity = reveal ? RevealedOpacity : RestOpacity;
        double targetScale = reveal ? 1.0 : RestScaleX;

        if (AccessibilityHelper.ReducedMotion())
        {
            Handle.BeginAnimation(OpacityProperty, null);
            HandleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Handle.Opacity = targetOpacity;
            HandleScale.ScaleX = targetScale;
            return;
        }
        // Apple-grade: ease-out on enter, transform/opacity only, ~160 ms.
        var dur = new Duration(TimeSpan.FromMilliseconds(160));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Handle.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, dur) { EasingFunction = ease });
        HandleScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(targetScale, dur) { EasingFunction = ease });
    }

    /// <summary>Monitor-coordinate physical-pixel footprint for the WGC exclude mask.</summary>
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
