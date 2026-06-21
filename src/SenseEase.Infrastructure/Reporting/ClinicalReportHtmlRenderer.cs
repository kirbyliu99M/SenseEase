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
/// <para><b>Visual identity (2026-06-21 design pass, then app-aligned).</b> The report is the app's calm
/// LIGHT sibling, not an independent artifact: it adopts the app's navy ink + royal-blue accent (the
/// SettingsGlassPalette / popup family) and the app's own type system — <b>Segoe UI for prose, Consolas /
/// Cascadia Mono for every number and technical label</b> (the app uses exactly this pairing for its
/// readouts). That mono-on-data treatment is the deliberate "mechanical / instrument" feel; the one boxed
/// anchor is the at-a-glance band. The royal blue is the CALM sibling <c>#4B6EF5</c> — never the app's loud
/// <c>#024AD8</c>, which the calm rule + a test forbid in the report.</para>
///
/// <para><b>Calm palette, pinned LIGHT (Q7).</b> Inline SVG can't resolve theme tokens, so the colours are
/// baked to concrete hex: FMS / accent <c>#4B6EF5</c>, on-screen-motion p90 <c>#2F7D6E</c>,
/// protection-trigger bars <c>#9A6B22</c> (the calm siblings of the app's accent-blue / steady-green /
/// rising-amber). The flagged gamer-neon <c>#024AD8</c> (and the live chart's <c>#7B8DB0</c>/<c>#E54747</c>)
/// are deliberately NOT used — a test asserts their absence.</para>
///
/// <para><b>De-identification.</b> The renderer only ever sees the typed export DTO: per-day points carry
/// no category/app field at all, the per-category rows already hold the de-identified category label, and
/// the onset / consent summaries are pure counts + durations. Every dynamic string is HTML-escaped.</para>
///
/// <para><b>Medical guardrail.</b> The stat band + onset section show raw, unmatched logged numbers with
/// neutral labels and a standing caveat; nothing interprets the user's clinical state. The onset section is
/// deterministic (never narrated) and its labels are verified clean against <see cref="ReportClaimGuard"/>.</para>
/// </summary>
public static class ClinicalReportHtmlRenderer
{
    // Calm palette, baked to concrete hex (see class remarks). Series colours are the calm siblings of the
    // app's accent-blue / comfort-steady-green / comfort-rising-amber.
    private const string FmsStroke = "#4B6EF5";   // comfort line + identity accent (app royal-blue family)
    private const string OfiStroke = "#2F7D6E";   // on-screen-motion p90
    private const string MaskFill  = "#9A6B22";   // protection-trigger bars
    private const string Accent    = "#4B6EF5";   // the mark + each heading tick
    private const string Navy      = "#0E1E47";   // headings / identity ink (app TextPrimary family)
    private const string Ink       = "#1E2742";   // body ink (navy-slate)
    private const string InkSoft   = "#5B6A88";   // meta / secondary / sublabels
    private const string Label     = "#46557A";   // eyebrow / technical labels
    private const string BandBg    = "#EEF2FC";   // faint app light-blue glass — the one boxed anchor
    private const string BandLine  = "#D7E0F2";
    private const string PageBg    = "#FFFFFF";
    private const string Hairline  = "#E3E7F0";   // navy-tinted hairline

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

        // Our OWN running header, pinned top-right of every printed page (hidden on screen). With @page
        // margin:0 the browser's auto header/footer chrome (date / title / file URL / page number) is
        // suppressed, so this is the only header in the PDF. See AppendStyle's print block.
        sb.Append("<div class=\"print-header\">")
          .Append(Esc(T(english, "SenseEase · Visual comfort report", "SenseEase · 視覺舒適報告"))).Append("</div>\n");

        // Print / Save-as-PDF affordance — uses the browser's own print engine (no PDF dependency). Hidden in
        // the printed output itself (.no-print). An inline handler is used DELIBERATELY so the document stays
        // script-free (no <script> tag — the HTML-escaping test asserts none appears) and works offline from file://.
        sb.Append("<div class=\"toolbar no-print\"><button type=\"button\" onclick=\"window.print()\">")
          .Append(Esc(T(english, "Print / Save as PDF", "列印 / 存成 PDF"))).Append("</button></div>\n");

