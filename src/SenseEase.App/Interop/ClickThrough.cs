using System;
using System.ComponentModel;
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
    private const int WS_EX_NOACTIVATE = 0x08000000;
    // Beta-1 theme-3 (overlay = system UI): WS_EX_TOOLWINDOW removes a top-level window from the
    // Alt-Tab switcher AND the taskbar; WS_EX_APPWINDOW does the opposite (force onto the
    // taskbar) so it must be CLEARED for TOOLWINDOW to take effect.
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW  = 0x00040000;

    // On x64 the window-long APIs traffic in pointer-sized values, so use the
    // *Ptr variants (nint) rather than the 32-bit GetWindowLong/SetWindowLong,
    // which truncate on 64-bit. On 32-bit Windows the *Ptr exports are absent,
    // so fall back to the 32-bit entry points there.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static nint GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    private static nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : SetWindowLong32(hWnd, nIndex, (int)dwNewLong);

    /// <summary>
    /// Make the given window click-through. Call after the HWND exists
    /// (e.g. from Window.SourceInitialized).
    /// </summary>
    public static void Enable(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();

        Marshal.SetLastSystemError(0);
        nint exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (exStyle == 0 && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "GetWindowLongPtr(GWL_EXSTYLE) failed.");
        }

        nint newStyle = exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED;
        Marshal.SetLastSystemError(0);
        nint previous = SetWindowLongPtr(hwnd, GWL_EXSTYLE, newStyle);
        if (previous == 0 && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "SetWindowLongPtr(GWL_EXSTYLE) failed.");
        }
    }

    /// <summary>Beta-1 theme-3: pure exstyle transform for the SYSTEM-UI overlay — click-through
    /// (TRANSPARENT|LAYERED), absent from Alt-Tab + taskbar (set TOOLWINDOW, clear APPWINDOW),
    /// and never foreground-activating (NOACTIVATE). Pure so it is unit-testable without an HWND.</summary>
    internal static nint ComputeSystemUiExStyle(nint current)
        => (current | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
           & ~(nint)WS_EX_APPWINDOW;

    /// <summary>Beta-1 theme-3: make the overlay behave as system UI — click-through AND absent
    /// from the Alt-Tab switcher + taskbar, and never stealing foreground focus on show. Use this
    /// for the FOV overlay instead of <see cref="Enable"/> (which only sets click-through). Honest
    /// ceiling: this cannot outrank true exclusive-fullscreen or hardware-overlay (MPO) video —
    /// that is a display-pipeline fact, not a z-order one. Call after the HWND exists.</summary>
    public static void EnableSystemUiClickThrough(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();

        Marshal.SetLastSystemError(0);
        nint exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (exStyle == 0 && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "GetWindowLongPtr(GWL_EXSTYLE) failed.");
        }

        nint newStyle = ComputeSystemUiExStyle(exStyle);
        Marshal.SetLastSystemError(0);
        nint previous = SetWindowLongPtr(hwnd, GWL_EXSTYLE, newStyle);
        if (previous == 0 && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "SetWindowLongPtr(GWL_EXSTYLE) failed.");
        }
    }

    /// <summary>
    /// Make the given window never steal foreground focus when it appears, while
    /// keeping its own controls clickable. Adds ONLY WS_EX_NOACTIVATE (no
    /// WS_EX_TRANSPARENT, which would make the card's buttons unclickable).
    /// Call after the HWND exists (e.g. from Window.SourceInitialized).
    /// </summary>
    public static void EnableNoActivate(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();

        Marshal.SetLastSystemError(0);
        nint exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (exStyle == 0 && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "GetWindowLongPtr(GWL_EXSTYLE) failed.");
        }

        nint newStyle = exStyle | WS_EX_NOACTIVATE;
        Marshal.SetLastSystemError(0);
        nint previous = SetWindowLongPtr(hwnd, GWL_EXSTYLE, newStyle);
        if (previous == 0 && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "SetWindowLongPtr(GWL_EXSTYLE) failed.");
        }
    }
}
