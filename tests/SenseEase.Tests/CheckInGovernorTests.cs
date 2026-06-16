using System;
using SenseEase.App.Configuration;
using SenseEase.App.Services;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Phase-1 event-driven feedback: the cadence governor's anti-fatigue rules — an 8-hour
/// cooldown between prompts + a 24-hour post-dismiss suppression. Deterministic via an injected clock.</summary>
public sealed class CheckInGovernorTests
{
    private DateTime _now = new(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
    private CheckInGovernor Make() => new(() => _now);

    [Fact]
    public void FirstPrompt_IsAllowed() => Assert.True(Make().ShouldPrompt());

    [Fact]
    public void WithinCooldown_AfterShown_IsBlocked_ThenAllowed()
    {
        var g = Make();
        Assert.True(g.ShouldPrompt());
        g.RecordShown();
        _now = _now.AddHours(4);
        Assert.False(g.ShouldPrompt());   // < 8 h cooldown
        _now = _now.AddHours(5);          // 9 h since shown
        Assert.True(g.ShouldPrompt());
    }

    [Fact]
    public void Dismiss_SuppressesForTwentyFourHours()
    {
        var g = Make();
        g.RecordShown();
        g.RecordDismissed();
        _now = _now.AddHours(23);
        Assert.False(g.ShouldPrompt());
        _now = _now.AddHours(2);          // 25 h since dismiss
        Assert.True(g.ShouldPrompt());
    }

    [Fact]
    public void Answer_ClearsDismissSuppression_ButCooldownStillApplies()
    {
        var g = Make();
        g.RecordShown();
        g.RecordDismissed();
        Assert.False(g.ShouldPrompt());   // suppressed
        _now = _now.AddHours(1);          // inside both the 8 h cooldown and the 24 h suppression
        g.RecordAnswered();               // clears suppression…
        Assert.False(g.ShouldPrompt());   // …but the 8 h cooldown still blocks
        _now = _now.AddHours(8);          // 9 h since RecordShown
        Assert.True(g.ShouldPrompt());
    }

    [Fact]
    public void Answer_DoesNotBypassCooldown()
    {
        var g = Make();
        g.RecordShown();
        _now = _now.AddMinutes(10);
        g.RecordAnswered();               // clears suppression, but the cooldown must still hold
        Assert.False(g.ShouldPrompt());   // 10 min << 8 h
    }

    [Fact]
    public void CustomCooldown_IsHonoured()
    {
        var g = Make();
        g.Cooldown = TimeSpan.FromHours(1);
        g.RecordShown();
        _now = _now.AddMinutes(30);
        Assert.False(g.ShouldPrompt());
        _now = _now.AddMinutes(40);       // 70 min > 1 h
        Assert.True(g.ShouldPrompt());
    }
}

/// <summary>The proactive-check-in consent tri-state accessor on <see cref="UserPreferences"/>
/// (mirrors the ThemeMode raw+typed tolerant-parse contract).</summary>
public sealed class ProactiveCheckInConsentPrefsTests
{
    [Fact]
    public void Default_IsUnset()
    {
        var p = new UserPreferences();
        Assert.Equal(ProactiveCheckInConsent.Unset, p.ProactiveCheckInsConsent);
        Assert.Equal("Unset", p.ProactiveCheckInsConsentRaw);
    }

    [Fact]
    public void TypedSetter_WritesCanonicalRaw()
    {
        var p = new UserPreferences { ProactiveCheckInsConsent = ProactiveCheckInConsent.Enabled };
        Assert.Equal("Enabled", p.ProactiveCheckInsConsentRaw);
        p.ProactiveCheckInsConsent = ProactiveCheckInConsent.Declined;
        Assert.Equal("Declined", p.ProactiveCheckInsConsentRaw);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("ENABLED_TYPO")]
    public void UnknownRaw_DegradesToUnset(string raw)
        => Assert.Equal(ProactiveCheckInConsent.Unset,
            new UserPreferences { ProactiveCheckInsConsentRaw = raw }.ProactiveCheckInsConsent);

    [Fact]
    public void TypedGetter_IsCaseInsensitive()
        => Assert.Equal(ProactiveCheckInConsent.Enabled,
            new UserPreferences { ProactiveCheckInsConsentRaw = "enabled" }.ProactiveCheckInsConsent);
}
