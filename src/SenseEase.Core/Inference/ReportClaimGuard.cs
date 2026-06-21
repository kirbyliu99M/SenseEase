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
///   <item>distinctive roots match at the <b>substring</b> level — "toler" catches tolerate/tolerance,
///   "recover" catches recovered/recovery, "symptom" catches symptoms — closing the R6 bodily-state stems
///   that <c>Scrub</c>'s R6 leaves OFF when the intent is empty;</item>
///   <item>stems that also live inside benign words (adapt→adapter, improv, sensiti, resilien) plus the EN
///   paraphrase gaps (progress / better / easier / stronger) match at a <b>word boundary</b>
///   (<see cref="BannedClaimWordsEn"/>) — every inflection still catches, but "adapter"/"adaptive" can't
///   false-fire (R7), and the EN side is now symmetric with the ZH 進步/改善 terms (R2);</item>
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
    /// <summary>EN claim roots + multi-word phrases matched as an ordinal SUBSTRING on the lower-cased
    /// prose — distinctive enough that no benign word contains them ("toler" catches tolerance/tolerated,
    /// "recover" catches recovery/recovered). Superset of the static lint's <c>BannedEnglishFull</c> roots
    /// (build-gate 4). The substring-risky stems + the EN paraphrase gaps live in
    /// <see cref="BannedClaimWordsEn"/> (word-boundary) instead. None of these may appear in the
    /// deterministic fallback templates — a golden test asserts every template passes this guard.</summary>
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
        // ── §8 multi-word claim phrases (distinctive — safe as plain substrings) ──
        "feel better",
        "getting used to",
        // NOTE: the substring-risky stems (improve / adapt / sensit / resilien) + the EN paraphrase gaps
        // moved to BannedClaimWordsEn below, which matches at a WORD BOUNDARY (R7) so "adapter"/"adaptive"
        // can't false-fire while every inflection still catches.
    };

    /// <summary>Claim stems matched at a WORD BOUNDARY (start-of-word), not as bare substrings — so a
    /// coincidental fragment inside a benign word can't fire (R7: "adapt" must not trip "adapter"/"adaptive")
    /// while every inflection still catches (improving/improvement, sensitivity, resilience, progressing).
    /// This list also closes the EN↔ZH asymmetry the audit found (R2): the ZH list already banned 進步/改善,
    /// but the EN side had no "progress" and only the exact phrase "feel better" — so paraphrased improvement
    /// claims ("making progress", "comfort is better", "getting easier", "more resilient") slipped the second
    /// wall. The de-identified posture is mapped to a neutral strength word, so the report never legitimately
    /// needs adapt/sensitive/resilient; rejecting such prose to the template is the safe outcome.</summary>
    private static readonly Regex BannedClaimWordsEn = new(
        @"\b(?:adapt(?!er|ive)|improv|sensiti|resilien|progress|better|easier|easing|stronger)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        // ── R2 EN↔ZH symmetry twins (high-precision improvement phrasings) ────
        "好轉", "變好",
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

        if (BannedClaimWordsEn.IsMatch(prose)) return true;

        foreach (string term in BannedClaimTermsZh)
            if (prose.Contains(term, StringComparison.Ordinal)) return true;

        return MinutesOfClaim.IsMatch(prose);
    }
}
