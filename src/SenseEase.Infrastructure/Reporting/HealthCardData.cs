using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Reporting;

/// <summary>VIMS condition summary worded for the in-app health card — NEVER an alarm ramp (this is a comfort
/// tool, not antivirus): Stable (teal) · Moderate (amber) · Severe (deep indigo). Unknown = not enough data.</summary>
public enum VimsCondition { Unknown, Stable, Moderate, Severe }

/// <summary>Health-report CARD model (Kirby 2026-06-26): the in-app, one-page replacement for the old
/// file-generating HTML/JSON report. Three hero numbers + a worded condition badge + a daily-protection trend,
/// all computed in memory from the data already on disk — NO file is ever written. De-identified by
/// construction: it carries only numeric aggregates + a coarse condition word (no app names, no utterances, no
/// finer-than-date timestamps), inheriting <see cref="ClinicalExportBuilder"/>'s D-17 posture.</summary>
public sealed record HealthCardData(
    int TimesProtected,
    int SymptomsShielded,
    VimsCondition Condition,
    IReadOnlyList<int> TrendDailyProtection,
    int DaysObserved)
{
    public static HealthCardData Empty { get; } =
        new(0, 0, VimsCondition.Unknown, Array.Empty<int>(), 0);
}

/// <summary>Pure aggregator for <see cref="HealthCardData"/> — no I/O, no clock dependency beyond the passed
/// <paramref name="nowUtc"/>, so it golden-tests with in-memory lists (mirrors <see cref="ClinicalExportBuilder"/>).
/// The App reads the symptom + behaviour stores and the profile snapshot, then calls this; the card window
/// just renders the result. Replaces the report's HTML+JSON file emission entirely.</summary>
public static class HealthCardDataBuilder
{
    /// <param name="events">All symptom-log rows (FMS reports/estimates + overlay_consent); filtered to the window here.</param>
    /// <param name="behaviorDays">The daily behaviour aggregates (one row/day; carries MaskActivations + OfiP90).</param>
    /// <param name="profile">Raw habituation profile string (RESILIENT / TYPICAL / SENSITIVE), or null.</param>
    /// <param name="currentVimsTier">The profile snapshot's current tier (1–12), or null when no profile.</param>
    public static HealthCardData Build(
        IEnumerable<SymptomEvent>? events,
        IEnumerable<DailyBehaviorAggregate>? behaviorDays,
        string? profile,
        int? currentVimsTier,
        DateTime nowUtc,
        int windowDays = 30)
    {
        string sinceDate = nowUtc.ToUniversalTime().AddDays(-windowDays)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var evWindow = (events ?? Enumerable.Empty<SymptomEvent>())
            .Where(e => e is not null && OnOrAfterDate(e!.TsUtc, sinceDate))
            .ToList();
        var behWindow = (behaviorDays ?? Enumerable.Empty<DailyBehaviorAggregate>())
            .Where(b => b is not null && !string.IsNullOrWhiteSpace(b!.Date)
                        && string.Compare(b.Date, sinceDate, StringComparison.Ordinal) >= 0)
            .ToList();

        // The de-identified per-day series (MEASURED FMS + behaviour mask/OFI), reused as-is.
        var series = ClinicalExportBuilder.BuildDailySeries(evWindow, behWindow);

        // (1) Times we protected you = total FOV-mask activations across the window (behaviour_log).
        int timesProtected = series.Sum(p => p.MaskActivations ?? 0);

        // (2) Possible symptoms shielded = the impactful share of those protections, ESTIMATED against the
        // comfort profile (a more sensitive user is helped on a larger fraction of engagements). This is an
        // honest estimate, not a measurement — surfaced as "possible". Weight per coarse profile strength.
        double shieldWeight = (profile ?? "").ToUpperInvariant() switch
        {
            "SENSITIVE" => 0.65,
            "RESILIENT" => 0.25,
            _           => 0.45,   // TYPICAL / unknown
        };
        int symptomsShielded = (int)Math.Round(timesProtected * shieldWeight, MidpointRounding.AwayFromZero);

        // (3) VIMS condition: prefer the recent MEASURED FMS level (0–20, D-18/19), else the profile tier
        // (1–12). Calm wording only — Stable / Moderate / Severe (never a red alarm).
        var export = ClinicalExportBuilder.Build(evWindow);
        var condition = MapCondition(export.Measured, currentVimsTier);

        var trend = series.Select(p => p.MaskActivations ?? 0).ToList();
        int days = export.Window.DaysObserved;
        return new HealthCardData(timesProtected, symptomsShielded, condition, trend, days);
    }

    /// <summary>Map to a calm condition word. Uses the recent FMS OUTCOME mean when there is measured data
    /// (the latest-quartile level), nudged gentler when the trend is improving; falls back to the profile
    /// tier; Unknown when neither exists (the card then shows "Not enough data yet" rather than a fake state).</summary>
    private static VimsCondition MapCondition(FmsAggregate measured, int? currentTier)
    {
        if (measured.Count > 0 && measured.OutcomeMean is { } recent)
        {
            // Improving (outcome below baseline) softens the read by half a band.
            double adj = measured.ChangeFromBaseline is { } chg && chg < 0 ? recent - 1.0 : recent;
            if (adj < 4.0) return VimsCondition.Stable;     // FMS 0–20
            if (adj < 9.0) return VimsCondition.Moderate;
            return VimsCondition.Severe;
        }
        if (currentTier is { } tier && tier > 0)
        {
            if (tier < 4) return VimsCondition.Stable;       // native 1–12
            if (tier < 7) return VimsCondition.Moderate;
            return VimsCondition.Severe;
        }
        return VimsCondition.Unknown;
    }

    private static bool OnOrAfterDate(string? tsUtc, string sinceDate)
    {
        if (string.IsNullOrWhiteSpace(tsUtc)) return true; // fail-open (never silently drop undated data)
        return !DateTime.TryParse(tsUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
               || string.Compare(t.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                 sinceDate, StringComparison.Ordinal) >= 0;
    }
}
