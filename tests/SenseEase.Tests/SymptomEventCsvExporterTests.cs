using System;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>The long-term-analysis raw CSV export of symptom events (header + per-event rows +
/// RFC-4180 quoting).</summary>
public sealed class SymptomEventCsvExporterTests
{
    [Fact]
    public void ToCsv_EmptyOrNull_IsHeaderOnly()
    {
        string csv = SymptomEventCsvExporter.ToCsv(Array.Empty<SymptomEvent>());
        Assert.StartsWith(
            "ts_utc,schema_version,kind,measured,scale,value,source,context_tier,intervention_state,trigger,foreground_key",
            csv);
        // exactly one (header) line
        Assert.Single(csv.TrimEnd('\r', '\n').Split('\n'));
        Assert.Equal(csv, SymptomEventCsvExporter.ToCsv(null));
    }

    [Fact]
    public void ToCsv_WritesOneRowPerEvent_WithExpectedColumns()
    {
        var ev = ComfortRatingScale.BuildMeasuredFmsReport(
            fms: 11, contextTier: 7, maskActive: true, foregroundKey: "video/vlc", trigger: "spike");
        ev.TsUtc = "2026-06-16T10:00:00.0000000Z";

        string csv = SymptomEventCsvExporter.ToCsv(new[] { ev });
        var lines = csv.TrimEnd('\r', '\n').Split(new[] { "\r\n" }, StringSplitOptions.None);

        Assert.Equal(2, lines.Length); // header + 1 row
        string row = lines[1];
        Assert.Equal(
            "2026-06-16T10:00:00.0000000Z,1,fms_report,true,fms20,11,slider,7,on,spike,video/vlc",
            row);
    }

    [Fact]
    public void ToCsv_QuotesFieldsWithCommasAndQuotes()
    {
        var ev = new SymptomEvent { ForegroundKey = "weird,key\"x" };
        string csv = SymptomEventCsvExporter.ToCsv(new[] { ev });
        Assert.Contains("\"weird,key\"\"x\"", csv);
    }
}
