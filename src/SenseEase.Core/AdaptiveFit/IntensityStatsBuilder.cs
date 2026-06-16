using System;
using System.Collections.Generic;
using System.Linq;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;

namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// Pure, deterministic builder that distils a single user's durable event log into a
/// <see cref="UserIntensityStats"/> snapshot. NET-NEW (explicitly NOT
/// HabituationTrajectoryBuilder): this is the per-user stats pipeline feeding the
/// Adaptive Fit selector (Group C).
///
/// <para>No I/O, no engine wiring, no flag-on hot path. Single-user only (the entries
/// passed in are this user's own log) — NOT collaborative filtering. All math is
/// computed in one pass over a materialised, time-filtered copy of the input.</para>
/// </summary>
public static class IntensityStatsBuilder
{
    /// <summary>Substring that marks a durable entry as a "spike" recurrence event.
    /// Matches the engine's <c>Habituation.Spike</c> category (and any future
    /// <c>*.Spike</c> category) case-insensitively.</summary>
    private const string SpikeCategoryMarker = "Spike";

    /// <summary>Floor on the window length (hours) used for the per-hour spike rate,
    /// so a burst of entries inside a few seconds can't blow the rate up to infinity.</summary>
    private const double MinWindowHours = 1.0 / 60.0; // 1 minute

    /// <summary>
    /// Build the rolling intensity profile from <paramref name="durableEntries"/>.
    /// </summary>
    /// <param name="durableEntries">This user's durable log slice (any order; may be empty/null).</param>
    /// <param name="now">The reference "now" used to bound the spike-rate window.
    /// Spans are measured from the earliest entry to this instant.</param>
    /// <param name="triggerPressure">The effective trigger pressure used to normalise
    /// each entry's Pressure into the ~[0..1+] fraction the selector expects. Non-positive
    /// values fall back to <see cref="SenseDefaults.TriggerPressure"/> to avoid div-by-zero.</param>
    /// <param name="stablePressureFraction">The calm cut-off (normalised-pressure below
    /// this counts as calm). Defaults to <see cref="SenseDefaults.HabituationStablePressureFraction"/>.</param>
    /// <param name="fmsSelector">Optional per-entry FMS accessor (test / forward-compat). The base
    /// <see cref="EventLogEntry"/> carries no FMS field, so in production this is null and the
    /// measured-FMS aggregate is supplied via <paramref name="fmsMean"/>/<paramref name="fmsCount"/>
    /// instead. Used only when <paramref name="fmsCount"/> is 0.</param>
    /// <param name="fmsMean">Production path (B1): the PRECOMPUTED mean of measured FMS reports
    /// (0..20) over the same window, read from <c>symptom_log.jsonl</c> by the caller (the symptom
    /// store is separate from the event log, so it cannot be joined per-entry here and the builder
    /// stays pure/no-I/O). Ignored unless <paramref name="fmsCount"/> &gt; 0.</param>
    /// <param name="fmsCount">Number of measured FMS reports behind <paramref name="fmsMean"/>;
    /// &gt; 0 selects the precomputed path and is surfaced as
    /// <see cref="UserIntensityStats.FmsSampleCount"/> (the selector's presence gate).</param>
    /// <returns>A populated snapshot, or <see cref="UserIntensityStats.Neutral"/> for empty input.</returns>
    public static UserIntensityStats Build(
        IEnumerable<EventLogEntry> durableEntries,
        DateTime now,
        double triggerPressure,
        double stablePressureFraction = SenseDefaults.HabituationStablePressureFraction,
        Func<EventLogEntry, double?>? fmsSelector = null,
        double fmsMean = 0.0,
        int fmsCount = 0)
    {
        if (durableEntries is null)
            return UserIntensityStats.Neutral;

        // Materialise once; the input may be a lazy/one-shot sequence.
        List<EventLogEntry> entries = durableEntries.ToList();
        if (entries.Count == 0)
            return UserIntensityStats.Neutral;

        // Guard the normaliser: a non-positive trigger would make the fraction useless.
        double trigger = triggerPressure > 0.0 ? triggerPressure : SenseDefaults.TriggerPressure;

        // ── Normalised-pressure samples (Pressure / effective trigger) ─────────────
        double[] normPressures = entries
            .Select(e => e.Pressure / trigger)
            .OrderBy(v => v)
            .ToArray();

        double p50 = Percentile(normPressures, 0.50);
        double p90 = Percentile(normPressures, 0.90);

        // ── Calm ratio: fraction strictly below the stable-pressure fraction ───────
        int calmCount = normPressures.Count(v => v < stablePressureFraction);
        double calmRatio = (double)calmCount / normPressures.Length;

        // ── Spike recurrence per hour ──────────────────────────────────────────────
        int spikeCount = entries.Count(e => IsSpike(e.Category));
        double windowHours = WindowHours(entries, now);
        double spikesPerHour = spikeCount / windowHours;

        // ── FMS aggregate (measured discomfort) ────────────────────────────────────
        // Production: the caller injects a precomputed mean+count from symptom_log.jsonl
        // (separate store ⇒ not joinable per-entry; keeps the builder pure). Otherwise fall
        // back to the per-entry fmsSelector (test / forward-compat). FmsSampleCount > 0 is the
        // selector's presence gate (so "no reports" carries no FMS bias).
        double fmsAggregate = 0.0;
        int fmsSampleCount = 0;
        if (fmsCount > 0)
        {
            fmsAggregate = fmsMean;
            fmsSampleCount = fmsCount;
        }
        else if (fmsSelector is not null)
        {
            double sum = 0.0;
            int n = 0;
            foreach (EventLogEntry e in entries)
            {
                double? fms = fmsSelector(e);
                if (fms.HasValue)
                {
                    sum += fms.Value;
                    n++;
                }
            }
            if (n > 0)
            {
                fmsAggregate = sum / n;
                fmsSampleCount = n;
            }
        }

        return new UserIntensityStats(
            PressureP50: p50,
            PressureP90: p90,
            SpikeRecurrencePerHour: spikesPerHour,
            CalmRatio: calmRatio,
            FmsAggregate: fmsAggregate,
            FmsSampleCount: fmsSampleCount,
            SampleCount: entries.Count);
    }

