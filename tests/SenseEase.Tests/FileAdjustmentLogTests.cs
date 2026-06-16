using System;
using System.IO;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Contract tests for FileAdjustmentLog. The Reset-boundary behaviour is the
/// regression blocker for the "FOV stuck at 40% after every restart" bug:
/// before this guard, WeightFor summed every historic user-driven FovRadius
/// delta on startup so the engine's _userFovOffset clamp at -60 made the
/// user boot into a tight FOV regardless of whether they had pressed Reset
/// in a prior session. Reset must be authoritative across restarts.
/// </summary>
public class FileAdjustmentLogTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(),
        $"senseease-adj-log-tests-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
    }

    [Fact]
    public void WeightFor_NoRecords_ReturnsZero()
    {
        var log = new FileAdjustmentLog(_tempPath);
        var w = log.WeightFor("FovRadius");
        Assert.Equal(0, w.Count);
        Assert.Equal(0.0, w.NetDelta);
    }

    [Fact]
    public void WeightFor_OnlyUserRecords_SumsAll()
    {
        var log = new FileAdjustmentLog(_tempPath);
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -7, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -3, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -5, Source = "user" });

        var w = log.WeightFor("FovRadius");
        Assert.Equal(3, w.Count);
        Assert.Equal(-15.0, w.NetDelta);
    }

    [Fact]
    public void WeightFor_ResetClearsPriorHistory()
    {
        // The bug repro: chatbot accumulated -150 of FovRadius deltas over time.
        // User pressed Reset. Without this fix, WeightFor still returns -150
        // (Math.Clamp(-150, -60, 0) = -60), so app boots at 100 - 60 = 40%.
        var log = new FileAdjustmentLog(_tempPath);
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -50, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -50, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -50, Source = "user" });

        // User clicks Reset — appends a "reset" source record.
        log.Append(new AdjustmentRecord { Parameter = "Reset", Source = "reset" });

        var w = log.WeightFor("FovRadius");
        Assert.Equal(0, w.Count);     // no user-source FovRadius records since Reset
        Assert.Equal(0.0, w.NetDelta); // therefore zero net delta
    }

    [Fact]
    public void WeightFor_OnlyCountsRecordsAfterMostRecentReset()
    {
        // Verifies the fix doesn't over-correct: post-Reset adjustments should
        // still be counted. User profiling resumes from a clean slate after
        // each Reset.
        var log = new FileAdjustmentLog(_tempPath);
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -50, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "Reset",     Delta = 0,   Source = "reset" });
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -7,  Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -3,  Source = "user" });

        var w = log.WeightFor("FovRadius");
        Assert.Equal(2, w.Count);
        Assert.Equal(-10.0, w.NetDelta);
    }

    [Fact]
    public void WeightFor_HonoursMostRecentResetWhenMultipleExist()
    {
        // Multiple Reset events across sessions — only the LAST one is the
        // tare boundary. Earlier resets and pre-reset entries are all
        // discarded; post-last-reset entries count.
        var log = new FileAdjustmentLog(_tempPath);
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -10, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "Reset",     Delta = 0,   Source = "reset" });
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -20, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "Reset",     Delta = 0,   Source = "reset" });
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -4,  Source = "user" });

        var w = log.WeightFor("FovRadius");
        Assert.Equal(1, w.Count);
        Assert.Equal(-4.0, w.NetDelta);
    }

    [Fact]
    public void WeightFor_ResetBoundary_PersistsAcrossLogReload()
    {
        // The whole point of the fix: the Reset boundary must survive a
        // process restart (i.e. a fresh FileAdjustmentLog constructed from
        // the same on-disk file). This is the actual repro path for the
        // 40%-FOV-on-startup bug.
        {
            var log = new FileAdjustmentLog(_tempPath);
            log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -50, Source = "user" });
            log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -50, Source = "user" });
            log.Append(new AdjustmentRecord { Parameter = "Reset",     Delta = 0,   Source = "reset" });
        }

        // Simulate app restart: new FileAdjustmentLog reads the same file.
        var reopened = new FileAdjustmentLog(_tempPath);
        var w = reopened.WeightFor("FovRadius");
        Assert.Equal(0, w.Count);
        Assert.Equal(0.0, w.NetDelta);
    }

    // ── B2 caution-feed: the manual nudge ("LevelNudge") joins the FOV caution ──────────

    [Fact]
    public void CombinedFovCautionWeight_MergesFovRadiusAndLevelNudge()
    {
        var log = new FileAdjustmentLog(_tempPath);
        log.Append(new AdjustmentRecord { Parameter = "FovRadius",  Delta = -6, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "LevelNudge", Delta = -8, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "LevelNudge", Delta = -8, Source = "user" });

        var w = MockInferenceEngine.CombinedFovCautionWeight(log);
        Assert.Equal(3, w.Count);                     // confidence = sum of both channels
        Assert.Equal(-22.0, w.NetDelta);              // -6 + -8 + -8 (sum; unused downstream)
        Assert.Equal(-8.0, w.MeanDelta, 9);           // most-cautious channel mean: min(fov -6, nudge -8)
    }

    [Fact]
    public void CombinedFovCautionWeight_UpNudges_NeverDiluteSteeperFovCaution()
    {
        // Regression for the B2-caution HIGH: appending fixed -8 up-nudges to a STEEPER FovRadius
        // history must NOT pull the caution mean toward -8 (which would relax faster = less protection).
        var log = new FileAdjustmentLog(_tempPath);
        log.Append(new AdjustmentRecord { Parameter = "FovRadius", Delta = -20, Source = "user" });
        double before = MockInferenceEngine.CombinedFovCautionWeight(log).MeanDelta;   // -20

        for (int i = 0; i < 10; i++)
            log.Append(new AdjustmentRecord { Parameter = "LevelNudge", Delta = -8, Source = "user" });
        double after = MockInferenceEngine.CombinedFovCautionWeight(log).MeanDelta;

        Assert.True(after <= before, $"up-nudges must not reduce caution (before {before}, after {after})");
        Assert.Equal(-20.0, after, 9);                // held at the steeper FOV level, not diluted to -8
    }

    [Fact]
    public void CombinedFovCautionWeight_NudgesRaiseCautionAboveShallowFov()
    {
        // Symmetric: when the nudge channel is MORE cautious than a shallow FOV history, it raises caution.
        var log = new FileAdjustmentLog(_tempPath);
        log.Append(new AdjustmentRecord { Parameter = "FovRadius",  Delta = -4, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "LevelNudge", Delta = -8, Source = "user" });

        Assert.Equal(-8.0, MockInferenceEngine.CombinedFovCautionWeight(log).MeanDelta, 9); // min(-4, -8)
    }

    [Fact]
    public void CombinedFovCautionWeight_LevelNudgeDoesNotAffectFovRadiusOffsetChannel()
    {
        // The nudge must NOT shift the FOV-offset restore (which keys on "FovRadius" only).
        var log = new FileAdjustmentLog(_tempPath);
        log.Append(new AdjustmentRecord { Parameter = "LevelNudge", Delta = -8, Source = "user" });

        Assert.Equal(0, log.WeightFor("FovRadius").Count);   // FOV-offset channel unaffected
        Assert.Equal(1, MockInferenceEngine.CombinedFovCautionWeight(log).Count); // caution sees it
    }

    [Fact]
    public void CombinedFovCautionWeight_ResetClearsBothChannels()
    {
        var log = new FileAdjustmentLog(_tempPath);
        log.Append(new AdjustmentRecord { Parameter = "FovRadius",  Delta = -50, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "LevelNudge", Delta = -50, Source = "user" });
        log.Append(new AdjustmentRecord { Parameter = "Reset",      Source = "reset" });

        var w = MockInferenceEngine.CombinedFovCautionWeight(log);
        Assert.Equal(0, w.Count);
        Assert.Equal(0.0, w.NetDelta);
    }

    [Fact]
    public void CombinedFovCautionWeight_NullLog_IsZero()
        => Assert.Equal(0, MockInferenceEngine.CombinedFovCautionWeight(null).Count);
}
