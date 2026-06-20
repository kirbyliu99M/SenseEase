using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 Phase 3 / Theme 5 (D-17): the de-identified clinical export. Pins the non-negotiables:
/// measured (PRO) and estimated FMS are NEVER merged, the protection ON-vs-OFF contrast (overall +
/// matched-by-tier) is computed correctly, and — the privacy red line — the serialised file leaks
/// NO raw app name (only de-identified category labels + numbers).
/// </summary>
public sealed class ClinicalExportTests
{
    private const string Day1A = "2026-06-10T10:00:00.0000000Z";
    private const string Day1B = "2026-06-10T10:05:00.0000000Z";
    private const string Day2  = "2026-06-11T10:00:00.0000000Z";

    private static SymptomEvent Measured(int value, string state, int tier, string? fg, string ts)
        => new()
        {
            Kind = SymptomEvent.Kinds.FmsReport, Measured = true, Scale = SymptomEvent.Scales.Fms20,
            Source = SymptomEvent.Sources.Stars, Value = value, InterventionState = state,
            ContextTier = tier, ForegroundKey = fg, TsUtc = ts,
        };

    private static SymptomEvent Estimated(int value, string ts)
        => new()
        {
            Kind = SymptomEvent.Kinds.FmsEstimate, Measured = false, Scale = SymptomEvent.Scales.Fms20,
            Source = SymptomEvent.Sources.NlDaily, Value = value, TsUtc = ts,
        };

    private static SymptomEvent Consent(string decision, string ts)
        => SymptomEvent.OverlayConsentRow(decision, contextTier: 5, maskActive: true,
               utcNow: DateTime.Parse(ts, System.Globalization.CultureInfo.InvariantCulture,
                                      System.Globalization.DateTimeStyles.RoundtripKind));

    private static List<SymptomEvent> Sample() => new()
    {
        Measured(14, SymptomEvent.InterventionStates.Off, 5, "video/youtube", Day1A),
        Measured(4,  SymptomEvent.InterventionStates.On,  5, "video/vlc",     Day1B),
        Measured(10, SymptomEvent.InterventionStates.On,  8, "game/dota2",    Day2),
        Estimated(12, Day2),
    };

    [Fact]
    public void EmptyInput_ProducesZeroedAggregate_NotNull()
    {
        var e = ClinicalExportBuilder.Build(Array.Empty<SymptomEvent>());
        Assert.Equal(0, e.Measured.Count);
        Assert.Null(e.Measured.Mean);
        Assert.Equal(0, e.Estimated.Count);
        Assert.Empty(e.ByCategory);
        Assert.Empty(e.OnVsOffByTier);
        Assert.Empty(e.OnVsOffByCategory);
        Assert.Equal(0, e.Exposure.TotalSamples);
        Assert.Null(e.Window.FirstDateUtc);
        Assert.True(e.DeIdentified);
    }

    [Fact]
    public void MeasuredAndEstimated_AreAggregatedSeparately_NeverMerged()
    {
        var e = ClinicalExportBuilder.Build(Sample());

        // Measured = the 3 star reports (14,4,10) — the estimate (12) must NOT pull the mean.
        Assert.Equal(3, e.Measured.Count);
        Assert.Equal((14 + 4 + 10) / 3.0, e.Measured.Mean!.Value, 2);
        Assert.Equal(4, e.Measured.Min);
        Assert.Equal(14, e.Measured.Max);

        // Estimated = the single nl_daily estimate, on its own.
        Assert.Equal(1, e.Estimated.Count);
        Assert.Equal(12, e.Estimated.Mean!.Value, 2);
    }