        // ── Header ────────────────────────────────────────────────────────────
        sb.Append("<header>\n<div class=\"mark\"></div>\n");
        sb.Append("<p class=\"eyebrow\">").Append(Esc(T(english, "SenseEase · On-device comfort summary", "SenseEase · 裝置端舒適彙整"))).Append("</p>\n");
        sb.Append("<h1>").Append(Esc(T(english, "Visual comfort report", "視覺舒適報告"))).Append("</h1>\n");
        // Friendly window range, not a cluttered "Generated …" timestamp (the generation time stays in the
        // JSON data record for provenance). e.g. "June 1 – June 14, 2026" / "2026年6月1日 – 6月14日".
        sb.Append("<p class=\"meta\">")
          .Append(Esc(FriendlyDateRange(export.Window.FirstDateUtc, export.Window.LastDateUtc, english)))
          .Append("  ·  ").Append(export.Window.DaysObserved).Append(' ')
          .Append(Esc(T(english, Plural(export.Window.DaysObserved, "day", "days"), "天")))
          .Append("</p>\n</header>\n");

        // ── At-a-glance band (the signature / hero) ─────────────────────────────
        AppendGlance(sb, export, english);

        // ── Summary ───────────────────────────────────────────────────────────
        sb.Append("<section>\n<h2>").Append(Esc(T(english, "Your SenseEase check-in", "你的使用紀錄"))).Append("</h2>\n");
        sb.Append("<p class=\"lead\">").Append(Esc(narrative.Summary)).Append("</p>\n</section>\n");

        // ── Comfort trend ───────────────────────────────────────────────────────
        sb.Append("<section>\n<h2>").Append(Esc(T(english, "Comfort over time", "舒適度變化"))).Append("</h2>\n");
        sb.Append("<p>").Append(Esc(narrative.ComfortTrend)).Append("</p>\n");
        sb.Append(LineChart(
            export.DailySeries.Select(p => (p.Date, p.MeasuredFmsMean)).ToList(),
            yMin: 0, yMax: 20, stroke: FmsStroke,
            caption: T(english, "Comfort check-ins", "舒適度回報"),
            note: T(english, "0–20, lower = calmer", "0–20，越低越平靜"),
            yUnit: "FMS",
            english: english));
        // #17: disclose how much of the comfort picture is self-reported vs language-inferred (kept separate,
        // never merged — D-18/19). Only shown when estimates exist.
        if (export.Estimated.Count > 0)
        {
            int m = export.Measured.Count, est = export.Estimated.Count;
            sb.Append("<p class=\"caveat\">").Append(Esc(T(english,
                $"Based on {m} measured {Plural(m, "check-in", "check-ins")}, plus {est} {Plural(est, "estimate", "estimates")} inferred from messages (shown separately, never merged into the trend).",
                $"根據 {m} 次實測回報，另有 {est} 次由訊息推估（分開呈現，不併入趨勢）。"))).Append("</p>\n");
        }
        sb.Append("</section>\n");

        // ── Protection ───────────────────────────────────────────────────────────
        sb.Append("<section>\n<h2>").Append(Esc(T(english, "Protection: on vs off", "防護：開啟與關閉"))).Append("</h2>\n");
        sb.Append("<p>").Append(Esc(narrative.Protection)).Append("</p>\n");
        AppendOnOffTable(sb, export, english);
        if (posture is not null) AppendPosture(sb, posture, english);
        sb.Append("</section>\n");

        // ── Exposure & on-screen motion ──────────────────────────────────────────
        sb.Append("<section>\n<h2>").Append(Esc(T(english, "Screen motion & protection", "畫面動態與防護"))).Append("</h2>\n");
        sb.Append("<p>").Append(Esc(narrative.Exposure)).Append("</p>\n");
        sb.Append(LineChart(
            export.DailySeries.Select(p => (p.Date, (double?)(p.OfiP90.HasValue ? p.OfiP90.Value : (double?)null))).ToList(),
            yMin: 0, yMax: NiceMax(export.DailySeries.Where(p => p.OfiP90.HasValue).Select(p => (double)p.OfiP90!.Value), 100),
            stroke: OfiStroke,
            caption: T(english, "Screen motion intensity", "畫面晃動程度"),
            note: null,
            yUnit: "",   // no "p90" tag — the 0/50/100 axis already conveys the scale (Kirby 2026-06-21)
            english: english));
        sb.Append(BarChart(
            export.DailySeries.Select(p => (p.Date, p.MaskActivations)).ToList(),
            fill: MaskFill,
            caption: T(english, "Protection activations per day", "每日防護啟動次數"),
            yUnit: T(english, "count", "次"),
            english: english));
        AppendCategoryTable(sb, export, english);
        AppendCategoryEffectiveness(sb, export, english);
        sb.Append("</section>\n");

        // ── Time to first discomfort signal (learning-loop ideas #1+#2, deterministic) ──
        AppendOnsetSection(sb, export, english);

