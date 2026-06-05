using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Phase-2.2.1 — OS-CHROME MOTION EXCLUSION FILTER.
///
/// Rationale: the pixel-diff and cursor-velocity sensing providers see EVERY visual
/// change on screen, but visually-induced motion sickness (vection) only arises from
/// EXTERNALLY-IMPOSED visual motion — not from animations that the user themselves
/// initiated as part of operating Windows. We therefore suppress OFI emission
/// during a short window around any of these user-driven OS-chrome events:
///
///   • Foreground window changes (Alt+Tab, Win+Tab, clicking another app)   ─ 800 ms
///   • Menu / popup-menu open or close (Start menu, context menu, dropdown) ─ 500 ms
///   • Window move/resize start or end (title-bar drag, edge resize)        ─ 500 ms
///   • Object show/hide (toast notifications, fly-outs)                     ─ 500 ms
///   • Keyboard typing (any character/space/backspace/enter key currently down) ─ 150 ms
///   • Plain cursor movement (any pointer-position delta since the last poll)   ─ 300 ms
///
/// Cursor-button-down and explicit mouse-capture drags are handled separately by
/// CursorVelocityProvider's existing self-driven-motion filter; this class is
/// strictly additive to that.
///
/// Cursor movement: GetCursorPos is polled at 20 Hz alongside the keyboard scan.
/// Any position delta sets _lastCursorMoveTicks → ShouldSuppressOfi true for the
/// next 300 ms. This catches both the user driving the cursor (Cursor mode's
/// sensor signal) AND the cursor sprite redrawing on the screen (WGC mode's
/// pixel-diff incidental noise from the cursor itself) — both are self-driven
/// and should not pump VIMS pressure.
///
/// Implementation:
///   • One process-wide singleton (Instance) installs two SetWinEventHook hooks
///     covering EVENT_SYSTEM_FOREGROUND..EVENT_SYSTEM_MOVESIZEEND (0x0003..0x000B)
///     and EVENT_OBJECT_SHOW..EVENT_OBJECT_HIDE (0x8002..0x8003).
///   • Flags: WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS so the callback runs
///     on a system-supplied thread and we don't echo our own UI events.
///   • EVENT_OBJECT_LOCATIONCHANGE (0x800B) is INTENTIONALLY NOT subscribed — it
///     fires on virtually every cursor pixel move and would defeat the purpose of
///     suppression by being on permanently.
///   • Typing detection polls a curated set of character-producing virtual keys at
///     20 Hz via GetAsyncKeyState — ~38 P/Invokes per 50 ms is negligible.
///   • Last-event timestamps are stored as Int64 UTC ticks updated via
///     Interlocked.Exchange; reads use Interlocked.Read, giving lock-free atomic
///     visibility for the ShouldSuppressOfi predicate consulted by sensing providers.
///
/// Failure mode: if hook installation throws (low-rights process, station/desktop
/// restrictions, shutdown race) the filter degrades to "never suppress" so sensing
/// continues uninterrupted.
/// </summary>
public sealed class WindowsActivityFilter
{
    // ── Suppression windows (ms) — chosen by event-class noise duration ──────────────
    private const int ForegroundSuppressionMs = 800; // Alt+Tab cross-fade ~300-500 ms; add headroom
    private const int MenuSuppressionMs       = 500; // Menu fade-in/out
    private const int MoveSizeSuppressionMs   = 500; // After drag ends, glass settles
    private const int ObjectShowHideMs        = 500; // Notification toasts slide-in
    private const int TypingSuppressionMs     = 150; // ~one keystroke cadence
    private const int CursorMoveSuppressionMs = 300; // any pointer movement is user-driven

    private const int TypingPollIntervalMs    = 50;  // 20 Hz keyboard + cursor poll

    // ── WinEvent constants (winuser.h) ───────────────────────────────────────────────
    private const uint EVENT_SYSTEM_FOREGROUND     = 0x0003;
    private const uint EVENT_SYSTEM_MENUSTART      = 0x0004;
    private const uint EVENT_SYSTEM_MENUEND        = 0x0005;
    private const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0006;
    private const uint EVENT_SYSTEM_MENUPOPUPEND   = 0x0007;
    private const uint EVENT_SYSTEM_MOVESIZESTART  = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND    = 0x000B;
    private const uint EVENT_OBJECT_SHOW           = 0x8002;
    private const uint EVENT_OBJECT_HIDE           = 0x8003;

