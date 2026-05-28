using System;
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

        // ── Profile switch: zh/en explicit profile name + switch verb ──
        foreach (var (kw, profileName) in ProfileSwitchKeywords)
        {
            if (Contains(text, lower, kw) && MentionsSwitch(text, lower))
            {
                return BuildProposal(
                    OverrideIntent.SwitchProfile,
                    "profile",
                    profileName,
                    $"lexicon: user asked to switch profile to {profileName}");
            }
        }

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

    /// <summary>"Relax / loosen / lower" — direction is reduce sensitivity.</summary>
    private static readonly string[] RelaxKeywords =
    {
        "調鬆", "放鬆", "鬆一點", "鬆點", "放寬", "降低", "減少",
        "loosen", "relax", "lower", "decrease", "reduce", "softer",
    };

    /// <summary>"Tighten / strict / raise" — direction is increase sensitivity.</summary>
    private static readonly string[] TightenKeywords =
    {
        "調緊", "收緊", "緊一點", "緊點", "提高", "增加", "加強",
        "tighten", "strict", "raise", "increase", "stronger", "stricter",
    };

    /// <summary>"Raise" verbs specifically for strike-limit direction.</summary>
    private static readonly string[] RaiseKeywords =
    {
        "提高", "增加", "raise", "increase",
    };

    /// <summary>"Lower" verbs specifically for strike-limit direction.</summary>
    private static readonly string[] LowerKeywords =
    {
        "降低", "減少", "lower", "decrease", "reduce",
    };

    // ── Noun matchers ────────────────────────────────────────────────────────

    private static bool MentionsSensitivity(string text, string lower) =>
        lower.Contains("sensitivity")
        || text.Contains("敏感度")
        || text.Contains("感測度");

    private static bool MentionsSwitch(string text, string lower) =>
        text.Contains("切換")
        || text.Contains("換成")
        || text.Contains("改成")
        || text.Contains("切到")
        || lower.Contains("switch")
        || lower.Contains("change")
        || lower.Contains("set to")
        || lower.Contains("change to");

    private static bool MentionsStrikeLimit(string text, string lower) =>
        lower.Contains("strike limit")
        || lower.Contains("strike count")
        || text.Contains("嚴重事件容忍度")
        || text.Contains("嚴重事件容忍")
        || text.Contains("容忍度")
        || text.Contains("觸發次數");

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
