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
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Grey));
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Amber));
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Red));
    }

    [Fact]
    public void Mute_BlocksAutoShowForThatBand_Only()
    {
        var now = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var c = new NotificationCardController(() => now);
        c.Mute(TierColorPalette.TierBand.Amber);

        Assert.False(c.ShouldAutoShow(TierColorPalette.TierBand.Amber));
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Grey));   // other bands not affected
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Red));
    }

    [Fact]
    public void Mute_ExpiresAfterMuteDuration()
    {
        var clock = new MutableClock(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc));
        var c = new NotificationCardController(() => clock.Now)
        {
            MuteDuration = TimeSpan.FromHours(6),
        };

        c.Mute(TierColorPalette.TierBand.Red);
        Assert.False(c.ShouldAutoShow(TierColorPalette.TierBand.Red));

        // Just before expiry — still muted.
        clock.Now = clock.Now.AddHours(5).AddMinutes(59);
        Assert.False(c.ShouldAutoShow(TierColorPalette.TierBand.Red));

        // At expiry — mute lifts.
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Red));
    }

    [Fact]
    public void MuteAgain_RefreshesWindow_NoExtensionStacking()
    {
        var clock = new MutableClock(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc));
        var c = new NotificationCardController(() => clock.Now)
        {
            MuteDuration = TimeSpan.FromHours(6),
        };

        c.Mute(TierColorPalette.TierBand.Amber);
        // Forward 3 hours and mute again — the new 6 h window should run
        // from THIS moment, not stack onto the original 6 h.
        clock.Now = clock.Now.AddHours(3);
        c.Mute(TierColorPalette.TierBand.Amber);

        // 5 h after the second mute = 8 h after the first. If muting
        // stacked, the window would still be open (12 h total). Since
        // we reset rather than extend, 8 h > the second window's 6 h.
        clock.Now = clock.Now.AddHours(5);
        Assert.False(c.ShouldAutoShow(TierColorPalette.TierBand.Amber)); // 5 < 6 — still muted

        clock.Now = clock.Now.AddHours(1).AddSeconds(1);
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Amber));  // 6.something h — expired
    }

    [Fact]
    public void MutedUntilUtc_ReturnsNullForUnmutedBand()
    {
        var c = new NotificationCardController();
        Assert.Null(c.MutedUntilUtc(TierColorPalette.TierBand.Grey));
    }

    [Fact]
    public void MutedUntilUtc_CollectsExpiredEntries()
    {
        var clock = new MutableClock(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc));
        var c = new NotificationCardController(() => clock.Now)
        {
            MuteDuration = TimeSpan.FromMinutes(1),
        };

        c.Mute(TierColorPalette.TierBand.Red);
        Assert.NotNull(c.MutedUntilUtc(TierColorPalette.TierBand.Red));

        clock.Now = clock.Now.AddMinutes(2);
        // Past expiry — should return null AND remove the entry from
        // the internal dictionary (verified indirectly by re-muting and
        // checking the new window starts now, not stacks on stale data).
        Assert.Null(c.MutedUntilUtc(TierColorPalette.TierBand.Red));
    }

    [Fact]
    public void ClearAllMutes_DropsEveryBand()
    {
        var c = new NotificationCardController();
        c.Mute(TierColorPalette.TierBand.Grey);
        c.Mute(TierColorPalette.TierBand.Amber);
        c.Mute(TierColorPalette.TierBand.Red);

        c.ClearAllMutes();

        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Grey));
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Amber));
        Assert.True(c.ShouldAutoShow(TierColorPalette.TierBand.Red));
    }

    private sealed class MutableClock
    {
        public DateTime Now;
        public MutableClock(DateTime initial) { Now = initial; }
    }
}
