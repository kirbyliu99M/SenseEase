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
    /// <summary>The card was expanded (true) or collapsed (false). The host suspends the consent
    /// linger tuck-away while expanded so the card can't be pulled away mid-decision (Kirby 2026-06-20).</summary>
    public event EventHandler<bool>? ExpandedChanged;

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
        // Refit: the window is created once + reused (Show/Hide), so re-collapse to the avatar on each show.
        IsVisibleChanged += OnVisibleReset;
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

    // ── Refit (2026-06-19): collapsed avatar <-> expanded message bubble. Reuses the popup's flash-free
    //    expand — the WINDOW is fixed-size, so the message SECTION grows Height + slides/fades INSIDE it
    //    (no layered-window resize = no flash; no Effect for the same reason). ReducedMotion snaps. ──
    private bool _expanded;
    private static readonly Duration ExpandDur = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration CollapseDur = new(TimeSpan.FromMilliseconds(150));

    private void OnAvatarClicked(object sender, RoutedEventArgs e) => SetExpanded(!_expanded);

    /// <summary>Re-collapse to the avatar (+ restore the notice dot) every time the bubble is shown,
    /// since ShellController creates the instance once and reuses it via Show()/Hide().</summary>
    private void OnVisibleReset(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        _expanded = false;
        ExpandedChanged?.Invoke(this, false); // re-shown collapsed → host clears any stale interacting state
        MessageBubble.BeginAnimation(HeightProperty, null);
        MessageBubble.BeginAnimation(OpacityProperty, null);
        MsgShift.BeginAnimation(TranslateTransform.YProperty, null);
        MessageBubble.Height = double.NaN;
        MessageBubble.Opacity = 0;
        MessageBubble.Visibility = Visibility.Collapsed;
        MsgShift.Y = 0;
        if (NoticeDot is not null) NoticeDot.Visibility = Visibility.Visible;
    }

    private void SetExpanded(bool expand)
    {
        if (_expanded == expand) return;
        _expanded = expand;
        ExpandedChanged?.Invoke(this, expand);

        bool rm;
        try { rm = AccessibilityHelper.ReducedMotion(); } catch { rm = false; }

        MessageBubble.BeginAnimation(HeightProperty, null);
        MessageBubble.BeginAnimation(OpacityProperty, null);
        MsgShift.BeginAnimation(TranslateTransform.YProperty, null);

        if (expand)
        {
            if (NoticeDot is not null) NoticeDot.Visibility = Visibility.Collapsed;
            MessageBubble.Visibility = Visibility.Visible;
            MessageBubble.Height = double.NaN;
            MessageBubble.Measure(new Size(MessageBubble.Width, double.PositiveInfinity));
            double target = MessageBubble.DesiredSize.Height;

            if (rm)
            {
                MessageBubble.Height = double.NaN; MessageBubble.Opacity = 1; MsgShift.Y = 0;
            }
            else
            {
                var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
                MessageBubble.Height = 0;
                var h = new DoubleAnimation(0, target, ExpandDur) { EasingFunction = ease };
                h.Completed += (_, _) => { if (_expanded) MessageBubble.Height = double.NaN; };
                MessageBubble.BeginAnimation(HeightProperty, h);
                MessageBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ExpandDur) { EasingFunction = ease });
                MsgShift.Y = 8;
                MsgShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, ExpandDur) { EasingFunction = ease });
            }

            // Focus Start so Enter activates it (effective once the bubble holds keyboard focus).
            Dispatcher.BeginInvoke(new Action(() => { try { StartBtn.Focus(); } catch { } }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (rm)
        {
            MessageBubble.Opacity = 0; MessageBubble.Height = double.NaN;
            MessageBubble.Visibility = Visibility.Collapsed;
            if (NoticeDot is not null) NoticeDot.Visibility = Visibility.Visible;
        }
        else
        {
            var ease = new QuinticEase { EasingMode = EasingMode.EaseIn };
            double from = MessageBubble.ActualHeight;
            MessageBubble.Height = from;
            var h = new DoubleAnimation(from, 0, CollapseDur) { EasingFunction = ease };
            h.Completed += (_, _) =>
            {
                if (_expanded) return;
                MessageBubble.Visibility = Visibility.Collapsed;
                MessageBubble.Height = double.NaN;
                if (NoticeDot is not null) NoticeDot.Visibility = Visibility.Visible;
            };
            MessageBubble.BeginAnimation(HeightProperty, h);
            MessageBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(MessageBubble.Opacity, 0, CollapseDur) { EasingFunction = ease });
            MsgShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 8, CollapseDur) { EasingFunction = ease });
        }
    }

    /// <summary>Keyboard (audit H2): Esc collapses (or declines if already collapsed); Enter/Space on the
    /// collapsed avatar expands; once expanded, Start is IsDefault so Enter activates it.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_expanded) SetExpanded(false);
            else { ConsentDeclined?.Invoke(this, EventArgs.Empty); Hide(); }
            return;
        }
        if (!_expanded && (e.Key == Key.Enter || e.Key == Key.Space))
        {
            e.Handled = true;
            SetExpanded(true);
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
        // R3-7: the window is now a fixed 320x240 that is mostly transparent; the VISIBLE content hugs the
        // right edge, vertically centred. Exclude only a CONTENT footprint (sized for the expanded bubble,
        // constant so it covers both the collapsed avatar and the expanded bubble) — not the whole window,
        // which would over-exclude the transparent area from sensing. Fall back to the window if unrealized.
        if (w <= 0 || h <= 0)
            return ((int)Math.Floor(Left * dpi.DpiScaleX), (int)Math.Floor(Top * dpi.DpiScaleY),
                    (int)Math.Ceiling(Width * dpi.DpiScaleX), (int)Math.Ceiling(Height * dpi.DpiScaleY));

        const double cw = 300, ch = 220;
        double cLeftDip = Left + (w - cw) - 4;          // right-aligned (RootGrid margin ~10)
        double cTopDip = Top + ((h - ch) / 2.0);        // vertically centred
        int left = (int)Math.Floor(cLeftDip * dpi.DpiScaleX);
        int top = (int)Math.Floor(cTopDip * dpi.DpiScaleY);
        int width = (int)Math.Ceiling(cw * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ch * dpi.DpiScaleY);
        return (left, top, width, height);
    }
}
