using System;
using System.Globalization;
using System.Linq;
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
/// <para><b>Two-wall compliance pipeline, per section.</b> Each of the four sections is generated
/// independently with a bounded token budget, then run through:
/// <c>CompleteShortAsync</c> (which already applies <c>ReplyComplianceScrubber.Scrub</c> for action-honesty)
/// → <see cref="ReportClaimGuard.ContainsBannedClaim"/> (the medical-overreach wall) → a language-pin sanity
/// check. ANY failure — model unavailable, null/blank output, a banned claim, or a language mismatch —
/// converges on that section's deterministic fallback template. So the report ALWAYS renders in full, and a
/// single bad section never poisons the others (failure isolation).</para>
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

        return new ReportNarrative
        {
            Summary      = await SectionAsync(SummaryPrompt(export, english),      ReportFallbacks.Summary(export, english),      english, ct).ConfigureAwait(false),
            ComfortTrend = await SectionAsync(ComfortPrompt(export, english),       ReportFallbacks.ComfortTrend(export, english), english, ct).ConfigureAwait(false),
            Protection   = await SectionAsync(ProtectionPrompt(export, posture, english), ReportFallbacks.Protection(export, posture, english), english, ct).ConfigureAwait(false),
            Exposure     = await SectionAsync(ExposurePrompt(export, english),      ReportFallbacks.Exposure(export, english),     english, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Generate one section: try the model, fall back to <paramref name="deterministic"/> on any
    /// failure / blank / banned-claim / language-mismatch. Never throws (other than honoring cancellation).</summary>
    private async Task<string> SectionAsync(string userPrompt, string deterministic, bool english, CancellationToken ct)
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

    // ── Prompts (numeric DTO only — no raw rows / keys / stored prose) ─────────────────────────────────

    // NOTE: the prohibition is phrased GENERICALLY (no enumerated banned words) so this prompt-builder file
    // needs no BannedPhraseLintTests ZH exemption — and ReportClaimGuard is the real enforcement anyway.
    private static string SystemPrompt(bool english) => english
        ? "You write one short, neutral section of a personal visual-comfort report from pre-computed numbers. " +
          "Restate ONLY what the numbers show, in 2-3 plain sentences. Do NOT invent any number. Do NOT make any " +
          "claim about the person's health, bodily state, or any change in how they feel over time — describe only " +
          "the app's settings and the logged figures, never a medical outcome. Write in English only."
        : "你要根據已算好的數字，寫出個人視覺舒適報告的一個簡短中性段落。只用 2-3 句平實的話陳述數字所顯示的內容。" +
          "不要捏造任何數字。不要對使用者的健康、身體狀況或感受隨時間的任何變化做出宣稱；只描述程式的設定與" +
          "紀錄到的數字，絕不描述醫療結果。只用繁體中文書寫。";

    private static string SummaryPrompt(ClinicalExport e, bool english)
    {
        string nums =
            $"days_observed={e.Window.DaysObserved}; first_date={e.Window.FirstDateUtc ?? "n/a"}; last_date={e.Window.LastDateUtc ?? "n/a"}; " +
            $"comfort_checkins={e.Measured.Count}; estimated_notes={e.Estimated.Count}.";
        return (english
            ? "Write the opening summary of the report. Numbers: "
            : "撰寫報告的開頭摘要。數字：") + nums;
    }

    private static string ComfortPrompt(ClinicalExport e, bool english)
    {
        // 0-20 FMS: higher = more discomfort. Give baseline + recent as raw numbers; do NOT pre-judge a direction.
        string nums =
            $"scale=0-20 (higher means more discomfort); checkins={e.Measured.Count}; " +
            $"mean={Fmt(e.Measured.Mean)}; earliest_quarter_mean={Fmt(e.Measured.BaselineMean)}; " +
            $"recent_quarter_mean={Fmt(e.Measured.OutcomeMean)}; days_with_a_checkin={e.DailySeries.Count(p => p.MeasuredFmsMean.HasValue)}.";
        return (english
            ? "Write the comfort-trend section. State the scale and the baseline vs recent figures plainly, without claiming a direction of change. Numbers: "
            : "撰寫舒適度趨勢段落。平實說明量表與起始相對近期的數字，不要聲稱變化的方向。數字：") + nums;
    }

    private static string ProtectionPrompt(ClinicalExport e, ReportPosture? p, bool english)
    {
        string strength = p is null ? "n/a" : StrengthWord(p.Strength, english);
        string nums =
            $"checkins_with_protection_on={e.OnVsOff.CountOn}; checkins_with_protection_off={e.OnVsOff.CountOff}; " +
            $"mean_fms_on={Fmt(e.OnVsOff.MeanFmsOn)}; mean_fms_off={Fmt(e.OnVsOff.MeanFmsOff)}; " +
            $"current_protection_strength={strength}.";
        return (english
            ? "Write the protection section. Compare the on-vs-off check-in figures plainly and state the current protection strength. Numbers: "
            : "撰寫防護段落。平實比較開啟與關閉防護時的數字，並說明目前的防護強度。數字：") + nums;
    }

    private static string ExposurePrompt(ClinicalExport e, bool english)
    {
        var days = e.DailySeries;
        int ofiDays = days.Count(d => d.OfiP90.HasValue);
        string ofiRange = ofiDays == 0 ? "n/a"
            : $"{days.Where(d => d.OfiP90.HasValue).Min(d => d.OfiP90!.Value)}-{days.Where(d => d.OfiP90.HasValue).Max(d => d.OfiP90!.Value)}";
        int maskTotal = days.Where(d => d.MaskActivations.HasValue).Sum(d => d.MaskActivations!.Value);
        string nums =
            $"days_observed={e.Window.DaysObserved}; days_with_motion_data={ofiDays}; on_screen_motion_p90_range={ofiRange}; " +
            $"total_protection_triggers={maskTotal}.";
        return (english
            ? "Write the exposure-and-motion section, pointing the reader to the charts below. Numbers: "
            : "撰寫曝光與動態段落，並引導讀者參考下方圖表。數字：") + nums;
    }

    private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture) : "n/a";

    internal static string StrengthWord(ProtectionStrength s, bool english) => s switch
    {
        ProtectionStrength.Light      => english ? "light"      : "輕度",
        ProtectionStrength.Protective => english ? "protective" : "較強",
        _                             => english ? "balanced"   : "適中",
    };
}