    private const uint WINEVENT_OUTOFCONTEXT       = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS     = 0x0002;
    private const uint WINEVENT_SKIPOWNTHREAD      = 0x0001;

    // ── P/Invoke ─────────────────────────────────────────────────────────────────────
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const short KEY_PRESSED_MASK = unchecked((short)0x8000);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // ── Singleton ────────────────────────────────────────────────────────────────────
    private static readonly Lazy<WindowsActivityFilter> _instance =
        new(() => new WindowsActivityFilter(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static WindowsActivityFilter Instance => _instance.Value;

    // ── State (Int64 UTC ticks; Interlocked-accessed for lock-free atomicity) ────────
    private long _lastForegroundTicks;
    private long _lastMenuTicks;
    private long _lastMoveSizeTicks;
    private long _lastObjectShowHideTicks;
    private long _lastKeyDownTicks;
    private long _lastCursorMoveTicks;
    private int  _lastCursorX = int.MinValue;
    private int  _lastCursorY = int.MinValue;

    private IntPtr _hookSystemRange;  // 0x0003..0x000B (foreground + menu + movesize)
    private IntPtr _hookObjectRange;  // 0x8002..0x8003 (show/hide)
    private readonly WinEventDelegate _systemRangeDelegate;
    private readonly WinEventDelegate _objectRangeDelegate;

    private Timer? _typingPollTimer;
    private bool _installed;
    private readonly object _lifecycleLock = new();

    // Curated virtual-key set covering character-producing keys.
    // VK_BACK(0x08), VK_RETURN(0x0D), VK_SPACE(0x20),
    // VK_0..VK_9 (0x30..0x39), VK_A..VK_Z (0x41..0x5A),
    // VK_OEM_1..VK_OEM_3 (0xBA..0xC0), VK_OEM_4..VK_OEM_8 (0xDB..0xDF),
    // VK_OEM_COMMA(0xBC), VK_OEM_MINUS(0xBD), VK_OEM_PERIOD(0xBE), VK_OEM_2(0xBF),
    // VK_OEM_PLUS(0xBB).
    private static readonly int[] TypingVirtualKeys = BuildTypingVirtualKeys();

    private static int[] BuildTypingVirtualKeys()
    {
        var keys = new System.Collections.Generic.List<int>(64)
        {
            0x08, // VK_BACK
            0x0D, // VK_RETURN
            0x20, // VK_SPACE
            0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, // OEM_1, OEM_PLUS, OEM_COMMA, OEM_MINUS, OEM_PERIOD, OEM_2, OEM_3
            0xDB, 0xDC, 0xDD, 0xDE, // OEM_4..OEM_7
        };
        for (int vk = 0x30; vk <= 0x39; vk++) keys.Add(vk); // 0..9
        for (int vk = 0x41; vk <= 0x5A; vk++) keys.Add(vk); // A..Z
        return keys.ToArray();
    }

    private WindowsActivityFilter()
    {
        _systemRangeDelegate = OnSystemWinEvent;
        _objectRangeDelegate = OnObjectWinEvent;
        TryInstall();
    }

    /// <summary>
    /// Best-effort hook installation. Failure is non-fatal; the filter degrades to
    /// "never suppress" by leaving all _last*Ticks at zero and ShouldSuppressOfi
    /// returning false (a tick of 0 corresponds to year 1; the elapsed-ms check
    /// becomes astronomically large, never within any suppression window).
    /// </summary>
    private void TryInstall()
    {
        lock (_lifecycleLock)
        {
            if (_installed) return;
            try
            {
                _hookSystemRange = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MOVESIZEEND,
                    IntPtr.Zero, _systemRangeDelegate, 0, 0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS | WINEVENT_SKIPOWNTHREAD);

                _hookObjectRange = SetWinEventHook(
                    EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE,
                    IntPtr.Zero, _objectRangeDelegate, 0, 0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS | WINEVENT_SKIPOWNTHREAD);

                _typingPollTimer = new Timer(PollKeyboard, null,
                    TypingPollIntervalMs, TypingPollIntervalMs);

                _installed = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsActivityFilter] install failed (degrading to no-op): {ex.Message}");
                // Best-effort: tear down anything partial.
                if (_hookSystemRange != IntPtr.Zero) { try { UnhookWinEvent(_hookSystemRange); } catch { } _hookSystemRange = IntPtr.Zero; }
                if (_hookObjectRange != IntPtr.Zero) { try { UnhookWinEvent(_hookObjectRange); } catch { } _hookObjectRange = IntPtr.Zero; }
                _typingPollTimer?.Dispose();
                _typingPollTimer = null;
                _installed = false;
            }
        }
    }