        // ── Disclaimer ───────────────────────────────────────────────────────────
        sb.Append("<footer>\n<p class=\"disclaimer\">").Append(Esc(T(english,
            "A quick note: SenseEase is a visual-comfort tool, not a medical device. These figures are for your own reference and don't replace a doctor's assessment. (Rest assured — only dates and numbers are recorded here, never your private content.)",
            "小提醒：SenseEase 是協助視覺舒適的工具，並非醫療器材。這份數據僅供個人參考，不能代替醫師診斷。（請放心：本程式僅紀錄日期與數值，絕不收集你的隱私內容。）")))
          .Append("</p>\n</footer>\n");

        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    // ── At-a-glance band ─────────────────────────────────────────────────────────

    /// <summary>The lead: 2-4 stat tiles of the most decision-relevant logged figures, then a standing
    /// medical-guardrail caveat. Tiles render only when their source has data; the whole band is omitted when
    /// nothing qualifies (e.g. an empty export), so it never shows a wall of dashes.</summary>
    private static void AppendGlance(StringBuilder sb, ClinicalExport e, bool english)
    {
        var c = e.OnVsOff;
        bool hasMask = e.DailySeries.Any(d => d.MaskActivations.HasValue);
        int maskTotal = e.DailySeries.Where(d => d.MaskActivations.HasValue).Sum(d => d.MaskActivations!.Value);
        bool hasSat = e.Satisfaction is { ResponseCount: > 0 };
        bool any = c.CountOn > 0 || c.CountOff > 0 || e.Measured.Count > 0 || hasMask || hasSat;
        if (!any) return;

        int dOn = c.CountOn, dOff = c.CountOff, dDays = e.Window.DaysObserved;
        sb.Append("<div class=\"glance\">\n");
        if (c.CountOn > 0)
            Stat(sb, T(english, "Protection on", "開啟防護"), Num(c.MeanFmsOn),
                 T(english, $"{dOn} {Plural(dOn, "check-in", "check-ins")} · FMS 0–20", $"{dOn} 次 · FMS 0–20"));
        if (c.CountOff > 0)
            Stat(sb, T(english, "Protection off", "關閉防護"), Num(c.MeanFmsOff),
                 T(english, $"{dOff} {Plural(dOff, "check-in", "check-ins")}", $"{dOff} 次"));
        if (e.Measured.Count > 0)
            Stat(sb, T(english, "Check-ins", "回報次數"), e.Measured.Count.ToString(CultureInfo.InvariantCulture),
                 T(english, $"over {dDays} {Plural(dDays, "day", "days")}", $"共 {dDays} 天"));
        if (hasMask)
            Stat(sb, T(english, "Protection triggers", "防護觸發"), maskTotal.ToString(CultureInfo.InvariantCulture),
                 T(english, "across the period", "整個期間"));
        if (e.Satisfaction is { ResponseCount: > 0 } sat)
            Stat(sb, T(english, "Satisfaction", "滿意度"), $"{Num(sat.MeanScore)}/{sat.ScaleMax}",
                 T(english, $"{sat.ResponseCount} {Plural(sat.ResponseCount, "response", "responses")}", $"{sat.ResponseCount} 次回應"));
        sb.Append("</div>\n");

        sb.Append("<p class=\"caveat\">").Append(Esc(T(english,
            "Descriptive summaries of logged numbers — not matched comparisons or a medical assessment. On the 0–20 comfort scale, lower means calmer.",
            "以上為紀錄數字的描述性彙整，並非對照比較或醫療評估。在 0–20 舒適度量表上，數字越低代表越平靜。"))).Append("</p>\n");
    }

    private static void Stat(StringBuilder sb, string label, string value, string sub)
        => sb.Append("<div class=\"stat\"><p class=\"label\">").Append(Esc(label))
             .Append("</p><p class=\"value\">").Append(Esc(value))
             .Append("</p><p class=\"sub\">").Append(Esc(sub)).Append("</p></div>\n");

    // ── Sections / tables ──────────────────────────────────────────────────────

    private static void AppendOnOffTable(StringBuilder sb, ClinicalExport e, bool english)
    {
        var c = e.OnVsOff;
        sb.Append("<table class=\"grid\">\n<thead><tr><th></th><th>")
          .Append(Esc(T(english, "Check-ins", "回報次數"))).Append("</th><th>")
          .Append(Esc(T(english, "Avg. discomfort", "平均不適感"))).Append("</th></tr></thead>\n<tbody>\n");
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
        // APPROVED domain term (Kirby 2026-06-21): this IS the habituation-model level, so it uses the real
        // term — ZH "視覺適應進度" (the perfect term, per Kirby), EN "Adaptation progress" — NOT the invented
        // euphemism "relaxation / 放寬". Safe as a FIXED, reviewed static label: the runtime ReportClaimGuard
        // only scans LLM-generated prose (where the adapt/適應 stem STAYS banned, so the model can never claim
        // the *user* adapted), and the static BannedPhraseLint list does not contain adapt/適應. Don't revert.
        Li(sb, T(english, "Adaptation progress", "視覺適應進度"), $"{Num(p.HabituationLevel)}/100");
        Li(sb, T(english, "Fine-tune flexibility", "畫面微調彈性"),
           $"{Num(p.OffsetPp)} {T(english, "pp", "百分點")} (" + T(english, "cap", "上限") + $" {p.OffsetCapPp})");
        Li(sb, T(english, "Recent visual pressure", "近期視覺壓力"),
           T(english, $"Level {p.CurrentTier.ToString(CultureInfo.InvariantCulture)}", $"第 {p.CurrentTier.ToString(CultureInfo.InvariantCulture)} 級"));
        sb.Append("</ul>\n");
    }

