using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;

namespace SenseEase.Infrastructure.Reporting;

/// <summary>
/// Health-report redesign §8 (P3): turns the de-identified numeric export into the report's plain-language
/// narrative. The LLM only PHRASES — the numbers are computed deterministically by
/// <see cref="ClinicalExportBuilder"/> and the prompt carries ONLY those numbers (never a raw row, a
/// <c>ForegroundKey</c>, a stored observation, or a <c>LongitudinalSummaryBuilder.BuildBlock</c> judgement
/// string — build-gates 1 + 3).
///
/// <para><b>Scope (option b, 2026-06-21): ONE model-phrased paragraph.</b> Only the opening
/// <see cref="ReportNarrative.Summary"/> is narrated by the model; the comfort / protection / exposure
/// sections are emitted straight from their deterministic <see cref="ReportFallbacks"/> templates. Their
/// prompts forbade exactly what an LLM is good at (no direction-of-change, no new number, just restate the
/// given figures), so the model added contention + an overreach surface for no content gain. Keeping one
/// narrated opening preserves a warm human voice while shrinking the risk surface to a single gated section.</para>
///
/// <para><b>Compliance pipeline (the Summary).</b> It is generated with a bounded token budget, then run
/// through: <c>CompleteShortAsync</c> (which already applies <c>ReplyComplianceScrubber.Scrub</c> for
/// action-honesty) → a chat-artifact reject → <see cref="ReportClaimGuard.ContainsBannedClaim"/> (the
/// medical-overreach wall) → a <b>number-lock</b> (every digit the model writes must be one we handed it, so
/// it can never round / drop / transpose a figure that contradicts the tables below) → a language-pin sanity
/// check. ANY failure — model unavailable, null/blank, a banned claim, a fabricated number, or a language
/// mismatch — converges on the deterministic Summary template, so the report ALWAYS renders in full.</para>
/// </summary>
public sealed class ClinicalReportNarrator
{
    // A report section wants 2-3 sentences — more than the 96-token rest-advice cap, still bounded so a
    // section can't run away in latency/context. Per-section + isolated (Q4).
    private const int MaxTokensPerSection = 160;

    private readonly IChatbotService? _chatbot;

    /// <param name="chatbot">The on-device chatbot, or null. When null / not loaded, every section uses its
    /// deterministic template — the report is fully populated with no network call.</param>
    public ClinicalReportNarrator(IChatbotService? chatbot) => _chatbot = chatbot;

    public async Task<ReportNarrative> BuildAsync(
        ClinicalExport export, ReportPosture? posture, bool english, CancellationToken ct = default)
    {
        if (export is null) throw new ArgumentNullException(nameof(export));

        // Option (b): only the Summary is model-phrased. The numbers it is allowed to state are pinned to
        // this set — the period length + the two check-in counts the Summary prompt is given; any OTHER
        // digit the model emits rejects the section to its template (the LLM phrases, never owns, a figure).
        var allowedSummaryNumbers = new HashSet<int>
        {
            export.Window.DaysObserved, export.Measured.Count, export.Estimated.Count,
        };

        return new ReportNarrative
        {
            Summary      = await SectionAsync(SummaryPrompt(export, english), ReportFallbacks.Summary(export, english),
                                              english, allowedSummaryNumbers, ct).ConfigureAwait(false),
            // Deterministic by design (no model call): the prompts added no value over the templates here.
            ComfortTrend = ReportFallbacks.ComfortTrend(export, english),
            Protection   = ReportFallbacks.Protection(export, posture, english),
            Exposure     = ReportFallbacks.Exposure(export, english),
        };
    }

