using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SenseEase.App.Services;

/// <summary>
/// Keeps SenseEase's own UI windows (and their tooltips) above the comfort overlay — WITHOUT a racing poll.
///
/// <para>Empirically (owner_diag.log: <c>ownerMatches=True topmost=True</c> yet the popup sat behind), Win32
/// ownership does NOT survive the overlay re-asserting <c>SetWindowPos(HWND_TOPMOST)</c> on itself: the
/// "owned-above-owner" rule fires on the make-topmost TRANSITION, not when an already-topmost owner re-tops,
/// so the overlay climbs over its owned popup and the OS leaves it there. The robust fix is to re-lift our
/// windows in the SAME synchronous tick the overlay re-asserts — <see cref="ReassertAboveOverlay"/> is called
/// from <c>OverlayWindow.ApplyState</c> immediately after the overlay's own re-assert, on the UI thread, so no
/// frame is ever presented with the overlay on top. That synchronous coupling is the crucial difference from
/// the earlier ~60 Hz DispatcherTimer keeper, which ran on a SEPARATE clock, always lost a frame to the race,
/// and flickered. Here there is no race and no thrash.</para>
/// </summary>
public static class OverlayZOrder
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_LIFT = 0x0001 | 0x0002 | 0x0010; // NOSIZE | NOMOVE | NOACTIVATE (perform the Z change)

    private static readonly HashSet<Window> _windows = new();

    /// <summary>Register a UI window to be kept above the overlay (idempotent; auto-removed on Closed). Call
    /// once the window's HWND exists (e.g. from SourceInitialized).</summary>
    public static void Keep(Window w)
    {
        if (w is null || !_windows.Add(w)) return;
        w.Closed += OnClosed;
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is Window w) { _windows.Remove(w); w.Closed -= OnClosed; }
    }

    /// <summary>Re-lift every registered visible window — then any open tooltip — back to the top of the
    /// topmost band. MUST be called synchronously, on the UI thread, immediately after the overlay re-asserts
    /// itself topmost, so the re-assert and these re-lifts land in one tick with no paint between (no flicker).
    /// Order: each window first (now above the overlay), then the open tooltip last (above its window).</summary>
    public static void ReassertAboveOverlay()
    {
        foreach (var w in _windows)
        {
            if (!w.IsVisible) continue;
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_LIFT);
        }
        IntPtr tip = FindOpenToolTipHwnd();
        if (tip != IntPtr.Zero) SetWindowPos(tip, HWND_TOPMOST, 0, 0, 0, 0, SWP_LIFT);
    }

    // A shown WPF ToolTip lives in its own popup-rooted HwndSource (verified: root=PopupRoot). Skip the
    // Window-rooted sources (the big visual trees) so this stays cheap.
    private static IntPtr FindOpenToolTipHwnd()
    {
        foreach (PresentationSource s in PresentationSource.CurrentSources)
            if (s is HwndSource hs && hs.RootVisual is DependencyObject root
                && root is not Window && ContainsOpenToolTip(root))
                return hs.Handle;
        return IntPtr.Zero;
    }

    private static bool ContainsOpenToolTip(DependencyObject node)
    {
        if (node is ToolTip tt && tt.IsOpen) return true;
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
            if (ContainsOpenToolTip(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
}
