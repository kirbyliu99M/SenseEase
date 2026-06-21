using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SenseEase.App.Theme;

namespace SenseEase.App.Views;

/// <summary>One row of the tray menu. A separator carries no label/action.</summary>
public sealed record TrayMenuItem(string Label, Action? OnClick, bool Enabled = true, bool IsSeparator = false)
{
    public static readonly TrayMenuItem Separator = new(string.Empty, null, IsSeparator: true);
}

/// <summary>
/// Tray-menu 閃退 permanent fix (2026-06-22): the notification-area icon's right-click menu as a REAL
/// top-level <see cref="Window"/>, replacing the native WPF <see cref="System.Windows.Controls.ContextMenu"/>.
///
/// <para><b>Why a Window.</b> A ContextMenu is a Popup whose Win32 menu loop self-dismisses the instant it
/// loses activation. A windowless tray app (MainWindow parked off-screen / hidden) has no foreground window to
/// own that popup, so Windows tore the menu down ~165 ms after it opened (KB135788) — the "flash and vanish"
/// bug. Three prior Win32 work-arounds (NIM_MODIFY gate, then SetForegroundWindow on the off-screen MainWindow)
/// never held, because foreground on a window that doesn't OWN the menu doesn't keep the popup alive.</para>
///
/// <para><b>Why this holds.</b> A normal Window does NOT auto-dismiss on deactivation — only this code-behind
/// closes it (item click / Escape / a Deactivated that survives the open grace). So even if the process is
/// momentarily denied foreground, the menu simply STAYS UP until the user acts; it can no longer flash. On
/// show we still <see cref="Activate"/> + SetForegroundWindow so the normal click-away dismiss works; the
/// <see cref="_graceActive"/> window swallows the spurious activation-bounce Deactivated that caused the flash.</para>
///
/// <para>Owned by <see cref="Services.TrayIconService"/>: constructed once, <see cref="ShowMenu"/>n on each
/// right-click, Hidden on dismiss, Closed on dispose. Skinned to the former themed-menu look via SenseEaseTheme
/// + <see cref="ThemeManager.TrackElement"/> (light/dark, incl. a live OS flip).</para>
/// </summary>
public partial class TrayMenuWindow : Window
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    // Physical-pixel cursor position captured at the right-click (the menu anchors up-left from here).
    private int _cursorXPhysical, _cursorYPhysical;

    // True for a brief window after Show so an activation BOUNCE (the menu gets foreground then instantly
    // loses it on a headless app) can't fire the Deactivated → Hide and reproduce the flash. Genuine
    // click-away after the grace still dismisses.
    private bool _graceActive;
    private bool _wasActivated;           // did the menu ever actually take foreground this show?
    private DispatcherTimer? _graceTimer;

    public TrayMenuWindow()
    {
        InitializeComponent();
        Loaded   += (_, _) => { try { ThemeManager.TrackElement(this); } catch { /* no Application in headless tests */ } };
        Closed   += (_, _) => { try { ThemeManager.UntrackElement(this); } catch { } StopGrace(); };
        Activated   += (_, _) => _wasActivated = true;
        Deactivated += (_, _) => { if (!_graceActive && _wasActivated) Hide(); };
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) StopGrace(); };
    }

    /// <summary>Esc closes the menu (matches every other dismissible surface).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Hide(); return; }
        base.OnKeyDown(e);
    }

    /// <summary>Build + show the menu anchored at the current cursor. Safe to call while already visible
    /// (re-anchors at the new cursor). The window stays hidden (Opacity 0) until it is precisely positioned,
    /// so it never flashes at the wrong spot.</summary>
    public void ShowMenu(IReadOnlyList<TrayMenuItem> items)
    {
        if (GetCursorPos(out var p)) { _cursorXPhysical = p.X; _cursorYPhysical = p.Y; }
        BuildItems(items);

        _wasActivated = false;
        if (!IsVisible) { Opacity = 0; Show(); }

        // Position + activate after the SizeToContent layout pass so ActualWidth/Height are known.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PositionAtCursor();
            Opacity = 1;
            try { Activate(); } catch { }
            ForceForeground();
            ArmGrace();
        }), DispatcherPriority.Loaded);
    }

    private void BuildItems(IReadOnlyList<TrayMenuItem> items)
    {
        ItemsPanel.Children.Clear();
        foreach (var it in items)
        {
            if (it.IsSeparator)
            {
                ItemsPanel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(8, 4, 8, 4),
                    Background = TryBrush("Separator"),
                    SnapsToDevicePixels = true,
                });
                continue;
            }

            var btn = new Button
            {
                Content = it.Label,
                Style = (Style)FindResource("TrayMenuItemButton"),
                IsEnabled = it.Enabled,
            };
            var action = it.OnClick;
            // Hide FIRST so the menu disappears immediately, THEN run the action (which may open a window).
            btn.Click += (_, _) => { Hide(); action?.Invoke(); };
            ItemsPanel.Children.Add(btn);
        }
    }

    /// <summary>Anchor the menu up-left from the cursor (the tray sits bottom-right), clamped to the virtual
    /// screen so it never opens partly off any monitor.</summary>
    private void PositionAtCursor()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = _cursorXPhysical / dpi.DpiScaleX;
        double cy = _cursorYPhysical / dpi.DpiScaleY;

        double w = ActualWidth  > 0 ? ActualWidth  : (double.IsNaN(Width)  ? 208 : Width);
        double h = ActualHeight > 0 ? ActualHeight : (double.IsNaN(Height) ? 0   : Height);

        double left = cx - w;   // grow up-left toward the screen, away from the tray corner
        double top  = cy - h;

        double vl = SystemParameters.VirtualScreenLeft;
        double vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth;
        double vb = vt + SystemParameters.VirtualScreenHeight;

        if (left < vl) left = vl;
        if (top  < vt) top  = vt;
        if (left + w > vr) left = vr - w;
        if (top  + h > vb) top  = vb - h;

        Left = left;
        Top  = top;
    }

    private void ForceForeground()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        }
        catch { /* best-effort; a denied foreground just means the menu stays up until the user acts */ }
    }

    /// <summary>Open ~250 ms in which a spurious Deactivated (activation bounce) is ignored. At the end, if we
    /// genuinely took foreground but already lost it (the user clicked away DURING the grace), dismiss; if we
    /// never took foreground at all, leave the menu up (no flash) — the user dismisses by clicking an item or Esc.</summary>
    private void ArmGrace()
    {
        StopGrace();
        _graceActive = true;
        _graceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _graceTimer.Tick += (_, _) =>
        {
            StopGrace();
            _graceActive = false;
            if (IsVisible && _wasActivated && !IsActive) Hide();
        };
        _graceTimer.Start();
    }

    private void StopGrace()
    {
        try { _graceTimer?.Stop(); } catch { }
        _graceTimer = null;
        _graceActive = false;
    }

    private Brush TryBrush(string key)
    {
        try { if (FindResource(key) is Brush b) return b; } catch { }
        return Brushes.Transparent;
    }
}
