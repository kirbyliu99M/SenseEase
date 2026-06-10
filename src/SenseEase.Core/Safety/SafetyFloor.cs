using System;
using System.Collections.Generic;
using System.Text;

namespace SenseEase.Core.Safety;

/// <summary>
/// Single source of truth for the clinical self-harm / severe-distress safety
/// floor (Alpha-1 Wave 1 — H1/H2/H3/H4/H8, C3).
///
/// <para>Both the chatbot view-model (the line-1 intercept in
/// <c>ChatbotViewModel.SendAsync</c>) and any active SDK chatbot service
/// call <see cref="Trips"/>, so the phrase vocabulary and crisis-line responses
/// cannot drift between call sites. This replaces previously hand-copied
/// floors that had diverged on a single character (half-width <c>:</c> vs
/// full-width <c>：</c>) — the C3 duplicate-source-drift pattern. The
/// canonical punctuation is the full-width <c>：</c>.</para>
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
        // (review #7) bare "想死" REMOVED — it matched the affectionate idiom
        // "想死你了" (I missed you). The specific forms above (想死了/我想死/想去死)
        // keep real-intent coverage without the greeting false-positive.
    };

    /// <summary>English + youth shorthand. Matched against the NFKC-lowered text (H3).</summary>
    public static readonly string[] PhrasesEn =
    {
        "suicide", "self-harm", "self harm", "selfharm",
        "kill myself", "end it all", "end my life",
        "not want to live", "do not want to live", "don't want to live",
        "want to die", "wanna die",
        // youth shorthand (H3). (review #6) "kms" REMOVED — it collides with the
        // kilometre abbreviation ("I walked 5 kms today"), a plausible input for a
        // motion-app user. "kys" is kept but matched on WORD BOUNDARIES (see
        // WordBoundaryRequiredEn) so "whiskys" etc. don't trip.
        "kys", "unalive", "unaliving",
        "end myself", "off myself", "no reason to live",
        "don't want to be here", "better off dead", "wish i was dead",
    };

    /// <summary>English tokens short enough to collide as substrings; matched only
    /// as whole words (review #6).</summary>
    private static readonly HashSet<string> WordBoundaryRequiredEn =
        new(StringComparer.Ordinal) { "kys" };

    /// <summary>Sale/idiom markers (Traditional + Simplified). When the ONLY Han
    /// trigger is "跳樓"/"跳楼" and one of these is present, treat it as the
    /// "跳樓大拍賣" fire-sale idiom, not self-harm (review #7).</summary>
    private static readonly string[] SaleIdiomMarkers =
        { "拍賣", "拍卖", "特賣", "特卖", "促銷", "促销", "清倉", "清仓", "跳樓價", "跳楼价", "大特價", "大特价", "折扣" };

    /// <summary>Detects self-harm / severe-distress phrases. Returns <c>true</c>
    /// and the localised crisis-line response when triggered. The user's CURRENT
    /// message language (Han vs not) selects the response — independent of any LLM
    /// state.</summary>
    public static bool Trips(string? userText, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(userText)) return false;

        // NFKC folds full-width Latin → ASCII; ToLowerInvariant handles case. NFKC
        // does NOT fold the curly apostrophe, so phone/desktop autocorrect of
        // "don't" (U+2019) would silently bypass the floor — fold the apostrophe
        // variants to ASCII first (review #1).
        string normalized = userText.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
            .Replace('’', '\'').Replace('‘', '\'').Replace('ʼ', '\'');
        bool zh = IsHan(userText);

        foreach (string p in PhrasesHan)
        {
            if (!normalized.Contains(p, StringComparison.Ordinal)) continue;
            // review #7: "跳樓/跳楼" alone is also the ubiquitous fire-sale idiom.
            // If a sale marker is present, skip THIS phrase and keep checking the
            // rest (so "我要跳樓" still trips, but "跳樓大拍賣" alone does not).
            if ((p == "跳樓" || p == "跳楼") && IsBenignSaleIdiom(normalized)) continue;
            response = CrisisResponseZh;
            return true;
        }

        foreach (string p in PhrasesEn)
        {
            bool hit = WordBoundaryRequiredEn.Contains(p)
                ? ContainsWord(normalized, p)                               // review #6
                : normalized.Contains(p, StringComparison.Ordinal);
            if (hit)
            {
                response = zh ? CrisisResponseZh : CrisisResponseEn;
                return true;
            }
        }

        return false;
    }

    private static bool IsBenignSaleIdiom(string normalized)
    {
        foreach (string m in SaleIdiomMarkers)
            if (normalized.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>True if <paramref name="word"/> occurs in <paramref name="text"/>
    /// bounded by non-alphanumeric characters (or the string ends).</summary>
    private static bool ContainsWord(string text, string word)
    {
        int idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            int end = idx + word.Length;
            bool rightOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftOk && rightOk) return true;
            idx = end;
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
