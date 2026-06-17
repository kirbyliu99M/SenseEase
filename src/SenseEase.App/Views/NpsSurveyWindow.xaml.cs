using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
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
    // Theme-aware panel acrylic tint (ABGR 0xAABBGGRR). Light: from Popup.PanelBg
    // #D1D7E4FC (light glass). Dark: from PopupGlassTokens.Dark's Popup.PanelBg #D11D232E —
    // deep slate so the survey card reads as dark glass with the light Popup.Ink ramp on top
    // (fixes white-on-light-glass contrast in Windows dark mode). Mirrors
    // NotificationCardWindow.ApplyThemeAwareAcrylic.
    private const uint PanelAcrylicTintLight = 0xD1FCE4D7u; // ARGB #D1D7E4FC → ABGR
    private const uint PanelAcrylicTintDark  = 0xD12E231Du; // ARGB #D11D232E → ABGR

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_FORCE_TOPMOST = 0x0001 | 0x0002 | 0x0010 | 0x0200;

    public NpsSurveyWindow() : this(new NpsSurveyViewModel()) { }

    public NpsSurveyWindow(NpsSurveyViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => AnchorBottomRight();
        SizeChanged += (_, _) => AnchorBottomRight();
        // TASK #6: track Auto/Light/Dark so the glass adapts instead of being fixed
        // light-blue (fixes white-on-light-glass contrast in Windows dark mode).
        Loaded += OnThemeLoaded;
        Closed += OnThemeClosed;
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
