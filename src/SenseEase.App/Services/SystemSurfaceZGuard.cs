using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SenseEase.App.Services;

/// <summary>
/// Keeps transient SYSTEM surfaces — native context menus (incl. the right-click menu off the IME 中 tray
/// icon), the IME candidate/composition UI, and the taskbar/tray strip — ABOVE the comfort overlay, so they
/// stay visible/usable instead of being buried under the dim.
///
/// <para>WHY: the overlay re-asserts <c>SetWindowPos(HWND_TOPMOST)</c> on itself ~once/second (see
/// <c>OverlayWindow.ApplyState</c>) and that climbs the overlay over EVERYTHING in the topmost band — the
/// Win32 "owned-above-owner" rule only fires on the make-topmost TRANSITION, not on an already-topmost re-top
/// (see <see cref="OverlayZOrder"/>). <see cref="OverlayZOrder"/> re-lifts SenseEase's OWN windows in that same
/// tick; a native menu / the ctfmon-TSF IME UI are FOREIGN HWNDs it can't know about, so nothing re-lifts them
/// and they sit under the overlay tint until they close. This guard closes that gap for those specific system
/// surfaces. The comfort mask still covers ordinary app windows — only a popup MENU, an IME UI window, or the
/// taskbar is ever lifted, never arbitrary foreign app windows.</para>
///
/// <para>Detection uses TWO NARROW WinEvent hooks (menu-popup start/end + IME show/hide) so there is no
/// system-wide window show/hide firehose; the callbacks are trivial. <see cref="LiftAboveOverlay"/> is called
/// from <c>OverlayWindow.ApplyState</c> in the SAME synchronous tick as the overlay's re-assert (mirroring
/// <see cref="OverlayZOrder.ReassertAboveOverlay"/>), so no frame paints with the overlay on top. Best-effort
/// and NON-THROWING throughout: any hook/interop failure degrades to "no lift", never breaking the overlay.</para>
/// </summary>
public static class SystemSurfaceZGuard
{
    // WinEvent ids (winuser.h). Two narrow ranges only — NOT the broad OBJECT_SHOW/HIDE firehose.
    private const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0006;
    private const uint EVENT_SYSTEM_MENUPOPUPEND   = 0x0007;
    private const uint EVENT_OBJECT_IME_SHOW       = 0x8027;
    private const uint EVENT_OBJECT_IME_HIDE       = 0x8028;
    private const uint WINEVENT_OUTOFCONTEXT       = 0x0000;   // callback delivered on the registering thread's msg loop
    private const uint WINEVENT_SKIPOWNPROCESS     = 0x0002;   // never track SenseEase's own menus

