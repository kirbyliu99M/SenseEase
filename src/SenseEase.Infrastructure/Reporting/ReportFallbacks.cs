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
        ? $"Here's an easy-to-read look at your screen time and visual comfort over the past " +
          $"{e.Window.DaysObserved} {Plural(e.Window.DaysObserved, "day", "days")} — you logged " +
          $"{e.Measured.Count} comfort {Plural(e.Measured.Count, "check-in", "check-ins")}. The figures and charts " +
          "below show on-screen motion and how the app set its protection for you. (These are logged numbers for " +
          "your reference, not a medical assessment.)"
        : $"這是你過去 {e.Window.DaysObserved} 天視覺舒適情況的簡明整理——你共回報了 {e.Measured.Count} 次。" +
          "下方的數字與圖表會說明畫面動態，以及程式為你設定的防護。（這些是供你參考的紀錄數字，並非醫療評估。）";

    public static string ComfortTrend(ClinicalExport e, bool english)
    {
        if (e.Measured.Count == 0)
            return english
                ? "You haven't logged any comfort check-ins in this window yet, so there's no trend to show."
                : "你在此期間還沒有舒適度回報紀錄，因此尚無趨勢可呈現。";

        return english
            ? $"Your comfort check-ins use a 0–20 scale, where a higher number means more discomfort. " +
              $"Your earliest check-ins averaged {Fmt(e.Measured.BaselineMean)}, and your most recent averaged {Fmt(e.Measured.OutcomeMean)} " +
              $"(overall {Fmt(e.Measured.Mean)} across {e.Measured.Count} {Plural(e.Measured.Count, "check-in", "check-ins")})."
            : $"你的舒適度回報採用 0–20 量表，數字越高表示越不適。" +
              $"你最早的紀錄平均為 {Fmt(e.Measured.BaselineMean)}，最近的紀錄平均為 {Fmt(e.Measured.OutcomeMean)}" +
              $"（{e.Measured.Count} 次回報的整體平均為 {Fmt(e.Measured.Mean)}）。";
    }

    public static string Protection(ClinicalExport e, ReportPosture? p, bool english)
    {
        var c = e.OnVsOff;
        string body;
        if (c.CountOn == 0 && c.CountOff == 0)
            body = english
                ? "There aren't enough check-ins yet to compare protection on versus off."
                : "目前的回報次數還不足以比較開啟與關閉防護。";
        else
            body = english
                ? $"When protection was on, your comfort check-ins averaged {Fmt(c.MeanFmsOn)} ({c.CountOn} {Plural(c.CountOn, "check-in", "check-ins")}); " +
                  $"when it was off, {Fmt(c.MeanFmsOff)} ({c.CountOff} {Plural(c.CountOff, "check-in", "check-ins")})."
                : $"開啟防護時，你的舒適度回報平均為 {Fmt(c.MeanFmsOn)}（{c.CountOn} 次）；" +
                  $"關閉防護時為 {Fmt(c.MeanFmsOff)}（{c.CountOff} 次）。";

        if (p is null) return body;
        string strength = ClinicalReportNarrator.StrengthWord(p.Strength, english);
        return english
            ? $"{body} Your protection is currently set to {strength}."
            : $"{body}你目前的防護強度為{strength}。";
    }

    public static string Exposure(ClinicalExport e, bool english)
    {
        int ofiDays  = e.DailySeries.Count(d => d.OfiP90.HasValue);
        int maskTotal = e.DailySeries.Where(d => d.MaskActivations.HasValue).Sum(d => d.MaskActivations!.Value);
        return english
            ? $"Over these {e.Window.DaysObserved} {Plural(e.Window.DaysObserved, "day", "days")}, the app logged on-screen motion on " +
              $"{ofiDays} {Plural(ofiDays, "day", "days")} and stepped in to protect you {maskTotal} {Plural(maskTotal, "time", "times")}. " +
              "The charts below show the day-by-day view."
            : $"在這 {e.Window.DaysObserved} 天裡，程式有 {ofiDays} 天記錄到畫面動態，" +
              $"並為你啟動防護共 {maskTotal} 次。逐日變化請見下方圖表。";
    }

    // #16: EN plural picker — the report stops emitting placeholder-looking "day(s)" / "check-in(s)".
    // English only; the ZH templates carry no plural inflection.
    private static string Plural(int n, string singular, string plural) => n == 1 ? singular : plural;

    private static string Fmt(double? v) =>
        v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture)
                   : "—";
}
