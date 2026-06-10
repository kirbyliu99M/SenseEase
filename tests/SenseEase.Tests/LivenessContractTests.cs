using SenseEase.App.ViewModels;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// D3 liveness / trust contract — derivation-layer gates (Chunk 1).
///
/// <para>The load-bearing guarantee of the whole contract: a dead sensing
/// pipeline must STOP looking alive, and a user-chosen pause must NEVER be
/// confusable with an accidental fault. These tests pin the single derivation
/// point (<see cref="MainViewModel.DeriveLiveness"/> + the VM wiring) so
/// "working" and "broken" can never collapse to the same liveness scalar.</para>
///
/// <para>The visual distinguishability assertions (faulted bar brush ≠ healthy
/// bar brush; dashed Paused outline ≠ solid Fault outline) live in the Chunk 2
/// bar/tray tests — those need an STA thread to construct the Window.</para>
/// </summary>
public class LivenessContractTests
{
    // ── 1) The pure derivation: priority order ──────────────────────────────

    [Theory]
    // pausedByUser outranks everything — even a dead pipeline reads "paused".
    [InlineData(false, true,  true,  LivenessState.PausedByUser)]
    [InlineData(false, true,  false, LivenessState.PausedByUser)]
    [InlineData(true,  true,  true,  LivenessState.PausedByUser)]
    // not paused + unhealthy → Faulted, regardless of mask.
    [InlineData(false, false, true,  LivenessState.Faulted)]
    [InlineData(false, false, false, LivenessState.Faulted)]
    // healthy + not paused → Protecting (mask dimming) vs Steady-watching (calm).
    [InlineData(true,  false, true,  LivenessState.Protecting)]
    [InlineData(true,  false, false, LivenessState.SteadyWatching)]
    public void DeriveLiveness_FollowsPriorityOrder(
        bool sensingHealthy, bool pausedByUser, bool maskActive, LivenessState expected)
    {
        Assert.Equal(expected, MainViewModel.DeriveLiveness(sensingHealthy, pausedByUser, maskActive));
    }

    // ── 2) Default is a healthy, no-new-visual state ─────────────────────────

    [Fact]
    public void MainViewModel_FreshInstance_DefaultsToSteadyWatching()
    {
        // Smoke-compat: the bare new-VM path (no fault, no pause, mask off) must
        // land on Steady-watching so nothing new lights up at startup.
        var vm = new MainViewModel();
        Assert.Equal(LivenessState.SteadyWatching, vm.LivenessState);
    }

    // ── 3) A sensing fault surfaces Faulted AND raises LivenessChanged ───────

    [Fact]
    public void MainViewModel_SensingFault_SurfacesFaulted_AndFires()
    {
        var vm = new MainViewModel();
        vm.SetPowerSaveActiveLookup(() => false); // not paused

        LivenessState? fired = null;
        vm.LivenessChanged += (_, s) => fired = s;

        vm.TestHook_RaiseSensingHealth(false); // pipeline died unexpectedly

        Assert.Equal(LivenessState.Faulted, vm.LivenessState);
        Assert.Equal(LivenessState.Faulted, fired); // fan-out event must carry the transition
    }

    // ── 4) Recovery returns to a healthy state ───────────────────────────────

    [Fact]
    public void MainViewModel_Recovery_ReturnsToHealthy()
    {
        var vm = new MainViewModel();
        vm.SetPowerSaveActiveLookup(() => false);

        vm.TestHook_RaiseSensingHealth(false);
        Assert.Equal(LivenessState.Faulted, vm.LivenessState);

        // A fresh real frame restores sensing. With the mask off this is
        // Steady-watching; once the mask engages it is Protecting.
        vm.TestHook_RaiseSensingHealth(true);
        Assert.Equal(LivenessState.SteadyWatching, vm.LivenessState);

        vm.UpdateFromStateForTests(new SenseState { MaskActive = true, VimsTier = 5 });
        Assert.Equal(LivenessState.Protecting, vm.LivenessState);
    }

