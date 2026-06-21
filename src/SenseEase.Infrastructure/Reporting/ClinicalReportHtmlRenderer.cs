using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SenseEase.Core.Inference;

namespace SenseEase.Infrastructure.Reporting;

/// <summary>
/// Health-report redesign §8 (P2): renders the de-identified <see cref="ClinicalExport"/> + the guarded
/// <see cref="ReportNarrative"/> into ONE self-contained HTML file (inline CSS + inline SVG charts) the
/// user opens in a browser and can print to PDF. PURE — no I/O, no LLM, no <c>DynamicResource</c> — so it
/// unit-tests with an in-memory export.
///
/// <para><b>Calm palette, pinned LIGHT (Q7).</b> Inline SVG can't resolve theme tokens, so the calm
/// light-theme colours are baked to concrete hex: FMS line <c>#4B6EF5</c>, on-screen-motion p90
/// <c>#2F7D6E</c>, protection-trigger bars <c>#9A6B22</c>, headings <c>#1F5249</c>, axis/grid a low-opacity
/// neutral. The flagged gamer-neon <c>#024AD8</c> (and the live chart's <c>#7B8DB0</c>/<c>#E54747</c>) are
/// deliberately NOT used — a test asserts their absence.</para>
///
/// <para><b>De-identification.</b> The renderer only ever sees the typed export DTO: per-day points carry
/// no category/app field at all, and the per-category rows already hold the de-identified category label
/// (stripped by <c>ExtractCategory</c> upstream). Every dynamic string is HTML-escaped.</para>
/// </summary>
public static class ClinicalReportHtmlRenderer
{
    // Calm light-theme palette (concrete hex — see class remarks).
    private const string FmsStroke   = "#4B6EF5";
    private const string OfiStroke   = "#2F7D6E";
    private const string MaskFill    = "#9A6B22";
    private const string Heading     = "#1F5249";
    private const string Ink         = "#1F2937";
    private const string InkSoft     = "#6B7280";
    private const string CardBg      = "#F7F8FA";
    private const string PageBg      = "#FFFFFF";
    private const string Hairline    = "#E5E7EB";

