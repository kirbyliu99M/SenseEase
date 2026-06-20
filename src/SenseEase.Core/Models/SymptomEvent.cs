using System;
using System.Globalization;

namespace SenseEase.Core.Models;

/// <summary>
/// Beta-1 Phase-1 (the shared EventLog foundation, Beta-1.md §2). One structured,
/// append-only record per symptom/comfort data-point, written to
/// <c>%LOCALAPPDATA%\SenseEase\symptom_log.jsonl</c> (one compact snake_case object per line).
/// This is the single schema that carries BOTH the explicit patient-reported FMS self-report
/// (D-18/D-19, the 5★/FMS comfort rating) AND the daily conversation-derived FMS estimate
/// (D-21), kept rigorously separated by <see cref="Measured"/>, plus the shared envelope
/// (<see cref="ContextTier"/>, <see cref="InterventionState"/>, <see cref="ForegroundKey"/>)
/// that lets Theme-2 per-app data join to Theme-5 outcomes ("video-type apps push this user to
/// FMS-moderate; mask ON cut it X→Y") and makes the per-user protection ON-vs-OFF delta
/// computable (the honest replacement for the retired "70%" claim, D-18).
///
/// <para>Privacy: numeric/categorical only — no transcript text, no raw window titles. Stores a
/// COARSE foreground key (e.g. "category:video" or a per-install hashed app key), never content.
/// Carries the sprint-locked PII annotations <see cref="Exportable"/>=false and
/// <see cref="DeletableOnClear"/>=true; the file lives in the %LOCALAPPDATA%\SenseEase home so
/// H24 "clear all my data" + H25 uninstall already cover it.</para>
/// </summary>
public sealed class SymptomEvent
{
    public int SchemaVersion { get; set; } = 1;
    public bool Exportable { get; set; } = false;
    public bool DeletableOnClear { get; set; } = true;

    /// <summary>ISO-8601 UTC timestamp of the data-point (round-trippable "o" format).</summary>
    public string TsUtc { get; set; } = string.Empty;

    /// <summary>Record discriminator: see <see cref="Kinds"/>.
    /// "fms_report" = explicit self-report (PRO); "fms_estimate" = derived estimate.</summary>
    public string Kind { get; set; } = Kinds.FmsReport;

    /// <summary>THE measured-vs-estimated separation (D-17/18/19/21 non-negotiable). True only
    /// for a patient-reported outcome (the 5★/FMS self-report); false for any sensor/NL-derived
    /// estimate. Export and any reduction claim present measured and estimated SEPARATELY.</summary>
    public bool Measured { get; set; }

    /// <summary>Rating scale identifier. D-19: the internal scale is FMS 0-20 ("fms20").</summary>
    public string Scale { get; set; } = Scales.Fms20;

    /// <summary>The discomfort value on <see cref="Scale"/> (FMS: 0 = none … 20 = severe).</summary>
    public int Value { get; set; }

    /// <summary>Where the value came from: see <see cref="Sources"/> ("slider" = the 0-20 FMS
    /// console rating; "stars" = the legacy 5★ rating mapped to FMS 0-20; "nl_daily" = the daily
    /// conversation-derived estimate).</summary>
    public string Source { get; set; } = Sources.Stars;

    /// <summary>The engine VIMS tier (1-12) in effect when the data-point was captured — the
    /// shared key that lets matched-tier ON-vs-OFF comparisons be made.</summary>
    public int ContextTier { get; set; }

    /// <summary>Protection state at capture: see <see cref="InterventionStates"/>
    /// ("on" | "off" | "baseline"). The basis of the within-subject ON-vs-OFF delta.</summary>
    public string InterventionState { get; set; } = InterventionStates.On;

    /// <summary>What prompted the sample: see <see cref="Triggers"/>
    /// ("spike" | "idle" | "session_end" | "manual"). "" for an estimate.</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>COARSE foreground-app key at capture (Theme-2 join), e.g. "category:video" or a
    /// per-install hashed key. Null until Theme-2 foreground capture is wired. NEVER a raw
    /// window title.</summary>
    public string? ForegroundKey { get; set; }

    /// <summary>Overlay-consent decision for a <c>kind=overlay_consent</c> row: see
    /// <see cref="Decisions"/> ("asked" | "accepted" | "declined" | "ignored"). Null on every FMS row,
    /// so a reader filters the consent timeline by <see cref="Kind"/>.</summary>
    public string? Decision { get; set; }

    // ── Value catalogs (string-valued for forward-compatible JSONL) ───────────
    public static class Kinds { public const string FmsReport = "fms_report"; public const string FmsEstimate = "fms_estimate"; public const string OverlayConsent = "overlay_consent"; }
    /// <summary>Overlay-consent decision discriminator (the <see cref="Decision"/> field).</summary>
    public static class Decisions { public const string Asked = "asked"; public const string Accepted = "accepted"; public const string Declined = "declined"; public const string Ignored = "ignored"; }
    public static class Scales { public const string Fms20 = "fms20"; }
    // "stars" is the legacy 5★ console widget (kept for back-compat reads of older logs + the
    // pinned ComfortRatingScaleTests); "slider" is the current 0-20 FMS console capture (D-redesign).
    public static class Sources { public const string Stars = "stars"; public const string NlDaily = "nl_daily"; public const string Slider = "slider"; }
    public static class InterventionStates { public const string On = "on"; public const string Off = "off"; public const string Baseline = "baseline"; }
    public static class Triggers { public const string Spike = "spike"; public const string Idle = "idle"; public const string SessionEnd = "session_end"; public const string Manual = "manual"; }

    /// <summary>Build an overlay-consent audit row (<c>kind=overlay_consent</c>). It carries the live
    /// context — the VIMS tier and protection ON/OFF at the moment of the decision — so the consent
    /// timeline joins to the same tier key as the FMS reports. It is NOT a measured or estimated value,
    /// so <see cref="Value"/>/<see cref="Scale"/>/<see cref="Source"/> are left empty.
    /// <paramref name="utcNow"/> is injected so the assembly is deterministically unit-testable.</summary>
    public static SymptomEvent OverlayConsentRow(string decision, int contextTier, bool maskActive, DateTime utcNow)
        => new()
        {
            TsUtc = utcNow.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            Kind = Kinds.OverlayConsent,
            Decision = decision,
            Measured = false,
            Scale = string.Empty,
            Source = string.Empty,
            Value = 0,
            ContextTier = contextTier,
            InterventionState = maskActive ? InterventionStates.On : InterventionStates.Off,
            Trigger = string.Empty,
        };
}
