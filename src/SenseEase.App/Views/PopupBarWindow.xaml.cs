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

    // Global left-button state, polled by the proximity timer for LIGHT DISMISS (a click anywhere
    // OUTSIDE the popup collapses it — the standard flyout behaviour). Needed because the bar shows
    // non-activating, so Window.Deactivated alone misses a click on the bare desktop.
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LBUTTON = 0x01;

    // ── #5 proximity auto-close + light dismiss ─────────────────────────────────
    // The bar would otherwise stay open forever (it shows non-activating, so Deactivated may
    // never fire). The same 50 ms poll does two things: (1) LIGHT DISMISS — a fresh left-click
    // anywhere outside the popup collapses it (the bare-desktop click the user expects to dismiss
    // it, which Deactivated misses because the window was never activated); (2) PROXIMITY — collapse
    // back to the edge handle once the cursor leaves. Both are skipped while pinned; proximity also
    // pauses while a panel (chat/settings) is open, but an explicit outside click still dismisses it.
    private readonly System.Windows.Threading.DispatcherTimer _autoClose;
    private int _farTicks;
    private bool _prevLeftDown; // edge-detect a fresh left press; primed true on show (OnVisibleChanged)
    private long _graceUntilTick; // Environment.TickCount64 deadline; no auto-close before it
    /// <summary>Cursor must be more than this (dip) outside the bar rect to start closing.</summary>
    public double CloseMarginDip { get; set; } = 96.0;
    /// <summary>Consecutive far ticks (×50 ms) before auto-collapse — a short grace.</summary>
    public int CloseGraceTicks { get; set; } = 8;

    /// <summary>Keep the bar open for <paramref name="ms"/> ms regardless of cursor distance —
    /// used when revealed from AFAR (tray / hotkey) so the user has time to move to it.</summary>
    public void GraceAutoClose(int ms) => _graceUntilTick = Environment.TickCount64 + ms;

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

        // Z-order (2026-06-23): register to be re-lifted above the comfort overlay in the SAME tick the
        // overlay re-asserts itself topmost (OverlayWindow.ApplyState → OverlayZOrder.ReassertAboveOverlay).
        // Synchronous with the overlay's re-assert ⇒ no racing timer ⇒ no flicker; tooltips re-lifted too.
        SourceInitialized += (_, _) => OverlayZOrder.Keep(this);
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
            // #2: unfurl LEFT out of the bar. Activate ONLY when chat opens so the input takes keyboard
            // focus (the bar is ShowActivated=False to avoid stealing focus). #4 clean swap: OpenPanelCleanly
            // waits for any open settings panel to finish closing before chat unfurls (never both at once).
            if (vm.IsChatOpen) { Activate(); OpenPanelCleanly(ChatPanel, ChatScale, SettingsFlyout, () => vm.IsChatOpen); }
            else AnimateCard(ChatPanel, ChatScale, false, () => vm.IsChatOpen, PanelUnfurlScaleX, PanelUnfurlScaleY);
        }
        else if (e.PropertyName == nameof(PopupBarViewModel.IsSettingsOpen))
        {
            if (vm.IsSettingsOpen) OpenPanelCleanly(SettingsFlyout, SettingsScale, ChatPanel, () => vm.IsSettingsOpen); // #3 + #4
            else AnimateCard(SettingsFlyout, SettingsScale, false, () => vm.IsSettingsOpen, PanelUnfurlScaleX, PanelUnfurlScaleY);
        }
        else if (e.PropertyName == nameof(PopupBarViewModel.IsExpanded))
        {
            // R3-6 (#1): the bar STRETCHES open from the centre — the sections' height unfolds within a
            // pinned window, with no opacity/cache/Effect render targets, so it is smooth AND flash-free
            // (see AnimateExpandSections).
            AnimateExpandSections(vm.IsExpanded);
        }
    }

    /// <summary>#4 clean swap: open <paramref name="card"/>, but if the OTHER panel is still on screen (it
    /// was just told to close) wait for its close to finish first — so the two panels never animate at once
    /// (the old simultaneous open+close "conflict"). One panel at a time, sequenced, no flash.</summary>
    private void OpenPanelCleanly(FrameworkElement card, ScaleTransform scale, FrameworkElement other, Func<bool> stillOpen)
    {
        void DoOpen() { if (stillOpen()) AnimateCard(card, scale, true, stillOpen, PanelUnfurlScaleX, PanelUnfurlScaleY); }
        if (other.Visibility == Visibility.Visible)
        {
            // The other panel's close animation runs CardCloseDur; open this one only once it has finished.
            var t = new System.Windows.Threading.DispatcherTimer { Interval = CardCloseDur.TimeSpan };
            t.Tick += (s, _) => { ((System.Windows.Threading.DispatcherTimer)s!).Stop(); DoOpen(); };
            t.Start();
        }
        else DoOpen();
    }

    // R3-7: cap the expand/collapse Height animation to an EVEN frame rate (divisor of 60/120Hz) so the
    // software-rendered layout animation keeps a consistent cadence instead of dropping frames (the "frame
    // lag"). Tunable: raise toward 60 if the machine renders it smoothly, lower if it still judders.
    private const int ExpandFps = 30;
    // R3-7b: the content fade+slide that MASKS the stepped Height (see AnimateExpandSections). This dip is
    // how far the unfolding content rises into place — small so it reads as a settle, not a jump.
    private const double SectionSlide = 8.0;

    /// <summary>R3-7 (centred stretch, flash-free): the bar STRETCHES open from its centre by animating the
    /// FULL-only sections' HEIGHT inside their ClipToBounds (controls "unfold" — a blind wiping open). The
    /// WINDOW is a FIXED size and never resizes (resizing a layered window = the flash); RootGrid is
    /// right+centre-aligned inside it, so growing the bar's content stretches the glass up AND down from the
    /// centre WITHIN the fixed window — no window resize, no re-anchor, no pin needed. No opacity/cache/Effect
    /// render targets (those flip ClearType / re-raster). ReducedMotion snaps; the Completed re-check makes
    /// latest-call-win on a rapid toggle.</summary>
    private void AnimateExpandSections(bool expand)
    {
        // (El, slide transform, fade?) — the FULL-only sections. DragHandle's opacity is Style-owned
        // (0.3 rest / 0.6 hover), so it SLIDES but does not opacity-fade.
        var items = new (FrameworkElement? El, TranslateTransform Shift, bool Fade)[]
        {
            (MeterSection, MeterShift, true),
            (ToolsSection, ToolsShift, true),
            (DragHandle,   DragShift,  false),
        };

        if (AccessibilityHelper.ReducedMotion())
        {
            foreach (var (el, shift, fade) in items)
            {
                if (el is null) continue;
                el.BeginAnimation(HeightProperty, null); el.Height = double.NaN;
                shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = 0;
                if (fade) { el.BeginAnimation(OpacityProperty, null); el.Opacity = 1; }
                el.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            }
            return;
        }

        // R3-7b smoothness: the bar STRETCHES by animating section Height = per-frame LAYOUT, which the
        // AllowsTransparency SOFTWARE renderer can't push at 60fps → the residual "frame lag". A BlurEffect
        // to soften it is OFF-LIMITS (an Effect re-allocates the surface = the flash we removed). Instead we
        // MASK the stepped height: the GLASS height grows on a CAPPED, even cadence (ExpandFps — cheap, and
        // low-detail so its steps are tolerable), while the high-detail CONTENT fades + slides into place on a
        // SMOOTH, uncapped, fully-composited animation (no layout, no render target). The eye tracks the fluid
        // fade/slide and stops reading the height steps. Gentle CubicEase spreads deltas; ReducedMotion snaps.
        var ease = new CubicEase { EasingMode = expand ? EasingMode.EaseOut : EasingMode.EaseIn };
        var dur  = new Duration(TimeSpan.FromMilliseconds(expand ? 260 : 170));

        if (expand)
        {
            // Measure natural heights (Measure only — no Arrange/paint → no flash), then animate 0→natural.
            foreach (var (el, _, _) in items)
            {
                if (el is null) continue;
                el.Visibility = Visibility.Visible;
                el.Height = double.NaN;
                el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            bool hooked = false;
            foreach (var (el, shift, fade) in items)
            {
                if (el is null) continue;
                double target = el.DesiredSize.Height;
                el.Height = 0;

                // Glass stretch — Height (layout), capped to an even cadence.
                var h = new DoubleAnimation(0, target, dur) { EasingFunction = ease };
                Timeline.SetDesiredFrameRate(h, ExpandFps);
                if (!hooked)
                {
                    hooked = true;
                    h.Completed += (_, _) =>
                    {
                        if (DataContext is PopupBarViewModel v && !v.IsExpanded) return; // collapsed mid-grow
                        foreach (var (e2, _, _) in items)
                            if (e2 is not null) { e2.BeginAnimation(HeightProperty, null); e2.Height = double.NaN; }
                    };
                }
                el.BeginAnimation(HeightProperty, h);

                // Content mask — fade + slide, SMOOTH (uncapped, composited): rises from +SectionSlide → 0.
                shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = SectionSlide;
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(SectionSlide, 0, dur) { EasingFunction = ease });
                if (fade)
                {
                    el.BeginAnimation(OpacityProperty, null); el.Opacity = 0;
                    el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
                }
            }
        }
        else
        {
            bool hooked = false;
            foreach (var (el, shift, fade) in items)
            {
                if (el is null) continue;
                double from = el.ActualHeight;
                var h = new DoubleAnimation(from, 0, dur) { EasingFunction = ease };
                Timeline.SetDesiredFrameRate(h, ExpandFps);
                if (!hooked)
                {
                    hooked = true;
                    h.Completed += (_, _) =>
                    {
                        if (DataContext is PopupBarViewModel v && v.IsExpanded) return; // re-expanded mid-sink
                        foreach (var (e2, sh2, fd2) in items)
                            if (e2 is not null)
                            {
                                e2.BeginAnimation(HeightProperty, null); e2.Height = double.NaN; e2.Visibility = Visibility.Collapsed;
                                sh2.BeginAnimation(TranslateTransform.YProperty, null); sh2.Y = 0;
                                if (fd2) { e2.BeginAnimation(OpacityProperty, null); e2.Opacity = 1; }
                            }
                    };
                }
                el.BeginAnimation(HeightProperty, h);

                // Content fades + slides back down as it goes.
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, SectionSlide, dur) { EasingFunction = ease });
                if (fade) el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, dur) { EasingFunction = ease });
            }
        }
    }

    /// <summary>A11Y (audit A2/H1): take keyboard focus on a DELIBERATE reveal (hotkey / tray) so a
    /// keyboard user can Tab the bar's controls. The PASSIVE proximity/edge reveals stay no-activate
    /// (ShowActivated=False) so they never steal focus from the user's app/game. Focus is deferred to
    /// DispatcherPriority.Loaded — after AnimateReveal flips BarRoot from Hidden to Visible — so it lands
    /// on a realized, focusable button. No animation geometry is touched (Activate is a foreground change).</summary>
    public void ActivateForKeyboard()
    {
        try { Activate(); } catch { }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { PlayPauseButton?.Focus(); } catch { }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void AnimateReveal()
    {
        // R3-7: the window is a fixed size that never resizes, so the old SizeToContent width-latch
        // workaround (EnsureContentShrinkWrap) is gone. RootGrid (right+centre-aligned) already hugs the
        // bar flush to the edge; just fade it in.
        AnimateCard(BarRoot, BarScale, open: true, stillOpen: null);
    }

    // Unified "Windows flyout" motion: fade + a subtle scale-up from the trigger edge. ONE easing,
    // ONE pair of durations — the bar reveal AND the chat / quick-settings cards all pop out the same
    // way (the user asked for a single consistent, calm, Windows-card feel). QuinticEase.EaseOut gives
    // a swift, confident start that then decelerates smoothly into rest — CubicEase's flatter start
    // read as a hesitant, "clumsy and deliberate" fade. ReducedMotion snaps.
    private static readonly Duration CardOpenDur  = new(TimeSpan.FromMilliseconds(230));
    private static readonly Duration CardCloseDur = new(TimeSpan.FromMilliseconds(145));
    private const double CardScaleFrom = 0.975; // bar reveal: subtle uniform pop (minimal motion)
    // Chat/settings panels UNFURL leftward out of the bar (their transform origin is the right edge):
    // a gentle ScaleX sweep + a near-still ScaleY so the panel "opens to the left" calmly. Kept subtle
    // (0.90, not 0.80) — a deep ScaleX squashes the text and reads as a whirl under software compositing.
    private const double PanelUnfurlScaleX = 0.90;
    private const double PanelUnfurlScaleY = 0.97;

    // R3-6: BitmapCache REMOVED. Toggling CacheMode on→null on a layered (AllowsTransparency) window
    // re-rasterizes the element each time = a visible flicker on every open/close (web-confirmed). With
    // the DropShadowEffects also gone (XAML), the cards are plain vector subtrees that composite fine in
    // software mode from opacity+transform alone — no cache, no flash, and text stays ClearType-crisp.

    private void AnimateCard(FrameworkElement card, ScaleTransform scale, bool open, Func<bool>? stillOpen,
                             double fromX = CardScaleFrom, double fromY = CardScaleFrom)
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
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            var fadeIn = new DoubleAnimation(0, 1, CardOpenDur) { EasingFunction = ease };
            card.BeginAnimation(OpacityProperty, fadeIn);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromX, 1, CardOpenDur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromY, 1, CardOpenDur) { EasingFunction = ease });
        }
        else
        {
            var ease = new QuinticEase { EasingMode = EasingMode.EaseIn }; // exit a touch quicker
            var fade = new DoubleAnimation(0, CardCloseDur) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                if (stillOpen != null && !stillOpen()) card.Visibility = Visibility.Collapsed;
            };
            card.BeginAnimation(OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromX, CardCloseDur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromY, CardCloseDur) { EasingFunction = ease });
        }
    }

    private void OnVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _pendingHide = false; // a reveal cancels any in-flight deferred condensed-hide
            // Prime _prevLeftDown TRUE: the reveal itself was a click (handle / tray), and the button
            // may still be down on the first tick — don't let that held press count as a fresh
            // outside-click that instantly re-collapses the bar. Only a NEW press dismisses.
            _farTicks = 0; _prevLeftDown = true; _autoClose.Start();
            // #2 (tremble) + R3-5 (flash): keep the bar HIDDEN (not just opacity 0 — Hidden never
            // paints, so it can't flash at full state for one frame before the fade), then DEFER the
            // reveal one layout pass so SizeToContent settles the window position first. AnimateReveal →
            // AnimateCard flips it back Visible and fades it in.
            BarRoot.BeginAnimation(OpacityProperty, null);
            BarRoot.Opacity = 0;
            BarRoot.Visibility = Visibility.Hidden;
            Dispatcher.BeginInvoke(new Action(AnimateReveal), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else { _autoClose.Stop(); ResetExpandStateForHide(); }
    }

    /// <summary>On hide, force the expand/pin state back to a clean condensed baseline so the NEXT reveal
    /// opens correctly even when the bar was hidden mid expand/collapse (an instant dismiss interrupts the
    /// collapse animation, which would otherwise leave the sections half-grown and the window pinned).</summary>
    private void ResetExpandStateForHide()
    {
        try
        {
            _fadingOut = false;
            // Drag cleanup (audit 2026-06-20): if the bar is hidden mid-drag (a programmatic dismiss, or an
            // auto-close that slipped through before the guard), release any held mouse/touch capture and
            // clear the drag flags so the NEXT reveal doesn't open "armed" with stale capture. Mirrors
            // EndDrag + the up-handlers; never throws (a release on an already-released device is benign).
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (_dragTouch is not null) { try { ReleaseTouchCapture(_dragTouch); } catch { } _dragTouch = null; }
            _dragArmed = false;
            _dragging = false;
            // P4: reset the idle-fade target (RootGrid, not the Window) so the next reveal is fully opaque.
            RootGrid.BeginAnimation(OpacityProperty, null); RootGrid.Opacity = 1;
            // Clear reveal/expand transforms so the next reveal opens condensed + clean.
            BarRoot.BeginAnimation(OpacityProperty, null); // value re-set to 0 by OnVisibleChanged on next show
            BarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); BarScale.ScaleX = 1;
            BarScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); BarScale.ScaleY = 1;
            foreach (var el in new FrameworkElement?[] { MeterSection, ToolsSection, DragHandle })
            {
                if (el is null) continue;
                el.BeginAnimation(HeightProperty, null); el.Height = double.NaN;
                el.Visibility = Visibility.Collapsed;
            }
            // R3-7: the window is a fixed size now — do NOT touch SizeToContent (it must stay Manual).
        }
        catch { /* a cleanup pass must never crash the hide */ }
    }

    private void OnAutoCloseTick(object? sender, EventArgs e)
    {
        if (!IsVisible) { _autoClose.Stop(); return; }
        if (DataContext is not PopupBarViewModel vm) return;

        // Drag guard (audit 2026-06-20): never auto-close mid-drag — a light-dismiss or idle-fade firing
        // while the user is dragging the bar by its handle would yank it out from under the gesture and
        // strand the drag state. Reset the idle grace so a drag excursion can't accumulate toward a close;
        // the drag's up-handler resumes normal proximity behaviour.
        if (_dragArmed || _dragging) { _farTicks = 0; return; }

        if (!GetCursorPos(out POINT p)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = p.X / dpi.DpiScaleX, cy = p.Y / dpi.DpiScaleY;
        // R3-7: the window is a fixed 360x496 that is mostly transparent; the VISIBLE content (RootGrid)
        // hugs the right edge, vertically centred. Measure proximity to the CONTENT rect, not the window
        // (else the bar would "stay near" across the whole 360px-wide transparent window).
        double contentRight  = Left + ActualWidth;                       // window right = grid right (right-aligned)
        double contentLeft   = contentRight - RootGrid.ActualWidth;      // grid hugs its content
        double contentTop    = Top + ((ActualHeight - RootGrid.ActualHeight) / 2.0); // grid vertically centred
        double contentBottom = contentTop + RootGrid.ActualHeight;
        double dx = Math.Max(0, Math.Max(contentLeft - cx, cx - contentRight));
        double dy = Math.Max(0, Math.Max(contentTop - cy, cy - contentBottom));
        double dist = Math.Sqrt((dx * dx) + (dy * dy));

        // (1) LIGHT DISMISS — a FRESH left-click strictly outside the content rect (the bar + any open
        // card) collapses it, even when the window was never activated (the bare-desktop click the
        // user expects to dismiss it, which Window.Deactivated misses). Honours the explicit dismiss
        // immediately: bypasses the reveal grace AND the panel-open hold below.
        // dist > 0 ⇔ the cursor is outside the content rect.
        bool leftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        bool freshClick = leftDown && !_prevLeftDown;
        _prevLeftDown = leftDown;
        if (freshClick && dist > 0) { _farTicks = 0; CollapseToHandle(fade: false); return; } // deliberate → instant

        // (2) IDLE FADE in flight: a fresh outside click (handled above) closes instantly; the cursor
        // RETURNING to the bar aborts the fade and keeps it open. Otherwise let the fade run to completion.
        if (_fadingOut)
        {
            if (dist <= CloseMarginDip) { CancelIdleFade(); _farTicks = 0; }
            return;
        }

        // (3) PROXIMITY — never auto-close mid-interaction (a panel open). Reset the grace so a
        // brief excursion while interacting doesn't collapse the moment the panel closes.
        if (vm.IsChatOpen || vm.IsSettingsOpen) { _farTicks = 0; return; }

        // Reveal-from-afar grace (tray / hotkey): stay open a fixed window so the user can reach it.
        if (Environment.TickCount64 < _graceUntilTick) { _farTicks = 0; return; }

        if (dist <= CloseMarginDip) { _farTicks = 0; return; }
        if (++_farTicks >= CloseGraceTicks) { _farTicks = 0; CollapseToHandle(fade: true); } // idle → gentle fade
    }

    // #5 idle fade-out: a calm recession when the user just drifts away (vs the snappy instant close on a
    // deliberate click elsewhere). Slightly slower than the action exits — it should feel like the surface
    // gently stepping back, not a dismiss.
    private static readonly Duration IdleFadeDur = new(TimeSpan.FromMilliseconds(320));
    private bool _fadingOut;
    private bool _pendingHide; // a condensed-baseline render is in flight before Hide() (R3-7 reopen-flash fix)

    /// <summary>#5: collapse the popup back to the discreet edge handle. A DELIBERATE dismiss (a fresh
    /// click on another window / the bare desktop, or Deactivated) closes INSTANTLY — the user made a
    /// choice, so it should feel snappy. An IDLE dismiss (the cursor drifted away and the grace elapsed)
    /// gently FADES the whole popup out first (a calm recession befitting a comfort tool), then hides.
    /// ShellController re-shows the edge handle on the resulting IsVisibleChanged. ReducedMotion snaps.</summary>
    private void CollapseToHandle(bool fade = false)
    {
        if (fade && !AccessibilityHelper.ReducedMotion())
        {
            if (_fadingOut) return; // a fade is already running
            _fadingOut = true;
            var ease = new QuinticEase { EasingMode = EasingMode.EaseIn };
            var a = new DoubleAnimation(1, 0, IdleFadeDur) { EasingFunction = ease };
            a.Completed += (_, _) => { if (_fadingOut) ResetAndHide(); };
            RootGrid.BeginAnimation(OpacityProperty, a); // P4: fade the CONTENT, not Window.Opacity (which
            return;                                       // recomposites the whole layered surface = flash)
        }
        ResetAndHide(); // instant (also used to finish / override an in-flight fade)
    }

    /// <summary>Public dismiss entry for the host (ShellController hotkey / handle toggle). Routes through
    /// the same reset + condensed-baseline hide as a click-away dismiss, so it has no reopen flash and no
    /// VM-reset gap (a raw Hide() would leave IsExpanded/IsChatOpen set and a stale expanded surface).</summary>
    public void DismissToHandle() => ResetAndHide();

    /// <summary>Reset the open panels + expand state, then hide so the next reveal opens clean.</summary>
    private void ResetAndHide()
    {
        _fadingOut = false;
        RootGrid.BeginAnimation(OpacityProperty, null);
        RootGrid.Opacity = 1;
        if (DataContext is PopupBarViewModel vm)
        { vm.IsChatOpen = false; vm.IsSettingsOpen = false; vm.IsExpanded = false; }
        HideToCondensed();
    }

    /// <summary>R3-7 reopen-flash fix: collapse the bar to its condensed baseline INSTANTLY, let the layered
    /// window render that condensed frame into its surface, and ONLY THEN hide. A layered (AllowsTransparency)
    /// window keeps its last-rendered bitmap across Hide→Show; the collapse-on-hide reset (ResetExpandStateForHide)
    /// runs while the window is already hidden, so it never renders — leaving the EXPANDED bitmap in the surface,
    /// which the next reveal flashes before WPF re-renders. Deferring Hide by two render frames lands the
    /// condensed frame in the surface first. The deferred hide is cancelled if a reveal arrives meanwhile.</summary>
    private void HideToCondensed()
    {
        ForceCondensedBaseline();          // bar is now condensed; let it render before we hide
        if (AccessibilityHelper.ReducedMotion()) { Hide(); return; } // no motion → no surface-staleness concern worth the defer
        _pendingHide = true;
        int frames = 0;
        EventHandler? onRender = null;
        onRender = (_, _) =>
        {
            if (!_pendingHide) { System.Windows.Media.CompositionTarget.Rendering -= onRender; return; } // a reveal cancelled it
            if (++frames < 2) return;       // wait until the condensed frame has actually composed into the surface
            System.Windows.Media.CompositionTarget.Rendering -= onRender;
            _pendingHide = false;
            Hide();
        };
        System.Windows.Media.CompositionTarget.Rendering += onRender;
    }

    /// <summary>Collapse the expand sections + any open panel to the condensed baseline INSTANTLY (cancel
    /// animations) AND blank BarRoot, so the pre-hide render leaves the layered surface BLANK (not the
    /// expanded bar — the reopen flash — and not the condensed bar either, which the reveal would show as a
    /// stale frame BEFORE the fade-in = a double flash on appearance). Blank → reveal shows nothing →
    /// AnimateReveal fades the condensed bar in cleanly, a single appearance.</summary>
    private void ForceCondensedBaseline()
    {
        foreach (var el in new FrameworkElement?[] { MeterSection, ToolsSection, DragHandle })
        {
            if (el is null) continue;
            el.BeginAnimation(HeightProperty, null); el.Height = double.NaN; el.Visibility = Visibility.Collapsed;
        }
        foreach (var (card, scale) in new (FrameworkElement card, ScaleTransform scale)[] { (ChatPanel, ChatScale), (SettingsFlyout, SettingsScale) })
        {
            card.BeginAnimation(OpacityProperty, null); card.Opacity = 0; card.Visibility = Visibility.Collapsed;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.ScaleX = 1;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); scale.ScaleY = 1;
        }
        BarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); BarScale.ScaleX = 1;
        BarScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); BarScale.ScaleY = 1;
        // Blank BarRoot so the pre-hide frame carries NO visible bar — the reveal then has a clean blank
        // surface to fade into (OnVisibleChanged re-asserts Opacity=0 + Hidden; AnimateReveal fades it in).
        BarRoot.BeginAnimation(OpacityProperty, null); BarRoot.Opacity = 0;
    }

    /// <summary>Abort an in-flight idle fade (the cursor came back to the bar) and restore it to rest.</summary>
    private void CancelIdleFade()
    {
        if (!_fadingOut) return;
        _fadingOut = false;
        RootGrid.BeginAnimation(OpacityProperty, null);
        RootGrid.Opacity = 1;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Click-away close (fires only when the window was activated, e.g. chat open). The
        // non-activated case is covered by the light-dismiss poll in OnAutoCloseTick. (Pin removed —
        // item 7 — so the bar is always dismissible.)
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
        // Wired to PreviewKeyDown (NOT KeyDown): with AcceptsReturn=True the TextBox's own class handler
        // eats PLAIN Enter (inserts a newline + marks it Handled) during the bubbling KeyDown, so a
        // bubbling handler never saw plain Enter — only Ctrl+Enter (which AcceptsReturn ignores) reached
        // it, which is why send "needed" Ctrl+Enter. The tunnelling Preview runs FIRST, so we intercept
        // plain Enter here and send. Shift+Enter is left un-handled so the TextBox inserts a newline.
        // IME-safe: a composing Enter arrives as Key.ImeProcessed (not Key.Enter), so it never sends.
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;
        // Plain Enter must NEVER insert a newline (AcceptsReturn would, since Preview tunnels first), so
        // mark it Handled UNCONDITIONALLY — then send only if the composer can (non-empty, not generating,
        // not paused). On an empty / mid-stream composer plain Enter is a clean no-op, not a stray blank line.
        e.Handled = true;
        if (DataContext is PopupBarViewModel { Chat: { } chat } && chat.SendCommand.CanExecute(null))
            chat.SendCommand.Execute(null);
    }

    /// <summary>Auto-scroll the chat to the newest content as it arrives (a reply streaming in, or a new
    /// bubble) — but ONLY when the user is already near the bottom, so scrolling up to read history is
    /// never yanked away. Fires on content growth, not on the user's own scrolling. View-only.</summary>
    private void OnChatScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange <= 0) return;                         // react to content growth only
        if (sender is not System.Windows.Controls.ScrollViewer sv) return;
        double bottomBefore = e.ExtentHeight - e.ExtentHeightChange;   // where "bottom" was before the add
        if (e.VerticalOffset + e.ViewportHeight >= bottomBefore - 24)  // were we within ~24px of it?
            sv.ScrollToBottom();
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
        // R3-7: the window is a FIXED 360x496 but mostly transparent — exclude only the VISIBLE content
        // (RootGrid, right-aligned + vertically centred), not the whole window, so the WGC capture/OFI
        // doesn't ignore the empty desktop region around the bar.
        double cw = RootGrid.ActualWidth, ch = RootGrid.ActualHeight;
        if (cw <= 0 || ch <= 0) { cw = ActualWidth; ch = ActualHeight; } // pre-layout fallback
        double cLeftDip = Left + ActualWidth - cw;                  // grid right-aligned to the window right
        double cTopDip  = Top + ((ActualHeight - ch) / 2.0);        // grid vertically centred
        int left   = (int)Math.Floor(cLeftDip * dpi.DpiScaleX);
        int top    = (int)Math.Floor(cTopDip * dpi.DpiScaleY);
        int width  = (int)Math.Ceiling(cw * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ch * dpi.DpiScaleY);
        return (left, top, width, height);
    }
}