    private static void Li(StringBuilder sb, string k, string v)
        => sb.Append("<li><span class=\"k\">").Append(Esc(k)).Append("</span><span class=\"v\">").Append(Esc(v)).Append("</span></li>\n");

    private static void AppendCategoryTable(StringBuilder sb, ClinicalExport e, bool english)
    {
        if (e.ByCategory is null || e.ByCategory.Count == 0) return;
        sb.Append("<h3>").Append(Esc(T(english, "Comfort by activity", "各類使用情境的舒適度"))).Append("</h3>\n");
        sb.Append("<table class=\"grid\">\n<thead><tr><th>")
          .Append(Esc(T(english, "Activity", "使用情境"))).Append("</th><th>")
          .Append(Esc(T(english, "Check-ins", "回報次數"))).Append("</th><th>")
          .Append(Esc(T(english, "Avg. discomfort", "平均不適感"))).Append("</th></tr></thead>\n<tbody>\n");
        foreach (var cat in e.ByCategory)
            sb.Append("<tr><td>").Append(Esc(CategoryDisplay(cat.Category, english))).Append("</td><td>").Append(cat.Count)
              .Append("</td><td>").Append(Esc(Num(cat.MeanFms))).Append("</td></tr>\n");
        sb.Append("</tbody>\n</table>\n");
    }

    /// <summary>#6: the within-category protection ON-vs-OFF MEASURED comfort means — "is protection helping
    /// in this kind of app?". Already computed + de-identified (<c>OnVsOffByCategory</c>); was never rendered.
    /// Means only (— where an arm has no data); the matched delta stays in the JSON record.</summary>
    private static void AppendCategoryEffectiveness(StringBuilder sb, ClinicalExport e, bool english)
    {
        if (e.OnVsOffByCategory is null || e.OnVsOffByCategory.Count == 0) return;
        sb.Append("<h3>").Append(Esc(T(english, "Protection effect by activity", "各使用情境的防護效果"))).Append("</h3>\n");
        sb.Append("<table class=\"grid\">\n<thead><tr><th>")
          .Append(Esc(T(english, "Activity", "使用情境"))).Append("</th><th>")
          .Append(Esc(T(english, "On (0–20)", "開啟（0–20）"))).Append("</th><th>")
          .Append(Esc(T(english, "Off (0–20)", "關閉（0–20）"))).Append("</th></tr></thead>\n<tbody>\n");
        foreach (var cc in e.OnVsOffByCategory)
            sb.Append("<tr><td>").Append(Esc(CategoryDisplay(cc.Category, english))).Append("</td><td>")
              .Append(Esc(Num(cc.MeanFmsOn))).Append("</td><td>").Append(Esc(Num(cc.MeanFmsOff))).Append("</td></tr>\n");
        sb.Append("</tbody>\n</table>\n");
    }

    /// <summary>#24: display label for a de-identified category key. The KEY stays English upstream (the
    /// de-identification choke point); only the rendered label is localized. Unknown keys pass through.</summary>
    private static string CategoryDisplay(string category, bool english)
    {
        if (english) return category;
        return category switch
        {
            "video"         => "觀看影片",
            "game"          => "遊戲",
            "browser"       => "網頁瀏覽",
            "social"        => "社群",
            "communication" => "通訊",
            "productivity"  => "生產力工具",
            "reading"       => "閱讀",
            "music"         => "音樂",
            "other"         => "其他",
            _               => category,
        };
    }

