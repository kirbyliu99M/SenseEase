using System;

namespace SenseEase.App;

/// <summary>
/// Beta-1: ONE-TIME NEUTRAL RECENTER for the head-pose pump.
///
/// <para>The sidecar maps ABSOLUTE head yaw to the clear-hole centre (<c>cx = 0.5 + gain·yaw</c>)
/// with no per-user baseline, so an off-axis camera or a habitual seating posture biases the hole
/// by a constant offset — the hole "shifts and locks" the instant a face is detected. This filter
/// cancels that: it captures the user's resting head position ONCE (at the first valid pose) and
/// feeds the pump the DEVIATION from it, so a habitual/centred head maps to a centred hole and the
/// hole follows movements away from rest 1:1.</para>
///
/// <para><b>Why one-time, not continuous (2026-06-15 regression fix):</b> the previous version
/// nudged the neutral toward the current pose while a head-speed EMA said the head was "still". That
/// gate was unit-broken — it averaged per-frame |Δraw| at the ~165 Hz pump rate against a ~15 Hz
/// sidecar, diluting head-speed ~11×, so the gate never closed and the baseline "learned" a held
/// gaze back toward centre on a ~4 s tau (the hole followed then drifted off and never reached the
/// target). A fixed one-time neutral does the legitimate job — cancel the constant offset — without
/// any drift. Stateful: call <see cref="Recenter"/> once per pump frame. Pure (no WPF), so it is
/// unit-testable off the UI thread.</para>
/// </summary>
public sealed class NeutralRecenterFilter
{
    private double _neutralCx = 0.5;
    private double _neutralCy = 0.5;
    private bool _seeded;

    /// <summary>The captured resting baseline (test/diagnostic seam).</summary>
    public double NeutralCx => _neutralCx;
    public double NeutralCy => _neutralCy;

    /// <summary>Map a raw pose to a recentred pose. <paramref name="valid"/>=false (face lost)
    /// passes the input through unchanged (the pump already targets centre on loss). The neutral is
    /// captured once, at the first valid pose, and never moves again. The output is clamped to
    /// [0,1]; the downstream follow-mode clamp still applies. <paramref name="dtSeconds"/> is kept
    /// for signature stability with the per-frame pump (no longer used).</summary>
    public (double cx, double cy) Recenter(double rawCx, double rawCy, double dtSeconds, bool valid)
    {
        if (!valid)
            return (rawCx, rawCy);

        // Capture the user's neutral ONCE so the hole starts centred and a constant posture/
        // camera-yaw offset is cancelled (no "shift and lock" on detection).
        if (!_seeded)
        {
            _neutralCx = rawCx;
            _neutralCy = rawCy;
            _seeded = true;
        }

        // Feed the DEVIATION from neutral, 1:1 — no continuous learning, so a held off-centre gaze
        // stays put (it never drifts back toward centre).
        return (Clamp01(0.5 + (rawCx - _neutralCx)), Clamp01(0.5 + (rawCy - _neutralCy)));
    }

    private static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
}
