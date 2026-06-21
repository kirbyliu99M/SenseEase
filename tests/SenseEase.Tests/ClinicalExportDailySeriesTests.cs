using System;
using System.Linq;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// P1 (health-report redesign, docs/HEALTH_REPORT_PLAN.md §8): the de-identified per-day trend-series
/// builder <see cref="ClinicalExportBuilder.BuildDailySeries"/>. Pins the load-bearing guarantees the
/// hard review's build-gates require: MEASURED-only FMS (D-18/19), every timestamp bucketed to a
/// calendar date (D-17), an outer-join with honest NULL gaps (never a fabricated 0), and the
/// no-category/app-leak surface (the point carries no app-bearing field at all).
/// </summary>
public class ClinicalExportDailySeriesTests
{
    // The app writes TsUtc in round-trippable "o" form; a "yyyy-MM-ddT08:00:00.0000000Z" literal is a
    // valid subset that ParseDate reduces to the calendar date.
    private static SymptomEvent Fms(string date, int value, bool measured = true,
        string kind = SymptomEvent.Kinds.FmsReport)
        => new() { TsUtc = date + "T08:00:00.0000000Z", Kind = kind, Measured = measured, Value = value };

    private static DailyBehaviorAggregate Beh(string date, int ofiP90, int maskActivations)
        => new() { Date = date, OfiP90 = ofiP90, MaskActivations = maskActivations };

    [Fact]
    public void OuterJoin_OnePointPerDistinctDate_NullWhereSourceAbsent()
    {
        var fms = new[] { Fms("2026-06-01", 4), Fms("2026-06-01", 8), Fms("2026-06-03", 10) };
        var beh = new[] { Beh("2026-06-02", 70, 3), Beh("2026-06-03", 90, 5) };

        var series = ClinicalExportBuilder.BuildDailySeries(fms, beh);

        Assert.Equal(new[] { "2026-06-01", "2026-06-02", "2026-06-03" }, series.Select(p => p.Date));

        var d1 = series[0]; // FMS only
        Assert.Equal(6.0, d1.MeasuredFmsMean);   // (4 + 8) / 2
        Assert.Equal(2, d1.MeasuredFmsCount);
        Assert.Null(d1.OfiP90);
        Assert.Null(d1.MaskActivations);

        var d2 = series[1]; // behaviour only — honest gap on FMS, not a fabricated 0
        Assert.Null(d2.MeasuredFmsMean);
        Assert.Null(d2.MeasuredFmsCount);
        Assert.Equal(70, d2.OfiP90);
        Assert.Equal(3, d2.MaskActivations);

        var d3 = series[2]; // both sources
        Assert.Equal(10.0, d3.MeasuredFmsMean);
        Assert.Equal(1, d3.MeasuredFmsCount);
        Assert.Equal(90, d3.OfiP90);
        Assert.Equal(5, d3.MaskActivations);
    }

    [Fact]
    public void MeasuredOnly_EstimateAndNonFmsKind_Excluded()
    {
        var rows = new[]
        {
            Fms("2026-06-01", 4, measured: true),
            Fms("2026-06-01", 20, measured: false),                                          // estimate → out
            Fms("2026-06-01", 18, measured: false, kind: SymptomEvent.Kinds.FmsEstimate),    // estimate → out
            new SymptomEvent { TsUtc = "2026-06-01T09:00:00.0000000Z",
                               Kind = SymptomEvent.Kinds.OverlayConsent, Measured = false, Value = 0 }, // non-FMS → out
        };

        var series = ClinicalExportBuilder.BuildDailySeries(rows, Array.Empty<DailyBehaviorAggregate>());

        Assert.Single(series);
        Assert.Equal(4.0, series[0].MeasuredFmsMean);  // ONLY the measured 4 (estimate/consent never merged)
        Assert.Equal(1, series[0].MeasuredFmsCount);
    }

    [Fact]
    public void Deid_DateIsCalendarOnly_AndPointCarriesNoAppOrCategoryField()
    {
        var series = ClinicalExportBuilder.BuildDailySeries(
            new[] { Fms("2026-06-01", 5) }, new[] { Beh("2026-06-01", 60, 2) });

        // D-17: the date is calendar-only (no sub-date time leaks onto an export/chart surface).
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", series[0].Date);

        // Build-gate 2 (structural): the per-day point has NO string field other than the date — so no
        // ForegroundKey / raw "category/app" value can ride into a chart label, legend, or tooltip.
        var stringProps = typeof(DailySeriesPoint).GetProperties()
            .Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Date" }, stringProps);
    }

    [Fact]
    public void Empty_Or_Null_Input_YieldsEmptySeries_NeverThrows()
    {
        Assert.Empty(ClinicalExportBuilder.BuildDailySeries(
            Array.Empty<SymptomEvent>(), Array.Empty<DailyBehaviorAggregate>()));
        Assert.Empty(ClinicalExportBuilder.BuildDailySeries(null!, null!));
    }

    [Fact]
    public void Unparseable_Timestamp_Day_IsDropped_NeverThrows()
    {
        var rows = new[]
        {
            new SymptomEvent { TsUtc = "not-a-date", Kind = SymptomEvent.Kinds.FmsReport, Measured = true, Value = 5 },
            Fms("2026-06-02", 7),
        };

        var series = ClinicalExportBuilder.BuildDailySeries(rows, Array.Empty<DailyBehaviorAggregate>());

        Assert.Single(series);                       // the bad-ts row is dropped, the good day survives
        Assert.Equal("2026-06-02", series[0].Date);
    }
}
