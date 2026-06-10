using System;
using System.IO;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>D6 (master plan M7) day-1 protection exit-gate.
///
/// <para><b>History note (updated 2026-06-11).</b>  The original header
/// described a "calm field defaulting ON at baseline tier 1". That was the
/// <em>planned</em> default; a Kirby override on 2026-06-06 flipped it: the
/// always-on persistent overlay now defaults OFF (D-12 cross-ref — see the
/// comment block at <c>_persistentOverlayEnabled = false</c> in MainViewModel).
/// The D6 floor invariants (VimsLevels[0].Alpha == 0, natural spike still wins)
/// remain load-bearing and are pinned below.</para>
///
/// <para>The tests call the REAL <see cref="MainViewModel.ResolveDisplayedTier"/>
/// (VM constructs in tests — see LivenessContractTests for the same pattern;
/// the TestDataRootFixture module initialiser redirects all file I/O off the
/// real per-user folder). The private-mirror helper that was here before has
/// been removed; the production method under test is the single source of
/// truth.</para>
///
/// <para>CW-9 isolation: these tests construct a real <see cref="MainViewModel"/>, and the
/// enabled=true cases (cases 3/4) drive <c>PersistentOverlayEnabled = true</c> through the
/// setter, which persists the flag via <c>SavePersistentOverlayPrefs()</c> → <c>Save()</c>.
/// Without per-class isolation that <c>true</c> leaks through the shared
/// <c>UserPreferences.Current</c> singleton (and the on-disk user-preferences.json) into a
/// later <c>MainViewModel</c> ctor's <c>HydratePersistentOverlayFromPrefs()</c>, breaking the
/// "defaults FALSE" pre-conditions of the pass-through cases. Mirror
/// <see cref="PersistentOverlayBaselinePersistenceTests"/> / <see cref="GradedExposureOptInTests"/>:
/// point <c>UserPreferences.FilePath</c> at a per-class temp subdir via
/// <c>AppConfig.DataRootOverride</c>, delete any stale prefs file, drop the cached singleton
/// before AND after each test, and serialise against the other override-retargeting classes
/// through the <c>TestDataRoot</c> collection. The developer's real user-preferences.json is
/// never touched.</summary>
[Collection("TestDataRoot")]
public class D6DayOneProtectionTests : IDisposable
{
    public D6DayOneProtectionTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(D6DayOneProtectionTests));
        if (File.Exists(UserPreferences.FilePath)) File.Delete(UserPreferences.FilePath);
        UserPreferences.ResetCacheForTesting();
    }

    public void Dispose() => UserPreferences.ResetCacheForTesting();

    // ── 1) Enabled=false default (Kirby 2026-06-06 override, D-12 cross-ref) ──

    [Fact]
    public void PersistentOverlayEnabled_DefaultsToFalse()
    {
        // D-12 cross-ref (2026-06-06 Kirby override): the always-on overlay toggle
        // defaults OFF for a more immersive, zero-intervention start — aligned with
        // the Alpha-2 auto-profiling direction. If this regresses to true a new user
        // would see an always-on dim overlay on every launch before any interaction.
        var vm = new MainViewModel();
        Assert.False(vm.PersistentOverlayEnabled,
            "PersistentOverlayEnabled must default FALSE (2026-06-06 Kirby override, D-12 cross-ref). " +
            "Enabling it by default would impose the always-on overlay on every new user launch.");
    }

    // ── 2) ResolveDisplayedTier: enabled=false → natural pass-through ─────────

    [Fact]
    public void ResolveDisplayedTier_WhenDisabled_PassesThroughNaturalTier()
    {
        // With the feature off (the default), ResolveDisplayedTier must be a
        // pure pass-through: the engine's raw VIMS tier drives the overlay,
        // no floor applied.
        var vm = new MainViewModel();
        Assert.False(vm.PersistentOverlayEnabled); // pre-condition

        Assert.Equal(1,  vm.ResolveDisplayedTier(1));
        Assert.Equal(5,  vm.ResolveDisplayedTier(5));
        Assert.Equal(10, vm.ResolveDisplayedTier(10));
    }

    // ── 3) ResolveDisplayedTier: enabled=true, baseline 1 → tier 1 floors ─────

    [Fact]
    public void BaselineTier1_FloorsToOne_AndIsZeroAlphaAtRest()
    {
        var vm = new MainViewModel();
        vm.PersistentOverlayEnabled      = true;
        vm.PersistentOverlayBaselineTier = 1;

        // Enabled at baseline 1 with a calm screen → displayed tier 1.
        Assert.Equal(1, vm.ResolveDisplayedTier(1));
        // L1 (VimsLevels[0]) must be 0% alpha — nothing visibly dims at rest.
        Assert.Equal(0.00, OverlayWindow.VimsLevels[0].Alpha, 3);
    }

    // ── 4) Natural spike still wins above the baseline floor ──────────────────

    [Fact]
    public void NaturalSpike_StillWins_OverBaseline1()
    {
        var vm = new MainViewModel();
        vm.PersistentOverlayEnabled      = true;
        vm.PersistentOverlayBaselineTier = 1;

        // A real pressure-driven tier still darkens the periphery above the floor.
        Assert.Equal(5,  vm.ResolveDisplayedTier(5));
        Assert.Equal(10, vm.ResolveDisplayedTier(10));
    }

    // ── 5) Disabled → clean pass-through (no baseline applied) ───────────────

    [Fact]
    public void Disabled_PassesThrough_NaturalTierUnchanged()
    {
        var vm = new MainViewModel();
        // Ensure disabled even if prefs on disk set it (isolated by TestDataRootFixture).
        vm.PersistentOverlayEnabled = false;

        Assert.Equal(3, vm.ResolveDisplayedTier(3));
    }
}
