using SenseEase.App.Services;
using Xunit;

namespace SenseEase.Tests;

/// <summary>SystemSurfaceZGuard (2026-06-28): keeps native menus + the IME 中 UI + the taskbar above the
/// comfort overlay so the overlay's ~1/s topmost re-assert can't bury them. The hook/lift Win32 paths need a
/// real desktop; here we pin the pure event→action mapping (the only branch-y logic) — Add on a menu/IME
/// surface opening, Remove on closing, Ignore for everything the two narrow hooks don't target.</summary>
public class SystemSurfaceZGuardTests
{
    // Win32 WinEvent ids (winuser.h) — the two narrow ranges this guard hooks.
    const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0006;
    const uint EVENT_SYSTEM_MENUPOPUPEND   = 0x0007;
    const uint EVENT_OBJECT_IME_SHOW       = 0x8027;
    const uint EVENT_OBJECT_IME_HIDE       = 0x8028;

    [Theory]
    [InlineData(EVENT_SYSTEM_MENUPOPUPSTART, true)]   // right-click 中 menu opens  → track
    [InlineData(EVENT_OBJECT_IME_SHOW,       true)]   // IME candidate/composition  → track
    [InlineData(EVENT_SYSTEM_MENUPOPUPEND,   false)]  // menu closes                → untrack
    [InlineData(EVENT_OBJECT_IME_HIDE,       false)]  // IME UI hides               → untrack
    public void Classify_AddOnOpen_RemoveOnClose(uint ev, bool isOpen)
        => Assert.Equal(isOpen ? SystemSurfaceZGuard.Track.Add : SystemSurfaceZGuard.Track.Remove,
                        SystemSurfaceZGuard.Classify(ev));

    [Theory]
    [InlineData(0x8002u)]  // EVENT_OBJECT_SHOW — deliberately NOT hooked (a system-wide window firehose)
    [InlineData(0x8003u)]  // EVENT_OBJECT_HIDE
    [InlineData(0x0003u)]  // EVENT_SYSTEM_FOREGROUND
    [InlineData(0x800Cu)]  // EVENT_OBJECT_NAMECHANGE
    [InlineData(0u)]
    public void Classify_IgnoresEverythingElse(uint ev)
        => Assert.Equal(SystemSurfaceZGuard.Track.Ignore, SystemSurfaceZGuard.Classify(ev));
}
