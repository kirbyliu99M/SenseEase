using System;
using SenseEase.App.Services;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Item 1 — the consent cooldown governor: a fresh ask is allowed until a decline starts
/// the quiet window; an accept clears it.</summary>
public class OverlayConsentGovernorTests
{
    [Fact]
    public void Decline_StartsCooldown_ThenClears()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = new OverlayConsentGovernor(() => now) { Cooldown = TimeSpan.FromMinutes(10) };

        Assert.True(g.ShouldAsk());            // fresh
        g.RecordDeclinedOrDismissed();
        Assert.False(g.ShouldAsk());           // within cooldown
        now = now.AddMinutes(11);
        Assert.True(g.ShouldAsk());            // cooldown elapsed
    }

    [Fact]
    public void Accept_ClearsCooldown()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = new OverlayConsentGovernor(() => now) { Cooldown = TimeSpan.FromMinutes(10) };
        g.RecordDeclinedOrDismissed();
        Assert.False(g.ShouldAsk());
        g.RecordAccepted();
        Assert.True(g.ShouldAsk());            // accept overrides the cooldown
    }
}

/// <summary>Item 1 — the MainViewModel consent gate. The visible mask is HELD (return true) on the
/// engine's auto-engage until the user agrees; a user-chosen always-on baseline is never gated; the
/// gate re-arms when the mask subsides so each fresh engagement re-asks.</summary>
public class ConsentGateTests
{
    [Fact]
    public void AutoEngage_Holds_AndAsks_UntilGranted_ThenReArms()
    {
        var vm = new MainViewModel();
        vm.SetIntensityLevel(1); // no always-on baseline → PersistentOverlayEnabled = false
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        // Tier rises into the visible range (>=2) from a clear screen → hold + ask exactly once.
        Assert.True(vm.ShouldHoldMaskForConsent(naturalTier: 5));
        Assert.Equal(1, asks);
        Assert.True(vm.ShouldHoldMaskForConsent(5)); // still pending → held, no new ask
        Assert.Equal(1, asks);

        // User agrees → released.
        vm.GrantOverlayConsent();
        Assert.False(vm.ShouldHoldMaskForConsent(5));

        // Tier falls back to L1 → re-arm; a NEW engagement asks again (accept cleared the cooldown).
        Assert.False(vm.ShouldHoldMaskForConsent(1));
        Assert.True(vm.ShouldHoldMaskForConsent(6));
        Assert.Equal(2, asks);
    }

    [Fact]
    public void AlwaysOnBaseline_IsNeverGated()
    {
        var vm = new MainViewModel();
        vm.SetIntensityLevel(6); // user opted into an always-on baseline → PersistentOverlayEnabled = true
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.False(vm.ShouldHoldMaskForConsent(naturalTier: 8));
        Assert.Equal(0, asks);
    }

    [Fact]
    public void Decline_KeepsHolding_WithoutReAsking()
    {
        var vm = new MainViewModel();
        vm.SetIntensityLevel(1);
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.True(vm.ShouldHoldMaskForConsent(5)); // ask #1
        vm.DeclineOverlayConsent();                  // cooldown begins
        Assert.True(vm.ShouldHoldMaskForConsent(5)); // still engaged → still held
        Assert.Equal(1, asks);                       // but no re-ask while pending/cooling down
    }
}
