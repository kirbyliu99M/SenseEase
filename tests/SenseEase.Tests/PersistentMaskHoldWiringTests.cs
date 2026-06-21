using System;
using System.IO;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Item 4 — VM-level WIRING regression for the UVHS bug found 2026-06-21: the "pin L6 + freeze the auto-level
/// loop" hard hold must be driven by the user's manual <b>常駐遮罩 / Persistent mask</b> switch
/// (<see cref="MainViewModel.SetPersistentMaskFromUser"/>), NOT by <b>自動防護 / Auto Protect</b>
/// (<see cref="MainViewModel.AlwaysAllowOverlay"/>, which is a standing-consent flag only). The hold was
/// originally mis-wired onto Auto Protect; these tests pin it to the right toggle and prove Auto Protect
/// stays freeze-free. The engine-level hold mechanics are covered by <see cref="OverlayAlwaysOnHoldTests"/>.
///
/// <para>Constructs a real <see cref="MainViewModel"/> (the D6/Liveness pattern). CW-9 isolation: both
/// toggles persist to <c>UserPreferences.Current</c>, so point it at a per-class temp subdir and reset the
/// cached singleton around each test, serialised via the <c>TestDataRoot</c> collection.</para>
/// </summary>
[Collection("TestDataRoot")]
public sealed class PersistentMaskHoldWiringTests : IDisposable
{
    public PersistentMaskHoldWiringTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(PersistentMaskHoldWiringTests));
        if (File.Exists(UserPreferences.FilePath)) File.Delete(UserPreferences.FilePath);
        UserPreferences.ResetCacheForTesting();
    }

    public void Dispose() => UserPreferences.ResetCacheForTesting();

    [Fact]
    public void PersistentMask_On_PinsTierAtSix_AndEnablesOverlay()
    {
        var vm = new MainViewModel();
        Assert.False(vm.PersistentOverlayEnabled); // pre-condition (defaults OFF)

        vm.SetPersistentMaskFromUser(true);

        Assert.True(vm.PersistentOverlayEnabled, "the manual Persistent-mask toggle must enable the always-on overlay");
        Assert.NotNull(vm.CurrentState);
        Assert.Equal(6, vm.CurrentState!.VimsTier);                       // engine pinned at the impactful tier
        Assert.Equal(6, vm.ResolveDisplayedTier(vm.CurrentState.VimsTier)); // floored display also reads 6
    }

    [Fact]
    public void PersistentMask_Off_DisablesOverlay()
    {
        var vm = new MainViewModel();
        vm.SetPersistentMaskFromUser(true);
        Assert.True(vm.PersistentOverlayEnabled);

        vm.SetPersistentMaskFromUser(false);
        Assert.False(vm.PersistentOverlayEnabled, "toggling Persistent mask off must release the always-on overlay");
    }

    [Fact]
    public void AutoProtect_On_DoesNotPinTier_NorEnablePersistentOverlay()
    {
        // THE regression: Auto Protect is standing consent only — it must NOT pin L6, freeze the loop, or
        // turn on the persistent overlay. (Before the fix, enabling Auto Protect pinned the tier at 6.)
        var vm = new MainViewModel();

        vm.AlwaysAllowOverlay = true;

        Assert.True(vm.AlwaysAllowOverlay);
        Assert.False(vm.PersistentOverlayEnabled, "Auto Protect must NOT enable the persistent mask");
        Assert.NotNull(vm.CurrentState);
        Assert.NotEqual(6, vm.CurrentState!.VimsTier); // follows the natural (calm) tier, not a pinned L6
    }
}
