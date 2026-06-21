using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

/// <summary>
/// Learning-loop Part D, Idea #1 (2026-06-21): the de-identified, REPORT-ONLY "overlay offered, not
/// engaged" metric. A purely MECHANICAL low-engagement indicator derived from the overlay-consent
/// timeline that is <b>already on disk</b> (the <c>kind=overlay_consent</c> rows in symptom_log.jsonl,
/// written by <c>MainViewModel.RecordOverlayConsentDecision</c>): each impactful-tier prompt is an
/// <see cref="SymptomEvent.Decisions.Asked"/> row, and a prompt the user saw but never acted on is an
/// <see cref="SymptomEvent.Decisions.Ignored"/> row (the linger time-out). "Offered, not engaged" =
/// Ignored / Asked.
///
/// <para><b>Safety partition (non-negotiable — see docs/LEARNING_LOOP_PLAN.md §D0).</b> This is NOT and
/// must NEVER become a measured FMS report. The consent rows carry <c>Measured=false</c> /
/// <c>Kind="overlay_consent"</c>, so the relax gate (<c>AdaptationEvidence.MeasuredOnFms</c>, which counts
/// a row only when <c>Measured &amp;&amp; Kind==FmsReport</c>) and the clinical FMS export
/// (<c>ClinicalExportBuilder.Build</c>, which hard-filters to FMS kinds) both ignore them by construction.
/// This aggregator preserves that: it produces COUNTS + a RATE only, feeds NEITHER
/// <c>AdaptationEvidence</c> NOR the gate, and is consumed only by the health report. A non-click can
/// therefore never relax protection. The forbidden mutation a "no-click ⇒ FMS=0" framing would invite —
/// writing a row as <c>Kind=fms_report, Measured=true, Value=0</c> — is exactly what this report-only
/// path avoids.</para>
///
/// <para>De-identification (D-17): the output is pure aggregate counts + a ratio; it carries NO
/// timestamps, utterances, or app names, so it inherits D-17 with zero new leak surface. PURE (no I/O,
/// no clock) so it is fully unit-tested. <b>Naming (ReportClaimGuard):</b> the report copy for this
/// metric must read "overlay offered, not engaged" — NEVER "tolerance"/"tolerated" (the guard bans the
/// <c>toler</c> stem); this is a low-engagement indicator, not a comfort claim.</para>
/// </summary>
public sealed class ConsentEngagementSummary
{
    /// <summary>Count of impactful-tier consent prompts raised (the denominator, Decision="asked").</summary>
    public int AskedCount { get; set; }
    /// <summary>Count of prompts the user accepted (turned the overlay on).</summary>
    public int AcceptedCount { get; set; }
    /// <summary>Count of prompts the user explicitly declined ("Not now").</summary>
    public int DeclinedCount { get; set; }
    /// <summary>Count of prompts shown but never acted on (the linger time-out) — "offered, not engaged".</summary>
    public int IgnoredCount { get; set; }
    /// <summary>Distinct calendar dates (UTC) on which any consent row was recorded — coverage.</summary>
    public int DaysObserved { get; set; }

    /// <summary>"Overlay offered, not engaged" rate = <see cref="IgnoredCount"/> / <see cref="AskedCount"/>,
    /// rounded to 2dp. NULL when no prompt was ever raised (an honest "no data", never a fabricated 0).
    /// A NUMBER, not a claim — the health report interprets it as a low-engagement indicator.</summary>
    public double? OfferedNotEngagedRate { get; set; }
}

/// <summary>
/// Pure aggregator for <see cref="ConsentEngagementSummary"/>. Takes the raw symptom events, keeps only
/// the <c>overlay_consent</c> rows, and returns the de-identified count summary — no I/O, no time
/// dependency. Mirrors <see cref="ClinicalExportBuilder"/>'s pure-builder shape.
/// </summary>
public static class ConsentEngagementBuilder
{
    public static ConsentEngagementSummary Build(IEnumerable<SymptomEvent> events)
    {
        var consent = (events ?? Enumerable.Empty<SymptomEvent>())
            .Where(e => e is not null && e.Kind == SymptomEvent.Kinds.OverlayConsent)
            .ToList();

        var summary = new ConsentEngagementSummary
        {
            AskedCount    = consent.Count(e => e.Decision == SymptomEvent.Decisions.Asked),
            AcceptedCount = consent.Count(e => e.Decision == SymptomEvent.Decisions.Accepted),
            DeclinedCount = consent.Count(e => e.Decision == SymptomEvent.Decisions.Declined),
            IgnoredCount  = consent.Count(e => e.Decision == SymptomEvent.Decisions.Ignored),
            DaysObserved  = consent
                .Select(e => ParseDate(e.TsUtc))
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .Distinct()
                .Count(),
        };

        // Rate is per impactful-tier prompt (the "asked" denominator, locked decision #4). Null when no
        // prompt was raised — an honest gap rather than a 0/0 fabrication.
        if (summary.AskedCount > 0)
            summary.OfferedNotEngagedRate =
                Math.Round((double)summary.IgnoredCount / summary.AskedCount, 2, MidpointRounding.AwayFromZero);

        return summary;
    }

    private static DateTime? ParseDate(string? tsUtc)
    {
        if (string.IsNullOrWhiteSpace(tsUtc)) return null;
        if (DateTime.TryParse(tsUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t))
            return (t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime()).Date;
        return null;
    }
}