    /// <summary>Generate one section: try the model, fall back to <paramref name="deterministic"/> on any
    /// failure / blank / banned-claim / language-mismatch. Never throws (other than honoring cancellation).</summary>
    private async Task<string> SectionAsync(
        string userPrompt, string deterministic, bool english,
        IReadOnlyCollection<int> allowedNumbers, CancellationToken ct)
    {
        if (_chatbot is null || !_chatbot.IsModelLoaded) return deterministic;

        string? raw;
        try
        {
            // CompleteShortAsync already runs ReplyComplianceScrubber.Scrub (action-honesty, R1/R6) on its
            // output — the first wall. We pass the bigger per-section budget via the maxTokens overload.
            raw = await _chatbot.CompleteShortAsync(SystemPrompt(english), userPrompt, MaxTokensPerSection, ct)
                                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return deterministic; }
        catch { return deterministic; }

        if (string.IsNullOrWhiteSpace(raw)) return deterministic;
        string s = raw.Trim();

        // CompleteShortAsync runs the CHAT ReplyComplianceScrubber, whose R1/R-lang backstops can replace a
        // non-compliant model reply with a CHAT fallback line (e.g. "…use the +/- control on the bar"). That
        // line is honest for chat but nonsense in a report — reject it so the section degrades to its own
        // deterministic template instead of leaking a chat artifact into the document.
        if (LooksLikeChatArtifact(s)) return deterministic;

        // Wall 2: the medical-overreach guard — any banned claim rejects the WHOLE section.
        if (ReportClaimGuard.ContainsBannedClaim(s)) return deterministic;

        // Wall 3: number-lock. The LLM only PHRASES the pre-computed figures, so every integer it writes
        // must be one we handed it. A rounded / dropped / transposed digit would silently contradict the
        // tables and charts below — exactly the surface a clinician quotes — so reject the whole section to
        // its (correct) template instead. Spelled-out numbers can't be checked, but the prompt asks for
        // digits; this catches the failure that matters.
        if (!AllNumbersAllowed(s, allowedNumbers)) return deterministic;

        // Language-pin sanity: never ship a section whose script contradicts the report language (the
        // EN↔ZH pin is unreliable on this model; a mismatched section reads as broken).
        bool hasCjk = s.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        if (english && hasCjk) return deterministic;     // EN report but the section is Chinese
        if (!english && !hasCjk) return deterministic;   // ZH report but the section has no CJK at all

        return s;
    }

    // The stable substrings of ReplyComplianceScrubber's chat fallback lines (HonestNoActionLine, both
    // languages). Report prose never contains these — a match means the scrubber swapped the model's reply
    // for a chat line, so the section must fall back to its report template.
    private static bool LooksLikeChatArtifact(string s) =>
        s.Contains("control on the bar", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("工具列上的", StringComparison.Ordinal) ||
        s.Contains("未變更任何設定", StringComparison.Ordinal);

    // Number-lock: every run of digits in the model's prose must parse to an int we supplied. An
    // unparseable run (e.g. an overflowing fabricated number) is treated as not-allowed — reject.
    private static bool AllNumbersAllowed(string prose, IReadOnlyCollection<int> allowed)
    {
        foreach (Match m in Regex.Matches(prose, @"\d+"))
            if (!int.TryParse(m.Value, out int n) || !allowed.Contains(n)) return false;
        return true;
    }

    // ── Prompts (numeric DTO only — no raw rows / keys / stored prose) ─────────────────────────────────

    // NOTE: the prohibition is phrased GENERICALLY (no enumerated banned words) so this prompt-builder file
    // needs no BannedPhraseLintTests ZH exemption — and ReportClaimGuard is the real enforcement anyway.
    private static string SystemPrompt(bool english) => english
        ? "You write the short, friendly opening of a personal visual-comfort report from pre-computed numbers. " +
          "Speak warmly and directly to the reader ('you', 'your'), in 2-3 plain sentences. Restate ONLY what the " +
          "numbers show. Do NOT invent any number. Do NOT make any claim about the person's health, bodily state, " +
          "or any change in how they feel over time — describe only the app's settings and the logged figures, " +
          "never a medical outcome. Write in English only."
        : "你要根據已算好的數字，寫出個人視覺舒適報告親切的開頭段落。以「你」直接、溫暖地稱呼讀者，用 2-3 句平實的話" +
          "陳述數字所顯示的內容。不要捏造任何數字。不要對使用者的健康、身體狀況或感受隨時間的任何變化做出宣稱；" +
          "只描述程式的設定與紀錄到的數字，絕不描述醫療結果。只用繁體中文書寫。";

    private static string SummaryPrompt(ClinicalExport e, bool english)
    {
        // Counts only — NO calendar dates. The header already prints the window, and a yyyy-MM-dd would
        // carry digits the number-lock doesn't list as "allowed" (it would reject the section). The model
        // may phrase the period in words ("over two weeks"), but every DIGIT it writes must be one of these.
        string nums =
            $"days_observed={e.Window.DaysObserved}; comfort_checkins={e.Measured.Count}; estimated_notes={e.Estimated.Count}.";
        return (english
            ? "Write the opening summary of the report in 2-3 sentences. State how long the period was and how many " +
              "comfort check-ins were logged. Do NOT state any calendar date. Numbers: "
            : "用 2-3 句撰寫報告的開頭摘要。說明觀察期間的長度，以及記錄到的舒適度回報次數。不要寫出任何日期。數字：") + nums;
    }

    internal static string StrengthWord(ProtectionStrength s, bool english) => s switch
    {
        ProtectionStrength.Light      => english ? "light"      : "輕度",
        ProtectionStrength.Protective => english ? "protective" : "較強",
        _                             => english ? "balanced"   : "適中",
    };
}
