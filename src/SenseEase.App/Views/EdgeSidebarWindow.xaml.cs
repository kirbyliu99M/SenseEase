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
    /// <summary>Revealed handle width (dip). Resting width is the XAML 4 dip.</summary>
    public double RevealedWidthDip { get; set; } = 12.0;
    public double RestWidthDip { get; set; } = 4.0;
    public double RestOpacity { get; set; } = 0.16;
    public double RevealedOpacity { get; set; } = 0.85;

    private readonly DispatcherTimer _proximityTimer;
    private bool _isHovered;
    private int _topmostTickCounter;

    /// <summary>Raised on left-click while revealed — the host opens the popup bar.</summary>
    public event EventHandler? Clicked;

    public EdgeSidebarWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
        IsHitTestVisible = false; // click-through until revealed

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
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _proximityTimer.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => { RepositionForWorkArea(); ReassertTopmost(); }));

    private void RepositionForWorkArea()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width;                       // flush to the right edge
        Top = work.Top + (work.Height - Height) / 2.0;   // vertically centred
    }

    public void ReassertTopmost()
    {
        if (!IsLoaded) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_FORCE_TOPMOST);
    }

    /// <summary>Tint the handle by the live status (low/mid/high) — readiness hook for
    /// the host's StateChanged fan-out.</summary>
    public void SetTier(int tier)
        => Handle.Background = PopupStatus.BrushForIndex(PopupStatus.IndexFor(tier));

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

        bool nowHovered = dist <= HoverProximityDip;
        if (nowHovered == _isHovered) return;
        _isHovered = nowHovered;
        IsHitTestVisible = nowHovered;
        AnimateReveal(nowHovered);
    }

    private void AnimateReveal(bool reveal)
    {
        double targetWidth = reveal ? RevealedWidthDip : RestWidthDip;
        double targetOpacity = reveal ? RevealedOpacity : RestOpacity;

        if (AccessibilityHelper.ReducedMotion())
        {
            Handle.BeginAnimation(WidthProperty, null);
            Handle.BeginAnimation(OpacityProperty, null);
            Handle.Width = targetWidth;
            Handle.Opacity = targetOpacity;
            return;
        }
        var dur = new Duration(TimeSpan.FromMilliseconds(140));
        Handle.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, dur));
        Handle.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, dur));
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