    // ── 5) THE anti-confusion gate: Paused never reads as Faulted ────────────

    [Fact]
    public void MainViewModel_PausedNeverConfusableWithFault()
    {
        // Calm-by-choice (Power Save) and calm-by-accident (fault) must never
        // collapse. Even with the pipeline reporting unhealthy, an active
        // user-pause must win — the highest-priority liveness signal.
        var vm = new MainViewModel();
        vm.SetPowerSaveActiveLookup(() => true); // user chose Power Save

        vm.TestHook_RaiseSensingHealth(false);   // and sensing also stopped

        Assert.Equal(LivenessState.PausedByUser, vm.LivenessState);
        Assert.NotEqual(LivenessState.Faulted, vm.LivenessState);
    }

    // ── 6) Mask drives the healthy split in lock-step with the engine tick ───

    [Fact]
    public void MainViewModel_MaskActive_DrivesProtectingVsSteadyWatching()
    {
        var vm = new MainViewModel();
        vm.SetPowerSaveActiveLookup(() => false);

        vm.UpdateFromStateForTests(new SenseState { MaskActive = true, VimsTier = 6 });
        Assert.Equal(LivenessState.Protecting, vm.LivenessState);

        vm.UpdateFromStateForTests(new SenseState { MaskActive = false, VimsTier = 1 });
        Assert.Equal(LivenessState.SteadyWatching, vm.LivenessState);
    }

    // ── 7) CW-8/INTENT-04: the LOCKED ~5 s silent-death→Faulted bound is pinned ──
    //
    // LOCKED_DIRECTION_ALPHA1 P1-1: silent death → Faulted visual must surface at
    // ~5 s — "3000 ms stall + ≤2000 ms heal window ≈ 5 s. Do not let it grow."
    // The provider thresholds are the single source of truth (provider-local, not
    // SenseDefaults). These literal asserts make any future drift (e.g. the prior
    // 6000 ms regression that pushed worst-case surface latency to ~6–7 s) fail
    // here immediately, long before a user would notice the slow fault surface.

    [Fact]
    public void WgcLivenessThresholds_PinTheLocked5sFaultBound()
    {
        // Literal pins — the exact locked numbers.
        Assert.Equal(3000, WgcSensingProvider.SensingStalledThresholdMs);
        Assert.Equal(5000, WgcSensingProvider.SensingFaultedThresholdMs);
        Assert.Equal(1000, WgcSensingProvider.LivenessWatchdogIntervalMs);
    }

    [Fact]
    public void WgcLivenessThresholds_RelationshipHolds_StallPlusHealLEFaulted()
    {
        // The watchdog self-heals at the stall threshold and only surfaces Faulted
        // if STILL stale at the faulted threshold. The heal window is the gap
        // between them; the locked invariant is stalled + heal-window ≤ faulted so
        // the surfaced-fault worst case never grows past ~5 s.
        int stalled   = WgcSensingProvider.SensingStalledThresholdMs;
        int faulted   = WgcSensingProvider.SensingFaultedThresholdMs;
        int healWindow = faulted - stalled;

        Assert.True(faulted > stalled, "faulted threshold must be later than the stall threshold");
        Assert.Equal(2000, healWindow);                 // the ≤2000 ms heal window
        Assert.True(stalled + healWindow <= faulted,    // the locked relationship
            "stalled + heal-window must not exceed the faulted threshold (locked ~5 s bound)");
        Assert.True(faulted <= 5000,                     // "Do not let it grow" — never past ~5 s
            "faulted threshold must not exceed the locked ~5 s bound");

        // Watchdog cadence must be fine enough that the surfaced-fault granularity
        // stays small relative to the bound (1 s poll on a 5 s bound).
        Assert.Equal(1000, WgcSensingProvider.LivenessWatchdogIntervalMs);
        Assert.True(WgcSensingProvider.LivenessWatchdogIntervalMs < stalled);
    }
}
