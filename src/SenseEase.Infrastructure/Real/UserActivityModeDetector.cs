using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Auto Driver / Passenger mode detector.
///
/// RATIONALE (vection / VIMS):
/// Classic driver-vs-passenger car-sickness research suggests that the actively
/// controlling user (driver) has proprioceptive awareness of their input and is
/// less prone to vection from passive optical flow, while a passively viewing
/// user (passenger) is more vulnerable. We mirror that distinction at the OS
/// level: when the user is actively producing input (mouse / keyboard / touch),
/// they behave like a "driver"; when idle and merely watching the screen, they
/// behave like a "passenger". The downstream engine already exploits this — see
/// MockInferenceEngine.Update(ofi, mode, ...) which attenuates passive flow
/// (× 0.25, weight 1.0) in Driver mode and uses the more sensitive
/// SenseDefaults.PassiveFlowWeight = 2.0 in Passenger mode.
///
/// MECHANISM:
/// Polls Win32 GetLastInputInfo at 1 Hz. GetLastInputInfo returns the tick of
/// the most recent input event for ANY application on the desktop, which is
/// exactly the behaviour we want — the user actively typing in another window
/// still indicates an active, "driver-like" state for the whole session.
///
/// HYSTERESIS (asymmetric attack / release, audio-compressor style):
///   - Passenger → Driver   when idleMs ≤ DriverThresholdMs    (2500 ms — fast attack)
///   - Driver    → Passenger when idleMs ≥ PassengerThresholdMs (5000 ms — slow release)
/// The gap between the two thresholds prevents rapid flapping at the boundary.
///
/// KNOWN LIMITATIONS:
///   - GetLastInputInfo only reflects LOCAL physical input. Remote Desktop /
///     RDP sessions: input injected by the remote client DOES update
///     GetLastInputInfo on the host (Microsoft docs confirm this for the
///     console session). However, when the host is locked or the session is
///     disconnected, polling continues without input updates and the detector
///     will report Passenger — acceptable, the user is not present.
///   - Touch / pen input: included via WM_INPUT, also reflected in
///     GetLastInputInfo.
///   - SendInput / synthetic input from other apps also updates this counter;
///     that is fine — if "something" is driving the cursor at a human-like
///     rate the user is plausibly interacting.
/// </summary>
public sealed class UserActivityModeDetector : IDisposable
{
    // Hysteresis thresholds. Kept as private const here (not in SenseDefaults)
    // because they are specific to this detector's policy, not a shared
    // perceptual constant.
    private const int DriverThresholdMs    = 2500; // Passenger → Driver when idle ≤ this
    private const int PassengerThresholdMs = 5000; // Driver → Passenger when idle ≥ this
    private const int PollIntervalMs       = 1000; // 1 Hz polling

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private Timer? _timer;
    private string _currentMode = "Passenger"; // matches MainViewModel initial value
    private int _lastIdleMs;                   // last reading from GetLastInputInfo
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>
    /// Raised when the detected mode transitions. Argument is the new mode string,
    /// either "Driver" or "Passenger". May be raised on a thread-pool thread —
    /// the consumer is responsible for marshalling to the UI thread.
    /// </summary>
    public event EventHandler<string>? ModeChanged;

    /// <summary>
    /// Raised after EVERY poll tick with the latest idle reading in milliseconds,
    /// regardless of whether the mode changed. Useful for status displays that
    /// want to show a live "idle Xs" counter. Same threading contract as
    /// ModeChanged — may fire on a thread-pool thread.
    /// </summary>
    public event EventHandler<int>? IdleSampled;

    /// <summary>
    /// Current detected mode ("Driver" or "Passenger").
    /// </summary>
    public string CurrentMode
    {
        get { lock (_gate) return _currentMode; }
    }

