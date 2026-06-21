using System;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Item 4 — the 常駐遮罩 / Persistent mask hard hold. <see cref="MockInferenceEngine.SetAutoLevelHold"/> pins
/// the displayed tier at the requested level (instant effect) and FREEZES the pressure→tier projection while
/// held (hard-latch, Kirby), then resumes from live pressure on release. Only <c>UpdateVimsTier</c> is
/// frozen — the habituation offset/strike loops + the B5/B6 auto-adaptation are NOT gated by the hold.
/// (UVHS fix 2026-06-21: this hold drives the Persistent-mask switch, NOT Auto Protect — see
/// <see cref="PersistentMaskHoldWiringTests"/> for the VM-level toggle wiring.)
/// </summary>
public sealed class OverlayAlwaysOnHoldTests
{
    private static readonly DateTime Clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static int Drive(MockInferenceEngine e, double ofi, int ticks, int startSec)
    {
        int tier = 0;
        for (int i = 0; i < ticks; i++)
            tier = e.Update(ofi, "Passenger", Clock.AddSeconds(startSec + i * 2)).VimsTier;
        return tier;
    }

    [Fact]
    public void SetAutoLevelHold_PinsTierAtSix_Immediately()
    {
        var e = new MockInferenceEngine();
        e.SetAutoLevelHold(true, 6);
        Assert.Equal(6, e.Current.VimsTier);
    }

    [Fact]
    public void Held_FreezesTier_AgainstBothHighAndLowOfi()
    {
        var e = new MockInferenceEngine();
        e.SetAutoLevelHold(true, 6);

        // A strong, sustained OFI would normally drive the tier well ABOVE 6 — frozen at 6.
        int afterHigh = Drive(e, ofi: 120.0, ticks: 30, startSec: 0);
        Assert.Equal(6, afterHigh);

        // Calm (OFI 0) would normally release the tier toward 1 — still frozen at 6.
        int afterCalm = Drive(e, ofi: 0.0, ticks: 60, startSec: 100);
        Assert.Equal(6, afterCalm);
    }

    [Fact]
    public void Release_ResumesProjection_TierFallsBelowSixWhenCalm()
    {
        var e = new MockInferenceEngine();
        e.SetAutoLevelHold(true, 6);
        Drive(e, ofi: 0.0, ticks: 10, startSec: 0);
        Assert.Equal(6, e.Current.VimsTier);          // frozen while held

        e.SetAutoLevelHold(false);                    // release → projection resumes
        int afterRelease = Drive(e, ofi: 0.0, ticks: 60, startSec: 100);
        Assert.True(afterRelease < 6, $"expected the tier to release below 6 once unfrozen, got {afterRelease}");
    }

    [Fact]
    public void Hold_RespectsLevelArgument_AndClampsToRange()
    {
        var e = new MockInferenceEngine();
        e.SetAutoLevelHold(true, 9);
        Assert.Equal(9, e.Current.VimsTier);
        e.SetAutoLevelHold(true, 99);                 // out-of-range clamps to the 1..12 tier scale
        Assert.Equal(12, e.Current.VimsTier);
    }
}
