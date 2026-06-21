using System.Collections.Generic;
using SenseEase.Core.Inference;
using SenseEase.Infrastructure.Reporting;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Health-report redesign §8 (P2): the pure HTML+SVG renderer
/// <see cref="ClinicalReportHtmlRenderer.Render"/>. Pins: it emits a self-contained document with charts,
/// it uses the calm LIGHT palette and NOT the flagged neon strokes (Q7), it drops the guarded narrative
/// in verbatim, it HTML-escapes dynamic content, and it renders fully in both languages.</summary>
public class ClinicalReportHtmlRendererTests
{
    private static ClinicalExport SampleExport() => new()
    {
        GeneratedUtc = "2026-06-21T08:00:00.0000000Z",
        Window   = new ExportWindow { FirstDateUtc = "2026-06-01", LastDateUtc = "2026-06-14", DaysObserved = 14, TotalDataPoints = 20 },
        Measured = new FmsAggregate { Count = 10, Mean = 6.2, BaselineMean = 8, OutcomeMean = 5, ChangeFromBaseline = -3 },
        OnVsOff  = new OnOffContrast { CountOn = 6, CountOff = 4, MeanFmsOn = 5, MeanFmsOff = 9, Delta = 4 },
        ByCategory = new List<CategoryAggregate> { new() { Category = "video", Count = 5, MeanFms = 6 } },
        DailySeries = new List<DailySeriesPoint>
        {
            new() { Date = "2026-06-01", MeasuredFmsMean = 8, MeasuredFmsCount = 2, OfiP90 = 70, MaskActivations = 3 },
            new() { Date = "2026-06-02", MeasuredFmsMean = null, OfiP90 = null, MaskActivations = null },   // gap day
            new() { Date = "2026-06-03", MeasuredFmsMean = 5, MeasuredFmsCount = 1, OfiP90 = 90, MaskActivations = 5 },
        },
        OnsetTiming = new SessionFirstSignalSummary
        {
            SessionCount = 9,
            MeanMinutesOverall = 7.5,
            ByChannel = new List<ChannelOnset>
            {
                new() { Channel = "chat",    SessionCount = 3, MeanMinutes = 9 },
                new() { Channel = "consent", SessionCount = 4, MeanMinutes = 5.5 },
                new() { Channel = "fms",     SessionCount = 2, MeanMinutes = 8 },
            },
        },
        ConsentEngagement = new ConsentEngagementSummary
        {
            AskedCount = 10, AcceptedCount = 4, DeclinedCount = 2, IgnoredCount = 4, DaysObserved = 6,
            OfferedNotEngagedRate = 0.4,
        },
        OnVsOffByCategory = new List<CategoryContrast>
        {
            new() { Category = "video", CountOn = 3, CountOff = 2, MeanFmsOn = 6, MeanFmsOff = 9, Delta = 3 },
        },
        Satisfaction = new SatisfactionSummary { MeanScore = 4.2, ScaleMax = 5, ResponseCount = 7 },
    };

    private static ReportNarrative Narrative() => new()
    {
        Summary      = "SUMMARY_TOKEN",
        ComfortTrend = "COMFORT_TOKEN",
        Protection   = "PROTECTION_TOKEN",
        Exposure     = "EXPOSURE_TOKEN",
    };

    private static ReportPosture Posture() => new()
    {
        Strength = ProtectionStrength.Balanced, HabituationLevel = 42, OffsetPp = 3.5, OffsetCapPp = 10, CurrentTier = 4,
    };

    [Fact]
    public void Render_ProducesSelfContainedHtml_WithChartsAndNarrative()
    {
        string html = ClinicalReportHtmlRenderer.Render(SampleExport(), Narrative(), Posture(), english: true);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
        Assert.Contains("<svg", html);                              // inline-SVG charts present
        Assert.Contains("Visual comfort report", html);            // EN title
        Assert.Contains("SUMMARY_TOKEN", html);
        Assert.Contains("COMFORT_TOKEN", html);
        Assert.Contains("PROTECTION_TOKEN", html);
        Assert.Contains("EXPOSURE_TOKEN", html);
        Assert.Contains("video", html);                            // de-identified category label rendered
        Assert.Contains("not a medical device", html);             // disclaimer reused
    }

    [Fact]
    public void Render_UsesCalmPalette_NotTheFlaggedNeonStrokes()
    {
        string html = ClinicalReportHtmlRenderer.Render(SampleExport(), Narrative(), Posture(), english: true);

        // Calm light-theme strokes (Q7).
        Assert.Contains("#4B6EF5", html);   // FMS
        Assert.Contains("#2F7D6E", html);   // OFI p90
        Assert.Contains("#9A6B22", html);   // mask freq

        // The flagged live-chart colours must NOT appear (calm CARDINAL RULE).
        Assert.DoesNotContain("#024AD8", html);
        Assert.DoesNotContain("#7B8DB0", html);
        Assert.DoesNotContain("#E54747", html);
    }

    [Fact]
    public void Render_EscapesDynamicContent()
    {
        var n = new ReportNarrative { Summary = "<script>alert('x')</script>", ComfortTrend = "a & b < c" };
        string html = ClinicalReportHtmlRenderer.Render(SampleExport(), n, null, english: true);

        Assert.DoesNotContain("<script>", html);     // the injected tag is neutralised
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("a &amp; b &lt; c", html);
    }

