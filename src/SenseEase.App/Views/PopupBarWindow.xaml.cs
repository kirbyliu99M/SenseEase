using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SenseEase.App.Accessibility;
using SenseEase.App.Services;
using SenseEase.App.Theme;
using SenseEase.App.ViewModels;

namespace SenseEase.App.Views;

/// <summary>
/// UI-RESHAPE — the popup bar window. Renders as clean translucent rounded cards (NO window-wide
/// acrylic: ACCENT_ENABLE_ACRYLICBLURBEHIND frosts the WHOLE window rect, which painted a "massive"
/// frosted rectangle in the empty area around a short flyout — iter-4 fix). Empty window area is
/// transparent (the RootGrid has no fill), so only the actual cards show. The Popup.* colour brushes
/// are referenced via DynamicResource so a live Light↔Dark toggle re-themes the cards (StaticResource
/// resolved once at load and never updated — the "dark mode doesn't change" bug, iter-4 fix).
/// Binds a <see cref="PopupBarViewModel"/>; the <c>ShellController</c> passes a live
/// <see cref="IShellController"/> so the engine owns the tier. Implements <see cref="IShellSurface"/>.
/// </summary>
public partial class PopupBarWindow : Window, IShellSurface
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_FORCE_TOPMOST = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

    // ── #5 proximity auto-close ─────────────────────────────────────────────────
    // The bar would otherwise stay open forever (it shows non-activating, so Deactivated may
    // never fire). A light cursor-proximity poll collapses it back to the edge handle once the
    // cursor leaves — UNLESS it is pinned or a panel (chat/settings) is open (the user is
    // mid-interaction; only an explicit click-away closes those).
    private readonly System.Windows.Threading.DispatcherTimer _autoClose;
    private int _farTicks;
    /// <summary>Cursor must be more than this (dip) outside the bar rect to start closing.</summary>
    public double CloseMarginDip { get; set; } = 96.0;
    /// <summary>Consecutive far ticks (×50 ms) before auto-collapse — a short grace.</summary>
    public int CloseGraceTicks { get; set; } = 8;

    // ── #3 vertical-only drag (the bar is locked to the right edge; it can only move up/down,
    //    and remembers where it was put via UserPreferences.PopupBarTop). ──
    private bool _dragging;
    private double _dragStartCursorY;
    private double _dragStartTop;

    public PopupBarWindow() : this(null, null) { }

    public PopupBarWindow(IShellController? controller, ChatbotViewModel? chat)
    {
        InitializeComponent();
        DataContext = new PopupBarViewModel(controller, chat);
        if (DataContext is INotifyPropertyChanged inpc) inpc.PropertyChanged += OnVmPropertyChanged;
        Deactivated += OnDeactivated;
        IsVisibleChanged += OnVisibleChanged;
        // Track Auto/Light/Dark so the cards re-theme live (the DynamicResource Popup.* brushes
        // re-resolve when PopupGlassTokens.Dark.xaml is merged/un-merged here).
        Loaded += OnThemeLoaded;
        Closed += OnThemeClosed;

        _autoClose = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
        { Interval = TimeSpan.FromMilliseconds(50) };
        _autoClose.Tick += OnAutoCloseTick;
    }

    // ── Light/Dark theming for the popup cards ──────────────────────────────────
    // The window merges the DEFAULT Popup.* tokens (PopupGlassTokens.xaml = slate) view-locally in
    // XAML. On dark we merge PopupGlassTokens.Dark.xaml (Game-Bar black/grey) ABOVE it; the
    // DynamicResource Popup.* refs then re-resolve to the dark values. We listen to BOTH theme
    // signals exactly as ThemeManager does: UserPreferences.Changed (the Settings → Appearance
    // picker / forced Light↔Dark) and the OS light/dark flip (Auto mode). Guarded so the window is
    // still constructible headless.

    /// <summary>Pack URI for the dark popup-token override (Page-compiled pure
    /// ResourceDictionary, no x:Class — pack-URI-mergeable like Palette.Dark).</summary>
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
        _autoClose.Stop();
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

    /// <summary>Idempotently bring the Popup.* tokens into sync with the effective theme: merge
    /// PopupGlassTokens.Dark.xaml when dark (add only if absent), remove it when light. The
    /// DynamicResource brush refs in the XAML re-resolve automatically. Never throws.</summary>
    private void ApplyPopupTheme()
    {
        bool dark;
        try { dark = ThemeManager.ShouldUseDark(); }
        catch { dark = false; } // no Application / registry → light default

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
                // Added LAST so it sits ABOVE the default PopupGlassTokens.xaml merged in XAML.
                if (existing is null)
                    merged.Add(new ResourceDictionary { Source = new Uri(DarkPopupTokensUri, UriKind.Absolute) });
            }
            else if (existing is not null)
            {
                merged.Remove(existing);
            }
        }
        catch { /* dictionary merge failed → leave on default */ }
    }

    // ── #2 reveal motion — chat/settings cards fade+slide in, subtly. A gentle ease-out (no
    //    overshoot, no clip churn) keeps it understated for a motion-sickness tool; ReducedMotion
    //    snaps. Driven by the VM's open flags. ──
    private const double PanelSlideOffset = 18.0;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not PopupBarViewModel vm) return;
        if (e.PropertyName == nameof(PopupBarViewModel.IsChatOpen))
        {
            // Activate ONLY when the chat opens, so the input can take keyboard focus — the bar
            // itself is shown non-activating (ShowActivated=False) to avoid stealing focus.
            if (vm.IsChatOpen) Activate();
            AnimatePanel(ChatPanel, ChatXform, isX: true, open: vm.IsChatOpen, isOpenNow: () => vm.IsChatOpen);
        }
        else if (e.PropertyName == nameof(PopupBarViewModel.IsSettingsOpen))
        {
            AnimatePanel(SettingsFlyout, SettingsXform, isX: false, open: vm.IsSettingsOpen, isOpenNow: () => vm.IsSettingsOpen);
        }
        else if (e.PropertyName == nameof(PopupBarViewModel.IsExpanded) && vm.IsExpanded)
        {
            // #2: fade the newly-revealed sections in so expand reads as a soft reveal, not a pop.
            // (The bar frame still resizes via SizeToContent; the centre-anchor keeps it from
            // shifting, and the content fade removes the "flash".) ReducedMotion snaps.
            FadeInSections();
        }
    }

    /// <summary>#2: gentle opacity fade-in for the meter + tools sections on expand.</summary>
    private void FadeInSections()
    {
        bool rm = AccessibilityHelper.ReducedMotion();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var s in new FrameworkElement[] { MeterSection, ToolsSection })
        {
            if (s is null) continue;
            if (rm) { s.BeginAnimation(OpacityProperty, null); s.Opacity = 1; continue; }
            s.Opacity = 0;
            s.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150))) { EasingFunction = ease });
        }
    }

    /// <summary>#2: a gentle slide-in-from-edge + fade for the bar itself on reveal, instead of an
    /// instant pop. ReducedMotion snaps.</summary>
    private void AnimateReveal()
    {
        if (AccessibilityHelper.ReducedMotion())
        {
            BarXform.BeginAnimation(TranslateTransform.XProperty, null);
            BarRoot.BeginAnimation(OpacityProperty, null);
            BarXform.X = 0; BarRoot.Opacity = 1;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BarRoot.Opacity = 0;
        BarRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160))) { EasingFunction = ease });
        BarXform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(14, 0, new Duration(TimeSpan.FromMilliseconds(180))) { EasingFunction = ease });
    }

    private void AnimatePanel(FrameworkElement panel, TranslateTransform xform, bool isX, bool open, Func<bool> isOpenNow)
    {
        var prop = isX ? TranslateTransform.XProperty : TranslateTransform.YProperty;

        if (AccessibilityHelper.ReducedMotion())
        {
            xform.BeginAnimation(prop, null);
            panel.BeginAnimation(OpacityProperty, null);
            xform.X = 0; xform.Y = 0;
            panel.Opacity = open ? 1.0 : 0.0;
            panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (open)
        {
            panel.Visibility = Visibility.Visible;
            // Understated settle: a short ease-out glide + fade. No overshoot (BackEase read as
            // "flashy"); transform/opacity only.
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            xform.BeginAnimation(prop, new DoubleAnimation(PanelSlideOffset, 0, new Duration(TimeSpan.FromMilliseconds(150))) { EasingFunction = ease });
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(130))) { EasingFunction = ease });
        }
        else
        {
            // Retract faster (≈70% of enter), ease-in toward the trigger.
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(100))) { EasingFunction = ease };
            // Collapse only once fully closed AND still closed (interruptible: a re-open cancels it).
            fade.Completed += (_, _) => { if (!isOpenNow()) panel.Visibility = Visibility.Collapsed; };
            xform.BeginAnimation(prop, new DoubleAnimation(PanelSlideOffset, new Duration(TimeSpan.FromMilliseconds(110))) { EasingFunction = ease });
            panel.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void OnVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) { _farTicks = 0; _autoClose.Start(); AnimateReveal(); }
        else _autoClose.Stop();
    }

    private void OnAutoCloseTick(object? sender, EventArgs e)
    {
        if (!IsVisible) { _autoClose.Stop(); return; }
        if (DataContext is not PopupBarViewModel vm) return;

        // Never auto-close while pinned or mid-interaction (a panel open). Reset the grace so a
        // brief excursion while interacting doesn't collapse the moment the panel closes.
        if (vm.IsPinned || vm.IsChatOpen || vm.IsSettingsOpen) { _farTicks = 0; return; }

        if (!GetCursorPos(out POINT p)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = p.X / dpi.DpiScaleX, cy = p.Y / dpi.DpiScaleY;
        double dx = Math.Max(0, Math.Max(Left - cx, cx - (Left + ActualWidth)));
        double dy = Math.Max(0, Math.Max(Top - cy, cy - (Top + ActualHeight)));
        double dist = Math.Sqrt((dx * dx) + (dy * dy));

        if (dist <= CloseMarginDip) { _farTicks = 0; return; }
        if (++_farTicks >= CloseGraceTicks) { _farTicks = 0; CollapseToHandle(); }
    }

    /// <summary>Collapse the popup back to the discreet edge handle and reset the panels +
    /// expand state so the next reveal opens clean. ShellController re-shows the sidebar on the
    /// resulting IsVisibleChanged.</summary>
    private void CollapseToHandle()
    {
        if (DataContext is PopupBarViewModel vm)
        { vm.IsChatOpen = false; vm.IsSettingsOpen = false; vm.IsExpanded = false; }
        Hide();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Click-away close — but a PINNED bar stays put (that is the point of the pin).
        if (DataContext is PopupBarViewModel { IsPinned: true }) return;
        CollapseToHandle();
    }

    private void OnChatInputVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // When the chat panel reveals, put the caret in the input so the user can type at once.
        // TOUCH: the on-screen keyboard only appears for an ACTIVE window with keyboard focus, so
        // activate first (defensive — IsChatOpen already activates) and re-assert focus after layout.
        if (e.NewValue is true && sender is System.Windows.Controls.TextBox tb)
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!IsActive) Activate();
                    tb.Focus();
                    System.Windows.Input.Keyboard.Focus(tb);
                }),
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnChatInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (DataContext is PopupBarViewModel { Chat: { } chat } && chat.SendCommand.CanExecute(null))
        {
            chat.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── #3 vertical-only drag (with a small threshold) + centre persist ─────────────
    private const double DragThresholdDip = 4.0;
    private bool _dragArmed;

    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return; // let buttons/inputs win
        _dragStartTop = Top;
        _dragStartCursorY = CursorYDip();
        _dragArmed = true;   // armed, but not actually dragging until past the threshold
        _dragging = false;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragArmed) return;
        double dy = CursorYDip() - _dragStartCursorY;
        if (!_dragging)
        {
            if (Math.Abs(dy) < DragThresholdDip) return; // ignore a plain click / micro-jitter
            _dragging = true;
        }
        // Move on the Y axis ONLY — the bar stays pinned to the right edge.
        var work = SystemParameters.WorkArea;
        Top = Math.Max(work.Top, Math.Min(work.Bottom - ActualHeight, _dragStartTop + dy));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left) return;
        bool wasDragging = _dragging;
        _dragArmed = false;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (wasDragging) PersistCentre();
    }

    private double CursorYDip()
        => GetCursorPos(out POINT p) ? p.Y / VisualTreeHelper.GetDpi(this).DpiScaleY : Top;

    /// <summary>Remember the dragged vertical CENTRE so the bar returns here AND stays put when it
    /// grows/shrinks (the centre is resize-invariant, unlike the top).</summary>
    private void PersistCentre()
    {
        try
        {
            Configuration.UserPreferences.Current.PopupBarCenterY = Top + (ActualHeight / 2.0);
            Configuration.UserPreferences.Current.Save();
        }
        catch { /* best-effort */ }
    }

    private static bool IsInteractive(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ButtonBase || d is TextBoxBase || d is System.Windows.Controls.Primitives.ToggleButton)
                return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null;
        }
        return false;
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
