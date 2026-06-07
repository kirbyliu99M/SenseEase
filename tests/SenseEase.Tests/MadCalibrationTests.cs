using System;
using System.Collections.Generic;
using System.Linq;
using SenseEase.Core.Sensing;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M1 calibration PURE-CORE coverage (backlog D1 reducible half):
/// <see cref="MadCalibrationReducer"/> (mean / nearest-rank p95 / derived-OFI +
/// clamp, CSV round-trip with comment+blank skipping) and
/// <see cref="NormFactorRecommender"/> (single-scene range, multi-scene
/// intersection, conflict). All deterministic — no I/O, no live capture, never
/// mutates SenseDefaults. The derived-OFI math mirrors WgcSensingProvider
/// (mad × normFactor, clamp [0,150]); see MadCalibration.cs file header.
/// </summary>
public class MadCalibrationTests
{
    // ── Reduce: mean / p95 / derived-OFI ────────────────────────────────────────
    [Fact]
    public void Reduce_Mean_IsArithmeticAverage()
    {
        var mads = new List<double> { 2, 4, 6, 8 }; // mean 5
        MadSceneStats s = MadCalibrationReducer.Reduce("video_1080p", 10.0, mads, normFactor: 4.0);

        Assert.Equal("video_1080p", s.SceneLabel);
        Assert.Equal(10.0, s.DurationS, 9);
        Assert.Equal(5.0, s.MeanMad, 9);
    }

    [Fact]
    public void Reduce_P95_IsNearestRank_RealObservedSample()
    {
        // 20 samples 1..20. Nearest-rank p95: rank = ceil(0.95*20) = 19 -> value 19.
        var mads = Enumerable.Range(1, 20).Select(i => (double)i).ToList();
        MadSceneStats s = MadCalibrationReducer.Reduce("scene_cut", 5.0, mads, normFactor: 1.0);

        Assert.Equal(19.0, s.P95Mad, 9); // nearest-rank, never interpolated
    }

    [Fact]
    public void Reduce_P95_UnsortedInput_OrderIndependent()
    {
        var ascending  = new List<double> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var shuffled   = new List<double> { 7, 1, 10, 3, 9, 2, 6, 4, 8, 5 };

        MadSceneStats a = MadCalibrationReducer.Reduce("a", 1.0, ascending, 1.0);
        MadSceneStats b = MadCalibrationReducer.Reduce("a", 1.0, shuffled,  1.0);

        Assert.Equal(a.P95Mad, b.P95Mad, 9);
        Assert.Equal(a.MeanMad, b.MeanMad, 9);
    }

    [Fact]
    public void Reduce_DerivedOfi_MirrorsWgcMadTimesFactor()
    {
        // mean MAD 5, factor 4 -> derived OFI mean 20 (well under the 150 clamp).
        var mads = new List<double> { 4, 5, 6 }; // mean 5
        MadSceneStats s = MadCalibrationReducer.Reduce("word_scroll", 8.0, mads, normFactor: 4.0);

        Assert.Equal(20.0, s.DerivedOfiMean, 9);
        // p95 (nearest-rank of {4,5,6}, rank ceil(0.95*3)=3) = 6 -> 6*4 = 24.
        Assert.Equal(6.0, s.P95Mad, 9);
        Assert.Equal(24.0, s.DerivedOfiP95, 9);
    }

    [Fact]
    public void Reduce_DerivedOfi_ClampsAt150()
    {
        // mean MAD 60, factor 4 -> 240 raw -> clamped to 150 (mirrors WGC clamp).
        var mads = new List<double> { 60, 60, 60 };
        MadSceneStats s = MadCalibrationReducer.Reduce("fullscreen_game", 12.0, mads, normFactor: 4.0);

        Assert.Equal(150.0, s.DerivedOfiMean, 9);
        Assert.Equal(150.0, s.DerivedOfiP95, 9);
    }