    [Fact]
    public void Render_Chinese_RendersFullyInChinese()
    {
        string html = ClinicalReportHtmlRenderer.Render(SampleExport(), Narrative(), Posture(), english: false);

        Assert.Contains("視覺舒適報告", html);   // ZH title
        Assert.Contains("並非醫療器材", html);   // ZH disclaimer
        Assert.DoesNotContain("Visual comfort report", html);
    }

    [Fact]
    public void Render_EmptyData_StillRenders_WithPlaceholders()
    {
        var empty = new ClinicalExport { GeneratedUtc = "2026-06-21T08:00:00.0000000Z" };
        string html = ClinicalReportHtmlRenderer.Render(empty, new ReportNarrative(), null, english: true);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("Not enough data yet.", html);   // chart placeholder, no throw
        Assert.DoesNotContain("Time to first discomfort signal", html);   // onset section omitted with no data
    }

    [Fact]
    public void Render_OnsetSection_ShowsNeutralChannelsAndConsentEngagement()
    {
        string html = ClinicalReportHtmlRenderer.Render(SampleExport(), Narrative(), Posture(), english: true);

        Assert.Contains("Time to first discomfort signal", html);   // the deterministic onset section
        Assert.Contains("Overlay", html);                           // consent channel → neutral label (not "consent")
        Assert.Contains("Chat", html);
        Assert.Contains("Offered, not engaged", html);              // idea #1 ride-along, guard-safe wording
        // Never the guard-banned framing for the engagement metric.
        Assert.DoesNotContain("toler", html.ToLowerInvariant());
        Assert.DoesNotContain("adapted", html.ToLowerInvariant());
    }

    [Fact]
    public void Render_UsesTheAppTypeSystem_SegoeUiAndConsolas()
    {
        string html = ClinicalReportHtmlRenderer.Render(SampleExport(), Narrative(), Posture(), english: true);

        Assert.Contains("Segoe UI", html);   // app prose font
        Assert.Contains("Consolas", html);   // app data / technical-readout font
    }

    [Fact]
    public void Render_HasPrintButton_ScriptFree_HiddenInPrint()
    {
        string html = ClinicalReportHtmlRenderer.Render(SampleExport(), Narrative(), Posture(), english: true);

        Assert.Contains("window.print()", html);   // the Print / Save-as-PDF affordance
        Assert.Contains("no-print", html);         // … hidden in the printed output itself
        Assert.DoesNotContain("<script", html);    // stays script-free (inline handler only, no dependency)
        Assert.Contains("@page{margin:0}", html);  // suppress the browser's auto header/footer chrome
        Assert.Contains("print-header", html);     // our own running header (top-right of each printed page)
    }

    [Fact]
    public void Render_SurfacesPerCategoryEffectivenessAndSatisfaction()
    {
        string html = ClinicalReportHtmlRenderer.Render(SampleExport(), Narrative(), Posture(), english: true);

        Assert.Contains("Protection effect by activity", html);   // #6 — computed but previously unrendered
        Assert.Contains("Satisfaction", html);                    // #20 — CSAT tile
    }

    [Fact]
    public void Render_NoPlaceholderPluralHack_ForMultiCounts()
    {
        // #16: with counts > 1 the EN copy must read naturally — never the debug-looking "day(s)" / "(s)".
        string html = ClinicalReportHtmlRenderer.Render(SampleExport(), new ReportNarrative { Summary = "x" }, Posture(), english: true);
        Assert.DoesNotContain("day(s)", html);
        Assert.DoesNotContain("check-in(s)", html);
    }

    [Fact]
    public void Render_OfiAboveAssumedMax_DoesNotClipOrEmitNaN()
    {
        var e = new ClinicalExport
        {
            GeneratedUtc = "2026-06-21T08:00:00.0000000Z",
            DailySeries = new List<DailySeriesPoint>
            {
                new() { Date = "2026-06-01", OfiP90 = 250 },   // exceeds the assumed 0–100 floor
                new() { Date = "2026-06-02", OfiP90 = 120 },
            },
        };
        string html = ClinicalReportHtmlRenderer.Render(e, new ReportNarrative(), null, english: true);

        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
        Assert.Contains("#2F7D6E", html);   // the motion line is still drawn (ceiling grew to fit)
    }

    [Fact]
    public void Render_SingleDaySeries_ShowsLonePoint_NoNaN()
    {
        var e = new ClinicalExport
        {
            GeneratedUtc = "2026-06-21T08:00:00.0000000Z",
            Measured = new FmsAggregate { Count = 1 },
            DailySeries = new List<DailySeriesPoint> { new() { Date = "2026-06-01", MeasuredFmsMean = 7, MeasuredFmsCount = 1 } },
        };
        string html = ClinicalReportHtmlRenderer.Render(e, new ReportNarrative(), null, english: true);

        Assert.DoesNotContain("NaN", html);
        Assert.Contains("<circle", html);   // the single day still renders a dot (centered)
    }
}
