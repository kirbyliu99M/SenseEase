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
    [InlineData(0, NpsBucket.Detractor)]
    [InlineData(6, NpsBucket.Detractor)]
    [InlineData(7, NpsBucket.Passive)]
    [InlineData(8, NpsBucket.Passive)]
    [InlineData(9, NpsBucket.Promoter)]
    [InlineData(10, NpsBucket.Promoter)]
    public void BucketFor_SplitsAtNpsBoundaries(int score, NpsBucket expected)
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

        svc.RecordResponse(10, "love it");              // promoter
        svc.RecordResponse(5, null);                    // detractor
        svc.RecordResponse(8, null);                    // passive

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
        svc.RecordResponse(11, null);
        svc.RecordResponse(-1, null);
        Assert.Null(svc.ComputeNps());                  // nothing logged
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
