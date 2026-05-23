using System;
using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Unit tests for <see cref="PressureSpikeDetector"/>.</summary>
public class PressureSpikeDetectorTests
{
    private static DateTime T0 => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Push_Returns_True_When_Pressure_Rises_30Pct_Within_Window()
    {
        var d = new PressureSpikeDetector();

        Assert.False(d.Push(50, T0));
        Assert.False(d.Push(55, T0.AddSeconds(1)));
        // 50 → 70 over 2s is a 40% rise, within the 3s window.
        Assert.True(d.Push(70, T0.AddSeconds(2)));
    }

    [Fact]
    public void Push_Returns_False_For_Slow_Rise_Over_Long_Window()
    {
        var d = new PressureSpikeDetector();

        // 50 → 65 over 10 seconds. With a 3 s window, the baseline at any
        // tick is at most ~3 s old, so the per-window slice never sees a
        // 30 % jump.
        double p = 50;
        bool fired = false;
        for (int i = 0; i <= 20; i++)
        {
            // step the pressure by 0.75 every 0.5 s — 1.5/sec → +15 over 10 s.
            if (d.Push(p, T0.AddSeconds(i * 0.5))) { fired = true; break; }
            p += 0.75;
        }

        Assert.False(fired);
    }

    [Fact]
    public void Push_Returns_False_When_Baseline_Below_MinBaseline()
    {
        var d = new PressureSpikeDetector(minBaseline: 5.0);

        Assert.False(d.Push(0.1, T0));
        Assert.False(d.Push(0.5, T0.AddSeconds(1)));
        // 0.1 → 2.0 is a huge relative rise, but baseline < minBaseline.
        Assert.False(d.Push(2.0, T0.AddSeconds(2)));
    }

    [Fact]
    public void Push_Respects_Cooldown_After_Firing()
    {
        var d = new PressureSpikeDetector(cooldownSeconds: 30.0);

        Assert.False(d.Push(50, T0));
        Assert.True(d.Push(80, T0.AddSeconds(2)));

        // 5 s later, another fresh spike (40 → 80) — cooldown should swallow it.
        Assert.False(d.Push(40, T0.AddSeconds(5)));
        Assert.False(d.Push(80, T0.AddSeconds(7)));
    }

    [Fact]
    public void Push_Refires_After_Cooldown_Expires()
    {
        var d = new PressureSpikeDetector(cooldownSeconds: 30.0);

        Assert.False(d.Push(50, T0));
        Assert.True(d.Push(80, T0.AddSeconds(2)));

        // After cooldown, a new spike (50 → 80 within window) must fire again.
        Assert.False(d.Push(50, T0.AddSeconds(40)));
        Assert.True(d.Push(80, T0.AddSeconds(42)));
    }

    [Fact]
    public void Push_Ignores_Falls()
    {
        var d = new PressureSpikeDetector();

        Assert.False(d.Push(100, T0));
        Assert.False(d.Push(90, T0.AddSeconds(1)));
        Assert.False(d.Push(50, T0.AddSeconds(2)));
    }

    [Fact]
    public void Reset_Clears_History_And_Cooldown()
    {
        var d = new PressureSpikeDetector(cooldownSeconds: 30.0);

        Assert.False(d.Push(50, T0));
        Assert.True(d.Push(80, T0.AddSeconds(2)));

        d.Reset();

        // After reset, the next Push has no prior sample — must not fire even
        // though without reset the cooldown would still be active.
        Assert.False(d.Push(50, T0.AddSeconds(10)));
        // Reset must also clear cooldown — without it, this Push at T+12s
        // would be suppressed (only 10 s after the original fire at T+2s).
        Assert.True(d.Push(80, T0.AddSeconds(12)));
        Assert.Equal(0.0, new PressureSpikeDetector().LastNormalizedSpike);
    }

    [Fact]
    public void LastNormalizedSpike_Reflects_Magnitude()
    {
        var d = new PressureSpikeDetector();

        Assert.False(d.Push(50, T0));
        Assert.True(d.Push(70, T0.AddSeconds(2))); // (70-50)/50 = 0.40
        Assert.InRange(d.LastNormalizedSpike, 0.0, 1.0);
        Assert.InRange(d.LastNormalizedSpike, 0.35, 0.45);

        // A much bigger jump — clamped at 1.0 because new value ≥ 2× baseline.
        var d2 = new PressureSpikeDetector();
        Assert.False(d2.Push(40, T0));
        Assert.True(d2.Push(200, T0.AddSeconds(2)));
        Assert.Equal(1.0, d2.LastNormalizedSpike);
    }
}
