using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Phase 1 tests for <see cref="HabituationProfileParams"/> — POCO
/// values + <see cref="HabituationProfileParams.GetPostSpikePauseMs"/> tier
/// dispatch. Engine integration tests live in HabituationTests.cs and land
/// later (Phase 3 strike counter + Phase 4 persistence).</summary>
public class HabituationProfileTests
{
    // ── Spec-table fidelity ──────────────────────────────────────────────────
    // Locked numerics. If any of these change, docs/HABITUATION-PROFILE.md §1
    // also needs an update — they must stay in sync because the doc is the
    // reference reviewers cite.

    [Fact]
    public void Resilient_MatchesSpecTable()
    {
        var p = HabituationProfileParams.Resilient;
        Assert.Equal(120_000, p.StableWindowMs);
        Assert.Equal( 30_000, p.StepIntervalMs);
        Assert.Equal(    2.0, p.StepMagnitudePp);
        Assert.Equal( 90_000, p.PostSpikePauseLowMs);
        Assert.Equal(120_000, p.PostSpikePauseMidMs);
        Assert.Equal(     30, p.HabOffsetCapPp);
        Assert.Equal(      3, p.MinSamplesForFullWeight);
        Assert.Equal(SevereClearMode.Halve, p.SevereClearMode);
        Assert.Equal(    2.0, p.SevereStrikeLimit);
    }

    [Fact]
    public void Typical_MatchesSpecTable()
    {
        var p = HabituationProfileParams.Typical;
        Assert.Equal(180_000, p.StableWindowMs);
        Assert.Equal( 45_000, p.StepIntervalMs);
        Assert.Equal(    1.5, p.StepMagnitudePp);
        Assert.Equal( 90_000, p.PostSpikePauseLowMs);
        Assert.Equal(180_000, p.PostSpikePauseMidMs);
        Assert.Equal(     25, p.HabOffsetCapPp);
        Assert.Equal(      4, p.MinSamplesForFullWeight);
        Assert.Equal(SevereClearMode.Halve, p.SevereClearMode);
        Assert.Equal(    2.0, p.SevereStrikeLimit);
    }

    [Fact]
    public void Sensitive_MatchesSpecTable()
    {
        var p = HabituationProfileParams.Sensitive;
        Assert.Equal(360_000, p.StableWindowMs);
        Assert.Equal( 60_000, p.StepIntervalMs);
        Assert.Equal(    1.0, p.StepMagnitudePp);
        Assert.Equal(180_000, p.PostSpikePauseLowMs);
        Assert.Equal(300_000, p.PostSpikePauseMidMs);
        Assert.Equal(     15, p.HabOffsetCapPp);
        Assert.Equal(      6, p.MinSamplesForFullWeight);
        Assert.Equal(SevereClearMode.Halve, p.SevereClearMode);
        // SENSITIVE strike limit is 1 — first severe event after a profile
        // reset zeros the offset, instead of halving twice.
        Assert.Equal(    1.0, p.SevereStrikeLimit);
    }

    // ── For() dispatch ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(HabituationProfile.Resilient)]
    [InlineData(HabituationProfile.Typical)]
    [InlineData(HabituationProfile.Sensitive)]
    public void For_RoundTripsAllEnumValues(HabituationProfile profile)
    {
        HabituationProfileParams resolved = HabituationProfileParams.For(profile);
        Assert.NotNull(resolved);
        // Sanity: HabOffsetCapPp differs between profiles (30 / 25 / 15) so it
        // works as a fingerprint to ensure For() returns the right instance.
        int expectedCap = profile switch
        {
            HabituationProfile.Resilient => 30,
            HabituationProfile.Typical   => 25,
            HabituationProfile.Sensitive => 15,
            _ => -1,
        };
        Assert.Equal(expectedCap, resolved.HabOffsetCapPp);
    }

    // ── GetPostSpikePauseMs tier dispatch ────────────────────────────────────

    [Theory]
    // tier ≤ 5 → low pause, no user-confirm required
    [InlineData(1, false,  90_000)]
    [InlineData(3, false,  90_000)]
    [InlineData(5, false,  90_000)]
    // tier 6 / 7 → mid pause, still no confirm
    [InlineData(6, false, 120_000)]
    [InlineData(7, false, 120_000)]
    // tier ≥ 8 → -1, requires user confirm
    [InlineData(8, true,       -1)]
    [InlineData(9, true,       -1)]
    [InlineData(10, true,      -1)]
    public void GetPostSpikePauseMs_Resilient(int vimsTier, bool expectConfirm, int expectMs)
    {
        int ms = HabituationProfileParams.Resilient.GetPostSpikePauseMs(vimsTier, out bool confirm);
        Assert.Equal(expectConfirm, confirm);
        Assert.Equal(expectMs, ms);
    }

    [Theory]
    [InlineData(1, false,  90_000)]
    [InlineData(5, false,  90_000)]
    [InlineData(6, false, 180_000)]
    [InlineData(7, false, 180_000)]
    [InlineData(8, true,       -1)]
    [InlineData(9, true,       -1)]
    public void GetPostSpikePauseMs_Typical(int vimsTier, bool expectConfirm, int expectMs)
    {
        int ms = HabituationProfileParams.Typical.GetPostSpikePauseMs(vimsTier, out bool confirm);
        Assert.Equal(expectConfirm, confirm);
        Assert.Equal(expectMs, ms);
    }

    [Theory]
    // SENSITIVE doubles the low-tier pause to 180_000 ms.
    [InlineData(1, false, 180_000)]
    [InlineData(3, false, 180_000)]
    [InlineData(5, false, 180_000)]
    // Mid-tier pause is 300_000 ms (5 min) — explicit spec value, verifies
    // the most punitive auto-resume path for the most cautious profile.
    [InlineData(6, false, 300_000)]
    [InlineData(7, false, 300_000)]
    // Tier 8+ same as other profiles — require user confirm.
    [InlineData(8, true,       -1)]
    [InlineData(9, true,       -1)]
    public void GetPostSpikePauseMs_Sensitive(int vimsTier, bool expectConfirm, int expectMs)
    {
        int ms = HabituationProfileParams.Sensitive.GetPostSpikePauseMs(vimsTier, out bool confirm);
        Assert.Equal(expectConfirm, confirm);
        Assert.Equal(expectMs, ms);
    }

    // ── Boundary case: tier 8 specifically (the spec calls this out) ────────

    [Theory]
    [InlineData(HabituationProfile.Resilient)]
    [InlineData(HabituationProfile.Typical)]
    [InlineData(HabituationProfile.Sensitive)]
    public void GetPostSpikePauseMs_Tier8_AlwaysRequiresUserConfirm(HabituationProfile profile)
    {
        int ms = HabituationProfileParams.For(profile).GetPostSpikePauseMs(8, out bool confirm);
        Assert.True(confirm);
        Assert.Equal(-1, ms);
    }

    // ── Unit conversion sanity ───────────────────────────────────────────────

    [Fact]
    public void SecondsHelpers_RoundTripMs()
    {
        var p = HabituationProfileParams.Resilient;
        Assert.Equal(120.0, p.StableWindowSeconds);
        Assert.Equal( 30.0, p.StepIntervalSeconds);
        Assert.Equal( 90.0, p.PostSpikePauseLowSeconds);
        Assert.Equal(120.0, p.PostSpikePauseMidSeconds);
    }
}
