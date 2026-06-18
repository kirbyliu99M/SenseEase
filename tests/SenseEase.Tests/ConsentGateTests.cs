using System;
using SenseEase.App.Services;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>R3 #5 — the consent cooldown governor: the cooldown is timed from the ASK (and re-armed
/// on decline) and runs in parallel with an approved session; plus a startup-quiet window.</summary>
public class OverlayConsentGovernorTests
{
    private static OverlayConsentGovernor New(Func<DateTime> clock) =>
        new(clock) { Cooldown = TimeSpan.FromMinutes(10), StartupQuiet = TimeSpan.Zero };

    [Fact]
    public void Ask_StartsCooldownFromTheAsk_ThenElapses()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = New(() => now);

        Assert.True(g.ShouldAsk());            // fresh
        g.RecordAsked();
        Assert.False(g.ShouldAsk());           // cooling down (from the ask)
        now = now.AddMinutes(11);
        Assert.True(g.ShouldAsk());            // elapsed
    }

    [Fact]
    public void Accept_KeepsTheFromAskCooldown()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = New(() => now);
        g.RecordAsked();
        g.RecordAccepted();
        Assert.False(g.ShouldAsk());           // accept does NOT clear it (the session owns re-asking)
        now = now.AddMinutes(11);
        Assert.True(g.ShouldAsk());
    }

    [Fact]
    public void Decline_StartsCooldown()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = New(() => now);
        g.RecordDeclinedOrDismissed();
        Assert.False(g.ShouldAsk());
        now = now.AddMinutes(11);
        Assert.True(g.ShouldAsk());
    }

    [Fact]
    public void StartupQuiet_BlocksTheEarlyPrompt()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = new OverlayConsentGovernor(() => now) { StartupQuiet = TimeSpan.FromSeconds(60) };
        Assert.False(g.ShouldAsk());           // within the startup-quiet window
        now = now.AddSeconds(61);
        Assert.True(g.ShouldAsk());            // past it
    }
}

/// <summary>R3 #5 — the MainViewModel PER-SESSION consent gate. While dormant the overlay is held off
/// entirely (clamped to L1) and the user is asked only once the tier reaches the impactful level (>=6);
/// granting activates the session (all levels run un-held); a user-chosen baseline is implicit consent.</summary>
public class ConsentGateTests
{
    private static MainViewModel NewVm()
    {
        var vm = new MainViewModel();
        vm.SetIntensityLevel(1); // no always-on baseline → PersistentOverlayEnabled = false (dormant)
        vm.OverlayConsentGovernorForTests.StartupQuiet = TimeSpan.Zero; // bypass launch quiet in tests
        return vm;
    }

    [Fact]
    public void Dormant_HoldsEverything_AndAsksOnlyAtTheImpactTier()
    {
        var vm = NewVm();
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        // Below the impact tier (6): still held (dormant overlay shows nothing) but NO prompt.
        Assert.True(vm.ShouldHoldMaskForConsent(5));
        Assert.Equal(0, asks);

        // At the impact tier: held + ask ONCE; repeat ticks don't re-ask while pending.
        Assert.True(vm.ShouldHoldMaskForConsent(6));
        Assert.Equal(1, asks);
        Assert.True(vm.ShouldHoldMaskForConsent(7));
        Assert.Equal(1, asks);
    }

    [Fact]
    public void Grant_ActivatesSession_AllLevelsRunUnheld()
    {
        var vm = NewVm();
        Assert.True(vm.ShouldHoldMaskForConsent(6)); // ask
        vm.GrantOverlayConsent();

        // Session live → nothing is held, and a brief dip to L1 does NOT end it (needs sustained calm).
        Assert.False(vm.ShouldHoldMaskForConsent(8));
        Assert.False(vm.ShouldHoldMaskForConsent(3));
        Assert.False(vm.ShouldHoldMaskForConsent(1));
    }

    [Fact]
    public void AlwaysOnBaseline_IsImplicitConsent_NeverGated()
    {
        var vm = new MainViewModel();
        vm.SetIntensityLevel(6); // user opted into an always-on baseline → PersistentOverlayEnabled = true
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.False(vm.ShouldHoldMaskForConsent(8));
        Assert.Equal(0, asks);
    }

    [Fact]
    public void Decline_StaysDormant_NoReAskWhileCoolingDown()
    {
        var vm = NewVm();
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.True(vm.ShouldHoldMaskForConsent(6)); // ask #1
        vm.DeclineOverlayConsent();                  // cooldown begins (from the ask)
        Assert.True(vm.ShouldHoldMaskForConsent(7)); // still dormant → held
        Assert.Equal(1, asks);                       // but no re-ask while cooling down
    }
}