    public static string Render(ClinicalExport export, ReportNarrative narrative, ReportPosture? posture, bool english)
    {
        if (export is null) throw new ArgumentNullException(nameof(export));
        narrative ??= new ReportNarrative();

        var sb = new StringBuilder(8192);
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(english ? "en" : "zh-Hant").Append("\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(Esc(T(english, "Visual comfort report", "視覺舒適報告"))).Append("</title>\n");
        AppendStyle(sb);
        sb.Append("</head>\n<body>\n<main class=\"report\">\n");

        // ── Header ────────────────────────────────────────────────────────────
        sb.Append("<header>\n<h1>").Append(Esc(T(english, "Visual comfort report", "視覺舒適報告"))).Append("</h1>\n");
        string generated = (export.GeneratedUtc ?? "").Length >= 10 ? export.GeneratedUtc!.Substring(0, 10) : "—";
        sb.Append("<p class=\"meta\">")
          .Append(Esc(T(english, "Generated", "產生日期"))).Append(": ").Append(Esc(generated))
          .Append("  ·  ").Append(Esc(T(english, "Observation window", "觀察期間"))).Append(": ")
          .Append(Esc(export.Window.FirstDateUtc ?? "—")).Append(" → ").Append(Esc(export.Window.LastDateUtc ?? "—"))
          .Append("  ·  ").Append(export.Window.DaysObserved).Append(' ').Append(Esc(T(english, "day(s)", "天")))
          .Append("</p>\n</header>\n");

        // ── Summary ───────────────────────────────────────────────────────────
        Section(sb, T(english, "Summary", "摘要"), narrative.Summary);

        // ── Comfort trend ───────────────────────────────────────────────────────
        sb.Append("<section>\n<h2>").Append(Esc(T(english, "Comfort trend", "舒適度趨勢"))).Append("</h2>\n");
        sb.Append("<p>").Append(Esc(narrative.ComfortTrend)).Append("</p>\n");
        sb.Append(LineChart(
            export.DailySeries.Select(p => (p.Date, p.MeasuredFmsMean)).ToList(),
            yMin: 0, yMax: 20, stroke: FmsStroke,
            caption: T(english, "Comfort check-ins (0–20, higher = more discomfort)", "舒適度回報（0–20，越高越不適）"),
            english: english));
        sb.Append("</section>\n");

        // ── Protection ───────────────────────────────────────────────────────────
        sb.Append("<section>\n<h2>").Append(Esc(T(english, "Protection", "防護"))).Append("</h2>\n");
        sb.Append("<p>").Append(Esc(narrative.Protection)).Append("</p>\n");
        AppendOnOffTable(sb, export, english);
        if (posture is not null) AppendPosture(sb, posture, english);
        sb.Append("</section>\n");

        // ── Exposure & on-screen motion ──────────────────────────────────────────
        sb.Append("<section>\n<h2>").Append(Esc(T(english, "Exposure & on-screen motion", "曝光與畫面動態"))).Append("</h2>\n");
        sb.Append("<p>").Append(Esc(narrative.Exposure)).Append("</p>\n");
        sb.Append(LineChart(
            export.DailySeries.Select(p => (p.Date, (double?)(p.OfiP90.HasValue ? p.OfiP90.Value : (double?)null))).ToList(),
            yMin: 0, yMax: NiceMax(export.DailySeries.Where(p => p.OfiP90.HasValue).Select(p => (double)p.OfiP90!.Value), 100),
            stroke: OfiStroke,
            caption: T(english, "On-screen motion (90th percentile)", "畫面動態（第 90 百分位）"),
            english: english));
        sb.Append(BarChart(
            export.DailySeries.Select(p => (p.Date, p.MaskActivations)).ToList(),
            fill: MaskFill,
            caption: T(english, "Protection triggers per day", "每日防護觸發次數"),
            english: english));
        AppendCategoryTable(sb, export, english);
        sb.Append("</section>\n");

        // ── Disclaimer ───────────────────────────────────────────────────────────
        sb.Append("<footer>\n<p class=\"disclaimer\">").Append(Esc(T(english,
            "SenseEase is a comfort tool, not a medical device. This report summarises on-device numbers only (no app names, no message text, dates only) and is not a diagnosis.",
            "SenseEase 是舒適輔助工具，並非醫療器材。本報告僅彙整裝置上的數字（不含應用名稱、不含訊息內容、僅含日期），不構成診斷。")))
          .Append("</p>\n</footer>\n");

        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    // ── Sections / tables ──────────────────────────────────────────────────────

    private static void Section(StringBuilder sb, string heading, string body)
    {
        sb.Append("<section>\n<h2>").Append(Esc(heading)).Append("</h2>\n<p>").Append(Esc(body)).Append("</p>\n</section>\n");
    }

    private static void AppendOnOffTable(StringBuilder sb, ClinicalExport e, bool english)
    {
        var c = e.OnVsOff;
        sb.Append("<table class=\"grid\">\n<thead><tr><th></th><th>")
          .Append(Esc(T(english, "Check-ins", "回報次數"))).Append("</th><th>")
          .Append(Esc(T(english, "Mean (0–20)", "平均（0–20）"))).Append("</th></tr></thead>\n<tbody>\n");
        Row(sb, T(english, "Protection on", "開啟防護"),  c.CountOn,  c.MeanFmsOn);
        Row(sb, T(english, "Protection off", "關閉防護"), c.CountOff, c.MeanFmsOff);
        sb.Append("</tbody>\n</table>\n");

        static void Row(StringBuilder sb, string label, int count, double? mean)
            => sb.Append("<tr><td>").Append(Esc(label)).Append("</td><td>").Append(count)
                 .Append("</td><td>").Append(Esc(Num(mean))).Append("</td></tr>\n");
    }

    private static void AppendPosture(StringBuilder sb, ReportPosture p, bool english)
    {
        string strength = ClinicalReportNarrator.StrengthWord(p.Strength, english);
        sb.Append("<ul class=\"posture\">\n");
        Li(sb, T(english, "Protection strength", "防護強度"), strength);
        Li(sb, T(english, "Relaxation progress", "放寬進度"), $"{Num(p.HabituationLevel)}/100");
        Li(sb, T(english, "Current offset", "目前偏移"), $"{Num(p.OffsetPp)} pp (" + T(english, "cap", "上限") + $" {p.OffsetCapPp})");
        Li(sb, T(english, "Recent pressure tier", "近期壓力等級"), p.CurrentTier.ToString(CultureInfo.InvariantCulture));
        sb.Append("</ul>\n");

        static void Li(StringBuilder sb, string k, string v)
            => sb.Append("<li><span class=\"k\">").Append(Esc(k)).Append("</span><span class=\"v\">").Append(Esc(v)).Append("</span></li>\n");
    }

    private static void AppendCategoryTable(StringBuilder sb, ClinicalExport e, bool english)
    {
        if (e.ByCategory is null || e.ByCategory.Count == 0) return;
        sb.Append("<h3>").Append(Esc(T(english, "Comfort by app category", "各類應用的舒適度"))).Append("</h3>\n");
        sb.Append("<table class=\"grid\">\n<thead><tr><th>")
          .Append(Esc(T(english, "Category", "類別"))).Append("</th><th>")
          .Append(Esc(T(english, "Check-ins", "回報次數"))).Append("</th><th>")
          .Append(Esc(T(english, "Mean (0–20)", "平均（0–20）"))).Append("</th></tr></thead>\n<tbody>\n");
        foreach (var cat in e.ByCategory)
            sb.Append("<tr><td>").Append(Esc(cat.Category)).Append("</td><td>").Append(cat.Count)
              .Append("</td><td>").Append(Esc(Num(cat.MeanFms))).Append("</td></tr>\n");
        sb.Append("</tbody>\n</table>\n");
    }

    // ── SVG charts ───────────────────────────────────────────────────────────────

    private const int ChartW = 720, ChartH = 200, PadL = 44, PadR = 16, PadT = 14, PadB = 28;

    private static string LineChart(List<(string date, double? value)> points, double yMin, double yMax,
        string stroke, string caption, bool english)
    {
        var withVal = points.Where(p => p.value.HasValue).ToList();
        if (withVal.Count == 0) return Placeholder(caption, english);

        double plotW = ChartW - PadL - PadR, plotH = ChartH - PadT - PadB;
        int n = points.Count;
        double X(int i) => n <= 1 ? PadL + plotW / 2 : PadL + plotW * i / (n - 1);
        double Y(double v) => PadT + plotH * (1 - (Clamp(v, yMin, yMax) - yMin) / Math.Max(1e-9, yMax - yMin));

        var sb = new StringBuilder(2048);
        ChartOpen(sb, caption);
        Axes(sb, yMin, yMax);

        // Polyline broken at null gaps: emit a fresh <polyline> per contiguous run.
        var run = new List<string>();
        void Flush()
        {
            if (run.Count >= 2)
                sb.Append("<polyline fill=\"none\" stroke=\"").Append(stroke).Append("\" stroke-width=\"2\" points=\"")
                  .Append(string.Join(" ", run)).Append("\"/>\n");
            run.Clear();
        }
        for (int i = 0; i < n; i++)
        {
            if (points[i].value is double v)
                run.Add($"{F(X(i))},{F(Y(v))}");
            else Flush();
        }
        Flush();
        // Dots for each real point (so a lone day still shows).
        for (int i = 0; i < n; i++)
            if (points[i].value is double v)
                sb.Append("<circle cx=\"").Append(F(X(i))).Append("\" cy=\"").Append(F(Y(v)))
                  .Append("\" r=\"2.5\" fill=\"").Append(stroke).Append("\"/>\n");
        AxisDates(sb, points);
        ChartClose(sb);
        return sb.ToString();
    }

    private static string BarChart(List<(string date, int? value)> points, string fill, string caption, bool english)
    {
        var withVal = points.Where(p => p.value.HasValue).ToList();
        if (withVal.Count == 0 || withVal.All(p => p.value!.Value == 0)) return Placeholder(caption, english);

        double yMax = NiceMax(points.Where(p => p.value.HasValue).Select(p => (double)p.value!.Value), 5);
        double plotW = ChartW - PadL - PadR, plotH = ChartH - PadT - PadB;
        int n = points.Count;
        double slot = plotW / Math.Max(1, n);
        double barW = Math.Max(1.5, Math.Min(18, slot * 0.6));

        var sb = new StringBuilder(2048);
        ChartOpen(sb, caption);
        Axes(sb, 0, yMax);
        for (int i = 0; i < n; i++)
        {
            if (points[i].value is not int iv || iv <= 0) continue;
            double h = plotH * (Clamp(iv, 0, yMax) / Math.Max(1e-9, yMax));
            double x = PadL + slot * i + (slot - barW) / 2;
            double y = PadT + plotH - h;
            sb.Append("<rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(y)).Append("\" width=\"").Append(F(barW))
              .Append("\" height=\"").Append(F(h)).Append("\" fill=\"").Append(fill).Append("\" rx=\"1\"/>\n");
        }
        AxisDates(sb, points.Select(p => (p.date, (double?)(p.value.HasValue ? p.value!.Value : (double?)null))).ToList());
        ChartClose(sb);
        return sb.ToString();
    }

    private static void ChartOpen(StringBuilder sb, string caption)
    {
        sb.Append("<figure class=\"chart\">\n<figcaption>").Append(Esc(caption)).Append("</figcaption>\n");
        sb.Append("<svg viewBox=\"0 0 ").Append(ChartW).Append(' ').Append(ChartH)
          .Append("\" role=\"img\" aria-label=\"").Append(Esc(caption)).Append("\" xmlns=\"http://www.w3.org/2000/svg\">\n");
    }

    private static void ChartClose(StringBuilder sb) => sb.Append("</svg>\n</figure>\n");

    private static void Axes(StringBuilder sb, double yMin, double yMax)
    {
        double plotH = ChartH - PadT - PadB;
        // 3 horizontal gridlines + y labels.
        for (int g = 0; g <= 2; g++)
        {
            double frac = g / 2.0;
            double y = PadT + plotH * frac;
            double val = yMax - (yMax - yMin) * frac;
            sb.Append("<line x1=\"").Append(PadL).Append("\" y1=\"").Append(F(y)).Append("\" x2=\"").Append(ChartW - PadR)
              .Append("\" y2=\"").Append(F(y)).Append("\" stroke=\"").Append(Hairline).Append("\" stroke-width=\"1\"/>\n");
            sb.Append("<text x=\"").Append(PadL - 6).Append("\" y=\"").Append(F(y + 3))
              .Append("\" text-anchor=\"end\" font-size=\"10\" fill=\"").Append(InkSoft).Append("\">")
              .Append(Esc(Num(val))).Append("</text>\n");
        }
    }

    private static void AxisDates(StringBuilder sb, List<(string date, double? value)> points)
    {
        if (points.Count == 0) return;
        double plotW = ChartW - PadL - PadR;
        void Label(int i, string anchor)
        {
            double x = points.Count <= 1 ? PadL + plotW / 2 : PadL + plotW * i / (points.Count - 1);
            sb.Append("<text x=\"").Append(F(x)).Append("\" y=\"").Append(ChartH - 8)
              .Append("\" text-anchor=\"").Append(anchor).Append("\" font-size=\"10\" fill=\"").Append(InkSoft).Append("\">")
              .Append(Esc(points[i].date)).Append("</text>\n");
        }
        Label(0, "start");
        if (points.Count > 1) Label(points.Count - 1, "end");
    }

    private static string Placeholder(string caption, bool english)
    {
        var sb = new StringBuilder(256);
        sb.Append("<figure class=\"chart\">\n<figcaption>").Append(Esc(caption)).Append("</figcaption>\n");
        sb.Append("<p class=\"nodata\">").Append(Esc(T(english, "Not enough data yet.", "資料尚不足。"))).Append("</p>\n</figure>\n");
        return sb.ToString();
    }

    // ── Style ────────────────────────────────────────────────────────────────────

    private static void AppendStyle(StringBuilder sb)
    {
        sb.Append("<style>\n")
          .Append("*{box-sizing:border-box}")
          .Append("body{margin:0;background:").Append(PageBg).Append(";color:").Append(Ink)
          .Append(";font-family:-apple-system,Segoe UI,Roboto,'Microsoft JhengHei',sans-serif;line-height:1.55}")
          .Append(".report{max-width:820px;margin:0 auto;padding:32px 24px}")
          .Append("h1{font-size:22px;margin:0 0 4px;color:").Append(Heading).Append("}")
          .Append("h2{font-size:16px;margin:0 0 8px;color:").Append(Heading).Append("}")
          .Append("h3{font-size:13px;margin:18px 0 6px;color:").Append(Ink).Append("}")
          .Append(".meta{color:").Append(InkSoft).Append(";font-size:12px;margin:0 0 8px}")
          .Append("header{border-bottom:1px solid ").Append(Hairline).Append(";padding-bottom:14px;margin-bottom:8px}")
          .Append("section{background:").Append(CardBg).Append(";border:1px solid ").Append(Hairline)
          .Append(";border-radius:10px;padding:16px 18px;margin:14px 0}")
          .Append("p{margin:0 0 8px}")
          .Append("table.grid{width:100%;border-collapse:collapse;font-size:13px;margin:8px 0}")
          .Append("table.grid th,table.grid td{text-align:left;padding:6px 8px;border-bottom:1px solid ").Append(Hairline).Append("}")
          .Append("table.grid th{color:").Append(InkSoft).Append(";font-weight:600}")
          .Append("table.grid td:nth-child(2),table.grid td:nth-child(3){text-align:right;font-variant-numeric:tabular-nums}")
          .Append("table.grid th:nth-child(2),table.grid th:nth-child(3){text-align:right}")
          .Append("ul.posture{list-style:none;margin:8px 0 0;padding:0;font-size:13px}")
          .Append("ul.posture li{display:flex;justify-content:space-between;padding:4px 0;border-bottom:1px solid ").Append(Hairline).Append("}")
          .Append("ul.posture .k{color:").Append(InkSoft).Append("}ul.posture .v{font-variant-numeric:tabular-nums}")
          .Append(".chart{margin:10px 0 4px}.chart figcaption{font-size:12px;color:").Append(InkSoft).Append(";margin-bottom:4px}")
          .Append(".chart svg{width:100%;height:auto;display:block}")
          .Append(".nodata{font-size:12px;color:").Append(InkSoft).Append(";font-style:italic}")
          .Append("footer{margin-top:16px}.disclaimer{font-size:11px;color:").Append(InkSoft).Append("}")
          .Append("@media print{body{background:#fff}section{break-inside:avoid}}")
          .Append("\n</style>\n");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string T(bool english, string en, string zh) => english ? en : zh;
    private static string Num(double? v) => v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture) : "—";
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>A clean axis ceiling ≥ the data max (and ≥ <paramref name="floor"/>): round up to 1/2/5×10ⁿ.</summary>
    private static double NiceMax(IEnumerable<double> values, double floor)
    {
        double max = floor;
        foreach (double v in values) if (v > max) max = v;
        if (max <= 0) return floor;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (double step in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (step * mag >= max) return step * mag;
        return 10 * mag;
    }

    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&#39;");
    }
}