    [Fact]
    public void Reduce_EmptyTrace_YieldsZeroRow()
    {
        MadSceneStats s = MadCalibrationReducer.Reduce("idle_desktop", 30.0, new List<double>(), normFactor: 4.0);

        Assert.Equal(0.0, s.MeanMad, 9);
        Assert.Equal(0.0, s.P95Mad, 9);
        Assert.Equal(0.0, s.DerivedOfiMean, 9);
        Assert.Equal(0.0, s.DerivedOfiP95, 9);
        Assert.Equal(30.0, s.DurationS, 9);
    }

    // ── CSV: round-trip + header/blank/comment skipping ─────────────────────────
    [Fact]
    public void ToCsvRow_ParseCsv_RoundTrip()
    {
        MadSceneStats original = MadCalibrationReducer.Reduce(
            "video_1080p", 10.0, new List<double> { 3, 5, 7, 9 }, normFactor: 4.0);

        string row = MadCalibrationReducer.ToCsvRow(original);
        var lines = new[] { MadCalibrationReducer.CsvHeader, row };

        IReadOnlyList<MadSceneStats> parsed = MadCalibrationReducer.ParseCsv(lines);

        Assert.Single(parsed);
        MadSceneStats rt = parsed[0];
        Assert.Equal(original.SceneLabel, rt.SceneLabel);
        Assert.Equal(original.DurationS, rt.DurationS, 4);
        Assert.Equal(original.MeanMad, rt.MeanMad, 4);
        Assert.Equal(original.P95Mad, rt.P95Mad, 4);
        Assert.Equal(original.DerivedOfiMean, rt.DerivedOfiMean, 4);
        Assert.Equal(original.DerivedOfiP95, rt.DerivedOfiP95, 4);
    }

    [Fact]
    public void ParseCsv_SkipsHeader_Blank_AndCommentLines()
    {
        var lines = new[]
        {
            "# M1 WGC MAD calibration capture 2026-06-07",
            MadCalibrationReducer.CsvHeader,
            "",
            "   ",
            "# scene below was captured at norm-factor 4.0",
            "idle_desktop,30,0.4,1,1.6,4",
            "   # indented comment is still a comment",
            "video_1080p,10,8,22,32,88",
        };

        IReadOnlyList<MadSceneStats> parsed = MadCalibrationReducer.ParseCsv(lines);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("idle_desktop", parsed[0].SceneLabel);
        Assert.Equal(0.4, parsed[0].MeanMad, 4);
        Assert.Equal("video_1080p", parsed[1].SceneLabel);
        Assert.Equal(22.0, parsed[1].P95Mad, 4);
    }

    [Fact]
    public void ParseCsv_HeaderOnlyFile_YieldsNoRows()
    {
        // The on-disk baseline ships as HEADER ONLY (no fabricated data).
        IReadOnlyList<MadSceneStats> parsed =
            MadCalibrationReducer.ParseCsv(new[] { MadCalibrationReducer.CsvHeader });

        Assert.Empty(parsed);
    }

    [Fact]
    public void ParseCsv_MalformedRow_Throws()
    {
        var tooFew = new[] { MadCalibrationReducer.CsvHeader, "idle_desktop,30,0.4" };
        Assert.Throws<FormatException>(() => MadCalibrationReducer.ParseCsv(tooFew));

        var nonNumeric = new[] { MadCalibrationReducer.CsvHeader, "idle_desktop,thirty,0.4,1,1.6,4" };
        Assert.Throws<FormatException>(() => MadCalibrationReducer.ParseCsv(nonNumeric));
    }

    // ── Recommend: single scene ─────────────────────────────────────────────────
    [Fact]
    public void Recommend_SingleScene_MidpointOfBand()
    {
        // mean MAD 10. Target OFI band [20, 60] -> admissible factor [2, 6] -> mid 4.
        var scenes = new List<MadSceneStats>
        {
            MadCalibrationReducer.Reduce("video_1080p", 10.0, new List<double> { 10 }, normFactor: 1.0),
        };
        var targets = new List<(string, double, double)>
        {
            ("video_1080p", 20.0, 60.0),
        };

        NormFactorRecommendation rec = NormFactorRecommender.Recommend(scenes, targets);

        Assert.False(rec.HasConflict);
        Assert.Equal(2.0, rec.FactorRangeLow, 6);
        Assert.Equal(6.0, rec.FactorRangeHigh, 6);
        Assert.Equal(4.0, rec.RecommendedFactor, 6);
    }