    private const uint SWP_LIFT = 0x0001 | 0x0002 | 0x0010;    // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private delegate void WinEventDelegate(
        IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);

    private static readonly object _lock = new();
    private static readonly HashSet<IntPtr> _open = new();   // transient system popups (menus / IME UI) currently shown
    private static IntPtr _hookMenu, _hookIme;
    private static bool _started;                            // guards a single install (even if one hook came back null)
    private static WinEventDelegate? _cb;                    // held in a field so the GC can't collect it while hooked
    private static IntPtr _taskbar;                          // cached Shell_TrayWnd (re-found if it dies)

    /// <summary>Install the hooks (idempotent). Call from the overlay's UI thread once its HWND exists, so the
    /// out-of-context callbacks are delivered on the UI thread — the same thread <see cref="LiftAboveOverlay"/>
    /// runs on. Never throws. A partial install (one hook fails) is rolled back so it can't leak a hook; the
    /// taskbar lift in <see cref="LiftAboveOverlay"/> works regardless of the hooks.</summary>
    public static void Start()
    {
        lock (_lock)
        {
            if (_started) return;                            // single install — don't re-enter + leak a hook
            try
            {
                _cb = OnWinEvent;
                _hookMenu = SetWinEventHook(EVENT_SYSTEM_MENUPOPUPSTART, EVENT_SYSTEM_MENUPOPUPEND,
                    IntPtr.Zero, _cb, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                _hookIme = SetWinEventHook(EVENT_OBJECT_IME_SHOW, EVENT_OBJECT_IME_HIDE,
                    IntPtr.Zero, _cb, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                _started = true;
            }
            catch
            {
                // partial install — unhook whatever took, reset, leave _started false so a later call can retry.
                try { if (_hookMenu != IntPtr.Zero) UnhookWinEvent(_hookMenu); } catch { }
                try { if (_hookIme  != IntPtr.Zero) UnhookWinEvent(_hookIme);  } catch { }
                _hookMenu = _hookIme = IntPtr.Zero;
                _cb = null;
            }
        }
    }

    /// <summary>Uninstall the hooks + drop the tracked set. Safe to call repeatedly.</summary>
    public static void Stop()
    {
        lock (_lock)
        {
            try { if (_hookMenu != IntPtr.Zero) UnhookWinEvent(_hookMenu); } catch { }
            try { if (_hookIme  != IntPtr.Zero) UnhookWinEvent(_hookIme);  } catch { }
            _hookMenu = _hookIme = IntPtr.Zero;
            _cb = null;
            _started = false;
            _open.Clear();
        }
    }

    private static void OnWinEvent(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            switch (Classify(ev))
            {
                case Track.Add:    lock (_lock) _open.Add(hwnd);    break;
                case Track.Remove: lock (_lock) _open.Remove(hwnd); break;
            }
        }
        catch { /* a WinEvent callback must never throw */ }
    }

    internal enum Track { Ignore, Add, Remove }

    /// <summary>Pure event→action mapping for the two narrow hooks: a menu/IME surface opening is tracked
    /// (Add), closing is dropped (Remove). A MENUPOPUPSTART hwnd is by definition a popup menu and an
    /// IME_SHOW hwnd an IME window, so no class filter is needed; the narrow hook ranges already exclude
    /// ordinary windows. Unit-testable without an HWND.</summary>
    internal static Track Classify(uint ev) => ev switch
    {
        EVENT_SYSTEM_MENUPOPUPSTART => Track.Add,
        EVENT_SYSTEM_MENUPOPUPEND   => Track.Remove,
        EVENT_OBJECT_IME_SHOW       => Track.Add,
        EVENT_OBJECT_IME_HIDE       => Track.Remove,
        _                           => Track.Ignore,
    };

    /// <summary>Lift the taskbar/tray + every tracked transient system popup back above the overlay (which has
    /// just re-topped). MUST be called synchronously on the UI thread immediately AFTER the overlay's
    /// ReassertTopmost and BEFORE <see cref="OverlayZOrder.ReassertAboveOverlay"/> — same tick, no paint
    /// between — so the final band is: SenseEase windows &gt; system popups/taskbar &gt; overlay &gt; apps.
    /// Prunes destroyed popup HWNDs. Never throws.</summary>
    public static void LiftAboveOverlay()
    {
        try
        {
            // Taskbar/tray strip — where the docked 中 indicator + clock live; keep it un-dimmed at rest.
            if (_taskbar == IntPtr.Zero || !IsWindow(_taskbar)) _taskbar = FindWindow("Shell_TrayWnd", null);
            if (_taskbar != IntPtr.Zero) SetWindowPos(_taskbar, HWND_TOPMOST, 0, 0, 0, 0, SWP_LIFT);

            List<IntPtr>? dead = null;
            lock (_lock)
            {
                foreach (var h in _open)
                {
                    if (IsWindow(h)) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_LIFT);
                    else (dead ??= new List<IntPtr>()).Add(h);
                }
                if (dead != null) foreach (var h in dead) _open.Remove(h);
            }
        }
        catch { /* best-effort z-lift */ }
    }

    /// <summary>Test seam: count of system popups currently tracked for lifting.</summary>
    internal static int TrackedCountForTests { get { lock (_lock) return _open.Count; } }
}
