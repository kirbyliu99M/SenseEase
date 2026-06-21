using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SenseEase.Core.Inference;

/// <summary>
/// Health-report redesign §8 (Q3): the runtime medical-overreach wall for the LLM-authored report
/// narrative — the SECOND, independent line of defence after <c>ReplyComplianceScrubber.Scrub</c>.
/// The §6 red line: copy describes the <b>app's behaviour</b>, never the <b>user's clinical
/// improvement</b> (no therapy / rehab / tolerance / desensitization / recovery / adaptation wording).
///
/// <para>This guard scans NON-DETERMINISTIC LLM prose, so its vocabulary deliberately EXCEEDS the
/// static <c>BannedPhraseLintTests</c> list (which only had to police hand-authored copy):
/// <list type="bullet">
///   <item>it matches at the <b>stem</b> level — "toler" catches tolerate/tolerated/tolerance,
///   "sensit" catches desensitize/sensitivity, "resilien" catches resilient/resilience, "recover"
///   catches recovered/recovery, "adapt" catches adaptation/adapting, "improve" catches
///   improving/improvement — closing the R6 bodily-state stems that <c>Scrub</c>'s R6 leaves OFF when
///   the intent is empty;</item>
///   <item>it runs <b>both</b> the EN and ZH lists on EVERY section regardless of the UI language pin
///   (the EN↔ZH pin is a known-unreliable failure mode on a temperature-0.4 model).</item>
/// </list>
/// A single hit rejects the WHOLE section; the caller then emits that section's deterministic fallback.</para>
///
/// <para>Pure Core (no I/O, never throws) so it is the single source of the banned-claim vocabulary:
/// <c>BannedPhraseLintTests</c> references <see cref="BannedClaimStemsEn"/> / <see cref="BannedClaimTermsZh"/>
/// and asserts the guard can never drift WEAKER than the static lint (anti-drift — build-gate 4).</para>
/// </summary>
public static class ReportClaimGuard
{
    /// <summary>EN claim STEMS (lower-case, ordinal substring match on the lower-cased prose). Stem-level
    /// on purpose so an inflected form ("tolerated", "improving", "adapting", "recovered") is caught, not
    /// only the exact noun. Superset of the static lint's <c>BannedEnglishFull</c> roots (build-gate 4)
    /// PLUS the §8-signed-off expansion (improve / feel better / toler / adapt / sensit / resilien /
    /// recover / getting used to). None of these may appear in the deterministic fallback templates — a
    /// golden test asserts every template passes this guard.</summary>
    public static readonly IReadOnlyList<string> BannedClaimStemsEn = new[]
    {
        // ── lint roots (this guard must be a superset) ───────────────────────
        "toler",        // ⊇ tolerance / tolerated
        "desensiti",    // ⊇ desensitize / desensitisation
        "neuroplastic",
        "rehab",
        "therap",       // ⊇ therapy / therapeutic / therapist
        "recover",      // ⊇ recovery / recovered (bare, per §8)
        "absorbed",
        "prevented",
        "cured",
        "symptom",
        "reduced your", "less sick", "needing this less", "since you started", "motion you didn't",
        // ── §8 signed-off expansion (adversarial-LLM-prose stems) ────────────
        "improve",      // ⊇ improving / improvement
        "feel better",
        "adapt",        // ⊇ adaptation / adapting
        "sensit",       // ⊇ sensitivity / sensitised (the de-identified posture label is mapped to a
                        //    neutral strength word so the report never emits "sensitive")
        "resilien",     // ⊇ resilient / resilience (same — never emitted raw)
        "getting used to",
    };

    /// <summary>繁中 banned claim terms (ordinal substring match on the raw prose). Superset of the lint's
    /// <c>BannedChinese</c> PLUS the §8 expansion (適應 / 改善 / 進步 / 習慣了 / 減輕 / 減少). The ZH
    /// templates avoid these (e.g. they phrase a trend with 較先前為低 rather than 減少).</summary>
    public static readonly IReadOnlyList<string> BannedClaimTermsZh = new[]
    {
        // ── lint terms ───────────────────────────────────────────────────────
        "耐受", "容忍度", "去敏感", "神經可塑", "復健", "治療",
        "痊癒", "康復", "症狀", "減少了你", "不再需要",
        // ── §8 signed-off expansion ──────────────────────────────────────────
        "適應", "改善", "進步", "習慣了", "減輕", "減少",
    };

    // The lint's duration-of-use claim ("30 minutes of …" implies therapeutic dosing over time). Mirrored
    // here so the guard is a superset of the lint's regex rule too; the templates state raw counts/days
    // instead, never "N minutes of".
    private static readonly Regex MinutesOfClaim =
        new(@"\d+\s*minutes?\s+of\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when <paramref name="prose"/> contains ANY banned medical-overreach claim, scanning
    /// the EN stems AND the ZH terms AND the minutes-of regex — all three, every call, regardless of the
    /// section's language. Pure; null/blank → false; never throws.</summary>
    public static bool ContainsBannedClaim(string? prose)
    {
        if (string.IsNullOrWhiteSpace(prose)) return false;

        string lower = prose.ToLowerInvariant();
        foreach (string stem in BannedClaimStemsEn)
            if (lower.Contains(stem, StringComparison.Ordinal)) return true;

        foreach (string term in BannedClaimTermsZh)
            if (prose.Contains(term, StringComparison.Ordinal)) return true;

        return MinutesOfClaim.IsMatch(prose);
    }
}
