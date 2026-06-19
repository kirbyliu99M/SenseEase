using System;
using System.Collections.Generic;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

// ─────────────────────────────────────────────────────────────────────────────
// M3 reliability layer — deterministic detection of explicit profile-override
// requests from user text. Runs ALONGSIDE the LLM, not in place of it. When
// the user explicitly asks for a sensitivity / profile / strike-limit change
// (a small, finite vocabulary), we build the ProfileOverrideProposal in C#
// directly so the system doesn't depend on Mistral 3B nailing the structured-
// token output every time.
//
// Wiring (ChatbotViewModel.SendAsync):
//   1. Send the user's message to the LLM as normal (the chatbot's natural-
//      language reply lives there)
//   2. Run HabituationOverrideParser on the LLM's raw output (Phase 7e path)
//   3. If parser returned a Proposal, use it
//   4. ELSE run HabituationProposalLexicon.TryDetect on the user's input —
//      if it matches a known explicit-request pattern, synthesize the
//      proposal deterministically
//
// The lexicon DOES NOT shadow the LLM's reply — only the structured-token
// extraction. User still sees the LLM's conversational response in the chat
// bubble; the proposal Card surfaces from whichever path produced a proposal.
//
// Coverage philosophy: explicit requests only. The lexicon doesn't try to
// infer proposal-worthy moments from ambiguous text — that's the LLM's job.
// "感覺好多了" stays an LLM-judgment call; "把 sensitivity 調鬆" is a direct
// request that the lexicon catches.
// ─────────────────────────────────────────────────────────────────────────────

public static class HabituationProposalLexicon
{
    /// <summary>Step size for AdjustSensitivity proposals built by the
    /// lexicon. 0.1 keeps each request as a "small adjustment" — matches the
    /// spec's preference for gradual chatbot-driven changes. Users wanting
    /// bigger jumps go through Settings.</summary>
    public const double SensitivityStepSize = 0.1;

    /// <summary>Step size for AdjustStrikeLimit proposals.</summary>
    public const int StrikeLimitStepSize = 1;

    /// <summary>Default expiry window for lexicon-built proposals
    /// (Q4.3-locked 48 hours).</summary>
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromHours(48);

    /// <summary>M3 7e+: detect a "show me the profiles" / "I want to switch
    /// profile" intent where the user hasn't named a specific profile yet.
    /// Caller surfaces a chat bubble with the 3 profile option cards so the
    /// user can browse + pick interactively. Returns true ONLY for the
    /// picker-request pattern; if the user named a profile, this returns
    /// false and the regular <see cref="TryDetect"/> path takes over.</summary>
    public static bool TryDetectProfilePickerRequest(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return false;
        string text = userText.Trim();
        string lower = text.ToLowerInvariant();

        // Bare-word shortcut: user types just "profile" / "Profile" / "PROFILE"
        // (with optional trailing punctuation/whitespace) → trigger picker
        // without requiring an additional verb. Cheap one-word entry point.
        string stripped = text.TrimEnd('?', '？', '.', '。', '!', '！', ' ', '\t').Trim();
        if (string.Equals(stripped, "profile", StringComparison.OrdinalIgnoreCase))
            return true;

        // If the user named a specific profile, this is NOT a picker request —
        // the regular TryDetect path will build the proposal directly.
        foreach (var (kw, _) in ProfileSwitchKeywords)
        {
            if (Contains(text, lower, kw)) return false;
        }

        // Generic profile-mention vocabulary: zh + en, without naming a
        // specific profile.
        bool mentionsProfile =
            text.Contains("profile")
            || text.Contains("模式")
            || text.Contains("設定檔")
            || text.Contains("等級")
            || lower.Contains("profile");

        if (!mentionsProfile) return false;

        // Verb: ask/show/list/want-to-change indicators.
        return MentionsSwitch(text, lower)
            || text.Contains("看看")
            || text.Contains("查看")
            || text.Contains("有哪些")
            || text.Contains("有什麼")
            || text.Contains("選")
            || lower.Contains("show")
            || lower.Contains("list")
            || lower.Contains("available")
            || lower.Contains("what ")        // covers "what profiles…" / "what are…"
            || lower.Contains("what's")
            || lower.Contains("which")
            || lower.Contains("pick");
    }

