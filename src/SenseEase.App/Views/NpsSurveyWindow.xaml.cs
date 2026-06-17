using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using SenseEase.App.Interop;
using SenseEase.App.Services;
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
    // Light glass acrylic tint (ABGR), from Popup.PanelBg #D1D7E4FC.
    private const uint PanelAcrylicTint = 0xD1FCE4D7;

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
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ClickThrough.EnableNoActivate(this);
        AcrylicGlass.EnableAcrylic(this, PanelAcrylicTint);
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
