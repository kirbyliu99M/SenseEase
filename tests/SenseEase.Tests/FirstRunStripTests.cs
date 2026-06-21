using System;
using System.IO;
using System.Windows;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>UX-13: the one-time first-run welcome strip is keyed off the EXISTING
/// <see cref="AppConfig.IsFirstRun"/> flag (previously dead). These pin the contract:
/// it SHOWS on a fresh install (flag file absent), DISMISS persists
/// <see cref="AppConfig.MarkFirstRunComplete"/> and hides it, and it NEVER reappears
/// on a later launch once the flag is written. (Vestibular-removal: the prompt-sequencing
/// rule — the strip held back the now-removed vestibular prompt bar — was trimmed.)
///
/// <para>CW-9 isolation: <see cref="AppConfig.FirstRunFlagPath"/> and
/// <c>UserPreferences.FilePath</c> both route through <c>AppConfig.DataRootOverride</c>;
/// this class points it at its own temp subdir (serialised against the other
/// override-retargeting classes via the <c>TestDataRoot</c> collection) so a fresh subdir
/// has no flag file ⇒ <c>IsFirstRun==true</c>, and we never touch the developer's real
/// data.</para></summary>
[Collection("TestDataRoot")]
public class FirstRunStripTests : IDisposable
{
    public FirstRunStripTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(FirstRunStripTests));
        // Fresh prefs so nothing leaks from a sibling class.
        if (File.Exists(UserPreferences.FilePath)) File.Delete(UserPreferences.FilePath);
        UserPreferences.ResetCacheForTesting();
    }

    public void Dispose() => UserPreferences.ResetCacheForTesting();

    [Fact]
    public void FreshInstall_FirstRunStrip_Shows()
    {
        // No flag file in the fresh temp subdir ⇒ first run.
        Assert.True(AppConfig.IsFirstRun, "fresh temp data root must read as first run");

        var vm = new MainViewModel();

        Assert.True(vm.IsFirstRunStripVisible);
        Assert.Equal(Visibility.Visible, vm.FirstRunStripVisibility);
    }

    [Fact]
    public void FirstRunStrip_VisibleUntilDismissed()
    {
        // Vestibular-removal: the prompt-sequencing half (the strip held back the now-removed
        // vestibular bar) was trimmed; the surviving contract is the strip's own show→dismiss.
        var vm = new MainViewModel();

        Assert.Equal(Visibility.Visible, vm.FirstRunStripVisibility);

        vm.DismissFirstRunCommand.Execute(null);

        Assert.Equal(Visibility.Collapsed, vm.FirstRunStripVisibility);
    }

    [Fact]
    public void Dismiss_PersistsFlag_AndHidesStrip()
    {
        var vm = new MainViewModel();
        Assert.True(vm.IsFirstRunStripVisible);

        vm.DismissFirstRunCommand.Execute(null);

        Assert.False(vm.IsFirstRunStripVisible);
        Assert.Equal(Visibility.Collapsed, vm.FirstRunStripVisibility);
        // MarkFirstRunComplete wrote the flag file.
        Assert.False(AppConfig.IsFirstRun, "dismiss must persist the first-run-complete flag");
    }

    [Fact]
    public void Dismiss_IsIdempotent()
    {
        var vm = new MainViewModel();
        vm.DismissFirstRunCommand.Execute(null);
        // Second dismiss must be a harmless no-op (strip already gone).
        vm.DismissFirstRunCommand.Execute(null);
        Assert.False(vm.IsFirstRunStripVisible);
    }

    [Fact]
    public void AfterMarkFirstRunComplete_NeverShowsAgain_OnNextLaunch()
    {
        // Simulate a prior session having completed first-run.
        AppConfig.MarkFirstRunComplete();
        Assert.False(AppConfig.IsFirstRun);

        // A fresh VM (next launch) must never surface the strip.
        var vm = new MainViewModel();
        Assert.False(vm.IsFirstRunStripVisible);
        Assert.Equal(Visibility.Collapsed, vm.FirstRunStripVisibility);
    }
}
