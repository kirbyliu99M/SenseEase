using System;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

/// <summary>
/// Beta-1 D-18/D-19: the 5★ comfort rating ↔ FMS 0-20 mapping, and the builder that turns a
/// rating into a MEASURED <see cref="SymptomEvent"/> (a patient-reported outcome, PRO). The
/// main-console rating is Teams-post-call style — 1★ = very uncomfortable … 5★ = very
/// comfortable — and is the honest per-user signal that REPLACES the retired population "70%"
/// claim (D-18): from it the symptom log computes a within-subject protection ON-vs-OFF delta
/// and a comfort trend, never a population promise.
///
/// <para>D-19: the internal scale is FMS 0-20 (Keshavarz &amp; Hecht 2011 single-item, 0 = no
/// symptoms … 20 = severe). Comfort is the INVERSE of discomfort, so MORE stars ⇒ LOWER FMS.
/// The five steps are evenly spaced across the 0-20 range — 5 FMS points per star — and each
/// star is symptom-anchored (the console shows the anchor text; the numeric mapping lives here
/// so the widget and the unit tests share one definition):</para>
/// <list type="bullet">
///   <item>5★ → FMS 0  — no symptoms, fully comfortable</item>
///   <item>4★ → FMS 5  — barely any discomfort</item>
///   <item>3★ → FMS 10 — mild discomfort</item>
///   <item>2★ → FMS 15 — clear discomfort</item>
///   <item>1★ → FMS 20 — strong symptoms</item>
/// </list>
/// <para>Pure + static — no I/O, no engine state. The producer (the console widget, via
/// <c>MainViewModel.RecordComfortRating</c>) and the tests both call this.</para>
/// </summary>
public static class ComfortRatingScale
{
    public const int MinStars = 1;
    public const int MaxStars = 5;
    public const int FmsMin = 0;
    public const int FmsMax = 20;

    /// <summary>Map a 1-5 comfort star rating to FMS 0-20 (discomfort): 5★→0, 4★→5, 3★→10,
    /// 2★→15, 1★→20. Throws <see cref="ArgumentOutOfRangeException"/> for stars outside 1-5 so a
    /// miswired caller fails loud rather than silently recording a garbage clinical PRO.</summary>
    public static int StarsToFms(int stars)
    {
        if (stars < MinStars || stars > MaxStars)
            throw new ArgumentOutOfRangeException(
                nameof(stars), stars, "Comfort rating must be 1-5 stars.");
        return (MaxStars - stars) * 5; // 5★→0 … 1★→20, evenly spaced across 0-20
    }

    /// <summary>Assemble a MEASURED FMS report (<see cref="SymptomEvent.Kinds.FmsReport"/>,
    /// <see cref="SymptomEvent.Measured"/> = true, source = stars) from a star rating plus the
    /// LIVE capture context. The context — <paramref name="contextTier"/> (engine VIMS tier),
    /// <paramref name="maskActive"/> (protection ON vs OFF), and <paramref name="foregroundKey"/>
    /// (the coarse on-device app key, null unless per-app consent is on, D-16) — is what makes the
    /// matched-tier ON-vs-OFF delta and the Theme-2 per-app join computable. <paramref name="trigger"/>
    /// defaults to <see cref="SymptomEvent.Triggers.Manual"/> when blank. <see cref="SymptomEvent.TsUtc"/>
    /// is left blank for the store to stamp at append time.</summary>
    public static SymptomEvent BuildMeasuredReport(
        int stars, int contextTier, bool maskActive, string? foregroundKey, string trigger)
        => new SymptomEvent
        {
            Kind              = SymptomEvent.Kinds.FmsReport,
            Measured          = true,
            Scale             = SymptomEvent.Scales.Fms20,
            Value             = StarsToFms(stars),
            Source            = SymptomEvent.Sources.Stars,
            ContextTier       = contextTier,
            InterventionState = maskActive
                ? SymptomEvent.InterventionStates.On
                : SymptomEvent.InterventionStates.Off,
            Trigger           = string.IsNullOrWhiteSpace(trigger)
                ? SymptomEvent.Triggers.Manual
                : trigger,
            ForegroundKey     = foregroundKey,
        };
}