    /// <summary>Learning-loop ideas #1+#2 (report-only, DETERMINISTIC — never narrated, so it bypasses the
    /// number-lock; labels verified clean against <see cref="ReportClaimGuard"/>). A mean time-to-first-
    /// discomfort-signal table (overall + per channel) + the "overlay offered, not engaged" engagement line.
    /// Omitted entirely when neither source has data. Channels map to neutral labels (never raw keys).</summary>
    private static void AppendOnsetSection(StringBuilder sb, ClinicalExport e, bool english)
    {
        var o = e.OnsetTiming;
        var c = e.ConsentEngagement;
        bool hasOnset = o is not null && o.SessionCount > 0;
        bool hasConsent = c is not null && c.AskedCount > 0;
        if (!hasOnset && !hasConsent) return;

        sb.Append("<section>\n<h2>").Append(Esc(T(english, "Time to first discomfort signal", "首次不適訊號時間"))).Append("</h2>\n");
        sb.Append("<p>").Append(Esc(T(english,
            "Average minutes from opening the app to the first discomfort signal of a session, by how the signal was raised.",
            "從開啟程式到單次使用中首次出現不適訊號的平均分鐘，依訊號出現的方式分列。"))).Append("</p>\n");

        if (hasOnset)
        {
            sb.Append("<table class=\"grid\">\n<thead><tr><th></th><th>")
              .Append(Esc(T(english, "Sessions", "次數"))).Append("</th><th>")
              .Append(Esc(T(english, "Mean (min)", "平均（分）"))).Append("</th></tr></thead>\n<tbody>\n");
            OnsetRow(sb, T(english, "Overall", "整體"), o!.SessionCount, o.MeanMinutesOverall);
            foreach (var ch in o.ByChannel)
                OnsetRow(sb, ChannelLabel(ch.Channel, english), ch.SessionCount, ch.MeanMinutes);
            sb.Append("</tbody>\n</table>\n");
        }
        else
        {
            sb.Append("<p class=\"nodata\">").Append(Esc(T(english, "Not enough data yet.", "資料尚不足。"))).Append("</p>\n");
        }

        if (hasConsent)
        {
            // Idea #1 ride-along: a low-engagement indicator (NOT a comfort claim — the "tolerance" framing is
            // guard-banned). "Offered, not engaged" = prompts shown but never acted on, over prompts shown.
            string rate = c!.OfferedNotEngagedRate.HasValue
                ? c.OfferedNotEngagedRate.Value.ToString("0.##", CultureInfo.InvariantCulture)
                : "—";
            sb.Append("<ul class=\"posture\">\n");
            Li(sb, T(english, "Overlay prompts shown", "防護提示出現次數"), c.AskedCount.ToString(CultureInfo.InvariantCulture));
            Li(sb, T(english, "Offered, not engaged", "出現但未理會"),
               $"{c.IgnoredCount.ToString(CultureInfo.InvariantCulture)} ({rate})");
            sb.Append("</ul>\n");
        }
        sb.Append("</section>\n");

        static void OnsetRow(StringBuilder sb, string label, int count, double? value)
            => sb.Append("<tr><td>").Append(Esc(label)).Append("</td><td>").Append(count)
                 .Append("</td><td>").Append(Esc(Num(value))).Append("</td></tr>\n");
    }

    /// <summary>Raw onset channel key → a neutral, guard-safe label (never the raw "consent"/"fms"/"chat").</summary>
    private static string ChannelLabel(string channel, bool english) => channel switch
    {
        SessionOnsetChannels.Consent => T(english, "Overlay", "防護提示"),
        SessionOnsetChannels.Fms     => T(english, "Check-in", "舒適回報"),
        SessionOnsetChannels.Chat    => T(english, "Chat", "對話"),
        _                            => channel,
    };

    // ── SVG charts ───────────────────────────────────────────────────────────────

    private const int ChartW = 720, ChartH = 210, PadL = 44, PadR = 16, PadT = 18, PadB = 28;

