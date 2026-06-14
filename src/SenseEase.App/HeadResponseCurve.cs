using System;

namespace SenseEase.App;

/// <summary>
/// Beta-1 theme-4 fix (C): the head→screen response curve — a centre DEADZONE + EXPONENTIAL
/// gain, applied to the recentred head position (so it is centred on the user's NEUTRAL, after
/// <see cref="NeutralRecenterFilter"/>) before the One-Euro filter.
///
/// <para>Deadzone: small deviations from neutral map to exactly centre, so residual still-head
/// noise produces NO hole movement (Kirby symptom #3, jitter). Expo: beyond the deadzone the
/// gain ramps with an exponent, giving fine control near centre and full reach at the edges
/// (the standard head-as-pointer mapping). Full-deflection reach is PRESERVED (input ±0.5 →
/// output ±0.5), so the deadzone never shrinks the usable range. Pure + static — unit-testable.</para>
/// </summary>
public static class HeadResponseCurve
{
    /// <summary>Shape one axis. <paramref name="v"/> and the result are in [0,1] around the 0.5
    /// neutral. <paramref name="deadzone"/> is a fraction of the half-range [0,1); a magnitude at
    /// or below it returns exactly 0.5. <paramref name="expo"/> &gt; 1 gives fine near-centre
    /// control; ≤ 0 (or 1) is linear.</summary>
    public static double Shape(double v, double deadzone, double expo)
    {
        double dev = v - 0.5;
        double sign = dev < 0.0 ? -1.0 : 1.0;
        double mag = Math.Min(1.0, Math.Abs(dev) * 2.0);   // [0,1] fraction of the half-range
        double dz = deadzone < 0.0 ? 0.0 : (deadzone > 0.99 ? 0.99 : deadzone);
        if (mag <= dz) return 0.5;                          // inside the deadzone → no movement
        double t = (mag - dz) / (1.0 - dz);                 // renormalise to [0,1]
        double shaped = expo > 0.0 ? Math.Pow(t, expo) : t; // expo>1 ⇒ fine near centre
        return 0.5 + sign * shaped * 0.5;                   // back to [0,1]; full reach preserved
    }
}
