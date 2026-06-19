using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SenseEase.App.Interop;
using SenseEase.App.Services;
using SenseEase.App.Theme;

namespace SenseEase.App.Views;

/// <summary>
/// Item 1 — the consent bubble: a small, no-activate (never steals the user's app focus) glass card
/// that asks "start the comfort overlay?" the first time the engine wants to engage the mask. The
/// mask is held invisible (clamped to L1 in <c>MainWindow.OnStateChanged</c>) until the user taps
/// Start, so a missed / ignored bubble simply means no overlay — fail-safe, no actuation without
/// consent. Owned + positioned by <see cref="ShellController"/> (anchored near the bar/edge);
/// implements <see cref="IShellSurface"/> for the WGC exclude-rect union + topmost fan-out.
/// </summary>
public partial class ConsentBubbleWindow : Window, IShellSurface
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_FORCE_TOPMOST = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER

    /// <summary>User tapped Start — let the overlay engage.</summary>
    public event EventHandler? ConsentAccepted;
    /// <summary>User tapped "Not now" — keep the overlay off + start the cooldown.</summary>
    public event EventHandler? ConsentDeclined;

    public ConsentBubbleWindow()
    {
        InitializeComponent();
        // No-activate (NOT click-through) — the buttons stay clickable but the bubble never grabs
        // foreground from the user's app/game. Applied once the HWND exists.
        SourceInitialized += (_, _) => { try { ClickThrough.EnableNoActivate(this); } catch { } };
        // Track Auto/Light/Dark so the bubble adapts (it was light-token-locked: merged only the LIGHT
        // PopupGlassTokens, so it stayed light-blue glass even in Dark mode + showed failing navy ink).
        Loaded += OnThemeLoaded;
        Closed += OnThemeClosed;
    }

    // ── Dark-variant theming (mirrors NpsSurveyWindow / EdgeSidebarWindow). No acrylic on this
    //    surface, so this only swaps the Popup.* token dictionary; never throws (headless-guarded). ──
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

    /// <summary>Idempotently merge PopupGlassTokens.Dark.xaml ABOVE the light tokens when dark
    /// (add only if absent), remove it when light. Never throws.</summary>
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

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        ConsentAccepted?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    private void OnNotNowClicked(object sender, RoutedEventArgs e)
    {
        ConsentDeclined?.Invoke(this, EventArgs.Empty);
        Hide();
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
        int left = (int)Math.Floor(Left * dpi.DpiScaleX);
        int top = (int)Math.Floor(Top * dpi.DpiScaleY);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        return (left, top, width, height);
    }
}
