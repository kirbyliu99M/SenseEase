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
    }
}