    /// <summary>
    /// Most recent idle reading in milliseconds (from GetLastInputInfo). 0 before
    /// the first poll completes. Updated every poll tick whether or not the
    /// mode transitions.
    /// </summary>
    public int LastIdleMs
    {
        get { lock (_gate) return _lastIdleMs; }
    }

    /// <summary>
    /// Pure, side-effect-free policy function. Given the current idle time and
    /// the current mode, returns the new mode under the hysteresis rules.
    /// Extracted as static so it can be unit-tested without instantiating the
    /// timer or invoking P/Invoke.
    /// </summary>
    public static string DetectMode(int idleMs, string currentMode)
    {
        bool isDriver = string.Equals(currentMode, "Driver", StringComparison.OrdinalIgnoreCase);
        if (isDriver && idleMs >= PassengerThresholdMs) return "Passenger";
        if (!isDriver && idleMs <= DriverThresholdMs) return "Driver";
        return currentMode;
    }

    /// <summary>
    /// Begin polling. Safe to call repeatedly; subsequent calls are no-ops while
    /// the detector is already running.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UserActivityModeDetector));
            if (_timer != null) return;
            _timer = new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
        }
    }

    /// <summary>
    /// Force the detector's internal mode to <paramref name="newMode"/> without firing
    /// ModeChanged. Intended for the case where the user manually overrides the mode
    /// via UI — we want auto-detection to continue measuring idle, but starting from
    /// the user's chosen mode (so the asymmetric debounce thresholds make sense
    /// relative to where they are now, not where the detector last polled).
    /// </summary>
    public void OverrideMode(string newMode)
    {
        if (string.IsNullOrEmpty(newMode)) return;
        lock (_gate) { _currentMode = newMode; }
    }

    /// <summary>
    /// Stop polling. The current mode is retained; consumers can still read it.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Poll()
    {
        int idleMs = GetIdleMilliseconds();
        if (idleMs < 0) return; // P/Invoke failure — skip this tick

        // Cross-component read: WindowsActivityFilter's typing suppression flag is
        // a much higher-resolution signal of ACTIVE input than GetLastInputInfo
        // (the latter only updates on completed input events, not while a key is
        // physically held). If the filter says the user is typing RIGHT NOW we
        // force "Driver" regardless of the static DetectMode policy. We keep
        // DetectMode pure so it remains unit-testable in isolation.
        bool isTypingNow = false;
        // NE12: use the dedicated IsTypingActive flag, not SuppressionReason. The
        // latter returns only the single DOMINANT reason, so a concurrent
        // higher-priority chrome event (window-switch / toast / drag) would mask
        // active typing and the "typing forces Driver" override would silently miss.
        try { isTypingNow = WindowsActivityFilter.Instance.IsTypingActive; }
        catch { /* filter unavailable — fall back to GetLastInputInfo-only behaviour */ }

        string previous;
        string next;
        lock (_gate)
        {
            _lastIdleMs = idleMs;
            previous = _currentMode;
            next = DetectMode(idleMs, previous);
            if (isTypingNow) next = "Driver"; // override: typing is always active
            if (next != previous) _currentMode = next;
        }

        IdleSampled?.Invoke(this, idleMs);
        if (next != previous) ModeChanged?.Invoke(this, next);
    }

    /// <summary>
    /// Returns milliseconds since the most recent OS-level input event, or -1
    /// if the underlying call failed. Uses Environment.TickCount for the
    /// managed-side current tick — same monotonic 32-bit value as
    /// GetTickCount, no extra P/Invoke needed. Handles 49.7-day wraparound
    /// via unsigned subtraction.
    /// </summary>
    private static int GetIdleMilliseconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return -1;
        // Unsigned subtraction handles the 32-bit tick wraparound at ~49.7 days.
        uint now = (uint)Environment.TickCount;
        uint delta = now - info.dwTime;
        // Clamp to int range — anything beyond ~24.8 days idle is "very idle".
        return delta > int.MaxValue ? int.MaxValue : (int)delta;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        ModeChanged = null;
    }
}
