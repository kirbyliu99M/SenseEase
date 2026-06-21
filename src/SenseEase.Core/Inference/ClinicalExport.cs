using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

/// <summary>
/// Beta-1 Phase 3 / Theme 5 (D-17): the de-identified, aggregate, pure-numeric clinical export.
/// A health professional gets baseline-vs-outcome FMS, the within-subject protection ON-vs-OFF
/// contrast (overall and matched by tier), exposure dose, and per-CATEGORY symptom load — with
/// the measured patient-reported tier (D-18/19) and the estimated tier (D-21) kept RIGOROUSLY
/// SEPARATE and never merged into one number (the non-negotiable across D-17/18/19/21).
///
/// <para>De-identification (D-17): no raw app names, no utterances, no timestamps finer than a
/// calendar date — only numeric aggregates and coarse de-identified category labels leave the
/// device. The raw rows (<c>Exportable=false</c>) never export; this aggregate is the only export
/// path.</para>
/// </summary>
public sealed class ClinicalExport
{
    // v2 (B3): adds OnVsOffByCategory (per-app effectiveness). Additive + back-compatible —
    // an older reader simply ignores the new field; the rest of the schema is unchanged.
    public int SchemaVersion { get; set; } = 2;
    public bool DeIdentified { get; set; } = true;
    public string ExportKind { get; set; } = "aggregate";
    public string Scale { get; set; } = SymptomEvent.Scales.Fms20;
    /// <summary>When the export was generated (ISO-8601 UTC). Stamped by the service, not the
    /// pure builder, so the aggregation stays time-independent and unit-testable.</summary>
    public string? GeneratedUtc { get; set; }

    public ExportWindow Window { get; set; } = new();

    /// <summary>MEASURED patient-reported FMS (the 5★ rating, D-18/19) — never merged with
    /// <see cref="Estimated"/>.</summary>
    public FmsAggregate Measured { get; set; } = new();
    /// <summary>ESTIMATED FMS (the daily NL→FMS estimate, D-21) — presented separately, always
    /// labelled as an estimate.</summary>
    public FmsAggregate Estimated { get; set; } = new();

    /// <summary>The honest replacement for the retired "70%" claim (D-18): the within-subject
    /// MEASURED FMS difference with protection ON vs OFF, overall.</summary>
    public OnOffContrast OnVsOff { get; set; } = new();
    /// <summary>The same ON-vs-OFF contrast computed within each engine tier — the clinically
    /// meaningful comparison, since it controls for the confound that protection-ON correlates
    /// with higher-motion content. One entry per tier that has measured data.</summary>
    public List<TierContrast> OnVsOffByTier { get; set; } = new();

    /// <summary>De-identified per-category MEASURED symptom load (category only — the raw app
    /// name is stripped, D-17).</summary>
    public List<CategoryAggregate> ByCategory { get; set; } = new();

    /// <summary>B3 (output ②): the protection ON-vs-OFF MEASURED FMS contrast WITHIN each app
    /// category — the per-app effectiveness signal. One entry per category that has measured data;
    /// the matched delta is populated only when that category has BOTH an ON and an OFF arm.</summary>
    public List<CategoryContrast> OnVsOffByCategory { get; set; } = new();

    public ExposureDose Exposure { get; set; } = new();

    /// <summary>P1 (health-report redesign §8): de-identified per-DAY trend series — measured FMS mean +
    /// behaviour OFI p90 + mask frequency, date-bucketed + null-gapped. Empty until the service composes
    /// it via <see cref="ClinicalExportBuilder.BuildDailySeries"/> (that needs behavior_log, which
    /// <see cref="ClinicalExportBuilder.Build"/> does not read). Additive — schema stays v2.</summary>
    public List<DailySeriesPoint> DailySeries { get; set; } = new();
}

/// <summary>Observation window — calendar dates only (D-17: no finer-than-date timestamps).</summary>
public sealed class ExportWindow
{
    public string? FirstDateUtc { get; set; }   // yyyy-MM-dd
    public string? LastDateUtc { get; set; }
    public int DaysObserved { get; set; }        // distinct calendar dates with data
    public int TotalDataPoints { get; set; }
}