    private static string LineChart(List<(string date, double? value)> points, double yMin, double yMax,
        string stroke, string caption, string? note, string yUnit, bool english)
    {
        var withVal = points.Where(p => p.value.HasValue).ToList();
        if (withVal.Count == 0) return Placeholder(caption, english);

        double plotW = ChartW - PadL - PadR, plotH = ChartH - PadT - PadB;
        int n = points.Count;
        double X(int i) => n <= 1 ? PadL + plotW / 2 : PadL + plotW * i / (n - 1);
        double Y(double v) => PadT + plotH * (1 - (Clamp(v, yMin, yMax) - yMin) / Math.Max(1e-9, yMax - yMin));

        var sb = new StringBuilder(2048);
        ChartOpen(sb, caption, note);
        Axes(sb, yMin, yMax, yUnit);
        VGrid(sb, n);

        // Polyline broken at null gaps: emit a fresh <polyline> per contiguous run.
        var run = new List<string>();
        void Flush()
        {
            if (run.Count >= 2)
                sb.Append("<polyline fill=\"none\" stroke=\"").Append(stroke).Append("\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-linecap=\"round\" points=\"")
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
        XLabels(sb, points);
        ChartClose(sb);
        return sb.ToString();
    }

    private static string BarChart(List<(string date, int? value)> points, string fill, string caption, string yUnit, bool english)
    {
        var withVal = points.Where(p => p.value.HasValue).ToList();
        if (withVal.Count == 0 || withVal.All(p => p.value!.Value == 0)) return Placeholder(caption, english);

        double yMax = NiceMax(points.Where(p => p.value.HasValue).Select(p => (double)p.value!.Value), 5);
        double plotW = ChartW - PadL - PadR, plotH = ChartH - PadT - PadB;
        int n = points.Count;
        double barW = Math.Max(1.5, Math.Min(18, (plotW / Math.Max(1, n)) * 0.6));
        // #12: bars share the SAME x-scale as the line charts (centred on each day's point), so a trigger
        // bar lines up vertically with the motion-line spike for the same day, and with the date ticks.
        double X(int i) => n <= 1 ? PadL + plotW / 2 : PadL + plotW * i / (n - 1);

        var sb = new StringBuilder(2048);
        ChartOpen(sb, caption, null);
        Axes(sb, 0, yMax, yUnit);
        VGrid(sb, n);
        for (int i = 0; i < n; i++)
        {
            if (points[i].value is not int iv || iv <= 0) continue;
            double h = plotH * (Clamp(iv, 0, yMax) / Math.Max(1e-9, yMax));
            double cx = X(i);
            double y = PadT + plotH - h;
            sb.Append("<rect x=\"").Append(F(cx - barW / 2)).Append("\" y=\"").Append(F(y)).Append("\" width=\"").Append(F(barW))
              .Append("\" height=\"").Append(F(h)).Append("\" fill=\"").Append(fill).Append("\" rx=\"1\"/>\n");
            // Direct value label above each bar (chart-rule: bars carry their value).
            sb.Append("<text x=\"").Append(F(cx)).Append("\" y=\"").Append(F(y - 4))
              .Append("\" text-anchor=\"middle\" font-size=\"9\" fill=\"").Append(Ink).Append("\">").Append(iv).Append("</text>\n");
        }
        XLabels(sb, points.Select(p => (p.date, (double?)(p.value.HasValue ? p.value!.Value : (double?)null))).ToList());
        ChartClose(sb);
        return sb.ToString();
    }

    private static void ChartOpen(StringBuilder sb, string caption, string? note)
    {
        sb.Append("<figure class=\"chart\">\n<figcaption>").Append(Esc(caption));
        if (!string.IsNullOrEmpty(note))
            sb.Append(" <span class=\"unit\">· ").Append(Esc(note!)).Append("</span>");
        sb.Append("</figcaption>\n");
        string aria = note is null ? caption : caption + " (" + note + ")";
        sb.Append("<svg viewBox=\"0 0 ").Append(ChartW).Append(' ').Append(ChartH)
          .Append("\" role=\"img\" aria-label=\"").Append(Esc(aria)).Append("\" xmlns=\"http://www.w3.org/2000/svg\">\n");
    }

    private static void ChartClose(StringBuilder sb) => sb.Append("</svg>\n</figure>\n");

    private static void Axes(StringBuilder sb, double yMin, double yMax, string yUnit)
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
        // Y-axis unit tag (top-right) so a cropped/printed fragment still says what it measures.
        if (!string.IsNullOrEmpty(yUnit))
            sb.Append("<text x=\"").Append(ChartW - PadR).Append("\" y=\"12\" text-anchor=\"end\" font-size=\"9\" fill=\"")
              .Append(InkSoft).Append("\">").Append(Esc(yUnit)).Append("</text>\n");
    }

    /// <summary>Up to 5 evenly-spaced x tick indices (always including first + last).</summary>
    private static List<int> TickIndices(int n)
    {
        var idx = new List<int>();
        if (n <= 0) return idx;
        if (n <= 2) { idx.Add(0); if (n == 2) idx.Add(1); return idx; }
        int ticks = Math.Min(5, n);
        var seen = new HashSet<int>();
        for (int t = 0; t < ticks; t++)
        {
            int i = (int)Math.Round((double)t * (n - 1) / (ticks - 1));
            if (seen.Add(i)) idx.Add(i);
        }
        return idx;
    }

    /// <summary>Faint vertical gridlines at the tick indices — drawn under the data.</summary>
    private static void VGrid(StringBuilder sb, int n)
    {
        double plotW = ChartW - PadL - PadR;
        double X(int i) => n <= 1 ? PadL + plotW / 2 : PadL + plotW * i / (n - 1);
        foreach (int i in TickIndices(n))
            sb.Append("<line x1=\"").Append(F(X(i))).Append("\" y1=\"").Append(PadT).Append("\" x2=\"").Append(F(X(i)))
              .Append("\" y2=\"").Append(ChartH - PadB).Append("\" stroke=\"").Append(Hairline)
              .Append("\" stroke-width=\"1\" stroke-opacity=\"0.55\"/>\n");
    }

