using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SenseEase.App.Configuration;

namespace SenseEase.App.Views;

/// <summary>
/// Pop-up sprint Task 2 (render) + Task 4 (hover + click). 4 dip × 80 dip
/// top-centre Window, Topmost + AllowsTransparency + ShowInTaskbar=false.
/// NO flashing/pulsing/animation per spec §1.
/// <para>Three-tier colour mapping lives in <see cref="TierColorPalette"/>;
/// <see cref="BrushForTier"/> here forwards for back-compat.</para>
///
/// <para>Task 4 additions:</para>
/// <list type="bullet">
///   <item><b>Hover proximity:</b> A 30 Hz cursor poller (Win32
///         <c>GetCursorPos</c>) checks rect-distance from the cursor to the
///         bar. Within 200 dip → bar grows to 6 dip tall + +0.15 opacity
///         (smoothly animated). Polling not WPF MouseMove because the bar
///         is only 4 dip tall — MouseMove wouldn't fire until cursor is
///         on the bar itself.</item>
///   <item><b>Click:</b> When "near" (per the proximity poller) the bar
///         becomes hit-test-visible and a LeftMouseUp fires
///         <see cref="Clicked"/>. When "far" it's click-through so users
///         don't accidentally swallow clicks meant for the screen behind.</item>
/// </list>
///
/// <para>Always-on mask (Option II): caller engages on ContentRendered,
/// clears on Closed. See MainWindow.StartPersistentBar.</para>
/// </summary>
public partial class PersistentBarWindow : Window
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

    // Path Z UX iter 4: WPF's Topmost=true doesn't always survive Alt-Tab
    // round-trips against other windows that briefly claim topmost (some
    // overlays, fullscreen modes, splash screens). Win32 SetWindowPos with
    // HWND_TOPMOST is the authoritative path — it forces the bar to the
    // top of the topmost band without activating or resizing it.
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOSIZE         = 0x0001;
    private const uint SWP_NOMOVE         = 0x0002;
    private const uint SWP_NOACTIVATE     = 0x0010;
    private const uint SWP_NOOWNERZORDER  = 0x0200;
    private const uint SWP_FORCE_TOPMOST  = SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;

    // Tunables — public so tests/future settings can override without forking.
    /// <summary>Distance from the bar's rect (in dip) inside which the bar
    /// is "hovered" — grown + brightened + clickable. Spec: 200 dip.</summary>
    public double HoverProximityDip { get; set; } = 200.0;
    /// <summary>Grown bar height when hovering. Spec: 6 dip (vs base 4 dip).</summary>
    public double HoveredHeightDip { get; set; } = 6.0;
    /// <summary>Base height. Spec: 4 dip.</summary>
    public double BaseHeightDip { get; set; } = 4.0;
    /// <summary>Hover opacity delta added to base. Spec: +0.15.</summary>
    public double HoverOpacityBoost { get; set; } = 0.15;

    private readonly DispatcherTimer _proximityTimer;
    private bool _isHovered;
    private double _baseOpacity = 1.0;

    /// <summary>Raised on left-click while hovered. Host opens the
    /// notification card in response. Not fired when the bar is "far" —
    /// in that state the bar is click-through and the click reaches the
    /// window behind it.</summary>
    public event EventHandler? Clicked;

    /// <summary>Tier → bar brush. Proxy to <see cref="TierColorPalette"/>.</summary>
    public static SolidColorBrush BrushForTier(int tier) => TierColorPalette.BrushForTier(tier);

    // Path Z UX iter 3: remember the last tier we were asked to render so
    // the hover-transition handler can re-apply a brush of the right hue
    // without the host having to call SetTier again.
    private int _currentTier = 1;

    private SolidColorBrush PickBrushForCurrentState(int tier)
    {
        int effectiveTier = UserPreferences.Current.BarTrayColorEnabled ? tier : 1;
        return _isHovered
            ? TierColorPalette.HoverBrushForTier(effectiveTier)
            : TierColorPalette.BrushForTier(effectiveTier);
    }

    public PersistentBarWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;

        // 30 Hz proximity poll. Cheap (Win32 GetCursorPos + a couple of
        // float comparisons + an Animation.Begin only on transition); does
        // not pin the dispatcher.
        _proximityTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _proximityTimer.Tick += OnProximityTick;

        // Hit-test starts off (click-through) per Task 2 behaviour; the
        // proximity timer flips it true when hovered.
        IsHitTestVisible = false;

        MouseLeftButtonUp += OnLeftClick;

        // Path Z UX iter 2: re-position the bar when the user's display
        // configuration changes (multi-monitor connect/disconnect, primary-
        // monitor swap, taskbar move). Without this, the bar's Left/Top
        // computed in OnLoaded points at a now-stale work-area and the
        // proximity poller's cursor-distance maths gives the wrong answer.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _proximityTimer?.Stop();
        // Detach the static-event subscription so the bar can be GC'd.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RepositionForWorkArea();
        _proximityTimer.Start();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Dispatcher.Invoke because SystemEvents fires on a worker thread.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RepositionForWorkArea();
            // Multi-monitor swaps can also bury the bar behind a window that
            // briefly took focus; re-assert topmost just in case.
            ReassertTopmost();
        }));
    }

    private void RepositionForWorkArea()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2.0;
        Top  = work.Top;
        Height = BaseHeightDip;
    }

    /// <summary>Path Z UX iter 4: forces the bar to the top of the
    /// topmost-window band via Win32 SetWindowPos(HWND_TOPMOST). The
    /// iter-2 WPF Topmost=false→true dance was insufficient — user
    /// reported the Alt-Tab-then-back-to-MainWindow bug reappearing,
    /// because some apps (and even the WGC consent prompt + Aero
    /// snapshot animation) can land above WPF's idea of "topmost"
    /// without ever notifying the WPF Topmost property. Going through
    /// SetWindowPos directly is the authoritative path. Idempotent —
    /// no-op when bar is already at top.</summary>
    public void ReassertTopmost()
    {
        if (!IsLoaded) return;
        ForceTopmostViaWin32();
    }

    private void ForceTopmostViaWin32()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_FORCE_TOPMOST);
    }

    private void OnLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_isHovered) Clicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the bar's fill brush for the given VIMS tier. Idempotent —
    /// same tier → same frozen brush assignment.
    /// <para>Task 5: Toggle B = OFF forces the grey band regardless of tier
    /// (per spec §1 "Bar 顏色變化" toggle off → "永遠維持灰色透明").</para></summary>
    public void SetTier(int tier)
    {
        _currentTier = tier;
        var brush = PickBrushForCurrentState(tier);
        // Path Z bug-fix 5: while in sleep mode, update the snapshot for
        // ExitSleepMode to restore — but leave the live faint-grey fill in
        // place. Without this guard the engine's StateChanged stream
        // overwrites the paused fill within ~300 ms of EnterSleepMode and
        // ExitSleepMode then restores the stale pre-sleep brush.
        if (_preSleepBackground is not null)
            _preSleepBackground = brush;
        else
            BarFill.Background = brush;
    }

    // overlay-bar-redesign D3: snapshot of the bar's pre-sleep visual state.
    // Non-null while EnterSleepMode is active; both calls are idempotent so
    // back-to-back Enter or Exit is a no-op.
    private Brush? _preSleepBackground;

    /// <summary>overlay-bar-redesign D3: drop the bar into a visually
    /// distinct "sensing paused" state — faint near-transparent fill plus a
    /// dashed grey outline via the <c>SleepOverlay</c> rectangle. Idempotent.
    /// Restored by <see cref="ExitSleepMode"/>.</summary>
    public void EnterSleepMode()
    {
        if (_preSleepBackground is not null) return; // already in sleep mode
        _preSleepBackground = BarFill.Background;
        // Faint near-transparent grey fill so the bar is barely visible —
        // the dashed outline carries the "paused" affordance.
        BarFill.Background = new SolidColorBrush(Color.FromArgb(20, 0xDD, 0xDD, 0xDD));
        SleepOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>overlay-bar-redesign D3: restore the bar's pre-sleep fill
    /// and hide the dashed overlay. Idempotent — calling without a prior
    /// <see cref="EnterSleepMode"/> is a no-op.</summary>
    public void ExitSleepMode()
    {
        if (_preSleepBackground is null) return; // not in sleep mode
        BarFill.Background = _preSleepBackground;
        _preSleepBackground = null;
        SleepOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>Monitor-coords physical-pixel rect for the WGC exclude mask.
    /// DPI-aware via VisualTreeHelper.GetDpi. Returns the CURRENT footprint
    /// (so when hovered the rect reflects the grown size). The MainWindow
    /// wiring snapshots this on ContentRendered — for hover-time mask
    /// resizing see Task 7 polish.</summary>
    public (int Left, int Top, int Width, int Height) GetMonitorRectInPhysicalPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX;
        double sy = dpi.DpiScaleY;
        int left   = (int)Math.Floor(Left * sx);
        int top    = (int)Math.Floor(Top  * sy);
        int width  = (int)Math.Ceiling(ActualWidth  * sx);
        int height = (int)Math.Ceiling(ActualHeight * sy);
        return (left, top, width, height);
    }

    // Path Z UX iter 4: throttle the topmost-enforcement Win32 call to ~5 Hz
    // (every 6th tick at 30 Hz). Calling SetWindowPos every tick is cheap
    // (microseconds) but unnecessary; 5 Hz catches z-order theft within
    // ~200 ms which is well below human perception of "the bar stopped
    // working" — the failure window that user reports.
    private int _topmostTickCounter;

    // v0.8 hotfix follow-up: stickier hover. When cursor leaves the
    // proximity zone the bar previously snapped back to base height +
    // ambient alpha in ~100 ms — user feedback was that clicking another
    // window dismissed the hover-grown state too aggressively. Now we
    // require the cursor to STAY out of proximity for HoverOffGraceMs
    // before transitioning OFF. Brief excursions (cursor blip across the
    // screen, mouse-to-button-and-back) keep the hover state alive.
    private const int HoverOffGraceMs = 800;
    private DateTime? _hoverOffPendingSince;

    private void OnProximityTick(object? sender, EventArgs e)
    {
        // Periodic topmost re-assertion. Even if no focus event fired (rare
        // but possible — DWM Alt-Tab animation, fullscreen window claiming
        // topmost, splash screens), this guarantees the bar comes back to
        // top within ~200 ms of any disruption.
        if (++_topmostTickCounter >= 6)
        {
            _topmostTickCounter = 0;
            ForceTopmostViaWin32();
        }

        if (!GetCursorPos(out POINT physical)) return;

        // Translate physical cursor coords → DIP via this window's DPI.
        // Same window assumption simplifies multi-monitor edge cases for now
        // (bar is always on primary; cursor on a secondary won't be near).
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = physical.X / dpi.DpiScaleX;
        double cy = physical.Y / dpi.DpiScaleY;

        // Rectangle distance: clamp cursor to bar rect, then Euclidean to
        // (cx, cy). 0 means cursor is inside; otherwise the minimum gap.
        double dx = Math.Max(0, Math.Max(Left - cx, cx - (Left + ActualWidth)));
        double dy = Math.Max(0, Math.Max(Top  - cy, cy - (Top  + ActualHeight)));
        double dist = Math.Sqrt((dx * dx) + (dy * dy));

        bool nowHovered = dist <= HoverProximityDip;

        if (nowHovered)
        {
            // Cursor near. Cancel any pending hover-off countdown.
            _hoverOffPendingSince = null;
            if (!_isHovered)
            {
                _isHovered = true;
                IsHitTestVisible = true;
                AnimateHoverTransition(true);
            }
            return;
        }

        // Cursor not near. If we're currently hovered, start (or check)
        // the grace-period countdown rather than transitioning immediately.
        if (_isHovered)
        {
            if (_hoverOffPendingSince is null)
            {
                _hoverOffPendingSince = DateTime.UtcNow;
                return;
            }
            if ((DateTime.UtcNow - _hoverOffPendingSince.Value).TotalMilliseconds < HoverOffGraceMs)
                return;

            // Grace expired — actually transition off.
            _hoverOffPendingSince = null;
            _isHovered = false;
            IsHitTestVisible = false;
            AnimateHoverTransition(false);
        }
    }

    private void AnimateHoverTransition(bool toHovered)
    {
        double targetHeight = toHovered ? HoveredHeightDip : BaseHeightDip;
        double targetOpacity = toHovered ? Math.Min(1.0, _baseOpacity + HoverOpacityBoost) : _baseOpacity;
        var dur = new Duration(TimeSpan.FromMilliseconds(100));
        BeginAnimation(HeightProperty,  new DoubleAnimation(targetHeight,  dur));
        BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, dur));

        // Path Z UX iter 3: swap to the higher-alpha hover brush so the
        // tier colour reads clearly on cursor approach. Skip while in
        // sleep mode — the faint paused fill should stay paused-looking
        // regardless of cursor position.
        if (_preSleepBackground is null)
            BarFill.Background = PickBrushForCurrentState(_currentTier);
    }
}