/// <summary>P1 (health-report redesign §8): one de-identified per-DAY point for the comfort/exposure
/// trend charts. <see cref="Date"/> is the bucket key (D-17: calendar date only, never a finer
/// timestamp). FMS is MEASURED-only (D-18/19 — the estimate is never merged in). Every metric is
/// NULLABLE so a day with no data in a source renders an HONEST GAP, never a fabricated 0. Carries NO
/// category / app field, so it inherits D-17 de-identification with zero new leak surface.</summary>
public sealed class DailySeriesPoint
{
    public string Date { get; set; } = string.Empty;   // yyyy-MM-dd
    /// <summary>Mean MEASURED FMS (0–20) reported that day, or null if none.</summary>
    public double? MeasuredFmsMean { get; set; }
    /// <summary>Count of MEASURED FMS reports that day (the mean's support), or null if none.</summary>
    public int? MeasuredFmsCount { get; set; }
    /// <summary>That day's 90th-percentile optical-flow pressure (behavior_log), or null if no row.</summary>
    public int? OfiP90 { get; set; }
    /// <summary>That day's FOV-mask activation count (behavior_log), or null if no row.</summary>
    public int? MaskActivations { get; set; }
}

/// <summary>Numeric FMS summary for one signal tier (measured OR estimated, never mixed).</summary>
public sealed class FmsAggregate
{
    public int Count { get; set; }
    public double? Mean { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
    /// <summary>Mean of the EARLIEST quartile of samples (time-ordered) — the starting level.</summary>
    public double? BaselineMean { get; set; }
    /// <summary>Mean of the LATEST quartile of samples — the recent level.</summary>
    public double? OutcomeMean { get; set; }
    /// <summary>Outcome − baseline. Negative = discomfort is lower recently than at the start. A
    /// NUMBER, not a claim — the HP interprets it.</summary>
    public double? ChangeFromBaseline { get; set; }
}

/// <summary>Within-subject MEASURED FMS, protection ON vs OFF.</summary>
public sealed class OnOffContrast
{
    public int CountOn { get; set; }
    public int CountOff { get; set; }
    public double? MeanFmsOn { get; set; }
    public double? MeanFmsOff { get; set; }
    /// <summary>MeanOff − MeanOn. Positive = lower discomfort with protection on. Null unless both
    /// arms have data.</summary>
    public double? Delta { get; set; }
}

/// <summary>The ON-vs-OFF contrast within a single engine tier (matched comparison).</summary>
public sealed class TierContrast
{
    public int Tier { get; set; }
    public int CountOn { get; set; }
    public int CountOff { get; set; }
    public double? MeanFmsOn { get; set; }
    public double? MeanFmsOff { get; set; }
    public double? Delta { get; set; }
}

/// <summary>De-identified per-category MEASURED FMS (category label + numeric aggregates only).</summary>
public sealed class CategoryAggregate
{
    public string Category { get; set; } = string.Empty;
    public int Count { get; set; }
    public double? MeanFms { get; set; }
}

/// <summary>B3 (output ②, per-app effectiveness): the within-subject protection ON-vs-OFF MEASURED
/// FMS contrast computed WITHIN a single de-identified app category — "is protection helping in this
/// kind of app?". Same shape + null-unless-both-arms rule as <see cref="TierContrast"/>; the label is
/// the de-identified category only (the raw app name is stripped, D-17).</summary>
public sealed class CategoryContrast
{
    public string Category { get; set; } = string.Empty;
    public int CountOn { get; set; }
    public int CountOff { get; set; }
    public double? MeanFmsOn { get; set; }
    public double? MeanFmsOff { get; set; }
    /// <summary>MeanOff − MeanOn. Positive = lower discomfort with protection on, in THIS category.
    /// Null unless both arms have data (an honest "not enough data yet" rather than a fabricated 0).</summary>
    public double? Delta { get; set; }
}

/// <summary>How much data backs the numbers above — dose/coverage.</summary>
public sealed class ExposureDose
{
    public int TotalSamples { get; set; }
    public int MeasuredSamples { get; set; }
    public int EstimatedSamples { get; set; }
    public int InterventionOnSamples { get; set; }
    public int InterventionOffSamples { get; set; }
    public int InterventionBaselineSamples { get; set; }
    public int DaysObserved { get; set; }
}

/// <summary>
/// Pure aggregator for <see cref="ClinicalExport"/>. Takes the raw symptom events and returns the
/// de-identified numeric summary — no I/O, no time dependency (the service stamps GeneratedUtc),
/// so it is fully unit-tested. Measured and estimated are partitioned up front and never combined.
/// </summary>
public static class ClinicalExportBuilder
{
    /// <summary>P1 (health-report redesign §8): the de-identified per-DAY trend series, beside
    /// <see cref="Build"/>. PURE (no I/O) so it golden-tests with in-memory lists. MEASURED FMS only
    /// (mirrors Build's D-18/19 gate — the estimate never enters); every timestamp bucketed to a UTC
    /// calendar date (D-17); outer-joined with the daily behaviour rows on the date key so each metric
    /// is null where its source lacks that day (honest gap, never a fabricated 0). Reads NO
    /// <c>ForegroundKey</c>, so there is no category/app leak surface. Ascending by date.</summary>
    public static IReadOnlyList<DailySeriesPoint> BuildDailySeries(
        IEnumerable<SymptomEvent> events,
        IEnumerable<DailyBehaviorAggregate> behaviorDays)
    {
        // MEASURED FMS per day: keep only FMS-bearing kinds + Measured (mirrors Build's :163/:167 gate so
        // a non-FMS kind like overlay_consent can't become a phantom FMS=0), bucket to UTC calendar date.
        var fmsByDay = (events ?? Enumerable.Empty<SymptomEvent>())
            .Where(e => e is not null
                        && (e.Kind == SymptomEvent.Kinds.FmsReport || e.Kind == SymptomEvent.Kinds.FmsEstimate)
                        && e.Measured)
            .Select(e => (date: ParseDate(e.TsUtc), e.Value))
            .Where(x => x.date.HasValue)
            .GroupBy(x => x.date!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => (mean: Math.Round(g.Average(x => x.Value), 2), count: g.Count()));

        // behavior_log is already date-keyed numeric-only (one row/day). Last-write-wins on a duplicate
        // date so a malformed second row can't throw.
        var behByDay = new Dictionary<string, DailyBehaviorAggregate>(StringComparer.Ordinal);
        foreach (var b in behaviorDays ?? Enumerable.Empty<DailyBehaviorAggregate>())
            if (b is not null && !string.IsNullOrWhiteSpace(b.Date)) behByDay[b.Date] = b;

        // Outer-join: one point per distinct date in EITHER source, null where absent. yyyy-MM-dd sorts
        // chronologically as ordinal text.
        return fmsByDay.Keys.Union(behByDay.Keys, StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(d =>
            {
                bool hasFms = fmsByDay.TryGetValue(d, out var f);
                bool hasBeh = behByDay.TryGetValue(d, out var b);
                return new DailySeriesPoint
                {
                    Date             = d,
                    MeasuredFmsMean  = hasFms ? f.mean  : (double?)null,
                    MeasuredFmsCount = hasFms ? f.count : (int?)null,
                    OfiP90           = hasBeh ? b!.OfiP90          : (int?)null,
                    MaskActivations  = hasBeh ? b!.MaskActivations : (int?)null,
                };
            })
            .ToList();
    }

    public static ClinicalExport Build(IEnumerable<SymptomEvent> events)
    {
        var all = (events ?? Enumerable.Empty<SymptomEvent>())
            .Where(e => e is not null)
            // B6 Stage 0 (2026-06-21): the clinical export is FMS-only. An overlay_consent row (and any future
            // non-FMS kind) carries Measured=false / Value=0 / Scale="" — without this filter it falls into the
            // ESTIMATED partition (`e => !e.Measured` below) as a phantom FMS=0 report, inflating the estimated
            // count and pulling its mean toward 0, and inflates TotalSamples / Window. Keep only the FMS-bearing
            // kinds so the D-17/18/19 measured-vs-estimated separation stays clean. The gate is already immune
            // (AdaptationEvidence.MeasuredOnFms hard-filters Kind==FmsReport); this protects the export too.
            .Where(e => e.Kind == SymptomEvent.Kinds.FmsReport || e.Kind == SymptomEvent.Kinds.FmsEstimate)
            .OrderBy(e => ParseTsTicks(e.TsUtc))   // oldest-first, robust to unsorted input
            .ToList();

        var measured  = all.Where(e => e.Measured).ToList();
        var estimated = all.Where(e => !e.Measured).ToList();

        var export = new ClinicalExport
        {
            Measured  = Aggregate(measured),
            Estimated = Aggregate(estimated),
            OnVsOff   = BuildOnOff(measured),
            OnVsOffByTier = measured
                .GroupBy(e => e.ContextTier)
                .OrderBy(g => g.Key)
                .Select(g => BuildTierContrast(g.Key, g.ToList()))
                .ToList(),
            ByCategory = BuildByCategory(measured),
            OnVsOffByCategory = BuildByCategoryContrast(measured),
            Exposure   = BuildExposure(all, measured, estimated),
            Window     = BuildWindow(all),
        };
        return export;
    }

    private static FmsAggregate Aggregate(List<SymptomEvent> rows)
    {
        var agg = new FmsAggregate { Count = rows.Count };
        if (rows.Count == 0) return agg;

        var values = rows.Select(r => r.Value).ToList();
        agg.Mean = Round(values.Average());
        agg.Min  = values.Min();
        agg.Max  = values.Max();

        // Baseline = earliest quartile, outcome = latest quartile (each at least one sample).
        int q = Math.Max(1, rows.Count / 4);
        agg.BaselineMean = Round(values.Take(q).Average());
        agg.OutcomeMean  = Round(values.Skip(Math.Max(0, rows.Count - q)).Average());
        agg.ChangeFromBaseline = Round(agg.OutcomeMean.Value - agg.BaselineMean.Value);
        return agg;
    }

    private static OnOffContrast BuildOnOff(List<SymptomEvent> measured)
    {
        var on  = measured.Where(e => e.InterventionState == SymptomEvent.InterventionStates.On).Select(e => e.Value).ToList();
        var off = measured.Where(e => e.InterventionState == SymptomEvent.InterventionStates.Off).Select(e => e.Value).ToList();
        var c = new OnOffContrast
        {
            CountOn  = on.Count,
            CountOff = off.Count,
            MeanFmsOn  = on.Count  > 0 ? Round(on.Average())  : (double?)null,
            MeanFmsOff = off.Count > 0 ? Round(off.Average()) : (double?)null,
        };
        if (c.MeanFmsOn.HasValue && c.MeanFmsOff.HasValue)
            c.Delta = Round(c.MeanFmsOff.Value - c.MeanFmsOn.Value);
        return c;
    }

    private static TierContrast BuildTierContrast(int tier, List<SymptomEvent> rows)
    {
        var on  = rows.Where(e => e.InterventionState == SymptomEvent.InterventionStates.On).Select(e => e.Value).ToList();
        var off = rows.Where(e => e.InterventionState == SymptomEvent.InterventionStates.Off).Select(e => e.Value).ToList();
        var t = new TierContrast
        {
            Tier     = tier,
            CountOn  = on.Count,
            CountOff = off.Count,
            MeanFmsOn  = on.Count  > 0 ? Round(on.Average())  : (double?)null,
            MeanFmsOff = off.Count > 0 ? Round(off.Average()) : (double?)null,
        };
        if (t.MeanFmsOn.HasValue && t.MeanFmsOff.HasValue)
            t.Delta = Round(t.MeanFmsOff.Value - t.MeanFmsOn.Value);
        return t;
    }

    private static List<CategoryAggregate> BuildByCategory(List<SymptomEvent> measured)
        => measured
            .Where(e => !string.IsNullOrWhiteSpace(e.ForegroundKey))
            .GroupBy(e => ExtractCategory(e.ForegroundKey!))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CategoryAggregate
            {
                Category = g.Key,
                Count    = g.Count(),
                MeanFms  = Round(g.Select(e => e.Value).Average()),
            })
            .ToList();

