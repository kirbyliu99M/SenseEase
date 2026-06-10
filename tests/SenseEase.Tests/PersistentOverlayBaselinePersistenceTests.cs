using System;
using System.IO;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>CW-7 (UX-12 ≡ FEAT-15) — the always-on overlay baseline (enabled flag +
/// level) is now persisted in <see cref="UserPreferences"/> so the card's one effortless
/// comfort action survives restart instead of resetting to OFF / L1 every launch. These
/// tests pin two halves of the contract:
/// <list type="number">
///   <item>FRESH prefs (no file) ⇒ the VM starts with the always-on overlay OFF and the
///         baseline at L1 — the 2026-06-06 Kirby default-OFF override stands.</item>
///   <item>Setting the baseline writes it through to the prefs file, and a freshly
///         constructed VM (simulating a restart, prefs cache dropped) hydrates that
///         baseline BEFORE the overlay first shows.</item>
/// </list>
///
/// <para>Q4.5 note: the persisted baseline is a DISPLAY floor only (it floors the shown
/// tier via ResolveDisplayedTier); nothing here touches the earned habituation _offset,
/// so this stays clear of the B0 pause-preservation invariant.</para>
///
/// <para>CW-9 isolation: mirrors <see cref="GradedExposureOptInTests"/> — points
/// <c>UserPreferences.FilePath</c> at a per-class temp subdir via
/// <c>AppConfig.DataRootOverride</c>, serialised against the other override-retargeting
/// classes through the <c>TestDataRoot</c> collection, so the single static override never
/// races and the developer's real user-preferences.json is never touched.</para></summary>
[Collection("TestDataRoot")]
public class PersistentOverlayBaselinePersistenceTests : IDisposable
{
    public PersistentOverlayBaselinePersistenceTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(PersistentOverlayBaselinePersistenceTests));
        if (File.Exists(UserPreferences.FilePath)) File.Delete(UserPreferences.FilePath);
        UserPreferences.ResetCacheForTesting();
    }

    public void Dispose() => UserPreferences.ResetCacheForTesting();

    [Fact]
    public void FreshPrefs_StartsOverlayOff_BaselineL1()
    {
        // No prefs file on disk → defaults: always-on overlay OFF, baseline L1.
        var vm = new MainViewModel();

        Assert.False(vm.PersistentOverlayEnabled,
            "fresh install must start with the always-on overlay OFF (Kirby 2026-06-06 default-OFF override)");
        Assert.Equal(1, vm.PersistentOverlayBaselineTier);
    }

    [Fact]
    public void SettingBaseline_WritesToPrefsFile()
    {
        var vm = new MainViewModel();

        vm.PersistentOverlayBaselineTier = 5;
        vm.PersistentOverlayEnabled = true;

        // The setters persist immediately; reload the file fresh and assert it round-trips.
        UserPreferences.ResetCacheForTesting();
        var reloaded = UserPreferences.Current;
        Assert.True(reloaded.PersistentOverlayEnabled);
        Assert.Equal(5, reloaded.PersistentOverlayBaselineTier);
    }

    [Fact]
    public void PersistedBaseline_HydratesIntoFreshViewModel()
    {
        // Simulate a prior session that raised the baseline + turned the overlay on.
        UserPreferences.Current.PersistentOverlayEnabled = true;
        UserPreferences.Current.PersistentOverlayBaselineTier = 7;
        UserPreferences.Current.Save();

        // Restart: drop the cached singleton so the next VM re-reads from disk.
        UserPreferences.ResetCacheForTesting();
        var vm = new MainViewModel();

        // The ctor must have hydrated the baseline from the persisted prefs.
        Assert.True(vm.PersistentOverlayEnabled,
            "a persisted always-on overlay opt-in must survive restart");
        Assert.Equal(7, vm.PersistentOverlayBaselineTier);
    }

    [Fact]
    public void HydratedBaseline_FloorsDisplayedTier()
    {
        // A persisted baseline must actually floor the displayed tier after restart
        // (proves the hydrated fields feed ResolveDisplayedTier, not just round-trip).
        UserPreferences.Current.PersistentOverlayEnabled = true;
        UserPreferences.Current.PersistentOverlayBaselineTier = 6;
        UserPreferences.Current.Save();
        UserPreferences.ResetCacheForTesting();

        var vm = new MainViewModel();

        // Natural tier below the baseline → floored up to the baseline.
        Assert.Equal(6, vm.ResolveDisplayedTier(1));
        // Natural tier above the baseline still wins.
        Assert.Equal(9, vm.ResolveDisplayedTier(9));
    }

    [Fact]
    public void HandEditedOutOfRangeBaseline_ClampedOnHydrate()
    {
        // Defensive: a hand-edited prefs file with an out-of-range level must clamp to
        // the 1..12 native scale on hydrate rather than propagate a bad value.
        UserPreferences.Current.PersistentOverlayEnabled = true;
        UserPreferences.Current.PersistentOverlayBaselineTier = 99;
        UserPreferences.Current.Save();
        UserPreferences.ResetCacheForTesting();

        var vm = new MainViewModel();

        Assert.Equal(12, vm.PersistentOverlayBaselineTier);
    }
}
