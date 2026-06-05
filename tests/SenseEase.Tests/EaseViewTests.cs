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

        vm.EaseView();
        int after1 = vm.FovRadiusPercent;
        Assert.True(after1 < before, $"press 1 should tighten the FOV ({after1} < {before})");

        vm.EaseView();
        Assert.True(vm.FovRadiusPercent <= after1, "press 2 should tighten further (or hold at the clamp)");
    }

    // The deliberate-preference no-discomfort / no-pause / offset-preservation
    // guarantee is pinned at the engine level by
    // HabituationTests.ApplyUserFeedback_DeliberatePreference_DoesNotErodeHabituationOrPause
    // (a fresh VM starts with habituation paused, so a VM-level non-pause assertion
    // would be vacuous here).
}
