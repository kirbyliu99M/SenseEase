using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SenseEase.Core.AdaptiveFit;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-2 Adaptive Fit GROUP B — the per-user stats pipeline
/// (<see cref="IntensityStatsBuilder"/> + <see cref="UserIntensityStats"/> +
/// <c>IEventLogStore.ReadDurable</c>). All PURE Core math plus one file-backed
/// round-trip for the durable read path. No engine wiring, no flag, no STA.
///
/// <para>Single-user telemetry only (the user's own log) — NOT collaborative
/// filtering. The builder is deterministic, so every assertion is exact.</para>
/// </summary>
public class AdaptiveFitStatsTests
{
    // Helper: a durable-style entry. Timestamp defaults are overridden where the
    // spike-window math matters.
    private static EventLogEntry Entry(double pressure, string category = "AdaptiveFit.Sample", DateTime? ts = null)
        => new()
        {
            Category  = category,
            Pressure  = pressure,
            Timestamp = ts ?? new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc),
        };

    // ── Percentile math: known input → known p50 / p90 ─────────────────────────
    [Fact]
    public void Build_Percentiles_LinearInterpolation_KnownInputKnownOutput()
    {
        // trigger = 1.0 so normalised pressure == raw pressure.
        // Pressures 0..10 (11 samples). PERCENTILE.INC: rank = q*(n-1) = q*10.
        //   p50 -> rank 5  -> 5.0
        //   p90 -> rank 9  -> 9.0
        List<EventLogEntry> entries = Enumerable.Range(0, 11)
            .Select(i => Entry(i))
            .ToList();

        UserIntensityStats stats = IntensityStatsBuilder.Build(
            entries, now: DateTime.UtcNow, triggerPressure: 1.0);

        Assert.Equal(5.0, stats.PressureP50, 9);
        Assert.Equal(9.0, stats.PressureP90, 9);
        Assert.Equal(11, stats.SampleCount);
    }

    [Fact]
    public void Build_Percentile_InterpolatesBetweenSamples()
    {
        // Four samples 0,10,20,30 with trigger 1.0. rank for p50 = 0.5*3 = 1.5 ->
        // halfway between index1 (10) and index2 (20) = 15. p90 = 0.9*3 = 2.7 ->
        // 20 + 0.7*(30-20) = 27.
        var entries = new[] { Entry(30), Entry(0), Entry(20), Entry(10) }; // unsorted on purpose
        UserIntensityStats stats = IntensityStatsBuilder.Build(
            entries, DateTime.UtcNow, triggerPressure: 1.0);

        Assert.Equal(15.0, stats.PressureP50, 9);
        Assert.Equal(27.0, stats.PressureP90, 9);
    }

    [Fact]
    public void Build_NormalisesPressureByTrigger()
    {
        // Pressure 85, trigger 85 -> normalised 1.0 for every sample.
        var entries = new[] { Entry(85), Entry(85), Entry(85) };
        UserIntensityStats stats = IntensityStatsBuilder.Build(
            entries, DateTime.UtcNow, triggerPressure: 85.0);

        Assert.Equal(1.0, stats.PressureP50, 9);
        Assert.Equal(1.0, stats.PressureP90, 9);
    }

    // ── Calm ratio: fraction below the stable-pressure fraction ────────────────
    [Fact]
    public void Build_CalmRatio_FractionBelowStableFraction()
    {
        // trigger 100 -> normalised = pressure/100.
        // Stable fraction 0.25. Pressures: 10,20 (calm) and 30,40,50 (not).
        // => 2 of 5 calm => 0.4.
        var entries = new[] { Entry(10), Entry(20), Entry(30), Entry(40), Entry(50) };
        UserIntensityStats stats = IntensityStatsBuilder.Build(
            entries, DateTime.UtcNow, triggerPressure: 100.0,
            stablePressureFraction: 0.25);

        Assert.Equal(0.4, stats.CalmRatio, 9);
    }

    [Fact]
    public void Build_CalmRatio_UsesSenseDefaultStableFraction_ByDefault()
    {
        // Default fraction is HabituationStablePressureFraction (0.25). trigger 100.
        // 24 (<25, calm) and 26 (>=25, not) => 0.5.
        var entries = new[] { Entry(24), Entry(26) };
        UserIntensityStats stats = IntensityStatsBuilder.Build(
            entries, DateTime.UtcNow, triggerPressure: 100.0);

        Assert.Equal(SenseDefaults.HabituationStablePressureFraction, 0.25, 9); // pin the assumption
        Assert.Equal(0.5, stats.CalmRatio, 9);
    }

    // ── Spike recurrence per hour over a known window ──────────────────────────
    [Fact]
    public void Build_SpikeRecurrencePerHour_OverKnownWindow()
    {
        DateTime now = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);
        DateTime start = now.AddHours(-2); // 2-hour window

        // 3 spike-category entries + 2 non-spike. Earliest entry == start.
        var entries = new[]
        {
            Entry(90, "Habituation.Spike", start),
            Entry(90, "Habituation.Spike", now.AddHours(-1)),
            Entry(90, "Habituation.Spike", now.AddMinutes(-30)),
            Entry(10, "AdaptiveFit.Sample", now.AddMinutes(-45)),
            Entry(10, "AdaptiveFit.Sample", now.AddMinutes(-15)),
        };

        UserIntensityStats stats = IntensityStatsBuilder.Build(entries, now, triggerPressure: 100.0);

        // 3 spikes / 2 hours = 1.5 per hour.
        Assert.Equal(1.5, stats.SpikeRecurrencePerHour, 9);
        Assert.Equal(5, stats.SampleCount);
    }

    [Fact]
    public void Build_SpikeMatch_IsCaseInsensitiveSubstring()
    {
        DateTime now = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);
        // "Habituation.Spike" and a hypothetical "Engine.spikeDetected" both count;
        // a non-spike does not. Window pinned to exactly 1 hour.
        var entries = new[]
        {
            Entry(90, "Habituation.Spike",      now.AddHours(-1)),
            Entry(90, "Engine.spikeDetected",   now.AddMinutes(-30)),
            Entry(10, "AdaptiveFit.Sample",     now.AddMinutes(-10)),
        };

        UserIntensityStats stats = IntensityStatsBuilder.Build(entries, now, triggerPressure: 100.0);
        Assert.Equal(2.0, stats.SpikeRecurrencePerHour, 9); // 2 spikes / 1 hour
    }

    // ── FMS aggregate ──────────────────────────────────────────────────────────
    [Fact]
    public void Build_FmsAggregate_ZeroWhenNoFmsFieldPresent()
    {
        // EventLogEntry carries no FMS field, and no selector supplied -> 0.
        var entries = new[] { Entry(50), Entry(60) };
        UserIntensityStats stats = IntensityStatsBuilder.Build(
            entries, DateTime.UtcNow, triggerPressure: 100.0);

        Assert.Equal(0.0, stats.FmsAggregate, 9);
    }

    [Fact]
    public void Build_FmsAggregate_MeansSelectorValues_SkippingNulls()
    {
        var entries = new[] { Entry(50), Entry(60), Entry(70) };
        // FMS scores 4, 8, and (null) -> mean of the two present = 6.
        double?[] fms = { 4.0, 8.0, null };
        int i = 0;
        UserIntensityStats stats = IntensityStatsBuilder.Build(
            entries, DateTime.UtcNow, triggerPressure: 100.0,
            fmsSelector: _ => fms[i++]);

        Assert.Equal(6.0, stats.FmsAggregate, 9);
    }

    // ── Empty / null input -> neutral ──────────────────────────────────────────
    [Fact]
    public void Build_EmptyInput_ReturnsNeutral()
    {
        UserIntensityStats stats = IntensityStatsBuilder.Build(
            Array.Empty<EventLogEntry>(), DateTime.UtcNow, triggerPressure: 85.0);

        Assert.Same(UserIntensityStats.Neutral, stats);
        Assert.Equal(0, stats.SampleCount);
        Assert.Equal(0.0, stats.PressureP50, 9);
        Assert.Equal(0.0, stats.PressureP90, 9);
        Assert.Equal(0.0, stats.SpikeRecurrencePerHour, 9);
        Assert.Equal(0.0, stats.CalmRatio, 9);
        Assert.Equal(0.0, stats.FmsAggregate, 9);
    }

    [Fact]
    public void Build_NullInput_ReturnsNeutral()
    {
        UserIntensityStats stats = IntensityStatsBuilder.Build(
            null!, DateTime.UtcNow, triggerPressure: 85.0);
        Assert.Same(UserIntensityStats.Neutral, stats);
    }

    [Fact]
    public void Build_NonPositiveTrigger_FallsBackToSenseDefault_NoDivideByZero()
    {
        // trigger 0 -> falls back to SenseDefaults.TriggerPressure (85).
        var entries = new[] { Entry(85) };
        UserIntensityStats stats = IntensityStatsBuilder.Build(
            entries, DateTime.UtcNow, triggerPressure: 0.0);

        Assert.Equal(85.0 / SenseDefaults.TriggerPressure, stats.PressureP50, 9);
        Assert.False(double.IsNaN(stats.PressureP50));
        Assert.False(double.IsInfinity(stats.PressureP50));
    }

    // ── ReadDurable: default interface impl filters Recent() by `since` ─────────
    [Fact]
    public void InMemoryStore_ReadDurable_FiltersRecentBySince()
    {
        // Default interface members are reachable only through the interface type.
        SenseEase.Core.Abstractions.IEventLogStore store = new InMemoryEventLogStore();
        DateTime now = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

        store.Append(Entry(10, "AdaptiveFit.Sample", now.AddHours(-3)));
        store.Append(Entry(20, "AdaptiveFit.Sample", now.AddMinutes(-30)));

        IReadOnlyList<EventLogEntry> recent = store.ReadDurable(now.AddHours(-1));

        Assert.Single(recent);
        Assert.Equal(20.0, recent[0].Pressure);
    }

    // ── ReadDurable on the file store: durable round-trip across the JSONL ──────
    [Fact]
    public void FileStore_AdaptiveFitCategory_IsDurable_AndReadBackBySince()
    {
        string dir = Path.Combine(Path.GetTempPath(), "senseease-adaptivefit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileEventLogStore(dir);
            DateTime now = DateTime.UtcNow;

            // AdaptiveFit.* must now be a durable category (written to disk).
            store.Append(Entry(42, "AdaptiveFit.Sample", now.AddMinutes(-10)));
            // A non-durable category (no forensic prefix) must NOT round-trip from disk.
            store.Append(Entry(99, "Inference", now.AddMinutes(-5)));
            // An old AdaptiveFit entry, filtered out by `since`.
            store.Append(Entry(7, "AdaptiveFit.Sample", now.AddDays(-2)));

            IReadOnlyList<EventLogEntry> durable = store.ReadDurable(now.AddHours(-1));

            Assert.Single(durable);
            Assert.Equal(42.0, durable[0].Pressure);
            Assert.Equal("AdaptiveFit.Sample", durable[0].Category);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void FileStore_ReadDurable_EmptyDir_ReturnsEmpty_NotThrow()
    {
        string dir = Path.Combine(Path.GetTempPath(), "senseease-adaptivefit-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileEventLogStore(dir);
            IReadOnlyList<EventLogEntry> durable = store.ReadDurable(DateTime.UtcNow.AddDays(-7));
            Assert.Empty(durable);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── End-to-end: durable log -> builder -> stats ────────────────────────────
    [Fact]
    public void Build_FromFileStoreDurableLog_ProducesStats()
    {
        string dir = Path.Combine(Path.GetTempPath(), "senseease-adaptivefit-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileEventLogStore(dir);
            DateTime now = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

            store.Append(Entry(10, "AdaptiveFit.Sample", now.AddHours(-2)));
            store.Append(Entry(90, "Habituation.Spike",  now.AddHours(-1)));
            store.Append(Entry(50, "AdaptiveFit.Sample", now.AddMinutes(-30)));

            IReadOnlyList<EventLogEntry> durable = store.ReadDurable(now.AddHours(-3));
            UserIntensityStats stats = IntensityStatsBuilder.Build(durable, now, triggerPressure: 100.0);

            Assert.Equal(3, stats.SampleCount);
            // 1 spike over a 2-hour span (earliest entry at now-2h) = 0.5/hr.
            Assert.Equal(0.5, stats.SpikeRecurrencePerHour, 9);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
