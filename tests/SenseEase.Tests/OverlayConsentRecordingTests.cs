using System;
using System.IO;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>W-E/W-G/W-I (2026-06-20) — the overlay-consent RECORDING contract. An explicit "Not now" is
/// persisted (<see cref="UserPreferences.LastOverlayDeclineUtc"/>) so the in-memory
/// <c>OverlayConsentGovernor</c> re-seeds its 30-min decline cooldown on the next launch instead of
/// re-nagging on the first impactful rise; "Clear all my data" wipes that behavioural record. The full
/// asked/accepted/declined/ignored TIMELINE goes to symptom_log.jsonl (covered structurally by
/// <c>SymptomEventRecorderTests</c>); these tests pin the durable cooldown seed + its reset.
///
/// <para>Isolation mirrors <see cref="PersistentOverlayBaselinePersistenceTests"/> — a per-class temp
/// DataRoot via the <c>TestDataRoot</c> collection, so the developer's real user-preferences.json is
/// never touched and the single static override never races.</para></summary>
[Collection("TestDataRoot")]
public class OverlayConsentRecordingTests : IDisposable
{
    public OverlayConsentRecordingTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(OverlayConsentRecordingTests));
        if (File.Exists(UserPreferences.FilePath)) File.Delete(UserPreferences.FilePath);
        UserPreferences.ResetCacheForTesting();
    }

    public void Dispose() => UserPreferences.ResetCacheForTesting();

    private static MainViewModel NewDormantVm()
    {
        var vm = new MainViewModel();
        vm.SetIntensityLevel(1); // no always-on baseline → dormant (consent-gated)
        vm.OverlayConsentGovernorForTests.StartupQuiet = TimeSpan.Zero; // bypass the launch-quiet window
        return vm;
    }

    [Fact]
    public void Decline_PersistsTheDeclineTimestamp_AndIncrementsCount()
    {
        var vm = NewDormantVm();
        Assert.True(vm.ShouldHoldMaskForConsent(6)); // ask
        vm.DeclineOverlayConsent();

        UserPreferences.ResetCacheForTesting();      // reload from disk (simulate a fresh read)
        var reloaded = UserPreferences.Current;
        Assert.NotNull(reloaded.LastOverlayDeclineUtc);
        Assert.Equal(1, reloaded.OverlayDeclineCount);
    }

    [Fact]
    public void RecentPersistedDecline_SurvivesRestart_NoReAsk()
    {
        // A prior session declined moments ago.
        UserPreferences.Current.LastOverlayDeclineUtc = DateTime.UtcNow;
        UserPreferences.Current.OverlayDeclineCount = 1;
        UserPreferences.Current.Save();
        UserPreferences.ResetCacheForTesting();

        // Restart: the fresh VM must re-seed the 30-min cooldown from the persisted decline.
        var vm = NewDormantVm();
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.True(vm.ShouldHoldMaskForConsent(6)); // still held (dormant)…
        Assert.Equal(0, asks);                       // …but the seeded decline cooldown blocks a fresh nag
    }

    [Fact]
    public void StalePersistedDecline_DoesNotSuppress_ReAsksAfterRestart()
    {
        // A decline older than the 30-min window must NOT suppress forever.
        UserPreferences.Current.LastOverlayDeclineUtc = DateTime.UtcNow.AddMinutes(-31);
        UserPreferences.Current.Save();
        UserPreferences.ResetCacheForTesting();

        var vm = NewDormantVm();
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.True(vm.ShouldHoldMaskForConsent(6));
        Assert.Equal(1, asks);                       // window elapsed → re-asks on the next impactful rise
    }

    [Fact]
    public void ClearAllUserData_ResetsTheDeclineRecord()
    {
        UserPreferences.Current.LastOverlayDeclineUtc = DateTime.UtcNow;
        UserPreferences.Current.OverlayDeclineCount = 3;
        UserPreferences.Current.Save();

        SenseEase.App.Views.SettingsContent.ClearAllUserData();

        UserPreferences.ResetCacheForTesting();
        var reloaded = UserPreferences.Current;
        Assert.Null(reloaded.LastOverlayDeclineUtc);
        Assert.Equal(0, reloaded.OverlayDeclineCount);
    }
}
