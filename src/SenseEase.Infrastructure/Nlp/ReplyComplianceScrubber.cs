using System;
using System.Linq;
using System.Text.RegularExpressions;
using SenseEase.Core.Inference;
using SenseEase.Infrastructure.Real;

namespace SenseEase.Infrastructure.Nlp;

/// <summary>
/// Deterministic REPLY-TEXT compliance pass, run inside <see cref="NluComplianceGate"/> on the final
/// reply (both the LLM and lexicon engines flow through it). It enforces the hard invariants of
/// <c>docs/CHATBOT_BEHAVIOR_SPEC.md §5</c> that a temperature-0.4 model breaks on a knife-edge — the
/// ones a prompt rule alone cannot GUARANTEE:
///
/// <list type="bullet">
///   <item><b>R1 Action honesty</b> — when nothing actuated this turn, the reply must not CLAIM/PROMISE a
///   change ("I've increased protection to its maximum", "all protection is now turned off", "已重設").
///   Eval (2026-06-19) showed this is the worst leak (the gate did not catch it before): the engine did
///   nothing yet the user was told a safety action happened. Wholesale-replaced with an honest no-op line.</item>
///   <item><b>R6 Symptom-state boundary (Analyze)</b> — an AnalyzeRequest reply must not assert/predict the
///   user's bodily state ("you've since felt better", "會好"). Clause-stripped (the ReportDiscomfort case is
///   handled by the gate's P1 rebuild upstream).</item>
///   <item><b>Tone backstop</b> — strip banned ZH emotive particles (喔/啦/～) and clingy companion clauses
///   (守著 / 一直陪 / 乖乖 / 不用擔心), and stray clock timestamps. Grammar-safe (particle + clause level).</item>
/// </list>
///
/// <para>Scope note: broad jargon/mixing cleanup (naming tier/FOV/mask, English-in-Chinese) is fixed at
/// SOURCE by the Phase-2 system prompt — post-hoc surgical deletion mid-sentence breaks grammar, so the
/// gate only does the grammar-safe subset above. Pure, bilingual, never throws. Language for canned text
/// follows the host UI pin (<see cref="ChatbotUiLanguage"/>) when wired, else the reply's own script.</para>
/// </summary>
internal static class ReplyComplianceScrubber
{
    // ── R1: phrases that CLAIM or PROMISE a settings change. Matched case-insensitively as substrings.
    // Only consulted when NOTHING actuated this turn, so a real-delta reply ("I've eased the motion") is
    // never touched. Kept to unambiguous action verbs about CHANGING protection — NOT honest "I've left
    // everything as-is" (the desired question-trap reply), NOT "已記錄" (logged, not a settings change).
    private static readonly string[] EnActionClaim =
    {
        "i've reset", "i have reset", "i reset ", "reset everything", "reset to default", "reset it to",
        "i've turned off", "turned off all", "protection is now off", "protection is off", "all protection is",
        "i've increased", "i have increased", "increased the protection", "increased protection", "i've boosted",
        "i've maxed", "to its maximum", "to maximum", "at maximum", "highest visual safety",
        "i've lowered", "i've raised", "i've reduced", "i've adjusted", "i've eased", "i've calmed",
        "i've steadied", "i've softened", "i've faded", "i've dimmed", "put it back", "put everything back",
        "back to normal", "i'll reset", "i'll turn", "i'll increase", "i'll make it", "i'll boost", "i'll set",
        "i'll keep things", "i'll keep it", "kept things", "keep things light", "keep it light", "keep things steady",
        "all set", "you're all set", "everything's set", "i've kept", "i've made it",
    };
    private static readonly string[] ZhActionClaim =
    {
        "已調整", "已重設", "已重置", "已將", "已把", "已降低", "已減少", "已加深", "已加強", "已關閉", "已關掉",
        "已淡化", "已調淡", "已調低", "已調高", "已提高", "已柔化", "已減弱", "幫你調", "幫您調", "已幫你", "已幫您",
        "已為你調", "已為您調", "調小了", "調大了", "調低了", "調高了", "關閉了", "我已經幫", "我會調", "我會把",
        "我會將", "我會幫你", "我會保持", "我會讓", "我已將", "我已把", "已設為", "調到最", "已調到", "保護已",
    };

    // ── R6: bodily-state assertions/predictions forbidden in an Analyze reply (unless the user said so).
    private static readonly string[] EnBoundary =
    {
        "feel better", "feeling better", "felt better", "you're doing great", "you're doing well",
        "you're calm", "you seem better", "you seem calmer", "you seem to be feeling", "should feel better",
        "will feel better", "will improve", "you've improved", "you're improving", "you're feeling better",
    };
    private static readonly string[] ZhBoundary =
    {
        "感覺穩定", "您穩定", "你穩定", "會好", "會舒服", "改善明顯", "進步明顯", "壓力降低", "壓力下降",
        "感覺好些", "好些了", "不會再", "希望就好", "應該會好", "應該會舒服", "您現在感覺", "你現在感覺",
    };