    /// <summary>M3 7e+: canonical descriptions for each profile, surfaced as
    /// the option-card subtitle in the chatbot's profile picker. Mirrors the
    /// docstring blurbs on <see cref="HabituationProfileParams.Resilient"/>
    /// / .Typical / .Sensitive — kept here (not in Core) so the wording can
    /// evolve with UX needs without rebuilding engine logic.</summary>
    public static IReadOnlyList<(HabituationProfile profile, string description)> ProfileDescriptions { get; } =
        new[]
        {
            (HabituationProfile.Resilient,
             "為逐步適應族群設計。視覺保護觸發較緩慢、回復較快。適合對視覺刺激較不敏感的使用者。"),
            (HabituationProfile.Typical,
             "一般使用者預設。反應速度與保護強度間取得平衡，適合多數人的日常使用。"),
            (HabituationProfile.Sensitive,
             "為敏感族群設計。視覺保護更早觸發、保持時間更長。適合容易頭暈、疲勞的使用者。"),
        };

    /// <summary>Try to detect an explicit profile-override request in the
    /// user's text and synthesize a proposal. Returns null when the text
    /// doesn't match any known explicit pattern (most of the time —
    /// proposal requests are a small fraction of all chat turns).
    /// <para>Detection order matters: profile-switch checks first
    /// (more specific keywords), then strike-limit, then sensitivity
    /// (broadest pattern — any "loosen/tighten" + "sensitivity-like word").</para></summary>
    public static ProfileOverrideProposal? TryDetect(
        string? userText,
        HabituationProfileSnapshot? currentSnapshot)
    {
        if (string.IsNullOrWhiteSpace(userText)) return null;
        string text = userText.Trim();
        string lower = text.ToLowerInvariant();

        // ── Profile switch: DETECTION REMOVED (CW-3, honesty) ──
        // Profiles are auto-managed; there is no user-facing switcher, so the
        // lexicon fallback must NOT synthesize a SwitchProfile proposal (it would
        // surface a Card promising a switch the host hard-rejects). A bare
        // "I want to switch profile" is instead caught upstream by
        // TryDetectProfilePickerRequest and answered with the honest auto-managed
        // reply. The OverrideIntent.SwitchProfile enum + the processor's
        // defense-in-depth early-reject are kept intact elsewhere.

        // ── Strike limit: "strike limit" / "嚴重事件容忍度" + raise/lower verb ──
        if (MentionsStrikeLimit(text, lower))
        {
            int current = currentSnapshot is null ? 2 : (int)currentSnapshot.SevereStrikeLimit;
            if (MatchesAny(text, lower, RaiseKeywords))
            {
                int proposed = Math.Min(5, current + StrikeLimitStepSize);
                if (proposed != current)
                    return BuildProposal(
                        OverrideIntent.AdjustStrikeLimit,
                        "severe_strike_limit",
                        (long)proposed,
                        $"lexicon: user asked to raise strike limit from {current} to {proposed}");
            }
            else if (MatchesAny(text, lower, LowerKeywords))
            {
                int proposed = Math.Max(1, current - StrikeLimitStepSize);
                if (proposed != current)
                    return BuildProposal(
                        OverrideIntent.AdjustStrikeLimit,
                        "severe_strike_limit",
                        (long)proposed,
                        $"lexicon: user asked to lower strike limit from {current} to {proposed}");
            }
        }

        // ── Sensitivity: relax/tighten verb + sensitivity-like noun ──
        if (MentionsSensitivity(text, lower))
        {
            double current = currentSnapshot?.PersonalPressureScale ?? 1.0;
            if (MatchesAny(text, lower, RelaxKeywords))
            {
                double proposed = Math.Round(
                    Math.Max(HabituationOverrideProcessor.PersonalPressureScaleMin, current - SensitivityStepSize),
                    2);
                if (Math.Abs(proposed - current) > 1e-9)
                    return BuildProposal(
                        OverrideIntent.AdjustSensitivity,
                        "personal_pressure_scale",
                        proposed,
                        $"lexicon: user asked to relax sensitivity from {current:F2} to {proposed:F2}");
            }
            else if (MatchesAny(text, lower, TightenKeywords))
            {
                double proposed = Math.Round(
                    Math.Min(HabituationOverrideProcessor.PersonalPressureScaleMax, current + SensitivityStepSize),
                    2);
                if (Math.Abs(proposed - current) > 1e-9)
                    return BuildProposal(
                        OverrideIntent.AdjustSensitivity,
                        "personal_pressure_scale",
                        proposed,
                        $"lexicon: user asked to tighten sensitivity from {current:F2} to {proposed:F2}");
            }
        }

        return null;
    }

    // ── Keyword lookups ──────────────────────────────────────────────────────

