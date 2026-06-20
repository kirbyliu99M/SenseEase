namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// A single user's own rolling intensity profile, distilled from their durable
/// event log over a bounded window. This is the SHARED stats type that
/// <see cref="IntensityStatsBuilder"/> produces (Group B) and the Adaptive Fit
/// selector consumes (Group C) — identical field names on both sides.
///
/// <para>Fully local, single-user telemetry — NOT collaborative filtering: every
/// field is derived only from this user's own log. Pure data; no behaviour, no I/O.
/// All ratios are unitless fractions; pressures are normalised by the effective
/// trigger so they are comparable across calibration scales.</para>
///
/// <para>Reachable on the hot path ONLY when the Adaptive Fit selector flag is on
/// (default TRUE since c1461a9); this type itself is inert Core data with no engine wiring.</para>
/// </summary>
/// <param name="PressureP50">
/// Median of (Pressure / EffectiveTrigger) over the window, ~[0..1+]. RESERVED — computed but never
/// SCORED by <c>DeterministicSetSelector</c> (which keys on the P90 tail); kept for parity/future use.
/// See docs/B6 §3 (orphan ledger).</param>
/// <param name="PressureP90">
/// 90th percentile of the same normalised-pressure samples.</param>
/// <param name="SpikeRecurrencePerHour">
/// Spikes/hour from the durable log (spike-category entries ÷ window hours).</param>
/// <param name="CalmRatio">
/// Fraction of samples below the stable-pressure fraction, [0..1].</param>
/// <param name="FmsAggregate">
/// Rolling mean of any measured FMS value (0..20) over the window; 0 when none present.</param>
/// <param name="FmsSampleCount">
/// Count of measured FMS reports in the window. The selector applies its FMS term ONLY when
/// this is &gt; 0, so a window with no self-reports (or the Neutral profile) carries no FMS bias —
/// distinguishing "no data" from a genuine all-zero ("feeling fine") report set.</param>
/// <param name="SampleCount">
/// Window sample count (drives the min-samples confidence gate).</param>
public sealed record UserIntensityStats(
    double PressureP50,
    double PressureP90,
    double SpikeRecurrencePerHour,
    double CalmRatio,
    double FmsAggregate,
    int    FmsSampleCount,
    int    SampleCount)
{
    /// <summary>The safe, neutral profile used when there is no data: all-zero,
    /// zero samples. Callers gate on <see cref="SampleCount"/> to detect this.</summary>
    public static UserIntensityStats Neutral { get; } =
        new(PressureP50: 0.0,
            PressureP90: 0.0,
            SpikeRecurrencePerHour: 0.0,
            CalmRatio: 0.0,
            FmsAggregate: 0.0,
            FmsSampleCount: 0,
            SampleCount: 0);
}
