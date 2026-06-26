using System;
using System.IO;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>First-run privacy gate (Kirby 2026-06-26): on a fresh install the app must NOT start WGC capture
/// (the Windows yellow capture border) until the user dismisses the one-time welcome card. The VM defers the
/// ctor's <c>_sensing.Start()</c> while <see cref="AppConfig.IsFirstRun"/>, and
/// <see cref="MainViewModel.StartSensingAfterWelcome"/> (wired to the welcome's Dismissed event in
/// ShellController) releases it. Isolated in the TestDataRoot collection (per-class temp DataRoot) + the
/// first-run flag reset each test so the IsFirstRun read is deterministic.</summary>
[Collection("TestDataRoot")]
public class WelcomeSensingGateTests : IDisposable
{
    public WelcomeSensingGateTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(WelcomeSensingGateTests));
        ResetFirstRunFlag(); // force a clean first-run state for a deterministic IsFirstRun read
    }

    public void Dispose() => ResetFirstRunFlag();

    private static void ResetFirstRunFlag()
    {
        try { if (File.Exists(AppConfig.FirstRunFlagPath)) File.Delete(AppConfig.FirstRunFlagPath); }
        catch { /* best-effort test isolation */ }
    }

    [Fact]
    public void FirstRun_DefersSensing_UntilWelcomeDismissed()
    {
        Assert.True(AppConfig.IsFirstRun);            // fresh install
        var vm = new MainViewModel();
        Assert.True(vm.SensingDeferredForTests);      // WGC capture held → no yellow border at boot

        vm.StartSensingAfterWelcome();                // welcome dismissed → sensing starts
        Assert.False(vm.SensingDeferredForTests);

        vm.StartSensingAfterWelcome();                // idempotent: a second dismissal call is a no-op
        Assert.False(vm.SensingDeferredForTests);
    }

    [Fact]
    public void NonFirstRun_StartsSensingAtBoot_NoDefer()
    {
        AppConfig.MarkFirstRunComplete();             // simulate a prior run
        Assert.False(AppConfig.IsFirstRun);
        var vm = new MainViewModel();
        Assert.False(vm.SensingDeferredForTests);     // not deferred → sensing starts at the ctor, as before
    }
}
