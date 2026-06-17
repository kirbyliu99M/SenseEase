using System;

namespace SenseEase.App.Services;

/// <summary>
/// UI-RESHAPE prep — the contract every always-on overlay surface (edge sidebar,
/// popup bar, NPS survey) implements so the future <c>ShellController</c> can manage
/// them uniformly: union their rects for the WGC mask-exclude invariant (every
/// SenseEase-owned visible window on the captured monitor must be excluded from the
/// OFI feedback loop — see docs/POPUP-OVERLAY.md §3) and re-assert their z-order.
///
/// <para>Both members already exist on <c>PersistentBarWindow</c>
/// (<c>GetMonitorRectInPhysicalPixels</c>, <c>ReassertTopmost</c>); the new windows
/// mirror that implementation. <see cref="Closed"/> is satisfied implicitly by
/// <see cref="System.Windows.Window.Closed"/>.</para>
/// </summary>
public interface IShellSurface
{
    /// <summary>Monitor-coordinate physical-pixel footprint for the WGC exclude mask
    /// (DPI-aware). Returns the CURRENT footprint (grown size while hovered).</summary>
    (int Left, int Top, int Width, int Height) GetMonitorRectInPhysicalPixels();

    /// <summary>Force this window back to the top of the topmost band via
    /// Win32 SetWindowPos(HWND_TOPMOST) — idempotent, no activation.</summary>
    void ReassertTopmost();

    /// <summary>Raised when the surface closes so the host can unregister it from the
    /// exclude-rect union + topmost fan-out. Satisfied by <c>Window.Closed</c>.</summary>
    event EventHandler? Closed;
}
