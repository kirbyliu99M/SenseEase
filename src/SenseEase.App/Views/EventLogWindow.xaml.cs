using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SenseEase.App.Interop;
using SenseEase.App.Theme;
using SenseEase.App.ViewModels;

namespace SenseEase.App.Views;

/// <summary>
/// Dedicated Event Log window (UI/UX fine-tune 2026-06-07). The read-only
/// event/diagnostic log was a top-level tab; it now lives in its own window
/// opened from a button in Settings, so it keeps its full-page list without
/// crowding either the primary nav or the Settings scroll.
///
/// <para>DataContext is the shared <see cref="MainViewModel"/> so the
/// <c>{Binding EventLog}</c> markup is identical to the former tab. Theming
/// tracks light/dark via <see cref="ThemeManager.TrackElement"/> (the window
/// merges the light theme view-locally; tracking merges the dark override on
/// top, incl. a live OS flip in Auto mode).</para>
/// </summary>
public partial class EventLogWindow : Window
{
    public EventLogWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => ThemeManager.TrackElement(this);
        Closed += (_, _) => ThemeManager.UntrackElement(this);

        // UI-reshape Task 9 (2026-06-17): frosted Win11 acrylic behind the rounded Surface card,
        // applied once the HWND exists (mirrors NotificationCardWindow). Theme-aware tint — a dark
        // slate scrim in Dark mode, a soft near-white scrim in Light — so the frameless glass holds
        // together in either theme. ABGR order (0xAABBGGRR).
        SourceInitialized += (_, _) => ApplyThemeAwareAcrylic();
    }

    /// <summary>UI-reshape Task 9: pick an acrylic scrim that suits the current theme so the
    /// frameless glass reads correctly in Light and Dark (mirrors
    /// <c>NotificationCardWindow.ApplyThemeAwareAcrylic</c>). The Surface token paints the actual
    /// card fill on top of this frost; the scrim only governs what bleeds through the blur.</summary>
    private void ApplyThemeAwareAcrylic()
    {
        uint tint = ThemeManager.ShouldUseDark()
            ? 0xD9241E1Bu   // dark slate scrim
            : 0xD9FFFFFFu;  // soft near-white scrim
        AcrylicGlass.EnableAcrylic(this, tint);
    }

    /// <summary>UI-reshape Task 9: clip the window root to a rounded rect on every resize so the
    /// acrylic never shows a square translucent peek behind the rounded card corners — the glass
    /// reads as one clean rounded slab (same idea as <c>PopupBarWindow.OnRootSizeChanged</c>).</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        => RootGrid.Clip = new RectangleGeometry(
               new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16, 16);

    /// <summary>UI-reshape Task 9: the slim title row is the drag handle — a left-press on the bare
    /// row (the title text / empty space, not the round close button which handles its own click)
    /// moves the frameless window. DragMove throws if the button was already released (fast click);
    /// that is harmless, so it is swallowed.</summary>
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* DragMove throws outside a left-button-down context — ignore */ }
    }

    // TOUCH: DragMove() needs a held mouse button and is unreliable under touch, so move the
    // frameless dialog explicitly on touch. Absolute screen coords (PointToScreen) stay stable as
    // the window moves; e.Handled suppresses the unreliable mouse promotion of the same gesture.
    private TouchDevice? _moveTouch;
    private Point _moveStartScreenPx;
    private double _moveStartLeft, _moveStartTop;

    private void OnTitleBarTouchDown(object sender, TouchEventArgs e)
    {
        _moveTouch = e.TouchDevice;
        _moveStartScreenPx = PointToScreen(e.GetTouchPoint(this).Position);
        _moveStartLeft = Left; _moveStartTop = Top;
        try { ((UIElement)sender).CaptureTouch(e.TouchDevice); } catch { }
        e.Handled = true;
    }

    private void OnTitleBarTouchMove(object sender, TouchEventArgs e)
    {
        if (_moveTouch is null || e.TouchDevice != _moveTouch) return;
        var nowPx = PointToScreen(e.GetTouchPoint(this).Position);
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _moveStartLeft + (nowPx.X - _moveStartScreenPx.X) / dpi.DpiScaleX;
        Top  = _moveStartTop  + (nowPx.Y - _moveStartScreenPx.Y) / dpi.DpiScaleY;
        e.Handled = true;
    }

    private void OnTitleBarTouchUp(object sender, TouchEventArgs e)
    {
        if (_moveTouch is null || e.TouchDevice != _moveTouch) return;
        try { ((UIElement)sender).ReleaseTouchCapture(e.TouchDevice); } catch { }
        _moveTouch = null;
        e.Handled = true;
    }

    /// <summary>The "事件 / Action" column fills the remaining list width.
    /// GridView columns can't star-size, so set the last column on every resize.
    /// Time column = 90; subtract it plus the vertical scrollbar and a small fudge
    /// so a row never trips the (disabled) horizontal scrollbar. Mirrors the
    /// handler the former Event Log tab carried.</summary>
    private void OnEventLogListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (EventLogActionColumn is null) return;
        double available = EventLogList.ActualWidth - 90
                           - SystemParameters.VerticalScrollBarWidth - 12;
        if (available > 120) EventLogActionColumn.Width = available;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
