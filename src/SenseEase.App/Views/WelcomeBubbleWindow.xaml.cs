using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SenseEase.App.Accessibility;
using SenseEase.App.Interop;
using SenseEase.App.Services;
using SenseEase.App.Theme;

namespace SenseEase.App.Views;

/// <summary>
/// W-A first-run welcome (2026-06-20): a one-time, no-activate glass message bubble shown on the shell
/// entry path (<see cref="ShellController"/>.Start) when <c>AppConfig.IsFirstRun</c>, anchored near the
/// edge handle. It gives a fresh user context for the otherwise-unexplained edge tab + red consent dot
/// (onboarding was stranded in the now-hidden console). Owned + positioned by the controller; implements
/// <see cref="IShellSurface"/> for the WGC exclude-rect union + topmost fan-out (so the sensing loop never
/// feeds back its own pixels). Same flash-free rules as the consent bubble (fixed-size layered window, no
/// Effect); a gentle fade+rise entrance, ReducedMotion snaps; a single "Got it" dismiss.
/// </summary>
public partial class WelcomeBubbleWindow : Window, IShellSurface
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_FORCE_TOPMOST = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER

    private static readonly Duration EntranceDur = new(TimeSpan.FromMilliseconds(300));

    /// <summary>User dismissed the welcome ("Got it" / Esc / Enter) — the host hides + unregisters it.</summary>
    public event EventHandler? Dismissed;

    public WelcomeBubbleWindow()
    {
        InitializeComponent();
        // No-activate (NOT click-through): the button stays clickable but the bubble never grabs foreground
        // from the user's app/game. Applied once the HWND exists.
        SourceInitialized += (_, _) => { try { ClickThrough.EnableNoActivate(this); } catch { } };
        Loaded += OnThemeLoaded;
        Closed += OnThemeClosed;
        Loaded += (_, _) => PlayEntrance();
    }

    // ── Dark-variant theming (mirrors ConsentBubbleWindow / NpsSurveyWindow). No acrylic on this surface,
    //    so this only swaps the Popup.* token dictionary; never throws (headless-guarded). ──
    internal const string DarkPopupTokensUri =
        "pack://application:,,,/SenseEase.App;component/Theme/PopupGlassTokens.Dark.xaml";

    private void OnThemeLoaded(object? sender, RoutedEventArgs e)
    {
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

    /// <summary>Idempotently merge PopupGlassTokens.Dark.xaml ABOVE the light tokens when dark (add only if
    /// absent), remove it when light. Never throws.</summary>
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
        catch { /* leave on light default */ }
    }

    /// <summary>Gentle fade + rise entrance (the house "calm breath" bloom). ReducedMotion snaps.</summary>
    private void PlayEntrance()
    {
        bool rm;
        try { rm = AccessibilityHelper.ReducedMotion(); } catch { rm = false; }

        MessageBubble.BeginAnimation(OpacityProperty, null);
        EntranceShift.BeginAnimation(TranslateTransform.YProperty, null);

        if (rm)
        {
            MessageBubble.Opacity = 1; EntranceShift.Y = 0;
            return;
        }

        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        MessageBubble.Opacity = 0;
        MessageBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, EntranceDur) { EasingFunction = ease });
        EntranceShift.Y = 10;
        EntranceShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, EntranceDur) { EasingFunction = ease });

        // Focus "Got it" so Enter activates it (effective once the bubble holds keyboard focus).
        Dispatcher.BeginInvoke(new Action(() => { try { GotItBtn.Focus(); } catch { } }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnGotItClicked(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);

    /// <summary>Keyboard: Esc or Enter dismisses (Got it is IsDefault, so Enter activates it too).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Enter)
        {
            e.Handled = true;
            Dismissed?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnKeyDown(e);
    }

    public void ReassertTopmost()
    {
        if (!IsLoaded) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_FORCE_TOPMOST);
    }

    public (int Left, int Top, int Width, int Height) GetMonitorRectInPhysicalPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = ActualWidth, h = ActualHeight;
        // The window is a fixed 320x240 that is mostly transparent; the VISIBLE content hugs the right edge,
        // vertically centred. Exclude only a CONTENT footprint (not the whole window, which would
        // over-exclude the transparent area from sensing). Fall back to the window if unrealized.
        if (w <= 0 || h <= 0)
            return ((int)Math.Floor(Left * dpi.DpiScaleX), (int)Math.Floor(Top * dpi.DpiScaleY),
                    (int)Math.Ceiling(Width * dpi.DpiScaleX), (int)Math.Ceiling(Height * dpi.DpiScaleY));

        const double cw = 300, ch = 200;
        double cLeftDip = Left + (w - cw) - 4;          // right-aligned (RootGrid margin ~10)
        double cTopDip = Top + ((h - ch) / 2.0);        // vertically centred
        int left = (int)Math.Floor(cLeftDip * dpi.DpiScaleX);
        int top = (int)Math.Floor(cTopDip * dpi.DpiScaleY);
        int width = (int)Math.Ceiling(cw * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ch * dpi.DpiScaleY);
        return (left, top, width, height);
    }
}
