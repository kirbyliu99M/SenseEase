using SenseEase.App.ViewModels;
using SenseEase.Core.Models;
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
}
