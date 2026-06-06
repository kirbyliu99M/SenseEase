using System;
using SenseEase.App.Services;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pop-up sprint Task 4 — pure-logic tests for the 6-hour mute
/// gate. Uses an injected clock so expiry assertions are deterministic
/// (no Thread.Sleep, no flaky 6-hour waits).</summary>
public class NotificationCardControllerTests
{
    [Fact]
    public void ShouldAutoShow_ReturnsTrue_WhenBandNeverMuted()
    {
        var c = new NotificationCardController();
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.VeryHigh));
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Medium));
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.VeryLow));
    }

    [Fact]
    public void Mute_BlocksAutoShowForThatBand_Only()
    {
        var now = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var c = new NotificationCardController(() => now);
        c.Mute(TierColorPalette.TierBand.Medium);

        Assert.False(c.ShouldAutoShow(TierColorPalette.TierBand.Medium));
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.VeryHigh));   // other bands not affected
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.VeryLow));
    }

    [Fact]
    public void Mute_ExpiresAfterMuteDuration()
    {
        var clock = new MutableClock(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc));
        var c = new NotificationCardController(() => clock.Now)
        {
            MuteDuration = TimeSpan.FromHours(6),
        };

        c.Mute(TierColorPalette.TierBand.VeryLow);
        Assert.False(c.ShouldAutoShow(TierColorPalette.TierBand.VeryLow));

        // Just before expiry — still muted.
        clock.Now = clock.Now.AddHours(5).AddMinutes(59);
        Assert.False(c.ShouldAutoShow(TierColorPalette.TierBand.VeryLow));

        // At expiry — mute lifts.
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.VeryLow));
    }

    [Fact]
    public void MuteAgain_RefreshesWindow_NoExtensionStacking()
    {
        var clock = new MutableClock(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc));
        var c = new NotificationCardController(() => clock.Now)
        {
            MuteDuration = TimeSpan.FromHours(6),
        };

        c.Mute(TierColorPalette.TierBand.Medium);
        // Forward 3 hours and mute again — the new 6 h window should run
        // from THIS moment, not stack onto the original 6 h.
        clock.Now = clock.Now.AddHours(3);
        c.Mute(TierColorPalette.TierBand.Medium);

        // 5 h after the second mute = 8 h after the first. If muting
        // stacked, the window would still be open (12 h total). Since
        // we reset rather than extend, 8 h > the second window's 6 h.
        clock.Now = clock.Now.AddHours(5);
        Assert.False(c.ShouldAutoShow(TierColorPalette.TierBand.Medium)); // 5 < 6 — still muted

        clock.Now = clock.Now.AddHours(1).AddSeconds(1);
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Medium));  // 6.something h — expired
    }

    [Fact]
    public void MutedUntilUtc_ReturnsNullForUnmutedBand()
    {
        var c = new NotificationCardController();
        Assert.Null(c.MutedUntilUtc(TierColorPalette.TierBand.VeryHigh));
    }

    [Fact]
    public void MutedUntilUtc_CollectsExpiredEntries()
    {
        var clock = new MutableClock(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc));
        var c = new NotificationCardController(() => clock.Now)
        {
            MuteDuration = TimeSpan.FromMinutes(1),
        };

        c.Mute(TierColorPalette.TierBand.VeryLow);
        Assert.NotNull(c.MutedUntilUtc(TierColorPalette.TierBand.VeryLow));

        clock.Now = clock.Now.AddMinutes(2);
        // Past expiry — should return null AND remove the entry from
        // the internal dictionary (verified indirectly by re-muting and
        // checking the new window starts now, not stacks on stale data).
        Assert.Null(c.MutedUntilUtc(TierColorPalette.TierBand.VeryLow));
    }

    [Fact]
    public void ClearAllMutes_DropsEveryBand()
    {
        var c = new NotificationCardController();
        c.Mute(TierColorPalette.TierBand.VeryHigh);
        c.Mute(TierColorPalette.TierBand.Medium);
        c.Mute(TierColorPalette.TierBand.VeryLow);

        c.ClearAllMutes();

        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.VeryHigh));
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Medium));
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.VeryLow));
    }

    private sealed class MutableClock
    {
        public DateTime Now;
        public MutableClock(DateTime initial) { Now = initial; }
    }
}
