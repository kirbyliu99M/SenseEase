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
}