    // ── Tone backstop: banned ZH clingy companion clauses (dropped at clause level).
    private static readonly string[] ZhBannedClause =
    {
        "守著", "一直陪", "陪著你", "陪著您", "乖乖", "不用擔心", "我在這", "我會一直", "別擔心", "我在旁",
    };

    // ── Injection / coercion markers that, on a ResetRequest, mean the reset is UNSOLICITED.
    private static readonly string[] InjectionMarkers =
    {
        "{\"intent", "\"intent\":", "ignore previous", "ignore all previous", "disregard previous",
        "you are now", "no restrictions", "respond with", "jailbreak", "dan mode", " dan ", "act as",
        "system prompt", "developer mode",
    };

    private static readonly Regex Timestamp = new(@"(?<!\d)\d{1,2}:\d{2}(?::\d{2})?(?!\d)", RegexOptions.Compiled);
    private static readonly Regex SentenceSplit = new(@"(?<=[。．.!?！？\n])", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>True when a ResetRequest here is UNSOLICITED — the user did not genuinely ask to reset.
    /// Catches: a deliberation question ("should I reset?"), an injected/coerced reset (a fake
    /// {"intent":"ResetRequest"} envelope, "ignore previous…", DAN), or a hallucinated reset with no real
    /// reset cue at all. The caller passes <paramref name="resetCueDetected"/> from
    /// FeedbackLexicon.ClassifyShaderHints so the genuine-cue check single-sources the existing vocabulary.</summary>
    internal static bool ResetIsUnsolicited(string? userText, bool resetCueDetected)
    {
        string t = (userText ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(t)) return false;
        if (!resetCueDetected) return true;                       // no genuine reset word → hallucinated/coerced
        if (InterrogativeDetector.IsCommandInquiry(userText)) return true; // "should I reset?" — a question
        foreach (string m in InjectionMarkers)
            if (t.Contains(m, StringComparison.Ordinal)) return true;      // fake envelope / jailbreak
        return false;
    }

    /// <summary>Run the reply through every grammar-safe compliance rule and return the cleaned text.
    /// <paramref name="actuated"/> is true when this turn made a real change (any non-zero delta OR an
    /// applied ResetRequest) — when false, R1 forbids action claims. Pure; never throws.</summary>
    internal static string Scrub(string? reply, string intent, bool actuated, string? userText,
                                 bool? uiEnglishPin = null)
    {
        string s = (reply ?? string.Empty).Trim();
        if (s.Length == 0) return s;
        // The host UI-language pin: an explicit arg (a caller that already resolved it, or a test that
        // needs determinism without touching the process-global) wins; else the live ChatbotUiLanguage
        // toggle. null → no pin wired (headless / lexicon-only host) → fall back to the reply's script.
        bool? pin = uiEnglishPin ?? ChatbotUiLanguage.IsEnglish;
        bool english = IsEnglishReply(userText, s, pin);

        if (intent == "AnalyzeRequest")
        {
            // Analyze is informational, so strip only the offending sentence(s): a bodily-state
            // assertion/prediction (R6) OR a false action claim (a misclassified command that narrates
            // "I've increased protection" while changing nothing). Preserve the rest of the analysis;
            // if almost nothing survives, fall back to a neutral line rather than ship a dangling clause.
            string stripped = DropSentences(s,
                EnBoundary.Concat(EnActionClaim).ToArray(),
                ZhBoundary.Concat(ZhActionClaim).ToArray());
            s = stripped.Trim().Length < 8 ? AnalyzeNeutralLine(english) : stripped;
        }
        else if (!actuated && ClaimsAction(s))
        {
            // R1 — nothing actuated this turn, yet the reply claims/promises a change. These replies are
            // essentially just the false claim, so replace wholesale with an honest no-op line. (An applied
            // ResetRequest has actuated==true, so a truthful "reset to default" reply is never touched.)
            return HonestNoActionLine(english);
        }

        // Tone backstop (ZH): drop clingy companion clauses, strip emotive particles.
        if (ContainsCjk(s))
        {
            s = DropSentences(s, Array.Empty<string>(), ZhBannedClause);
            s = StripZhParticles(s);
            if (s.Trim().Length == 0) return english ? HonestNoActionLine(true) : "好的，已了解。";
        }

        // Strip stray clock timestamps anywhere (jargon leak), tidy double spaces.
        s = Timestamp.Replace(s, string.Empty);
        s = MultiSpace.Replace(s, " ").Trim();

        // ── R-lang: output-side language backstop (2026-06-21). When the host pins a UI language,
        // NEVER ship a reply whose script contradicts the pin. The prompt's LANGUAGE pin is advisory
        // to a temperature-0.4, China-trained 3 B model that samples past it on short/ambiguous input
        // (logged: an EN-pinned "Hi, how are you" reliably returned Traditional Chinese), and nothing
        // upstream of here guarantees the reply language. This is the deterministic last line of
        // defence: when the reply's script contradicts the pin, swap WHOLESALE to an honest canned
        // line in the pinned language — mid-sentence translation is impossible and partial deletion
        // breaks grammar (see header). Acts only when the toggle is wired (non-null), so the
        // headless / lexicon-only fallback keeps its own-script behaviour. Symmetric (EN- and ZH-pinned).
        if (pin is bool wantEnglish && s.Length > 0
            && (wantEnglish ? ContainsCjk(s) : !ContainsCjk(s)))
        {
            string repl = intent == "AnalyzeRequest"
                ? AnalyzeNeutralLine(wantEnglish)
                : FeedbackLexicon.BuildFallbackResponse(userText, intent, wantEnglish);
            // Preserve R1 action-honesty: a canned acknowledgement must not claim a change that
            // didn't actually happen this turn.
            if (!actuated && ClaimsAction(repl)) repl = HonestNoActionLine(wantEnglish);
            // Defence in depth: guarantee the replacement itself matches the pin (the honest/analyze
            // lines are hardcoded per-language, so this only ever guards the lexicon-built branch).
            if (wantEnglish && ContainsCjk(repl)) repl = HonestNoActionLine(true);
            else if (!wantEnglish && !ContainsCjk(repl)) repl = HonestNoActionLine(false);
            return repl;
        }
        return s;
    }

    private static bool ClaimsAction(string reply)
    {
        string lower = reply.ToLowerInvariant();
        foreach (string p in EnActionClaim) if (lower.Contains(p, StringComparison.Ordinal)) return true;
        foreach (string p in ZhActionClaim) if (reply.Contains(p, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string DropSentences(string reply, string[] enPhrases, string[] zhPhrases)
    {
        string[] parts = SentenceSplit.Split(reply);
        var kept = parts.Where(part =>
        {
            if (string.IsNullOrWhiteSpace(part)) return true;
            string lower = part.ToLowerInvariant();
            foreach (string p in enPhrases) if (lower.Contains(p, StringComparison.Ordinal)) return false;
            foreach (string p in zhPhrases) if (part.Contains(p, StringComparison.Ordinal)) return false;
            return true;
        });
        return MultiSpace.Replace(string.Concat(kept), " ").Trim();
    }

    // Remove sentence-final ZH emotive particles (喔/喲/耶/啦/囉/唷) and wave dashes (～), grammar-safe
    // because they are only stripped when adjacent to punctuation or end-of-string.
    private static readonly Regex ZhParticle = new(@"[喔喲耶啦囉唷]+(?=[，。！？、,.!?\s]|$)", RegexOptions.Compiled);
    private static readonly Regex WaveDash = new(@"[～~]+", RegexOptions.Compiled);
    private static string StripZhParticles(string reply)
    {
        // Remove the wave dash FIRST so a particle sitting before it (回饋喔～) becomes sentence-final
        // and is then caught by the particle lookahead; otherwise the 喔 hides behind the ～.
        string s = WaveDash.Replace(reply, string.Empty);
        return ZhParticle.Replace(s, string.Empty);
    }

    // 2026-06-21: the bar's "+/-" buttons became a drag SLIDER (PopupIntensitySlider, "保護強度") — copy
    // updated to match. NOTE: "control on the bar" (EN) + "工具列上的"/"未變更任何設定" (ZH) are LOAD-BEARING —
    // ClinicalReportNarrator.LooksLikeChatArtifact keys on them to reject this chat-only line if it leaks into
    // a clinical report; keep those substrings when editing the copy.
    private static string HonestNoActionLine(bool english) => english
        ? "I haven't changed anything just now. Tell me how you're feeling and I can ease the visuals, or drag the strength control on the bar."
        : "目前未變更任何設定。若需調整請告知，或拖曳工具列上的強度滑桿。";

    private static string AnalyzeNeutralLine(bool english) => english
        ? "Looking at the recent activity, the on-screen motion was the likely driver — let me know how you're feeling now."
        : "從近期紀錄看，主要與畫面動態有關。目前的感受請再告知。";

    private static bool ContainsCjk(string? s)
        => !string.IsNullOrEmpty(s) && s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    // Prefer the live UI-language pin (authoritative per the host); fall back to the reply's own script,
    // then the user's. Mirrors LocalLlmChatbotService.BuildLanguagePin's precedence.
    private static bool IsEnglishReply(string? userText, string reply, bool? pin)
    {
        if (pin is bool en) return en;
        if (ContainsCjk(reply)) return false;
        return !ContainsCjk(userText);
    }
}