    [Fact]
    public void OverlayConsentRows_DoNotContaminateTheEstimatedBucket_NorTheSampleCount()
    {
        // B6 Stage 0 regression: overlay_consent rows (Measured=false, Value=0, Scale="") must NEVER enter
        // the FMS partition. Before the FMS-kind filter they fell into Estimated (e => !e.Measured) as phantom
        // FMS=0 reports — inflating its count and dragging its mean toward 0 — and inflated TotalSamples.
        var withConsent = Sample();
        withConsent.Add(Consent(SymptomEvent.Decisions.Declined, Day1A));
        withConsent.Add(Consent(SymptomEvent.Decisions.Accepted, Day1B));
        withConsent.Add(Consent(SymptomEvent.Decisions.Ignored,  Day2));

        var e = ClinicalExportBuilder.Build(withConsent);

        // Estimated is STILL just the single nl_daily estimate (12): the 3 consent rows did not join it and
        // its mean is not dragged toward 0.
        Assert.Equal(1, e.Estimated.Count);
        Assert.Equal(12, e.Estimated.Mean!.Value, 2);
        // Measured untouched, and the total sample count excludes the consent rows.
        Assert.Equal(3, e.Measured.Count);
        Assert.Equal(4, e.Exposure.TotalSamples);   // 3 measured + 1 estimate — NOT 4 + 3 consent
    }

    [Fact]
    public void OnVsOff_Overall_ComputesDelta_OffMinusOn()
    {
        var e = ClinicalExportBuilder.Build(Sample()).OnVsOff;
        Assert.Equal(2, e.CountOn);                    // values 4, 10
        Assert.Equal(1, e.CountOff);                   // value 14
        Assert.Equal(7, e.MeanFmsOn!.Value, 2);        // (4+10)/2
        Assert.Equal(14, e.MeanFmsOff!.Value, 2);
        Assert.Equal(7, e.Delta!.Value, 2);            // 14 - 7 = +7 (lower discomfort with protection on)
    }

    [Fact]
    public void OnVsOffByTier_MatchedContrast_OnlyWhenBothArmsPresent()
    {
        var byTier = ClinicalExportBuilder.Build(Sample()).OnVsOffByTier;

        var t5 = byTier.Single(t => t.Tier == 5);
        Assert.Equal(4, t5.MeanFmsOn!.Value, 2);       // on @ tier5 = 4
        Assert.Equal(14, t5.MeanFmsOff!.Value, 2);     // off @ tier5 = 14
        Assert.Equal(10, t5.Delta!.Value, 2);

        var t8 = byTier.Single(t => t.Tier == 8);
        Assert.Equal(10, t8.MeanFmsOn!.Value, 2);
        Assert.Null(t8.MeanFmsOff);                    // no OFF sample at tier 8 …
        Assert.Null(t8.Delta);                         // … so no matched delta
    }

    [Fact]
    public void ByCategory_GroupsByDeIdentifiedCategory_StrippingAppName()
    {
        var cats = ClinicalExportBuilder.Build(Sample()).ByCategory;

        var video = cats.Single(c => c.Category == "video");
        Assert.Equal(2, video.Count);                  // youtube + vlc
        Assert.Equal((14 + 4) / 2.0, video.MeanFms!.Value, 2);

        var game = cats.Single(c => c.Category == "game");
        Assert.Equal(1, game.Count);

        // The raw app names must NOT survive as category labels.
        Assert.DoesNotContain(cats, c => c.Category.Contains("youtube"));
        Assert.DoesNotContain(cats, c => c.Category.Contains("vlc"));
        Assert.DoesNotContain(cats, c => c.Category.Contains("dota"));
    }

    [Fact]
    public void OnVsOffByCategory_MatchedContrast_OnlyWhenBothArmsPresent()
    {
        // B3 output ②: per-app effectiveness. video has BOTH arms (off=14 youtube, on=4 vlc) → a
        // matched delta; game has only an ON arm (10 dota2) → no delta (honest "not enough data").
        var byCat = ClinicalExportBuilder.Build(Sample()).OnVsOffByCategory;

        var video = byCat.Single(c => c.Category == "video");
        Assert.Equal(4, video.MeanFmsOn!.Value, 2);
        Assert.Equal(14, video.MeanFmsOff!.Value, 2);
        Assert.Equal(10, video.Delta!.Value, 2);       // 14 − 4 = +10 (protection helps in video apps)

        var game = byCat.Single(c => c.Category == "game");
        Assert.Equal(10, game.MeanFmsOn!.Value, 2);
        Assert.Null(game.MeanFmsOff);                   // no OFF sample in a game …
        Assert.Null(game.Delta);                        // … so no matched per-app delta
    }

