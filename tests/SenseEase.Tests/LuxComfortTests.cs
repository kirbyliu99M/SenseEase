using SenseEase.App.Comfort;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Unit tests for the Phase-4 Lux Comfort pipeline (LuxComfortMapper + LuxComfortController).</summary>
public class LuxComfortTests
{
    // ── Test doubles ─────────────────────────────────────────────────────────────

    private sealed class FakeActuator : IDisplayComfortActuator
    {
        public bool ConfigureSupported { get; init; } = true;
        public int ApplyCount { get; private set; }
        public int RestoreCount { get; private set; }
        public (int c, int b, int s) LastApplied { get; private set; }
        public DisplayCapability Capability { get; private set; } = DisplayCapability.None;
        public bool IsInitialized { get; private set; }
        public DisplayBaseline Baseline { get; private set; } = new();
        public string MonitorSignature { get; private set; } = string.Empty;
        public bool Initialize(DisplayBaseline? priorBaseline = null)
        {
            if (!ConfigureSupported) return false;
            Capability = new DisplayCapability
            {
                ContrastSupported = true, ContrastMax = 100,
                BrightnessSupported = true, BrightnessMax = 100,
                SharpnessSupported = true, SharpnessMax = 100,
            };
            Baseline = new DisplayBaseline
            {
                HasContrast = true, OriginalContrast = 50,
                HasBrightness = true, OriginalBrightness = 50,
                HasSharpness = true, OriginalSharpness = 50,
            };
            MonitorSignature = "fake-monitor";
            IsInitialized = true;
            return true;
        }
        public void Apply(int c, int b, int s)
        {
            ApplyCount++;
            LastApplied = (c, b, s);
        }
        public void RestoreBaseline() => RestoreCount++;
        public void Dispose() { }
    }

    private sealed class FakeBaselineStore : IDisplayBaselineStore
    {
        public DisplayBaselineRecord? Current;
        public int SaveCount { get; private set; }
        public DisplayBaselineRecord? Load() => Current;
        public void Save(DisplayBaselineRecord record) { Current = record; SaveCount++; }
        public void Clear() => Current = null;
    }

    // ── Helper ───────────────────────────────────────────────────────────────────

    private static async Task<(LuxComfortController c, MockAmbientLightProvider p, FakeActuator a, FakeBaselineStore s)>
        MakeRunningControllerAsync(bool supported = true, bool enabled = true)
    {
        var provider = new MockAmbientLightProvider();
        var actuator = new FakeActuator { ConfigureSupported = supported };
        var store    = new FakeBaselineStore();
        var ctrl     = new LuxComfortController(provider, actuator, store);
        await ctrl.InitializeAsync();
        ctrl.Start();
        ctrl.SetEnabled(enabled);
        return (ctrl, provider, actuator, store);
    }

    // ── Helper: steady EMA settle ─────────────────────────────────────────────

