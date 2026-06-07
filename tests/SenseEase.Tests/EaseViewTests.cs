using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>AF-1: the card's "緩和畫面" control must actually, visibly ease the view —
/// it routes through MainViewModel.EaseView → the D5 deliberate-preference path, so
/// each press tightens the FOV (smaller clear zone = more dimming) WITHOUT counting
/// as a discomfort recurrence. (The no-discomfort / habituation-preservation half is
/// pinned at the engine level by HabituationTests; here we lock the VM wiring + the
/// visible effect.)</summary>
public class EaseViewTests
{
    [Fact]
    public void EaseView_TightensFov_EachPress()
    {
        var vm = new MainViewModel();
        int before = vm.FovRadiusPercent;

        // Press repeatedly rather than asserting a single press moves the FOV. The
        // accumulated user FOV offset clamps at -60pp, which exceeds the earned-
        // habituation offset cap (<=30pp). So even on a machine whose %LOCALAPPDATA%
        // carries a real persisted habituation offset — which at REST (FOV already
        // fully open at 100) would absorb one small deliberate tighten — enough
        // presses always drive the composed FOV strictly below the fully-open
        // baseline. Each press must tighten or hold at the clamp (never loosen),
        // which is the deliberate-preference contract under test.
        int prev = before;
        for (int i = 0; i < 6; i++)
        {
            vm.EaseView();
            Assert.True(vm.FovRadiusPercent <= prev,
                $"press {i + 1} must tighten the FOV or hold at the clamp ({vm.FovRadiusPercent} <= {prev})");
            prev = vm.FovRadiusPercent;
        }

        Assert.True(vm.FovRadiusPercent < before,
            $"after repeated presses the FOV must be tighter than fully-open ({vm.FovRadiusPercent} < {before})");
    }

    // The deliberate-preference no-discomfort / no-pause / offset-preservation
    // guarantee is pinned at the engine level by
    // HabituationTests.ApplyUserFeedback_DeliberatePreference_DoesNotErodeHabituationOrPause
    // (a fresh VM starts with habituation paused, so a VM-level non-pause assertion
    // would be vacuous here).
}
