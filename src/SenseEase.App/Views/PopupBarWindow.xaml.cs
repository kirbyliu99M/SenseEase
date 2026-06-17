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

    // ── #2/#4 reveal motion — see AnimateCard: one unified fade+scale "Windows flyout" pop for the
    //    bar reveal AND the chat/settings cards. Driven by the VM's open flags. ReducedMotion snaps. ──
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not PopupBarViewModel vm) return;
        if (e.PropertyName == nameof(PopupBarViewModel.IsChatOpen))
        {
            // Activate ONLY when the chat opens, so the input can take keyboard focus — the bar
            // itself is shown non-activating (ShowActivated=False) to avoid stealing focus.
            if (vm.IsChatOpen) Activate();
            AnimateCard(ChatPanel, ChatScale, vm.IsChatOpen, () => vm.IsChatOpen);
        }
        else if (e.PropertyName == nameof(PopupBarViewModel.IsSettingsOpen))
        {
            AnimateCard(SettingsFlyout, SettingsScale, vm.IsSettingsOpen, () => vm.IsSettingsOpen);
        }
        else if (e.PropertyName == nameof(PopupBarViewModel.IsExpanded) && vm.IsExpanded)
        {
            // #2: fade the newly-revealed sections in so expand reads as a soft reveal, not a pop.
            // (The bar frame still resizes via SizeToContent; the centre-anchor keeps it from
            // shifting, and the content fade removes the "flash".) ReducedMotion snaps.
            FadeInSections();
        }
    }

    /// <summary>#2: gentle opacity fade-in for the in-bar meter + tools sections on expand (a fade,
    /// not a scale — scaling a vertical sub-section reads as squashing). Same easing/duration rhythm
    /// as the card pop so the whole UI feels unified. ReducedMotion snaps.</summary>
    private void FadeInSections()
    {
        bool rm = AccessibilityHelper.ReducedMotion();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var s in new FrameworkElement[] { MeterSection, ToolsSection })
        {
            if (s is null) continue;
            if (rm) { s.BeginAnimation(OpacityProperty, null); s.Opacity = 1; continue; }
            s.Opacity = 0;
            s.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, CardOpenDur) { EasingFunction = ease });
        }
    }

    private void AnimateReveal() => AnimateCard(BarRoot, BarScale, open: true, stillOpen: null);

    // Unified "Windows flyout" motion: fade + a subtle scale-up from the trigger edge. ONE easing,
    // ONE pair of durations — the bar reveal AND the chat / quick-settings cards all pop out the same
    // way (the user asked for a single consistent, calm, Windows-card feel). ReducedMotion snaps.
    private static readonly Duration CardOpenDur  = new(TimeSpan.FromMilliseconds(190));
    private static readonly Duration CardCloseDur = new(TimeSpan.FromMilliseconds(130));
    private const double CardScaleFrom = 0.96;

    private void AnimateCard(FrameworkElement card, ScaleTransform scale, bool open, Func<bool>? stillOpen)
    {
        if (AccessibilityHelper.ReducedMotion())
        {
            card.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = 1.0;
            card.Opacity = open ? 1.0 : 0.0;
            card.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (open)
        {
            card.Visibility = Visibility.Visible;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, CardOpenDur) { EasingFunction = ease });
            var s = new DoubleAnimation(CardScaleFrom, 1, CardOpenDur) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
        }
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn }; // exit a touch quicker
            var fade = new DoubleAnimation(0, CardCloseDur) { EasingFunction = ease };
            if (stillOpen != null) fade.Completed += (_, _) => { if (!stillOpen()) card.Visibility = Visibility.Collapsed; };
            card.BeginAnimation(OpacityProperty, fade);
            var s = new DoubleAnimation(CardScaleFrom, CardCloseDur) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
        }
    }

    private void OnVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _farTicks = 0; _autoClose.Start();
            // #2 (tremble): keep the bar invisible, then DEFER the reveal fade one layout pass so
            // SizeToContent has settled the window position — otherwise it visibly jitters as it sizes.
            BarRoot.BeginAnimation(OpacityProperty, null);
            BarRoot.Opacity = 0;
            Dispatcher.BeginInvoke(new Action(AnimateReveal), System.Windows.Threading.DispatcherPriority.Loaded);
        }
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
    // Mouse AND touch are handled explicitly. Touch→mouse promotion is unreliable for a drag, so
    // the touch path handles the gesture itself and sets e.Handled (which suppresses promotion) —
    // _dragTouch guards the mouse path from also firing on the same gesture (no double-drag).
    private const double DragThresholdDip = 4.0;
    private bool _dragArmed;
    private TouchDevice? _dragTouch; // non-null while a TOUCH owns the gesture

    // Mouse path
    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _dragTouch is not null) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return; // let buttons/inputs win
        BeginDrag(CursorYDip());
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragTouch is null && _dragArmed) UpdateDrag(CursorYDip());
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left || _dragTouch is not null) return;
        EndDrag();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    // Touch path (explicit — see note above)
    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        // Window-level event: only the BAR is the drag handle. A touch on the chat/settings card
        // (e.g. to scroll messages or tap a control) must NOT start a window drag — let it through.
        var src = e.OriginalSource as DependencyObject;
        if (!IsWithinBar(src) || IsInteractive(src)) return; // buttons/inputs/panels keep the touch
        _dragTouch = e.TouchDevice;
        BeginDrag(TouchScreenY(e));
        try { CaptureTouch(e.TouchDevice); } catch { }
        e.Handled = true; // suppress mouse promotion so the gesture isn't double-handled
    }

    /// <summary>True if <paramref name="d"/> is the bar Border or a descendant of it (the bar is the
    /// only drag handle; the chat/settings cards in col 0 must keep their own touches).</summary>
    private bool IsWithinBar(DependencyObject? d)
    {
        while (d is not null)
        {
            if (ReferenceEquals(d, BarRoot)) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null;
        }
        return false;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        base.OnTouchMove(e);
        if (_dragTouch is null || e.TouchDevice != _dragTouch) return;
        UpdateDrag(TouchScreenY(e));
        e.Handled = true;
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        if (_dragTouch is null || e.TouchDevice != _dragTouch) return;
        EndDrag();
        try { ReleaseTouchCapture(e.TouchDevice); } catch { }
        _dragTouch = null;
        e.Handled = true;
    }

    // Shared vertical-drag core (Y-only; the bar stays pinned to the right edge)
    private void BeginDrag(double startScreenY)
    {
        _dragStartTop = Top;
        _dragStartCursorY = startScreenY;
        _dragArmed = true;   // armed, but not actually dragging until past the threshold
        _dragging = false;
    }

    private void UpdateDrag(double currentScreenY)
    {
        if (!_dragArmed) return;
        double dy = currentScreenY - _dragStartCursorY;
        if (!_dragging)
        {
            if (Math.Abs(dy) < DragThresholdDip) return; // ignore a plain tap/click / micro-jitter
            _dragging = true;
        }
        var work = SystemParameters.WorkArea;
        Top = Math.Max(work.Top, Math.Min(work.Bottom - ActualHeight, _dragStartTop + dy));
    }

    private void EndDrag()
    {
        bool wasDragging = _dragging;
        _dragArmed = false;
        _dragging = false;
        if (wasDragging) PersistCentre();
    }

    private double CursorYDip()
        => GetCursorPos(out POINT p) ? p.Y / VisualTreeHelper.GetDpi(this).DpiScaleY : Top;

    /// <summary>The finger's ABSOLUTE screen Y in DIP — stable as the window moves during the drag:
    /// the window-relative point through PointToScreen recovers the absolute physical point
    /// regardless of the current Top, then we convert physical→DIP.</summary>
    private double TouchScreenY(TouchEventArgs e)
    {
        var winPt = e.GetTouchPoint(this).Position;   // window-relative DIP
        var screenPx = PointToScreen(winPt);          // absolute physical px (accounts for current Top)
        return screenPx.Y / VisualTreeHelper.GetDpi(this).DpiScaleY;
    }

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