    /// <summary>Profile-name keywords. CJK first (most common in this app),
    /// English second. The matcher does substring + case-insensitive.</summary>
    private static readonly (string keyword, string canonicalProfile)[] ProfileSwitchKeywords =
    {
        ("RESILIENT", "RESILIENT"),
        ("TYPICAL",   "TYPICAL"),
        ("SENSITIVE", "SENSITIVE"),
        ("resilient", "RESILIENT"),
        ("typical",   "TYPICAL"),
        ("sensitive", "SENSITIVE"),
        // zh equivalents — using descriptive phrases since profile names are
        // English-only in the engine.
        ("耐受",     "RESILIENT"),
        ("一般",     "TYPICAL"),
        ("敏感",     "SENSITIVE"),
    };

    /// <summary>"Relax / loosen / lower" — direction is reduce sensitivity. Includes the unambiguous
    /// "too sensitive" complaint phrasings (broadened 2026-06-19 so the deterministic net surfaces a
    /// proposal even when the LLM misses) — these mean "the protection is too sensitive, ease it".</summary>
    private static readonly string[] RelaxKeywords =
    {
        "調鬆", "放鬆", "鬆一點", "鬆點", "放寬", "降低", "減少", "太敏感", "太靈敏",
        "loosen", "relax", "lower", "decrease", "reduce", "softer", "too sensitive", "less sensitive", "overly sensitive",
    };

    /// <summary>"Tighten / strict / raise" — direction is increase sensitivity.</summary>
    private static readonly string[] TightenKeywords =
    {
        "調緊", "收緊", "緊一點", "緊點", "提高", "增加", "加強",
        "tighten", "strict", "raise", "increase", "stronger", "stricter",
    };

    /// <summary>"Raise" verbs specifically for strike-limit direction. Includes the "it pauses me too
    /// often" complaint (broadened 2026-06-19) — wanting FEWER pauses means a HIGHER strike limit.</summary>
    private static readonly string[] RaiseKeywords =
    {
        "提高", "增加", "raise", "increase",
        "pausing me", "keeps pausing", "stop pausing", "pauses too", "一直暫停", "太容易暫停", "動不動就暫停",
    };

    /// <summary>"Lower" verbs specifically for strike-limit direction.</summary>
    private static readonly string[] LowerKeywords =
    {
        "降低", "減少", "lower", "decrease", "reduce",
    };

    // ── Noun matchers ────────────────────────────────────────────────────────

    private static bool MentionsSensitivity(string text, string lower) =>
        lower.Contains("sensitivity")
        || lower.Contains("too sensitive")      // phrase-gated: NOT bare "sensitive" (would catch
        || lower.Contains("less sensitive")     // "sensitive to light" + an unrelated "lower brightness")
        || lower.Contains("overly sensitive")
        || text.Contains("敏感度")
        || text.Contains("太敏感")
        || text.Contains("太靈敏")
        || text.Contains("感測度");

    private static bool MentionsSwitch(string text, string lower) =>
        text.Contains("切換")
        || text.Contains("換成")
        || text.Contains("改成")
        || text.Contains("切到")
        || text.Contains("想換")
        || text.Contains("要換")
        || lower.Contains("switch")
        || lower.Contains("change")
        || lower.Contains("set to")
        || lower.Contains("change to");

    private static bool MentionsStrikeLimit(string text, string lower) =>
        lower.Contains("strike limit")
        || lower.Contains("strike count")
        || lower.Contains("pausing me")        // "it keeps pausing me" → wants a higher strike limit
        || lower.Contains("keeps pausing")
        || lower.Contains("stop pausing")
        || lower.Contains("pauses too")
        || text.Contains("嚴重事件容忍度")
        || text.Contains("嚴重事件容忍")
        || text.Contains("容忍度")
        || text.Contains("觸發次數")
        || text.Contains("一直暫停")
        || text.Contains("太容易暫停")
        || text.Contains("動不動就暫停");

    private static bool Contains(string text, string lower, string keyword)
    {
        // Plain substring match — Trim was already done at entry. CJK
        // text uses substring naturally; English uses case-insensitive via
        // the lower variant.
        if (keyword.Length == 0) return false;
        return text.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || lower.Contains(keyword);
    }

    private static bool MatchesAny(string text, string lower, string[] keywords)
    {
        foreach (var kw in keywords)
            if (Contains(text, lower, kw)) return true;
        return false;
    }

    private static ProfileOverrideProposal BuildProposal(
        OverrideIntent intent, string field, object value, string rationale)
        => new ProfileOverrideProposal(
            Intent: intent,
            TargetField: field,
            ProposedValue: value,
            Rationale: rationale,
            ExpiresAtUtc: DateTime.UtcNow.Add(DefaultExpiry));
}
