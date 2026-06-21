using System;
using System.Collections.Generic;
using System.Globalization;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Learning-loop Part D / Idea #1 (2026-06-21): the REPORT-ONLY "overlay offered, not engaged" aggregator
/// (<see cref="ConsentEngagementBuilder"/>). Pins the count/rate math, the "asked" denominator, the honest
/// null on no-data, and the safety partition — the metric is computed ONLY from <c>overlay_consent</c> rows
/// (Measured=false), never from FMS rows, so it can never become a measured-FMS relax signal.
/// </summary>
public sealed class ConsentEngagementTests
{
    private const string Day1 = "2026-06-10T10:00:00.0000000Z";
    private const string Day2 = "2026-06-11T10:00:00.0000000Z";

    private static SymptomEvent Consent(string decision, string ts)
        => SymptomEvent.OverlayConsentRow(decision, contextTier: 6, maskActive: false,
               utcNow: DateTime.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static SymptomEvent Fms(int value, string ts)
        => new()
        {
            Kind = SymptomEvent.Kinds.FmsReport, Measured = true, Scale = SymptomEvent.Scales.Fms20,
            Source = SymptomEvent.Sources.Slider, Value = value, TsUtc = ts,
        };

    [Fact]
    public void EmptyInput_ZeroCounts_NullRate()
    {
        var s = ConsentEngagementBuilder.Build(Array.Empty<SymptomEvent>());
        Assert.Equal(0, s.AskedCount);
        Assert.Equal(0, s.IgnoredCount);
        Assert.Equal(0, s.DaysObserved);
        Assert.Null(s.OfferedNotEngagedRate);   // honest "no data", never a fabricated 0
    }

    [Fact]
    public void CountsByDecision_AndRateIsIgnoredOverAsked()
    {
        var rows = new List<SymptomEvent>
        {
            Consent(SymptomEvent.Decisions.Asked,    Day1),
            Consent(SymptomEvent.Decisions.Asked,    Day1),
            Consent(SymptomEvent.Decisions.Asked,    Day2),
            Consent(SymptomEvent.Decisions.Asked,    Day2),
            Consent(SymptomEvent.Decisions.Accepted, Day1),
            Consent(SymptomEvent.Decisions.Declined, Day1),
            Consent(SymptomEvent.Decisions.Ignored,  Day2),
        };

        var s = ConsentEngagementBuilder.Build(rows);

        Assert.Equal(4, s.AskedCount);
        Assert.Equal(1, s.AcceptedCount);
        Assert.Equal(1, s.DeclinedCount);
        Assert.Equal(1, s.IgnoredCount);
        Assert.Equal(2, s.DaysObserved);
        // "offered, not engaged" = ignored / asked = 1/4 = 0.25 (locked decision #4: per impactful-tier prompt).
        Assert.Equal(0.25, s.OfferedNotEngagedRate!.Value, 2);
    }

    [Fact]
    public void FmsRows_AreIgnored_OnlyConsentRowsCount()
    {
        // Safety partition: the aggregator reads ONLY overlay_consent rows. An FMS report mixed in must not
        // change any consent count — and (the inverse, proven by ClinicalExportTests) a consent row never
        // enters the FMS partition. The two timelines are disjoint by Kind.
        var rows = new List<SymptomEvent>
        {
            Consent(SymptomEvent.Decisions.Asked,   Day1),
            Consent(SymptomEvent.Decisions.Ignored, Day1),
            Fms(0,  Day1),   // a real FMS=0 self-report — must NOT be miscounted as a consent "ignored"
            Fms(12, Day2),
        };

        var s = ConsentEngagementBuilder.Build(rows);

        Assert.Equal(1, s.AskedCount);
        Assert.Equal(1, s.IgnoredCount);
        Assert.Equal(0, s.AcceptedCount);
        Assert.Equal(1.0, s.OfferedNotEngagedRate!.Value, 2);   // 1 ignored / 1 asked — FMS rows excluded
    }

    [Fact]
    public void NoAsked_RateIsNull_EvenWithOtherRows()
    {
        // Denominator is the "asked" prompt count; with no prompt raised the rate is an honest null, not 0/0.
        var rows = new List<SymptomEvent>
        {
            Consent(SymptomEvent.Decisions.Accepted, Day1),
            Consent(SymptomEvent.Decisions.Ignored,  Day1),
        };

        var s = ConsentEngagementBuilder.Build(rows);

        Assert.Equal(0, s.AskedCount);
        Assert.Equal(1, s.IgnoredCount);
        Assert.Null(s.OfferedNotEngagedRate);
    }
}
