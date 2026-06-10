using System;
using System.IO;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>D-1 (CW-1 step 3) — the graded-exposure (habituation) opt-in is PAUSED by
/// default and the Settings 漸進適應 / Graded exposure toggle is the sole user-intent flip.
/// These tests pin the startup-hydration contract: a fresh <see cref="MainViewModel"/> must
/// apply <c>SetHabituationPaused(!HabituationEnabled)</c> from the persisted pref BEFORE the
/// first Update tick, so the engine's pause state matches the user's opt-in at launch.
///
/// <para>The pause-preservation half (selector set-moves never auto-unpause) and the Q4.5
/// offset-safety half are pinned at the engine level by
/// <see cref="HabituationPauseIntegrityTests"/>; here we lock only the App-layer pref→engine
/// hydration wiring.</para>
///
/// <para>CW-9 isolation: <c>UserPreferences.FilePath</c> routes through
/// <c>AppConfig.DataRootOverride</c>; this class points it at its own temp subdir and serialises
/// against the other override-retargeting classes via the <c>TestDataRoot</c> collection, so the
/// single static override never races mid-flight and we never touch the developer's real
/// user-preferences.json.</para></summary>
[Collection("TestDataRoot")]
public class GradedExposureOptInTests : IDisposable
{
    public GradedExposureOptInTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(GradedExposureOptInTests));
        if (File.Exists(UserPreferences.FilePath)) File.Delete(UserPreferences.FilePath);
        UserPreferences.ResetCacheForTesting();
    }

    public void Dispose() => UserPreferences.ResetCacheForTesting();

    [Fact]
    public void PrefPaused_StartsEngineHabituationPaused()
    {
        // Opt-out (default): graded exposure is paused.
        UserPreferences.Current.HabituationEnabled = false;

        var vm = new MainViewModel();

        // The ctor must have hydrated the engine to PAUSED before the first tick.
        Assert.True(vm.HabituationPaused,
            "pref=false (opt-out) must leave the engine habituation paused at startup");
        Assert.False(vm.GradedExposureEnabled);
    }

    [Fact]
    public void PrefEnabled_StartsEngineHabituationRunning()
    {
        // Opt-in: graded exposure runs.
        UserPreferences.Current.HabituationEnabled = true;

        var vm = new MainViewModel();

        // The ctor must have hydrated the engine to RUNNING (un-paused) before the first tick.
        Assert.False(vm.HabituationPaused,
            "pref=true (opt-in) must leave the engine habituation running at startup");
        Assert.True(vm.GradedExposureEnabled);
    }
}
