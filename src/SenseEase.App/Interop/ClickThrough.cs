using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SenseEase.App.Interop;

/// <summary>
/// Win32 interop helper to make a WPF window click-through, so mouse input passes
/// to the windows beneath the overlay. Applies WS_EX_TRANSPARENT | WS_EX_LAYERED.
/// </summary>
public static class ClickThrough
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// Make the given window click-through. Call after the HWND exists
    /// (e.g. from Window.SourceInitialized).
    /// </summary>
    public static void Enable(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
    }
}
