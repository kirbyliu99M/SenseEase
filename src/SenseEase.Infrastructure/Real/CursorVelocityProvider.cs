using System;
using System.Runtime.InteropServices;
using System.Threading;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Phase-2.1 real sensing: derives OFI from cursor speed via Win32 GetCursorPos.
/// Must be constructed on the UI thread so the captured SynchronizationContext
/// can marshal OpticalFlowSampled back to the UI thread automatically.
/// RETIRED PATH: WgcOpticalFlowProvider (Phase 2.2) is the live default; this
/// provider is currently unreachable in normal startup and is scheduled for
/// removal per D-9.
///
/// FALSE-POSITIVE REJECTION (Phase 2.1.1):
/// Self-generated cursor motion (the user moving their own hand — window drags,
/// selections, scrollbar drags) does NOT induce visually-induced motion sickness.
/// Vection requires externally-imposed visual motion; the user has full
/// proprioceptive awareness of their own hand. We therefore suppress OFI whenever
/// we detect evidence that cursor motion is user-driven:
///   - Any mouse button currently held (LMB / RMB / MMB) — drag in progress
///   - A window currently has mouse capture (GetCapture() != 0) — explicit drag
/// During suppression we emit OFI = 0 (rather than skipping the tick) so the
/// downstream leaky integrator decays naturally and the idle-timer logic in
/// MainViewModel keeps running.
/// Controlled by the RejectUserDrivenCursorOfi constructor parameter
/// (default true). AppConfig.RejectCursorDragOfi exposes the same toggle to the
/// App layer; wire it through composition if you want runtime control.
/// </summary>
public sealed class CursorVelocityProvider : ISensingProvider
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // Returns a short whose high-order bit is set if the key/button is currently down.
    // We check (state & 0x8000) — the low bit is a "was pressed since last call" flag we ignore.
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Returns the handle of the window (in the calling thread) that has captured the
    // mouse. Non-zero during explicit drag operations (window moves, scrollbar tracking).
    // Note: only sees capture inside the calling thread's input queue, so this is a
    // best-effort signal complementing the button-state checks.
    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;
    private const int KEY_PRESSED_MASK = unchecked((short)0x8000);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly SynchronizationContext? _ctx;
    private readonly Timer _timer;
    private readonly bool  _rejectUserDriven;

    private POINT    _lastPos;
    private DateTime _lastTick;
    private bool     _running;
    private double   _smoothedOfi;

    // Cached SendOrPostCallback + pending sample field — avoids per-tick closure
    // allocations (~60 obj/sec at 30 Hz). "Latest sample wins" if the dispatcher
    // is briefly back-pressured — engine's EMA + integrator absorbs single drops.
    private OpticalFlowSample? _pendingSample;
    private readonly SendOrPostCallback? _raiseSampleCallback;

    public event EventHandler<OpticalFlowSample>? OpticalFlowSampled;

    public CursorVelocityProvider() : this(rejectUserDrivenCursorOfi: true) { }

    public CursorVelocityProvider(bool rejectUserDrivenCursorOfi)
    {
        // Capture UI SynchronizationContext so events always fire on the UI thread.
        _ctx              = SynchronizationContext.Current;
        _timer            = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        _rejectUserDriven = rejectUserDrivenCursorOfi;
        _raiseSampleCallback = _ =>
        {
            var s = _pendingSample;
            if (s != null) OpticalFlowSampled?.Invoke(this, s);
        };
    }

    public void Start()
    {
        GetCursorPos(out _lastPos);
        _lastTick = DateTime.UtcNow;
        _running  = true;
        _timer.Change(SenseDefaults.CursorVelocityPollIntervalMs,
                      SenseDefaults.CursorVelocityPollIntervalMs);
    }

    public void Stop()
    {
        _running = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// True if the cursor motion observed this tick is attributable to the user's own
    /// hand (button held or window has mouse capture). In that case OFI is suppressed.
    /// </summary>
    private static bool IsUserDrivenCursorActivity()
    {
        if ((GetAsyncKeyState(VK_LBUTTON) & KEY_PRESSED_MASK) != 0) return true;
        if ((GetAsyncKeyState(VK_RBUTTON) & KEY_PRESSED_MASK) != 0) return true;
        if ((GetAsyncKeyState(VK_MBUTTON) & KEY_PRESSED_MASK) != 0) return true;
        if (GetCapture() != IntPtr.Zero) return true;
        return false;
    }

    private void OnTick(object? state)
    {
        if (!_running) return;

        GetCursorPos(out POINT pos);
        DateTime now = DateTime.UtcNow;

        double dt = (now - _lastTick).TotalSeconds;
        if (dt <= 0) { _lastPos = pos; _lastTick = now; return; }

        double rawOfi;
        // Suppression has TWO independent sources (Phase 2.2.1):
        //   1. Cursor-driven (Agent B): mouse button held or window has capture.
        //   2. OS-chrome activity (WindowsActivityFilter): Alt+Tab, menu open, window
        //      move/resize, notification, or typing within their respective windows.
        // Either one is sufficient to zero this tick. Case #2 catches scenarios where
        // the user alt-tabs or types WITHOUT holding a mouse button — case #1 alone
        // would let those bleed through as spurious "vection".
        bool suppress =
            (_rejectUserDriven && IsUserDrivenCursorActivity())
            || WindowsActivityFilter.Instance.ShouldSuppressOfi;

        if (suppress)
        {
            // User is dragging, alt-tabbing, typing, etc. — treat as zero ambient
            // optical flow so the leaky integrator decays. We still advance _lastPos
            // so the next tick after suppression ends doesn't see an artificial spike
            // from the accumulated delta.
            rawOfi = 0.0;
        }
        else
        {
            double dx = pos.X - _lastPos.X;
            double dy = pos.Y - _lastPos.Y;
            rawOfi = Math.Clamp(
                Math.Sqrt(dx * dx + dy * dy) / dt / SenseDefaults.CursorVelocityNormFactor,
                0, 150);
        }

        // EMA smoothing: eliminates instantaneous speed spikes.
        _smoothedOfi = SenseDefaults.CursorEmaAlpha * _smoothedOfi
                     + (1.0 - SenseDefaults.CursorEmaAlpha) * rawOfi;

        _lastPos  = pos;
        _lastTick = now;

        var sample = new OpticalFlowSample { OpticalFlowIndex = _smoothedOfi, SampledAt = now };

        // Cached SendOrPostCallback path — avoids per-tick closure allocation.
        // "Latest sample wins" if dispatcher back-pressured; engine EMA absorbs drops.
        _pendingSample = sample;
        if (_ctx != null && _raiseSampleCallback != null)
            _ctx.Post(_raiseSampleCallback, null);
        else
            OpticalFlowSampled?.Invoke(this, sample);
    }
}
