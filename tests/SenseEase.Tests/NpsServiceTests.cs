using System;
using System.Collections.Generic;
using System.IO;
using SenseEase.App.Services;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UI-RESHAPE prep — unit coverage for the net-new NPS KPI service. Uses the internal
/// test ctor (isolated temp dir + injected clock + injected first-run probe) so it never
/// touches global AppConfig state and is fully deterministic.
/// </summary>
public sealed class NpsServiceTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "se-nps-tests-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData(1, NpsBucket.Detractor)]
    [InlineData(2, NpsBucket.Detractor)]
    [InlineData(3, NpsBucket.Detractor)]
    [InlineData(4, NpsBucket.Passive)]
    [InlineData(5, NpsBucket.Promoter)]
    public void BucketFor_SplitsAtFivePointBoundaries(int score, NpsBucket expected)
        => Assert.Equal(expected, NpsService.BucketFor(score));

    [Fact]
    public void ShouldAsk_FalseUntilMinimumSessions()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = new NpsService(NewDir(), () => now, isFirstRun: () => false);

        Assert.False(svc.ShouldAsk());                  // 0 sessions
        svc.RecordSessionStart();
        svc.RecordSessionStart();
        Assert.False(svc.ShouldAsk());                  // 2 < MinSessions(3)
        svc.RecordSessionStart();
        Assert.True(svc.ShouldAsk());                   // 3 sessions, eligible
    }

    [Fact]
    public void ShouldAsk_FalseWhileFirstRunIncomplete()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = new NpsService(NewDir(), () => now, isFirstRun: () => true);
        svc.RecordSessionStart(); svc.RecordSessionStart(); svc.RecordSessionStart();
        Assert.False(svc.ShouldAsk());
    }

    [Fact]
    public void ShouldAsk_RespectsCooldownAfterShown()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = new NpsService(NewDir(), () => now, isFirstRun: () => false);
        svc.RecordSessionStart(); svc.RecordSessionStart(); svc.RecordSessionStart();

        svc.RecordShown();
        Assert.False(svc.ShouldAsk());                  // inside 30-day cooldown
        now = now.AddDays(31);
        Assert.True(svc.ShouldAsk());                   // cooldown elapsed
    }

    [Fact]
    public void ShouldAsk_DismissArmsLongerQuietWindow()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = new NpsService(NewDir(), () => now, isFirstRun: () => false);
        svc.RecordSessionStart(); svc.RecordSessionStart(); svc.RecordSessionStart();

        svc.RecordDismissed();
        now = now.AddDays(31);
        Assert.False(svc.ShouldAsk());                  // 90-day dismiss-suppression outlasts cooldown
        now = now.AddDays(61);                          // total 92 days
        Assert.True(svc.ShouldAsk());
    }

    [Fact]
    public void RecordResponse_AppendsAndComputesNps()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dir = NewDir();
        var svc = new NpsService(dir, () => now, isFirstRun: () => false);

        Assert.Null(svc.ComputeNps());                  // no responses yet

        svc.RecordResponse(5, "love it");               // promoter
        svc.RecordResponse(2, null);                    // detractor
        svc.RecordResponse(4, null);                    // passive

        // (1 promoter − 1 detractor) / 3 * 100 = 0
        Assert.Equal(0.0, svc.ComputeNps()!.Value, 3);

        // A fresh instance over the same dir reads the same log.
        var svc2 = new NpsService(dir, () => now, isFirstRun: () => false);
        Assert.Equal(0.0, svc2.ComputeNps()!.Value, 3);
    }

    [Fact]
    public void RecordResponse_IgnoresOutOfRange()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = new NpsService(NewDir(), () => now, isFirstRun: () => false);
        svc.RecordResponse(0, null);                    // just below the 1–5 range
        svc.RecordResponse(6, null);                    // just above the 1–5 range
        svc.RecordResponse(-1, null);
        Assert.Null(svc.ComputeNps());                  // nothing logged
    }

    [Fact]
    public void ComputeNps_OnlyCountsCurrentScaleRows_LegacyRowsIgnored()
    {
        // Scale guard (2026-06-18): a pre-rescale 0–10 row (no scaleMax / an explicit older scale)
        // must NOT be mixed into the 1–5 KPI. Seed the log with two legacy rows, then a current one.
        var dir = NewDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "survey_log.jsonl"),
            "{\"ts\":\"2026-01-01T00:00:00Z\",\"score\":9}\n" +                 // legacy 0–10, no scaleMax
            "{\"ts\":\"2026-01-02T00:00:00Z\",\"score\":7,\"scaleMax\":10}\n"); // explicit old scale
        DateTime now = new(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var svc = new NpsService(dir, () => now, isFirstRun: () => false);

        Assert.Null(svc.ComputeNps());                  // only old-scale rows → none counted

        svc.RecordResponse(5, null);                    // current 1–5 row (stamped scaleMax=5) → Promoter
        Assert.Equal(100.0, svc.ComputeNps()!.Value, 3); // 1 promoter, 0 detractors; legacy rows ignored
    }

    [Fact]
    public void ComputeCsat_ReturnsMeanAndTopTwoBox_NullWhenEmpty()
    {
        // Option B: the PRIMARY 5-point KPI is CSAT (mean + top-2-box), not the experimental NPS-analog.
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = new NpsService(NewDir(), () => now, isFirstRun: () => false);

        Assert.Null(svc.ComputeCsat());                 // no responses yet

        svc.RecordResponse(5, null);                    // satisfied (top-box)
        svc.RecordResponse(4, null);                    // satisfied (top-2-box)
        svc.RecordResponse(2, null);                    // not satisfied

        var csat = svc.ComputeCsat();
        Assert.NotNull(csat);
        Assert.Equal(3, csat!.Value.Count);
        Assert.Equal(11.0 / 3.0, csat.Value.Mean, 3);              // (5+4+2)/3
        Assert.Equal(200.0 / 3.0, csat.Value.TopTwoBoxPercent, 3); // 2 of 3 scored >= 4
    }

    [Fact]
    public void RecordDismissed_IdleAutoDismiss_ArmsOnlyTheShortCooldown_NotThe90DayWindow()
    {
        // An idle timeout (un-answered) is a weaker signal than an explicit "Not now" → 30-day cooldown,
        // not the 90-day quiet window — so ignoring the card once does not silence it for a quarter.
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = new NpsService(NewDir(), () => now, isFirstRun: () => false);
        svc.RecordSessionStart(); svc.RecordSessionStart(); svc.RecordSessionStart();

        svc.RecordDismissed(explicitDecline: false);    // idle auto-dismiss
        now = now.AddDays(29);
        Assert.False(svc.ShouldAsk());                  // inside the 30-day cooldown
        now = now.AddDays(2);                            // total 31 days
        Assert.True(svc.ShouldAsk());                   // short cooldown elapsed (NOT 90-day) → re-asks
    }

    [Fact]
    public void State_SurvivesReload()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dir = NewDir();
        var svc = new NpsService(dir, () => now, isFirstRun: () => false);
        svc.RecordSessionStart(); svc.RecordSessionStart(); svc.RecordSessionStart();
        svc.RecordShown();

        var reloaded = new NpsService(dir, () => now, isFirstRun: () => false).State;
        Assert.Equal(3, reloaded.SessionsSeen);
        Assert.Equal(1, reloaded.AskCount);
        Assert.NotNull(reloaded.LastAskedUtc);
    }
}