    [Fact]
    public void OnVsOffByCategory_EmptyWhenNoForegroundKey()
    {
        // Per-app effectiveness only exists with per-app consent (a foreground key). Reports with no
        // key (consent off) must NOT manufacture a category bucket.
        var noKey = new List<SymptomEvent>
        {
            Measured(8,  SymptomEvent.InterventionStates.On,  5, null, Day1A),
            Measured(12, SymptomEvent.InterventionStates.Off, 5, null, Day1B),
        };
        Assert.Empty(ClinicalExportBuilder.Build(noKey).OnVsOffByCategory);
    }

    [Fact]
    public void OnVsOffByCategory_LeaksNoRawAppName()
    {
        // The privacy red line also holds for the new per-app contrast (D-17): category labels only.
        var byCat = ClinicalExportBuilder.Build(Sample()).OnVsOffByCategory;
        Assert.All(byCat, c =>
        {
            Assert.DoesNotContain("youtube", c.Category);
            Assert.DoesNotContain("vlc", c.Category);
            Assert.DoesNotContain("dota", c.Category);
        });
    }

    [Fact]
    public void Exposure_And_Window_CountCorrectly()
    {
        var e = ClinicalExportBuilder.Build(Sample());
        Assert.Equal(4, e.Exposure.TotalSamples);
        Assert.Equal(3, e.Exposure.MeasuredSamples);
        Assert.Equal(1, e.Exposure.EstimatedSamples);
        Assert.Equal(2, e.Exposure.InterventionOnSamples);
        Assert.Equal(1, e.Exposure.InterventionOffSamples);
        Assert.Equal(2, e.Exposure.DaysObserved);      // 2026-06-10 and 2026-06-11
        Assert.Equal("2026-06-10", e.Window.FirstDateUtc);
        Assert.Equal("2026-06-11", e.Window.LastDateUtc);
        Assert.Equal(4, e.Window.TotalDataPoints);
    }

    [Fact]
    public void Baseline_And_Outcome_AreEarliestAndLatestSamples()
    {
        // Measured, time-ordered: 14, 4, 10. q = max(1, 3/4) = 1.
        var m = ClinicalExportBuilder.Build(Sample()).Measured;
        Assert.Equal(14, m.BaselineMean!.Value, 2);    // earliest
        Assert.Equal(10, m.OutcomeMean!.Value, 2);     // latest
        Assert.Equal(-4, m.ChangeFromBaseline!.Value, 2);
    }

    [Fact]
    public void Build_SortsUnorderedInput_ByTimestamp()
    {
        // Feed reversed; baseline must still be the chronologically-earliest sample.
        var reversed = Sample().AsEnumerable().Reverse();
        var m = ClinicalExportBuilder.Build(reversed).Measured;
        Assert.Equal(14, m.BaselineMean!.Value, 2);
    }

    [Theory]
    [InlineData("video/youtube", "video")]
    [InlineData("category:video", "category")]   // tolerate the doc-form colon delimiter
    [InlineData("other", "other")]               // no delimiter → whole token
    public void ExtractCategory_StripsAppName(string key, string expected)
        => Assert.Equal(expected, ClinicalExportBuilder.ExtractCategory(key));

    [Fact]
    public void SerialisedJson_LeaksNoRawAppName_OnlyDeIdentifiedCategories()
    {
        // THE privacy red line (D-17): a doctor-facing file must carry de-identified categories +
        // numbers, never a raw app name. Serialise a real export and assert the app names are gone.
        var svc = new ClinicalExportService(new FileSymptomEventStore(Path.Combine(Path.GetTempPath(), "unused.jsonl")));
        var export = ClinicalExportBuilder.Build(Sample());
        string json = svc.ToJson(export);

        Assert.Contains("video", json);              // de-identified category present …
        Assert.Contains("game", json);
        Assert.Contains("on_vs_off_by_category", json);  // B3.1: the per-app effectiveness block reaches the JSON
        Assert.DoesNotContain("youtube", json);      // … raw app names stripped
        Assert.DoesNotContain("vlc", json);
        Assert.DoesNotContain("dota", json);
        Assert.Contains("\"de_identified\": true", json);
    }
}