    /// <summary>Date labels at the tick indices: full yyyy-MM-dd at the ends, MM-DD in between (less crowding).</summary>
    private static void XLabels(StringBuilder sb, List<(string date, double? value)> points)
    {
        int n = points.Count;
        if (n == 0) return;
        double plotW = ChartW - PadL - PadR;
        double X(int i) => n <= 1 ? PadL + plotW / 2 : PadL + plotW * i / (n - 1);
        foreach (int i in TickIndices(n))
        {
            bool end = i == 0 || i == n - 1;
            string d = points[i].date;
            string text = end || d.Length < 10 ? d : d.Substring(5);   // MM-DD for interior ticks
            string anchor = i == 0 ? "start" : (i == n - 1 ? "end" : "middle");
            sb.Append("<text x=\"").Append(F(X(i))).Append("\" y=\"").Append(ChartH - 8)
              .Append("\" text-anchor=\"").Append(anchor).Append("\" font-size=\"9\" fill=\"").Append(InkSoft).Append("\">")
              .Append(Esc(text)).Append("</text>\n");
        }
    }

    private static string Placeholder(string caption, bool english)
    {
        var sb = new StringBuilder(256);
        sb.Append("<figure class=\"chart\">\n<figcaption>").Append(Esc(caption)).Append("</figcaption>\n");
        sb.Append("<p class=\"nodata\">").Append(Esc(T(english, "Not enough data yet.", "資料尚不足。"))).Append("</p>\n</figure>\n");
        return sb.ToString();
    }

    // ── Style ────────────────────────────────────────────────────────────────────

    // The app's own type system: Segoe UI (Variable) for prose, Consolas / Cascadia Mono for data + labels.
    private const string SansStack = "'Segoe UI Variable Text','Segoe UI','Microsoft JhengHei UI','Microsoft JhengHei',system-ui,sans-serif";
    private const string MonoStack = "Consolas,'Cascadia Mono','SFMono-Regular',ui-monospace,monospace";

