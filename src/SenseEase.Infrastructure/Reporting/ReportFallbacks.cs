using System.Globalization;
using System.Linq;
using SenseEase.Core.Inference;

namespace SenseEase.Infrastructure.Reporting;

/// <summary>
/// Health-report redesign §8 (P3): the per-section DETERMINISTIC narrative templates — used whenever the
/// model is unavailable, returns nothing, produces a banned claim, or drifts language. They restate the
/// pre-computed numbers in neutral, factual prose and are deliberately authored to contain NO
/// <see cref="ReportClaimGuard"/> stem (a golden test asserts every template passes the guard, in both
/// languages). They describe the app's settings and the logged figures — never a medical outcome.
/// </summary>
internal static class ReportFallbacks
{
    public static string Summary(ClinicalExport e, bool english) => english
        ? $"This report covers {e.Window.DaysObserved} day(s) of use, with {e.Measured.Count} comfort check-in(s) logged. " +
          "The figures and charts below describe on-screen motion and how the app's protection was set. " +
          "It is a summary of logged numbers, not a medical assessment."
        : $"本報告涵蓋 {e.Window.DaysObserved} 天的使用，記錄了 {e.Measured.Count} 次舒適度回報。" +
          "下方的數字與圖表描述畫面動態，以及程式防護的設定。這是紀錄數字的彙整，並非醫療評估。";

    public static string ComfortTrend(ClinicalExport e, bool english)
    {
        if (e.Measured.Count == 0)
            return english
                ? "No comfort check-ins were logged in this window, so there is no comfort trend to show yet."
                : "此期間沒有舒適度回報紀錄，因此尚無舒適度趨勢可呈現。";

        return english
            ? $"Comfort check-ins use a 0–20 scale where a higher number means more discomfort. " +
              $"The earliest entries averaged {Fmt(e.Measured.BaselineMean)}; the most recent averaged {Fmt(e.Measured.OutcomeMean)} " +
              $"(overall average {Fmt(e.Measured.Mean)} across {e.Measured.Count} check-in(s))."
            : $"舒適度回報採用 0–20 量表，數字越高表示越不適。" +
              $"最早的紀錄平均為 {Fmt(e.Measured.BaselineMean)}；最近的紀錄平均為 {Fmt(e.Measured.OutcomeMean)}" +
              $"（{e.Measured.Count} 次回報的整體平均為 {Fmt(e.Measured.Mean)}）。";
    }

    public static string Protection(ClinicalExport e, ReportPosture? p, bool english)
    {
        var c = e.OnVsOff;
        string body;
        if (c.CountOn == 0 && c.CountOff == 0)
            body = english
                ? "There are not yet enough check-ins to compare protection on versus off."
                : "目前的回報次數還不足以比較開啟與關閉防護的差異。";
        else
            body = english
                ? $"With protection on, comfort check-ins averaged {Fmt(c.MeanFmsOn)} ({c.CountOn} check-in(s)); " +
                  $"with protection off, {Fmt(c.MeanFmsOff)} ({c.CountOff} check-in(s))."
                : $"開啟防護時，舒適度回報平均為 {Fmt(c.MeanFmsOn)}（{c.CountOn} 次）；" +
                  $"關閉防護時為 {Fmt(c.MeanFmsOff)}（{c.CountOff} 次）。";

        if (p is null) return body;
        string strength = ClinicalReportNarrator.StrengthWord(p.Strength, english);
        return english
            ? $"{body} The current protection strength is {strength}."
            : $"{body}目前的防護強度為{strength}。";
    }

    public static string Exposure(ClinicalExport e, bool english)
    {
        int ofiDays  = e.DailySeries.Count(d => d.OfiP90.HasValue);
        int maskTotal = e.DailySeries.Where(d => d.MaskActivations.HasValue).Sum(d => d.MaskActivations!.Value);
        return english
            ? $"Across {e.Window.DaysObserved} day(s), on-screen motion (90th-percentile) was logged on {ofiDays} day(s), " +
              $"and the app's protection triggered {maskTotal} time(s). See the charts below for the day-by-day view."
            : $"在 {e.Window.DaysObserved} 天內，有 {ofiDays} 天記錄到畫面動態（第 90 百分位），" +
              $"程式防護共觸發 {maskTotal} 次。逐日變化請見下方圖表。";
    }

    private static string Fmt(double? v) =>
        v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture)
                   : "—";
}
