using System;
using System.Text;

namespace SenseEase.Core.Safety;

/// <summary>
/// Single source of truth for the clinical self-harm / severe-distress safety
/// floor (Alpha-1 Wave 1 — H1/H2/H3/H4/H8, C3).
///
/// <para>Both the chatbot view-model (the line-1 intercept in
/// <c>ChatbotViewModel.SendAsync</c>) and the two SDK chatbot services
/// (<c>MistralChatbotService</c>, <c>CalibrationChatbotService</c>) call
/// <see cref="Trips"/>, so the phrase vocabulary and crisis-line responses
/// cannot drift between call sites. This replaces the two previously hand-copied
/// floors that had already diverged on a single character (half-width <c>:</c> in
/// Mistral vs full-width <c>：</c> in Calibration) — the C3 duplicate-source-drift
/// pattern. The canonical punctuation is the full-width <c>：</c>.</para>
///
/// <para>Matching normalises with <see cref="NormalizationForm.FormKC"/> +
/// <c>ToLowerInvariant</c> first, so full-width Latin (e.g. <c>ｄｉｅ</c>) and case
/// variants trip too (H3). Tuned to favour false positives over misses: missing a
/// real distress signal is far worse than over-triggering on an idiom.</para>
/// </summary>
public static class SafetyFloor
{
    /// <summary>Placeholder persisted to chat_history in place of a tripped
    /// phrase, so the on-disk timeline stays coherent without retaining the
    /// trigger text (H2). Never persist the raw user text on a trip.</summary>
    public const string RedactedPlaceholder = "[REDACTED: safety_floor]";

    /// <summary>Crisis-line response (Traditional Chinese). Fixed, non-PII —
    /// safe to display, persist, and log. Canonical full-width <c>：</c> (C3).</summary>
    public const string CrisisResponseZh =
        "聽起來你現在很辛苦。SenseEase 不是醫療裝置，無法處理這種狀況。請立即聯繫專業協助：台灣 - 衛福部安心專線 1925；國際 - findahelpline.com。";

    /// <summary>Crisis-line response (English). Fixed, non-PII.</summary>
    public const string CrisisResponseEn =
        "It sounds like you're going through something really hard right now. SenseEase isn't a medical device and can't help with this. Please reach out to a crisis line immediately: US - 988; International - findahelpline.com.";

    /// <summary>Han (Chinese) phrases — Traditional AND Simplified. NFKC does NOT
    /// equate Han variants, so each script must be listed explicitly (H3). The bare
    /// <c>想死</c> (identical in both scripts) subsumes the longer forms but they are
    /// kept for readability.</summary>
    public static readonly string[] PhrasesHan =
    {
        // Traditional
        "自殘", "自殺", "結束生命", "跳下去", "跳樓",
        "不想活了", "不想活下去", "不想再活",
        "想死了", "想去死", "我想死",
        // Simplified equivalents (H3)
        "自残", "自杀", "结束生命", "跳楼",
        // bare 想死 (H3) — same code points in both scripts
        "想死",
    };

    /// <summary>English + youth shorthand. Matched against the NFKC-lowered text (H3).</summary>
    public static readonly string[] PhrasesEn =
    {
        "suicide", "self-harm", "self harm", "selfharm",
        "kill myself", "end it all", "end my life",
        "not want to live", "do not want to live", "don't want to live",
        "want to die", "wanna die",
        // youth shorthand (H3)
        "kys", "kms", "unalive", "unaliving",
        "end myself", "off myself", "no reason to live",
        "don't want to be here", "better off dead", "wish i was dead",
    };

    /// <summary>Detects self-harm / severe-distress phrases. Returns <c>true</c>
    /// and the localised crisis-line response when triggered. The user's CURRENT
    /// message language (Han vs not) selects the response — independent of any LLM
    /// state.</summary>
    public static bool Trips(string? userText, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(userText)) return false;

        // NFKC folds full-width Latin → ASCII; ToLowerInvariant handles case.
        // Han phrases are unaffected by either, so a single normalised string
        // serves both vocabularies.
        string normalized = userText.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        bool zh = IsHan(userText);

        foreach (string p in PhrasesHan)
        {
            if (normalized.Contains(p, StringComparison.Ordinal))
            {
                response = CrisisResponseZh;
                return true;
            }
        }

        foreach (string p in PhrasesEn)
        {
            if (normalized.Contains(p, StringComparison.Ordinal))
            {
                response = zh ? CrisisResponseZh : CrisisResponseEn;
                return true;
            }
        }

        return false;
    }

    /// <summary>Any U+4E00..U+9FFF Han character → treat as Chinese input.</summary>
    public static bool IsHan(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
            if (c >= '一' && c <= '鿿') return true;
        return false;
    }
}