    /// <summary>B3 (output ②): per-category protection ON-vs-OFF contrast. Same matched-arms rule as
    /// <see cref="BuildTierContrast"/> (delta only when both arms exist), grouped by the de-identified
    /// category. Only rows with a foreground key (i.e. captured while per-app consent was on) participate.</summary>
    private static List<CategoryContrast> BuildByCategoryContrast(List<SymptomEvent> measured)
        => measured
            .Where(e => !string.IsNullOrWhiteSpace(e.ForegroundKey))
            .GroupBy(e => ExtractCategory(e.ForegroundKey!))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var rows = g.ToList();
                var on  = rows.Where(e => e.InterventionState == SymptomEvent.InterventionStates.On).Select(e => e.Value).ToList();
                var off = rows.Where(e => e.InterventionState == SymptomEvent.InterventionStates.Off).Select(e => e.Value).ToList();
                var c = new CategoryContrast
                {
                    Category   = g.Key,
                    CountOn    = on.Count,
                    CountOff   = off.Count,
                    MeanFmsOn  = on.Count  > 0 ? Round(on.Average())  : (double?)null,
                    MeanFmsOff = off.Count > 0 ? Round(off.Average()) : (double?)null,
                };
                if (c.MeanFmsOn.HasValue && c.MeanFmsOff.HasValue)
                    c.Delta = Round(c.MeanFmsOff.Value - c.MeanFmsOn.Value);
                return c;
            })
            .ToList();

    private static ExposureDose BuildExposure(List<SymptomEvent> all, List<SymptomEvent> measured, List<SymptomEvent> estimated)
        => new ExposureDose
        {
            TotalSamples     = all.Count,
            MeasuredSamples  = measured.Count,
            EstimatedSamples = estimated.Count,
            InterventionOnSamples       = measured.Count(e => e.InterventionState == SymptomEvent.InterventionStates.On),
            InterventionOffSamples      = measured.Count(e => e.InterventionState == SymptomEvent.InterventionStates.Off),
            InterventionBaselineSamples = measured.Count(e => e.InterventionState == SymptomEvent.InterventionStates.Baseline),
            DaysObserved     = DistinctDays(all),
        };

    private static ExportWindow BuildWindow(List<SymptomEvent> all)
    {
        var win = new ExportWindow { TotalDataPoints = all.Count, DaysObserved = DistinctDays(all) };
        var dates = all.Select(e => ParseDate(e.TsUtc)).Where(d => d.HasValue).Select(d => d!.Value).ToList();
        if (dates.Count > 0)
        {
            win.FirstDateUtc = dates.Min().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            win.LastDateUtc  = dates.Max().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return win;
    }

    // ── De-identification + parsing helpers ──────────────────────────────────

    /// <summary>The de-identified category half of a "category/app" foreground key (D-17 strips
    /// the raw app name). Falls back to the whole token when no delimiter is present.</summary>
    internal static string ExtractCategory(string foregroundKey)
    {
        int slash = foregroundKey.IndexOf('/');
        if (slash > 0) return foregroundKey.Substring(0, slash);
        int colon = foregroundKey.IndexOf(':');   // tolerate the "category:app" doc form too
        if (colon > 0) return foregroundKey.Substring(0, colon);
        return foregroundKey;
    }

    private static int DistinctDays(List<SymptomEvent> rows)
        => rows.Select(e => ParseDate(e.TsUtc)).Where(d => d.HasValue).Select(d => d!.Value).Distinct().Count();

    private static DateTime? ParseDate(string? tsUtc)
    {
        if (TryParseUtc(tsUtc, out DateTime utc)) return utc.Date;
        return null;
    }

    private static long ParseTsTicks(string? tsUtc)
        => TryParseUtc(tsUtc, out DateTime utc) ? utc.Ticks : long.MaxValue; // unparseable sorts last

    private static bool TryParseUtc(string? s, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t))
        {
            utc = t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
            return true;
        }
        return false;
    }

    private static double Round(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