    // ── Recommend: multi-scene intersection ─────────────────────────────────────
    [Fact]
    public void Recommend_MultiScene_IntersectsAdmissibleRanges()
    {
        // Scene A: mean 10, band [20,60] -> factor [2,6].
        // Scene B: mean 20, band [60,100] -> factor [3,5].
        // Intersection [3,5] -> mid 4.
        var scenes = new List<MadSceneStats>
        {
            MadCalibrationReducer.Reduce("video_1080p",     1.0, new List<double> { 10 }, 1.0),
            MadCalibrationReducer.Reduce("fullscreen_game", 1.0, new List<double> { 20 }, 1.0),
        };
        var targets = new List<(string, double, double)>
        {
            ("video_1080p",     20.0, 60.0),
            ("fullscreen_game", 60.0, 100.0),
        };

        NormFactorRecommendation rec = NormFactorRecommender.Recommend(scenes, targets);

        Assert.False(rec.HasConflict);
        Assert.Equal(3.0, rec.FactorRangeLow, 6);
        Assert.Equal(5.0, rec.FactorRangeHigh, 6);
        Assert.Equal(4.0, rec.RecommendedFactor, 6);
    }

    [Fact]
    public void Recommend_SkipsStaticAndUnmatchedScenes()
    {
        // idle_desktop has mean MAD 0 (static) -> cannot constrain, must be skipped.
        // unmatched_scene has no target -> skipped. Only video constrains the factor.
        var scenes = new List<MadSceneStats>
        {
            MadCalibrationReducer.Reduce("idle_desktop",    1.0, new List<double> { 0, 0, 0 }, 1.0),
            MadCalibrationReducer.Reduce("unmatched_scene", 1.0, new List<double> { 5 },       1.0),
            MadCalibrationReducer.Reduce("video_1080p",     1.0, new List<double> { 10 },      1.0),
        };
        var targets = new List<(string, double, double)>
        {
            ("idle_desktop", 0.0, 5.0),
            ("video_1080p", 20.0, 60.0),
        };

        NormFactorRecommendation rec = NormFactorRecommender.Recommend(scenes, targets);

        Assert.False(rec.HasConflict);
        Assert.Equal(4.0, rec.RecommendedFactor, 6); // driven solely by video_1080p
    }

    // ── Recommend: conflict (empty intersection) ────────────────────────────────
    [Fact]
    public void Recommend_ConflictingScenes_ReportsConflict()
    {
        // Scene A: mean 10, band [20,30] -> factor [2,3].
        // Scene B: mean 10, band [80,90] -> factor [8,9].
        // No overlap -> conflict; recommended factor 0, notes explain.
        var scenes = new List<MadSceneStats>
        {
            MadCalibrationReducer.Reduce("video_1080p",     1.0, new List<double> { 10 }, 1.0),
            MadCalibrationReducer.Reduce("fullscreen_game", 1.0, new List<double> { 10 }, 1.0),
        };
        var targets = new List<(string, double, double)>
        {
            ("video_1080p",     20.0, 30.0),
            ("fullscreen_game", 80.0, 90.0),
        };

        NormFactorRecommendation rec = NormFactorRecommender.Recommend(scenes, targets);

        Assert.True(rec.HasConflict);
        Assert.Equal(0.0, rec.RecommendedFactor, 9);
        Assert.Contains("Conflict", rec.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recommend_NoScenesOrNoTargets_IsConflict()
    {
        var empty = new List<MadSceneStats>();
        var targets = new List<(string, double, double)> { ("video_1080p", 20.0, 60.0) };

        NormFactorRecommendation a = NormFactorRecommender.Recommend(empty, targets);
        Assert.True(a.HasConflict);
        Assert.Equal(0.0, a.RecommendedFactor, 9);

        var scenes = new List<MadSceneStats>
        {
            MadCalibrationReducer.Reduce("video_1080p", 1.0, new List<double> { 10 }, 1.0),
        };
        NormFactorRecommendation b = NormFactorRecommender.Recommend(
            scenes, new List<(string, double, double)>());
        Assert.True(b.HasConflict);
    }
}
