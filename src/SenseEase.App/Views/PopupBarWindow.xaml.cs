using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SenseEase.App.Interop;
using SenseEase.App.Services;
using SenseEase.App.ViewModels;

namespace SenseEase.App.Views;

/// <summary>
/// UI-RESHAPE prep — the glass popup bar window. Frosted via Win11 acrylic
/// (<see cref="AcrylicGlass"/>, light-blue tint) and never steals focus
/// (<see cref="ClickThrough.EnableNoActivate"/>, so its buttons stay clickable). Draggable
/// by its bar background. Binds a <see cref="PopupBarViewModel"/>; the future
/// <c>ShellController</c> passes a live <see cref="IShellController"/> so the engine owns
/// the tier. Implements <see cref="IShellSurface"/> for the WGC-exclude + topmost contract.
/// </summary>
public partial class PopupBarWindow : Window, IShellSurface
{
    // Light-blue glass acrylic tint (ABGR 0xAABBGGRR), from Popup.BarBg #85D2E0FF.
    private const uint BarAcrylicTint = 0x85FFE0D2;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_FORCE_TOPMOST = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER

    public PopupBarWindow() : this(null) { }

    public PopupBarWindow(IShellController? controller)
    {
        InitializeComponent();
        DataContext = new PopupBarViewModel(controller);
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Buttons must remain clickable, but the bar must never steal foreground focus
        // from the user's app — NoActivate (NOT the transparent click-through variant).
        ClickThrough.EnableNoActivate(this);
        AcrylicGlass.EnableAcrylic(this, BarAcrylicTint);
    }

    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return; // let buttons/inputs win
        try { DragMove(); } catch { /* DragMove throws if not in a left-button-down context */ }
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