    private static void AppendStyle(StringBuilder sb)
    {
        sb.Append("<style>\n")
          .Append("*{box-sizing:border-box}")
          .Append("body{margin:0;background:").Append(PageBg).Append(";color:").Append(Ink)
          .Append(";font-family:").Append(SansStack).Append(";line-height:1.6;-webkit-print-color-adjust:exact;print-color-adjust:exact}")
          .Append(".report{max-width:760px;margin:0 auto;padding:40px 28px 56px}")
          // header / identity
          .Append(".mark{width:34px;height:3px;background:").Append(Accent).Append(";border-radius:2px;margin-bottom:14px}")
          .Append(".eyebrow{font-family:").Append(MonoStack).Append(";margin:0 0 6px;font-size:11px;font-weight:600;letter-spacing:.16em;text-transform:uppercase;color:").Append(Label).Append("}")
          .Append("h1{font-size:26px;line-height:1.2;margin:0 0 8px;color:").Append(Navy).Append(";letter-spacing:-.01em;font-weight:700}")
          .Append(".meta{font-family:").Append(MonoStack).Append(";color:").Append(InkSoft).Append(";font-size:11.5px;letter-spacing:.01em;margin:0}")
          .Append("header{padding-bottom:18px}")
          // at-a-glance band
          .Append(".glance{display:flex;flex-wrap:wrap;gap:1px;background:").Append(BandLine)
          .Append(";border:1px solid ").Append(BandLine).Append(";border-radius:12px;overflow:hidden;margin:18px 0 8px}")
          .Append(".stat{flex:1 1 130px;background:").Append(BandBg).Append(";padding:14px 16px}")
          .Append(".stat .label{font-family:").Append(MonoStack).Append(";font-size:10px;font-weight:600;letter-spacing:.1em;text-transform:uppercase;color:").Append(Label).Append(";margin:0 0 6px}")
          .Append(".stat .value{font-family:").Append(MonoStack).Append(";font-size:27px;font-weight:700;color:").Append(Navy).Append(";line-height:1;margin:0}")
          .Append(".stat .sub{font-size:11px;color:").Append(InkSoft).Append(";margin:4px 0 0}")
          .Append(".caveat{font-size:11px;color:").Append(InkSoft).Append(";font-style:italic;margin:0}")
          // sections
          .Append("section{padding-top:22px;margin-top:22px;border-top:1px solid ").Append(Hairline).Append("}")
          .Append("h2{font-size:15px;margin:0 0 9px;color:").Append(Navy).Append(";font-weight:700;border-left:3px solid ").Append(Accent).Append(";padding-left:9px}")
          .Append("h3{font-family:").Append(MonoStack).Append(";font-size:11px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:").Append(Label).Append(";margin:18px 0 6px}")
          .Append("p{margin:0 0 10px}")
          .Append(".lead{font-size:14px;color:").Append(Ink).Append("}")
          // tables — labels sans, figures mono
          .Append("table.grid{width:100%;border-collapse:collapse;font-size:13px;margin:8px 0}")
          .Append("table.grid th,table.grid td{text-align:left;padding:7px 8px;border-bottom:1px solid ").Append(Hairline).Append("}")
          .Append("table.grid th{font-family:").Append(MonoStack).Append(";color:").Append(Label).Append(";font-weight:600;font-size:10.5px;letter-spacing:.06em;text-transform:uppercase}")
          .Append("table.grid td:nth-child(2),table.grid td:nth-child(3){text-align:right;font-family:").Append(MonoStack).Append(";font-variant-numeric:tabular-nums}")
          .Append("table.grid th:nth-child(2),table.grid th:nth-child(3){text-align:right}")
          // posture / key-value list — values mono
          .Append("ul.posture{list-style:none;margin:10px 0 0;padding:0;font-size:13px}")
          .Append("ul.posture li{display:flex;justify-content:space-between;padding:5px 0;border-bottom:1px solid ").Append(Hairline).Append("}")
          .Append("ul.posture .k{color:").Append(InkSoft).Append("}ul.posture .v{font-family:").Append(MonoStack).Append(";font-weight:600;color:").Append(Navy).Append("}")
          // charts — mono axis/labels for the instrument feel
          .Append(".chart{margin:12px 0 4px}")
          .Append(".chart figcaption{font-size:12px;color:").Append(Ink).Append(";margin-bottom:6px;font-weight:600}")
          .Append(".chart .unit{font-weight:400;color:").Append(InkSoft).Append("}")
          .Append(".chart svg{width:100%;height:auto;display:block;font-family:").Append(MonoStack).Append("}")
          .Append(".nodata{font-size:12px;color:").Append(InkSoft).Append(";font-style:italic}")
          // footer
          .Append("footer{margin-top:28px;padding-top:14px;border-top:1px solid ").Append(Hairline).Append("}")
          .Append(".disclaimer{font-size:11px;color:").Append(InkSoft).Append(";line-height:1.5;margin:0}")
          // print / save-as-PDF toolbar (screen only)
          .Append(".toolbar{display:flex;justify-content:flex-end;margin:0 0 10px}")
          .Append(".toolbar button{font-family:inherit;font-size:12px;font-weight:600;color:").Append(Navy)
          .Append(";background:#fff;border:1px solid #C7D0E6;border-radius:8px;padding:7px 13px;cursor:pointer}")
          .Append(".toolbar button:hover{background:").Append(BandBg).Append(";border-color:").Append(Accent).Append("}")
          .Append(".print-header{display:none}")
          // print: @page margin:0 suppresses the browser's auto header/footer chrome (date/title/URL/page#);
          // restore the margins with content padding, and show our own running header at the top-right.
          .Append("@media print{@page{margin:0}body{background:#fff}.report{max-width:100%;padding:14mm}")
          .Append(".no-print{display:none!important}")
          .Append(".print-header{display:block;position:fixed;top:7mm;right:14mm;font-family:").Append(MonoStack)
          .Append(";font-size:9px;letter-spacing:.04em;color:").Append(InkSoft).Append("}")
          .Append("section,figure,.glance{break-inside:avoid}h2{break-after:avoid}}")
          .Append("\n</style>\n");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string T(bool english, string en, string zh) => english ? en : zh;
    /// <summary>#16: EN plural picker so the report stops emitting the placeholder-looking "day(s)" / "(s)".
    /// English only — ZH has no plural inflection, so callers pass a plain ZH string.</summary>
    private static string Plural(int n, string singular, string plural) => n == 1 ? singular : plural;

    /// <summary>A human date range from the two yyyy-MM-dd bucket keys: "June 1 – June 14, 2026" /
    /// "2026年6月1日 – 6月14日". Falls back to the raw arrow form if a key won't parse.</summary>
    private static string FriendlyDateRange(string? firstIso, string? lastIso, bool english)
    {
        if (!DateTime.TryParse(firstIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) ||
            !DateTime.TryParse(lastIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
            return $"{firstIso ?? "—"} → {lastIso ?? "—"}";
        if (english)
            return a.Year == b.Year
                ? $"{a.ToString("MMMM d", CultureInfo.InvariantCulture)} – {b.ToString("MMMM d", CultureInfo.InvariantCulture)}, {b.Year}"
                : $"{a.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)} – {b.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}";
        return a.Year == b.Year
            ? $"{a.Year}年{a.Month}月{a.Day}日 – {b.Month}月{b.Day}日"
            : $"{a.Year}年{a.Month}月{a.Day}日 – {b.Year}年{b.Month}月{b.Day}日";
    }

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