    /// <summary>
    /// Releases WinEvent hooks and stops the typing poll timer. Idempotent.
    /// </summary>
    public void Shutdown()
    {
        lock (_lifecycleLock)
        {
            if (!_installed) return;
            try { if (_hookSystemRange != IntPtr.Zero) UnhookWinEvent(_hookSystemRange); } catch { }
            try { if (_hookObjectRange != IntPtr.Zero) UnhookWinEvent(_hookObjectRange); } catch { }
            _hookSystemRange = IntPtr.Zero;
            _hookObjectRange = IntPtr.Zero;
            try { _typingPollTimer?.Dispose(); } catch { }
            _typingPollTimer = null;
            _installed = false;
        }
    }

    // ── Hook callbacks (run on a system thread, not UI) ──────────────────────────────
    private void OnSystemWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        switch (eventType)
        {
            case EVENT_SYSTEM_FOREGROUND:
                Interlocked.Exchange(ref _lastForegroundTicks, nowTicks);
                break;
            case EVENT_SYSTEM_MENUSTART:
            case EVENT_SYSTEM_MENUEND:
            case EVENT_SYSTEM_MENUPOPUPSTART:
            case EVENT_SYSTEM_MENUPOPUPEND:
                Interlocked.Exchange(ref _lastMenuTicks, nowTicks);
                break;
            case EVENT_SYSTEM_MOVESIZESTART:
            case EVENT_SYSTEM_MOVESIZEEND:
                Interlocked.Exchange(ref _lastMoveSizeTicks, nowTicks);
                break;
        }
    }

    private void OnObjectWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // EVENT_OBJECT_SHOW/HIDE fires for many transient UI elements (tooltips,
        // dropdowns, balloon tips, toast notifications). Restrict to top-level
        // window objects (idObject == OBJID_WINDOW == 0) to skip the worst of the
        // sub-element noise while still catching toasts and fly-outs.
        const int OBJID_WINDOW = 0;
        if (idObject != OBJID_WINDOW) return;

        long nowTicks = DateTime.UtcNow.Ticks;
        if (eventType == EVENT_OBJECT_SHOW || eventType == EVENT_OBJECT_HIDE)
            Interlocked.Exchange(ref _lastObjectShowHideTicks, nowTicks);
    }

    // ── Keyboard poll ────────────────────────────────────────────────────────────────
    private void PollKeyboard(object? state)
    {
        try
        {
            long nowTicks = DateTime.UtcNow.Ticks;

            // Keyboard: 20 Hz scan of curated character-producing keys.
            foreach (int vk in TypingVirtualKeys)
            {
                if ((GetAsyncKeyState(vk) & KEY_PRESSED_MASK) != 0)
                {
                    Interlocked.Exchange(ref _lastKeyDownTicks, nowTicks);
                    break;
                }
            }

            // Cursor: any position delta since the last poll = self-driven motion.
            // Catches plain mouse movement (no button, no menu) which neither WinEvent
            // hook would surface (EVENT_OBJECT_LOCATIONCHANGE was deliberately not
            // subscribed to avoid permanent-on noise). Polled at the same 20 Hz cadence
            // since both signals share the timer.
            if (GetCursorPos(out POINT pt))
            {
                // Single-writer (this timer callback) reads/writes the prev-coord fields,
                // so plain ints are race-free here. Only _lastCursorMoveTicks is multi-reader
                // (read by ShouldSuppressOfi from arbitrary threads) and stays Interlocked.
                int prevX = _lastCursorX;
                int prevY = _lastCursorY;
                _lastCursorX = pt.X;
                _lastCursorY = pt.Y;
                if (prevX != int.MinValue && (pt.X != prevX || pt.Y != prevY))
                    Interlocked.Exchange(ref _lastCursorMoveTicks, nowTicks);
            }
        }
        catch
        {
            // Never let the timer crash the filter.
        }
    }

    // ── Public API ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// True when any OS-chrome activity (foreground switch, menu, window move/resize,
    /// notification show/hide, recent typing) occurred within its event-specific
    /// suppression window. Sensing providers should emit OFI = 0 while this returns true.
    /// Lock-free; safe to call from any thread.
    /// </summary>
    public bool ShouldSuppressOfi
    {
        get
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            // Take a consistent snapshot of each field via Interlocked.Read; this
            // also ensures torn-read safety on 32-bit runtimes (Int64 reads are not
            // atomic without it).
            return WithinWindow(nowTicks, Interlocked.Read(ref _lastForegroundTicks),     ForegroundSuppressionMs)
                || WithinWindow(nowTicks, Interlocked.Read(ref _lastMenuTicks),           MenuSuppressionMs)
                || WithinWindow(nowTicks, Interlocked.Read(ref _lastMoveSizeTicks),       MoveSizeSuppressionMs)
                || WithinWindow(nowTicks, Interlocked.Read(ref _lastObjectShowHideTicks), ObjectShowHideMs)
                || WithinWindow(nowTicks, Interlocked.Read(ref _lastKeyDownTicks),        TypingSuppressionMs)
                || WithinWindow(nowTicks, Interlocked.Read(ref _lastCursorMoveTicks),     CursorMoveSuppressionMs);
        }
    }

    /// <summary>
    /// Short tag identifying the DOMINANT reason OFI suppression is currently active,
    /// or empty string when nothing is suppressing. Mirrors the predicate ordering
    /// inside <see cref="ShouldSuppressOfi"/>: the worst/most-noticeable user-driven
    /// chrome event wins first, with typing and cursor motion ranked last because
    /// they are the most common low-level signals. Intended for UI status display
    /// and cross-component reads (e.g. UserActivityModeDetector treats "typing" as
    /// definitive "Driver" input). Lock-free; safe from any thread.
    /// </summary>
    public string SuppressionReason
    {
        get
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            if (WithinWindow(nowTicks, Interlocked.Read(ref _lastForegroundTicks),     ForegroundSuppressionMs)) return "window-switch";
            if (WithinWindow(nowTicks, Interlocked.Read(ref _lastMoveSizeTicks),       MoveSizeSuppressionMs))   return "window-drag";
            if (WithinWindow(nowTicks, Interlocked.Read(ref _lastMenuTicks),           MenuSuppressionMs))       return "menu";
            if (WithinWindow(nowTicks, Interlocked.Read(ref _lastObjectShowHideTicks), ObjectShowHideMs))        return "toast";
            if (WithinWindow(nowTicks, Interlocked.Read(ref _lastKeyDownTicks),        TypingSuppressionMs))     return "typing";
            if (WithinWindow(nowTicks, Interlocked.Read(ref _lastCursorMoveTicks),     CursorMoveSuppressionMs)) return "cursor";
            return string.Empty;
        }
    }

    /// <summary>NE12: typing-active flag INDEPENDENT of the <see cref="SuppressionReason"/>
    /// ranking. SuppressionReason returns only the single dominant reason, so a
    /// higher-priority chrome event (window-switch / toast / drag) masks concurrent
    /// typing — which made <c>UserActivityModeDetector</c> miss "the user is typing
    /// right now". This exposes the raw typing window directly. Lock-free.</summary>
    public bool IsTypingActive
        => WithinWindow(DateTime.UtcNow.Ticks, Interlocked.Read(ref _lastKeyDownTicks), TypingSuppressionMs);

    private static bool WithinWindow(long nowTicks, long lastTicks, int windowMs)
    {
        if (lastTicks == 0) return false; // never fired — clearly not within window
        long elapsedTicks = nowTicks - lastTicks;
        if (elapsedTicks < 0) return false; // clock went backwards; ignore
        // 1 ms == 10_000 ticks
        return elapsedTicks < ((long)windowMs * 10_000L);
    }
}
