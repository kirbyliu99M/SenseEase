using SenseEase.App.Interop;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 theme-3: the system-UI overlay exstyle transform. Pure (no HWND), so it's a
/// deterministic gate — unlike the STA overlay window tests. Pins that the overlay becomes
/// click-through + Alt-Tab/taskbar-absent + non-activating: the fix for the blank "SenseEase
/// Overlay" Alt-Tab entry and the Show()-steals-focus jolt (#2/#19/#20).
/// </summary>
public sealed class ClickThroughSystemUiTests
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_APPWINDOW   = 0x00040000;
    private const int WS_EX_NOACTIVATE  = 0x08000000;

    [Fact]
    public void ComputeSystemUiExStyle_SetsToolWindowNoActivateClickThrough_AndClearsAppWindow()
    {
        // Start from a normal top-level app window (APPWINDOW set — the worst case for Alt-Tab).
        nint result = ClickThrough.ComputeSystemUiExStyle(WS_EX_APPWINDOW);

        Assert.True((result & WS_EX_TOOLWINDOW) != 0, "must set WS_EX_TOOLWINDOW (out of Alt-Tab + taskbar)");
        Assert.True((result & WS_EX_NOACTIVATE) != 0, "must set WS_EX_NOACTIVATE (no focus steal)");
        Assert.True((result & WS_EX_TRANSPARENT) != 0, "must keep click-through");
        Assert.True((result & WS_EX_LAYERED) != 0, "must keep layered (transparency + capture-exclusion)");
        Assert.True((result & WS_EX_APPWINDOW) == 0, "must CLEAR WS_EX_APPWINDOW or TOOLWINDOW is overridden");
    }

    [Fact]
    public void ComputeSystemUiExStyle_PreservesUnrelatedBits()
    {
        const int someOtherBit = 0x00000004; // WS_EX_NOPARENTNOTIFY — must survive
        nint result = ClickThrough.ComputeSystemUiExStyle(someOtherBit);
        Assert.True((result & someOtherBit) != 0, "unrelated extended-style bits must be preserved");
    }
}
