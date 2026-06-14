using System;
using SenseEase.Core.Configuration;

namespace SenseEase.App;

/// <summary>
/// Beta-1 theme-4: velocity-gated NEUTRAL RECENTER for the head-pose pump.
///
/// <para>The sidecar maps ABSOLUTE head yaw to the clear-hole centre
/// (<c>cx = 0.5 + gain·yaw</c>) with no per-user baseline, so an off-axis camera or a habitual
/// seating posture biases the hole by a constant offset — the hole "shifts and locks" the instant
/// a face is detected. This filter cancels that: it learns the user's RESTING head position and
/// feeds the pump the DEVIATION from it, so a habitual/centred head maps to a centred hole and
/// the hole follows movements away from rest.</para>
///
/// <para><b>Velocity-gated (option B):</b> the neutral baseline is nudged toward the current pose
/// ONLY while the head is nearly still (a head-speed EMA below
/// <see cref="SenseDefaults.OverlayNeutralStillSpeed"/>). So a sustained off-centre GAZE — which
/// keeps the head moving/held away from rest — is never cancelled; only genuine resting posture
/// is absorbed. Stateful: call <see cref="Recenter"/> once per pump frame. Pure (no WPF), so it
/// is unit-testable off the UI thread.</para>
/// </summary>
public sealed class NeutralRecenterFilter
{
    private double _neutralCx = 0.5;
    private double _neutralCy = 0.5;
    private double _lastRawCx = double.NaN;
    private double _lastRawCy = double.NaN;
    private double _motionEma; // EMA of per-frame |Δraw| — a head-speed proxy (low ⇒ still)
    private bool _seeded;

    /// <summary>Current learned resting baseline (test/diagnostic seam).</summary>
    public double NeutralCx => _neutralCx;
    public double NeutralCy => _neutralCy;

    /// <summary>Map a raw pose to a recentred pose. <paramref name="valid"/>=false (face lost)
    /// passes the input through unchanged and freezes the baseline (the pump already targets
    /// centre on loss); it also resets motion tracking so a re-acquire is not read as a spike.
    /// The output is clamped to [0,1]; the downstream follow-mode clamp still applies.</summary>
    public (double cx, double cy) Recenter(double rawCx, double rawCy, double dtSeconds, bool valid)
    {
        if (!valid)
        {
            _lastRawCx = double.NaN;
            _lastRawCy = double.NaN;
            return (rawCx, rawCy);
        }
        if (dtSeconds <= 0 || dtSeconds > 1.0) dtSeconds = 0.006;

        // Seed the baseline to the FIRST valid pose so there is no initial offset jump — the
        // hole starts centred and the still-gated learning keeps it honest from there.
        if (!_seeded)
        {
            _neutralCx = rawCx;
            _neutralCy = rawCy;
            _seeded = true;
        }

        // Head-speed proxy: EMA of the per-frame absolute raw delta. Repeated samples (the pump
        // runs ~165 Hz over a ~15 Hz sidecar) contribute 0; a new sample contributes its change.
        // A still head ⇒ near-zero EMA; a moving head ⇒ a sustained higher EMA.
        double delta = (double.IsNaN(_lastRawCx) || double.IsNaN(_lastRawCy))
            ? 0.0
            : Math.Abs(rawCx - _lastRawCx) + Math.Abs(rawCy - _lastRawCy);
        _lastRawCx = rawCx;
        _lastRawCy = rawCy;
        double motionAlpha = Math.Exp(-dtSeconds / SenseDefaults.OverlayNeutralMotionTauSeconds);
        _motionEma = motionAlpha * _motionEma + (1.0 - motionAlpha) * delta;

        // Learn the resting posture ONLY while still (the velocity gate), slowly.
        if (_motionEma < SenseDefaults.OverlayNeutralStillSpeed)
        {
            double learn = 1.0 - Math.Exp(-dtSeconds / SenseDefaults.OverlayNeutralLearnTauSeconds);
            _neutralCx += (rawCx - _neutralCx) * learn;
            _neutralCy += (rawCy - _neutralCy) * learn;
        }

        return (Clamp01(0.5 + (rawCx - _neutralCx)), Clamp01(0.5 + (rawCy - _neutralCy)));
    }

    private static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
}