    /// <summary>True iff the category marks a spike-recurrence event (e.g.
    /// <c>Habituation.Spike</c>). Case-insensitive substring match so future
    /// <c>*.Spike</c> categories are picked up automatically.</summary>
    private static bool IsSpike(string? category) =>
        !string.IsNullOrEmpty(category)
        && category.IndexOf(SpikeCategoryMarker, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The window length in hours used for the per-hour spike rate: from the
    /// earliest entry timestamp to <paramref name="now"/>, floored at
    /// <see cref="MinWindowHours"/> so a tight burst can't divide by ~0.</summary>
    private static double WindowHours(IReadOnlyList<EventLogEntry> entries, DateTime now)
    {
        DateTime earliest = entries[0].Timestamp;
        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Timestamp < earliest)
                earliest = entries[i].Timestamp;
        }

        double hours = (now - earliest).TotalHours;
        return hours > MinWindowHours ? hours : MinWindowHours;
    }

    /// <summary>Linear-interpolated percentile (the "C = 1" / Excel PERCENTILE.INC
    /// method) over an ALREADY-SORTED ascending array. Deterministic; a single sample
    /// returns that sample; <paramref name="q"/> is clamped to [0,1].</summary>
    private static double Percentile(double[] sortedAscending, double q)
    {
        int n = sortedAscending.Length;
        if (n == 0) return 0.0;
        if (n == 1) return sortedAscending[0];

        if (q <= 0.0) return sortedAscending[0];
        if (q >= 1.0) return sortedAscending[n - 1];

        // Rank in [0, n-1]; split into integer index + fractional weight.
        double rank = q * (n - 1);
        int lo = (int)Math.Floor(rank);
        int hi = lo + 1;
        double frac = rank - lo;

        if (hi >= n) return sortedAscending[n - 1];
        return sortedAscending[lo] + frac * (sortedAscending[hi] - sortedAscending[lo]);
    }
}