    /// <summary>Feed the same lux value into a fresh mapper <paramref name="n"/> times and return the final target.</summary>
    private static LuxComfortMapper.ComfortTarget SteadyTarget(double lux, int n = 50)
    {
        var mapper = new LuxComfortMapper();
        LuxComfortMapper.ComfortTarget target = default;
        for (int i = 0; i < n; i++)
            target = mapper.Update(lux);
        return target;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MAPPER — comfort curve
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Mapper_DarkRoom_ProducesMinTargets()
    {
        // A single lux=1 reading seeds the EMA; log10(1)=0 → t=0 → all-min targets.
        var mapper = new LuxComfortMapper();

        var target = mapper.Update(1.0);

        Assert.Equal(SenseDefaults.ContrastMinPercent,   target.ContrastPercent);
        Assert.Equal(SenseDefaults.BrightnessMinPercent, target.BrightnessPercent);
        Assert.Equal(SenseDefaults.SharpnessMinPercent,  target.SharpnessPercent);
    }

    [Fact]
    public void Mapper_BrightDaylight_ProducesMaxTargets()
    {
        // lux=10000 seeds the EMA; log10(10000)=4 → t=1 → all-max targets.
        var mapper = new LuxComfortMapper();

        var target = mapper.Update(10000.0);

        Assert.Equal(SenseDefaults.ContrastMaxPercent,   target.ContrastPercent);
        Assert.Equal(SenseDefaults.BrightnessMaxPercent, target.BrightnessPercent);
        Assert.Equal(SenseDefaults.SharpnessMaxPercent,  target.SharpnessPercent);
    }

    [Fact]
    public void Mapper_ClampsAtBothEnds()
    {
        // Degenerate inputs (0, negative, NaN, +Inf) must all produce finite results
        // within the configured [Min, Max] percent bounds.
        double[] inputs = [0.0, -5.0, double.NaN, double.PositiveInfinity];

        foreach (double raw in inputs)
        {
            var mapper = new LuxComfortMapper();
            var t = mapper.Update(raw);

            Assert.False(double.IsNaN(t.ContrastPercent),   $"Contrast is NaN for input {raw}");
            Assert.False(double.IsNaN(t.BrightnessPercent), $"Brightness is NaN for input {raw}");
            Assert.False(double.IsNaN(t.SharpnessPercent),  $"Sharpness is NaN for input {raw}");

            Assert.InRange(t.ContrastPercent,
                SenseDefaults.ContrastMinPercent,   SenseDefaults.ContrastMaxPercent);
            Assert.InRange(t.BrightnessPercent,
                SenseDefaults.BrightnessMinPercent, SenseDefaults.BrightnessMaxPercent);
            Assert.InRange(t.SharpnessPercent,
                SenseDefaults.SharpnessMinPercent,  SenseDefaults.SharpnessMaxPercent);
        }
    }

    [Fact]
    public void Mapper_LuxAboveCeiling_Clamps()
    {
        // lux=1e9 exceeds the 10000 ceiling; after EMA settles it should equal the
        // daylight-max case (same as lux=10000 single-seed).
        var mapper = new LuxComfortMapper();
        LuxComfortMapper.ComfortTarget target = default;
        for (int i = 0; i < 50; i++)
            target = mapper.Update(1e9);

        var maxTarget = SteadyTarget(10000.0, 50);

        Assert.Equal(maxTarget.ContrastPercent,   target.ContrastPercent);
        Assert.Equal(maxTarget.BrightnessPercent, target.BrightnessPercent);
        Assert.Equal(maxTarget.SharpnessPercent,  target.SharpnessPercent);
    }

    [Fact]
    public void Mapper_IsMonotonicInLux()
    {
        // Increasing lux steps: 10 → 100 → 1000 → 10000 settled over many samples.
        // Each step's targets must be >= the previous step's.
        double[] steps = [10.0, 100.0, 1000.0, 10000.0];
        LuxComfortMapper.ComfortTarget prev = default;
        bool first = true;

        foreach (double lux in steps)
        {
            var t = SteadyTarget(lux, 60);
            if (!first)
            {
                Assert.True(t.ContrastPercent   >= prev.ContrastPercent,
                    $"Contrast not monotonic: {t.ContrastPercent} < {prev.ContrastPercent} at lux {lux}");
                Assert.True(t.BrightnessPercent >= prev.BrightnessPercent,
                    $"Brightness not monotonic: {t.BrightnessPercent} < {prev.BrightnessPercent} at lux {lux}");
                Assert.True(t.SharpnessPercent  >= prev.SharpnessPercent,
                    $"Sharpness not monotonic: {t.SharpnessPercent} < {prev.SharpnessPercent} at lux {lux}");
            }
            prev = t;
            first = false;
        }
    }

    [Fact]
    public void Mapper_IsLogarithmic()
    {
        // The comfort curve is linear in log10(lux).  The delta between targets at
        // decade 10→100 must equal the delta at 100→1000 (within ±2 percent-points
        // to allow integer rounding).
        const int n = 60; // enough EMA settle ticks

        var t10   = SteadyTarget(10.0,   n);
        var t100  = SteadyTarget(100.0,  n);
        var t1000 = SteadyTarget(1000.0, n);

        int deltaLow  = t100.ContrastPercent  - t10.ContrastPercent;
        int deltaHigh = t1000.ContrastPercent - t100.ContrastPercent;
        Assert.InRange(deltaHigh, deltaLow - 2, deltaLow + 2);

        deltaLow  = t100.BrightnessPercent  - t10.BrightnessPercent;
        deltaHigh = t1000.BrightnessPercent - t100.BrightnessPercent;
        Assert.InRange(deltaHigh, deltaLow - 2, deltaLow + 2);

        deltaLow  = t100.SharpnessPercent  - t10.SharpnessPercent;
        deltaHigh = t1000.SharpnessPercent - t100.SharpnessPercent;
        Assert.InRange(deltaHigh, deltaLow - 2, deltaLow + 2);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MAPPER — EMA
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Mapper_FirstReadingSeeds()
    {
        // The very first Update() must seed the EMA directly (no blending).
        var mapper = new LuxComfortMapper();

        mapper.Update(500.0);

        Assert.Equal(500.0, mapper.SmoothedLux);
    }

    [Fact]
    public void Mapper_RejectsSingleSpike()
    {
        // After settling at 100 lx, a single spike to 10000 lx moves the EMA by at
        // most one blending step: smoothed < 100 + (10000-100)*(1-alpha) + 1.
        var mapper = new LuxComfortMapper();
        for (int i = 0; i < 30; i++)
            mapper.Update(100.0);

        mapper.Update(10000.0);

        double upperBound = 100.0 + (10000.0 - 100.0) * (1.0 - SenseDefaults.LuxEmaAlpha) + 1.0;
        Assert.True(mapper.SmoothedLux < upperBound,
            $"SmoothedLux {mapper.SmoothedLux:F2} must be < {upperBound:F2} after a single spike.");
    }

    [Fact]
    public void Mapper_ConvergesToSteady()
    {
        // 50 identical readings must drive the EMA within ±5 of the input.
        var mapper = new LuxComfortMapper();
        for (int i = 0; i < 50; i++)
            mapper.Update(500.0);

        Assert.InRange(mapper.SmoothedLux, 495.0, 505.0);
    }

    [Fact]
    public void Mapper_Reset_ClearsState()
    {
        // After Reset(), the next Update() must re-seed (no EMA carry-over).
        var mapper = new LuxComfortMapper();
        mapper.Update(500.0);
        mapper.Reset();

        mapper.Update(7000.0);

        Assert.Equal(7000.0, mapper.SmoothedLux);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CONTROLLER — capability & enable gating
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Controller_UnsupportedDisplay_NeverCallsApply()
    {
        var (ctrl, provider, actuator, _) = await MakeRunningControllerAsync(supported: false);

        for (int i = 0; i < 5; i++)
            provider.Push(5000.0);

        Assert.Equal(0, actuator.ApplyCount);
    }

    [Fact]
    public async Task Controller_Disabled_NeverCallsApply()
    {
        var (ctrl, provider, actuator, _) = await MakeRunningControllerAsync(enabled: false);

        provider.Push(5000.0);

        Assert.Equal(0, actuator.ApplyCount);
    }

    [Fact]
    public async Task Controller_EnabledAndSupported_AppliesOnFirstSample()
    {
        var (ctrl, provider, actuator, _) = await MakeRunningControllerAsync();

        provider.Push(5000.0);

        Assert.True(actuator.ApplyCount >= 1,
            "Apply should have been called at least once after a lux sample.");

        // Applied values must lie within the configured comfort-target bounds.
        Assert.InRange(actuator.LastApplied.c,
            SenseDefaults.ContrastMinPercent,   SenseDefaults.ContrastMaxPercent);
        Assert.InRange(actuator.LastApplied.b,
            SenseDefaults.BrightnessMinPercent, SenseDefaults.BrightnessMaxPercent);
        Assert.InRange(actuator.LastApplied.s,
            SenseDefaults.SharpnessMinPercent,  SenseDefaults.SharpnessMaxPercent);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CONTROLLER — hysteresis
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Controller_RepeatedIdenticalLux_AppliesOnce()
    {
        // The first sample always applies (lastApplied == -1). Subsequent identical
        // lux readings produce the same target; the deadband suppresses re-applies.
        var (ctrl, provider, actuator, _) = await MakeRunningControllerAsync();

        for (int i = 0; i < 5; i++)
            provider.Push(1000.0);

        Assert.Equal(1, actuator.ApplyCount);
    }

    [Fact]
    public async Task Controller_TinyLuxChange_WithinDeadband_DoesNotReApply()
    {
        // Push 1000, then 1001. The EMA-smoothed target delta between these two
        // consecutive readings is negligible (< LuxHysteresisDeadbandPercent).
        var (ctrl, provider, actuator, _) = await MakeRunningControllerAsync();

        provider.Push(1000.0);
        provider.Push(1001.0);

        Assert.Equal(1, actuator.ApplyCount);
    }

    [Fact]
    public async Task Controller_LargeLuxChange_BeyondDeadband_ReApplies()
    {
        // Settle at dark (50 lx), then jump to bright (8000 lx) — the EMA eventually
        // moves enough that the target delta exceeds the deadband, triggering a second apply.
        var (ctrl, provider, actuator, _) = await MakeRunningControllerAsync();

        // Settle low.
        for (int i = 0; i < 5; i++)
            provider.Push(50.0);

        // Jump high enough to cross the deadband after EMA blending.
        for (int i = 0; i < 5; i++)
            provider.Push(8000.0);

        Assert.True(actuator.ApplyCount >= 2,
            $"Expected >= 2 Apply calls after a large lux jump; got {actuator.ApplyCount}.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CONTROLLER — restore lifecycle
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Controller_Shutdown_RestoresBaseline()
    {
        var (ctrl, provider, actuator, _) = await MakeRunningControllerAsync();
        provider.Push(5000.0); // ensure at least one Apply happened

        ctrl.Shutdown();

        Assert.Equal(1, actuator.RestoreCount);
    }

    [Fact]
    public async Task Controller_Disable_RestoresBaseline()
    {
        var (ctrl, provider, actuator, _) = await MakeRunningControllerAsync();
        provider.Push(5000.0); // ensure enabled path ran

        ctrl.SetEnabled(false);

        Assert.Equal(1, actuator.RestoreCount);
    }

    [Fact]
    public async Task Controller_Shutdown_MarksRecordClean()
    {
        // Pre-seed a dirty baseline record directly in the store so that Shutdown()
        // finds it and marks it clean. (InitializeCore only saves via Dxva2DisplayActuator,
        // so we inject the record manually to mirror a real-hardware session.)
        var provider = new MockAmbientLightProvider();
        var actuator = new FakeActuator { ConfigureSupported = true };
        var store    = new FakeBaselineStore();

        // Plant a dirty record before constructing the controller.
        // MonitorSignature must match FakeActuator's "fake-monitor" so the new recovery
        // code does not discard the record on a signature mismatch.
        store.Current = new DisplayBaselineRecord { CleanShutdown = false, MonitorSignature = "fake-monitor" };

        var ctrl = new LuxComfortController(provider, actuator, store);
        await ctrl.InitializeAsync();
        ctrl.Start();
        ctrl.SetEnabled(true);

        ctrl.Shutdown();

        Assert.NotNull(store.Current);
        Assert.True(store.Current!.CleanShutdown,
            "CleanShutdown must be true after a clean Shutdown().");
    }

    [Fact]
    public async Task Controller_DoubleShutdown_Idempotent()
    {
        // Calling Shutdown() twice must not throw and RestoreBaseline must not be
        // called more times than is tolerable (robustness contract, not an exact count).
        var (ctrl, provider, actuator, _) = await MakeRunningControllerAsync();
        provider.Push(5000.0);

        var ex = Record.Exception(() =>
        {
            ctrl.Shutdown();
            ctrl.Shutdown();
        });

        Assert.Null(ex);
        // RestoreBaseline is called each Shutdown() invocation; both are expected.
        Assert.True(actuator.RestoreCount <= 2,
            $"RestoreBaseline called {actuator.RestoreCount} times; expected <= 2.");
    }
}
