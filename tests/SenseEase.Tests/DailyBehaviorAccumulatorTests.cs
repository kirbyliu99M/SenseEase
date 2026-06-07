using System;
using System.IO;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UM1: the live daily-behaviour collector (<see cref="DailyBehaviorAccumulator"/>).
/// Pins the percentile / modal folding, the rising-edge counters, the net offset
/// delta, the same-day seed-merge telescoping, and the end-to-end data path
/// (accumulator → <see cref="DailyBehaviorAggregateWriter"/> → UM5's
/// <see cref="LongitudinalSummaryBuilder"/>) that activates the [behavior_trend] block.
/// </summary>
public class DailyBehaviorAccumulatorTests
{
    private static readonly DateTime T0 = new(2026, 6, 7, 9, 0, 0, DateTimeKind.Local);

    [Fact]
    public void BuildAggregate_BeforeBegin_IsNull()
    {
        var acc = new DailyBehaviorAccumulator();
        Assert.Null(acc.Date);
        Assert.Null(acc.BuildAggregate());
    }

    [Fact]
    public void BuildAggregate_OpenedButNoSamples_NoSeed_IsNull()
    {
        var acc = new DailyBehaviorAccumulator();
        acc.Begin("2026-06-07", seed: null, startOffsetPp: 0, startLevel: 0, nowLocal: T0);
        Assert.Equal("2026-06-07", acc.Date);
        Assert.Null(acc.BuildAggregate()); // nothing sampled, nothing seeded → nothing to emit
    }

    [Fact]
    public void Percentiles_AreNearestRank()
    {
        var acc = new DailyBehaviorAccumulator();
        acc.Begin("2026-06-07", null, 0, 0, T0);
        // OFI {10,20,...,100}: N=10, p50 rank=5 → 50, p90 rank=9 → 90.
        for (int v = 10; v <= 100; v += 10)
            acc.Sample(v, tier: 4, maskRising: false, spike: false, offsetPp: 0, level: 0, nowLocal: T0);

        var agg = acc.BuildAggregate()!;
        Assert.Equal(50, agg.OfiP50);
        Assert.Equal(90, agg.OfiP90);
    }

    [Fact]
    public void NegativeOfi_FoldsAsZero()
    {
        var acc = new DailyBehaviorAccumulator();
        acc.Begin("2026-06-07", null, 0, 0, T0);
        acc.Sample(-5, 4, false, false, 0, 0, T0);
        Assert.Equal(0, acc.BuildAggregate()!.OfiP50);
    }

    [Fact]
    public void TierModal_IsMostFrequent_TieGoesLower()
    {
        var acc = new DailyBehaviorAccumulator();
        acc.Begin("2026-06-07", null, 0, 0, T0);
        foreach (int t in new[] { 3, 4, 4, 4, 5, 5 }) // 4 dominates
            acc.Sample(10, t, false, false, 0, 0, T0);
        Assert.Equal(4, acc.BuildAggregate()!.TierModal);

        var tie = new DailyBehaviorAccumulator();
        tie.Begin("2026-06-07", null, 0, 0, T0);
        foreach (int t in new[] { 6, 6, 2, 2 }) // tie between 2 and 6 → lower wins
            tie.Sample(10, t, false, false, 0, 0, T0);
        Assert.Equal(2, tie.BuildAggregate()!.TierModal);
    }

    [Fact]
    public void Counters_SpikeMaskStable_CountRisingEdgesAndLevelUps()
    {
        var acc = new DailyBehaviorAccumulator();
        acc.Begin("2026-06-07", null, startOffsetPp: 0, startLevel: 2, nowLocal: T0);
        acc.Sample(10, 4, maskRising: true,  spike: true,  offsetPp: 0, level: 2, nowLocal: T0);
        acc.Sample(10, 4, maskRising: false, spike: true,  offsetPp: 0, level: 3, nowLocal: T0); // +1 stable
        acc.Sample(10, 4, maskRising: true,  spike: false, offsetPp: 0, level: 5, nowLocal: T0); // +2 stable

        var agg = acc.BuildAggregate()!;
        Assert.Equal(2, agg.SpikeCount);
        Assert.Equal(2, agg.MaskActivations);
        Assert.Equal(3, agg.StableWindowsCompleted); // (3-2)+(5-3)
    }

    [Fact]
    public void LevelDrop_DoesNotDecrementStableWindows()
    {
        var acc = new DailyBehaviorAccumulator();
        acc.Begin("2026-06-07", null, startOffsetPp: 0, startLevel: 5, nowLocal: T0);
        acc.Sample(10, 4, false, false, 0, level: 3, nowLocal: T0); // a drop — not a completed window
        acc.Sample(10, 4, false, false, 0, level: 4, nowLocal: T0); // back up by 1
        Assert.Equal(1, acc.BuildAggregate()!.StableWindowsCompleted);
    }

    [Fact]
    public void OffsetDelta_IsEndMinusStart()
    {
        var acc = new DailyBehaviorAccumulator();
        acc.Begin("2026-06-07", null, startOffsetPp: 10, startLevel: 0, nowLocal: T0);
        acc.Sample(10, 4, false, false, offsetPp: 12, level: 0, nowLocal: T0);
        acc.Sample(10, 4, false, false, offsetPp: 17, level: 0, nowLocal: T0);
        Assert.Equal(7.0, acc.BuildAggregate()!.OffsetDeltaPp); // 17 − 10
    }

