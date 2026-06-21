using System;

namespace SenseEase.Core.Inference;

/// <summary>
/// Deterministic guard: is a user message INQUIRING about the user's state (asking the bot whether / how
/// they feel) rather than STATING it? A tester asked <c>"do you think I'm dizzy now?"</c> and a small (3B)
/// model classified it as a current-discomfort REPORT and adjusted the overlay unsolicited — asking ABOUT
/// a symptom is not REPORTING one, and a question must never silently actuate a protection change.
///
/// <para>Deliberately NARROW: it keys on the "asking-the-bot-to-assess" FRAMING ("do you think…",
/// "am I…?", "你覺得…", "是不是…"), NOT on a bare "?" or a sentence-final "嗎" — because a genuine,
/// help-seeking report carries those too ("I'm dizzy, can you help?", "我頭暈，可以幫我嗎？") and must
/// still actuate. Wh-words like "why" are excluded for the same reason ("why am I so dizzy" is a report).
/// The downstream guard fires only when the message was ALSO classified ReportDiscomfort, so a stray
/// match on a non-symptom question (e.g. "is it too dark?") is harmless. Language-agnostic (English +
/// Traditional/Simplified Chinese).</para>
/// </summary>
public static class InterrogativeDetector
{
    // CJK "asking-the-bot" framing markers. A plain discomfort statement ("頭暈", "我想吐") — OR a
    // help-seeking report ("我頭暈，可以幫我嗎") — carries none of these; only a question that frames the
    // bot as the assessor does.
    private static readonly string[] CjkInquiryMarkers =
    {
        "你覺得", "你觉得", "你認為", "你认为", "是不是", "是否", "會不會", "会不会",
    };

    // English inquiry phrases (asking the bot to assess the user's state) — substring, anywhere.
    private static readonly string[] EnglishInquiryPhrases =
    {
        "do you think", "you think i", "do you feel i", "can you tell", "can you sense", "do i seem", "do i look",
    };

    // English inquiry openers — at the START only. A statement / help-seeking report never starts with
    // these ("am I dizzy?", "do I have vertigo?", "did I seem off?").
    private static readonly string[] EnglishInquiryOpeners =
    {
        "am i", "do i", "did i", "was i", "have i",
    };

    /// <summary>True iff the message ASKS about the user's state rather than STATES it. Null/blank ⇒ false.</summary>
    public static bool IsSymptomInquiry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();

        foreach (string m in CjkInquiryMarkers)
            if (t.Contains(m, StringComparison.Ordinal)) return true;

        string lower = t.ToLowerInvariant();
        foreach (string m in EnglishInquiryPhrases)
            if (lower.Contains(m, StringComparison.Ordinal)) return true;
        foreach (string opener in EnglishInquiryOpeners)
            if (lower.StartsWith(opener + " ", StringComparison.Ordinal)) return true;

        return false;
    }

    // English "asking whether I SHOULD do something" openers — at the START only. "should I reset",
    // "do I need to", "shall I" frame a deliberation, not a command — they must never trigger a
    // destructive action (a reset). Distinct from IsSymptomInquiry (which is about bodily state).
    private static readonly string[] EnglishCommandInquiryOpeners =
    {
        "should i", "shall i", "do i need", "do i have to", "ought i", "is it worth", "would it help if i",
    };

    // CJK "should I / do I need to" deliberation markers. 要不要 / 該不該 / 需不需要 are self-contained
    // questions; 需要 / 我應該 need a trailing 嗎 or ? to read as a question (else they are statements).
    private static readonly string[] CjkCommandInquiryMarkers = { "要不要", "該不該", "需不需要", "用不用" };
    private static readonly string[] CjkCommandInquiryStems = { "需要", "我應該", "我该", "我應", "應該要", "该不该" };

    /// <summary>True iff the message ASKS whether the user SHOULD take an action ("should I reset?",
    /// "要不要重設?") rather than COMMANDING it ("reset everything"). Used to stop a deliberation
    /// question from firing a destructive intent (e.g. ResetRequest → ResetPersonalization).
    /// Deliberately narrow + start-anchored so a plain imperative is never caught. Null/blank ⇒ false.</summary>
    public static bool IsCommandInquiry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        string lower = t.ToLowerInvariant();

        foreach (string opener in EnglishCommandInquiryOpeners)
            if (lower.StartsWith(opener + " ", StringComparison.Ordinal)) return true;

        foreach (string m in CjkCommandInquiryMarkers)
            if (t.Contains(m, StringComparison.Ordinal)) return true;

        bool hasQuestionTail = t.Contains('嗎') || t.Contains('?') || t.Contains('？') || t.Contains("吗");
        if (hasQuestionTail)
            foreach (string stem in CjkCommandInquiryStems)
                if (t.Contains(stem, StringComparison.Ordinal)) return true;

        return false;
    }

    // Uncertainty / hedging markers — the user NAMES a symptom but signals DOUBT about it ("I feel dizzy? I
    // don't know", "maybe a bit nauseous", "好像有點暈，不確定"). Distinct from IsSymptomInquiry (which asks the
    // BOT to assess): here the user hedges their OWN report. The caller pairs this with a specific-symptom
    // check, so a confident help-seeking report ("I'm dizzy, can you help?" — no hedge marker) still actuates.
    private static readonly string[] EnglishHedgeMarkers =
    {
        "i don't know", "i dont know", "not sure", "i'm not sure", "im not sure", "not certain",
        "maybe", "kind of", "kinda", "sort of", "i guess", "might be", "could be",
    };
    private static readonly string[] CjkHedgeMarkers =
    {
        "好像", "不確定", "不确定", "不知道", "可能", "也許", "也许", "大概", "說不上", "说不上",
    };

    /// <summary>True iff the message HEDGES a self-report — a statement carrying an uncertainty marker
    /// ("I feel dizzy? I don't know", "好像有點暈，不確定"). Pair with a specific-symptom check
    /// (FeedbackLexicon.MentionsSpecificSymptom) so only a *named symptom stated with doubt* triggers a
    /// clarifying question; a confident help-seeking report carries no hedge marker and still actuates.
    /// Null/blank ⇒ false.</summary>
    public static bool IsHedgedSymptom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        string lower = t.ToLowerInvariant();
        foreach (string m in EnglishHedgeMarkers) if (lower.Contains(m, StringComparison.Ordinal)) return true;
        foreach (string m in CjkHedgeMarkers) if (t.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }
}