    [Fact]
    public void SessionMinutes_FromSpan_Clamped()
    {
        var acc = new DailyBehaviorAccumulator();
        acc.Begin("2026-06-07", null, 0, 0, T0);
        acc.Sample(10, 4, false, false, 0, 0, T0);
        acc.Sample(10, 4, false, false, 0, 0, T0.AddMinutes(25));
        Assert.Equal(25, acc.BuildAggregate()!.SessionMinutes);
    }

    [Fact]
    public void Aggregate_CarriesPiiAnnotations()
    {
        var acc = new DailyBehaviorAccumulator();
        acc.Begin("2026-06-07", null, 0, 0, T0);
        acc.Sample(10, 4, false, false, 0, 0, T0);

        var agg = acc.BuildAggregate()!;
        Assert.Equal("2026-06-07", agg.Date);
        Assert.Equal(1, agg.SchemaVersion);
        Assert.False(agg.Exportable);      // sprint no-export constraint
        Assert.True(agg.DeletableOnClear); // sprint clear-on-wipe constraint
    }

    [Fact]
    public void SeedMerge_AdditiveCountersTelescopeAcrossSameDaySessions()
    {
        // Session A
        var a = new DailyBehaviorAccumulator();
        a.Begin("2026-06-07", seed: null, startOffsetPp: 0, startLevel: 0, nowLocal: T0);
        a.Sample(40, 4, maskRising: true, spike: true, offsetPp: 5, level: 1, nowLocal: T0.AddMinutes(10));
        var rowA = a.BuildAggregate()!;
        Assert.Equal(1, rowA.SpikeCount);
        Assert.Equal(1, rowA.MaskActivations);
        Assert.Equal(1, rowA.StableWindowsCompleted);
        Assert.Equal(5.0, rowA.OffsetDeltaPp);
        Assert.Equal(10, rowA.SessionMinutes);

        // Session B same day after a restart: re-opened seeded from rowA, the offset
        // resumes at 5 (restored from the persisted profile).
        var b = new DailyBehaviorAccumulator();
        b.Begin("2026-06-07", seed: rowA, startOffsetPp: 5, startLevel: 1, nowLocal: T0.AddMinutes(20));
        b.Sample(60, 5, maskRising: true, spike: true, offsetPp: 9, level: 2, nowLocal: T0.AddMinutes(35));
        var rowB = b.BuildAggregate()!;
        Assert.Equal(2, rowB.SpikeCount);             // 1 + 1
        Assert.Equal(2, rowB.MaskActivations);        // 1 + 1
        Assert.Equal(2, rowB.StableWindowsCompleted); // 1 + (2−1)
        Assert.Equal(9.0, rowB.OffsetDeltaPp);        // 5 + (9−5) telescopes to whole-day 0→9
        Assert.Equal(25, rowB.SessionMinutes);        // 10 + 15
    }

    [Fact]
    public void Reset_ReturnsToUnopenedState()
    {
        var acc = new DailyBehaviorAccumulator();
        acc.Begin("2026-06-07", null, 0, 0, T0);
        acc.Sample(10, 4, true, true, 3, 1, T0);
        acc.Reset();
        Assert.Null(acc.Date);
        Assert.Null(acc.BuildAggregate());
    }

    [Fact]
    public void EndToEnd_AccumulatorToWriterToSummary_ActivatesTrendBlock()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "se_behaviorlog_" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var writer = new DailyBehaviorAggregateWriter(path);

            // 8 organic days (≥ MinDaysForSummary = 7) routed through the real collector.
            for (int d = 1; d <= 8; d++)
            {
                var acc = new DailyBehaviorAccumulator();
                string date = $"2026-06-{d:00}";
                var t = new DateTime(2026, 6, d, 9, 0, 0, DateTimeKind.Local);
                acc.Begin(date, seed: null, startOffsetPp: d, startLevel: 0, nowLocal: t);
                acc.Sample(50 + d, tier: 4, maskRising: false, spike: d % 2 == 0,
                           offsetPp: d + 1, level: 0, nowLocal: t.AddMinutes(30));
                writer.Write(acc.BuildAggregate()!);
            }

            var rows = writer.ReadAll();
            Assert.Equal(8, rows.Count);

            string block = LongitudinalSummaryBuilder.BuildBlock(rows);
            Assert.StartsWith("[behavior_trend]", block);
            Assert.Contains("exposure_days: 8/30", block);
            Assert.Contains("ofi_p90_trend:", block);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void EndToEnd_SameDayRestart_WriterKeepsOneRow_CountersMerge()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "se_behaviorlog_" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var writer = new DailyBehaviorAggregateWriter(path);
            const string date = "2026-06-07";

            var a = new DailyBehaviorAccumulator();
            a.Begin(date, seed: null, startOffsetPp: 0, startLevel: 0, nowLocal: T0);
            a.Sample(40, 4, maskRising: true, spike: true, offsetPp: 0, level: 0, nowLocal: T0.AddMinutes(5));
            writer.Write(a.BuildAggregate()!);

            // Restart: re-open seeded from the row just written for today.
            DailyBehaviorAggregate? seed = null;
            foreach (var r in writer.ReadAll())
                if (r.Date == date) { seed = r; break; }
            Assert.NotNull(seed);

            var b = new DailyBehaviorAccumulator();
            b.Begin(date, seed: seed, startOffsetPp: 0, startLevel: 0, nowLocal: T0.AddMinutes(10));
            b.Sample(40, 4, maskRising: true, spike: true, offsetPp: 0, level: 0, nowLocal: T0.AddMinutes(15));
            writer.Write(b.BuildAggregate()!);

            var rows = writer.ReadAll();
            Assert.Single(rows);              // idempotent by date — still one row
            Assert.Equal(2, rows[0].SpikeCount);
            Assert.Equal(2, rows[0].MaskActivations);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }
}
