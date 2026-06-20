using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// B6 Stage 4a — the mechanical rename + reorder of <see cref="HabituationProfile"/>
/// (Sensitive→Reactive, Typical→Adapting; Resilient kept) and the persisted-string
/// back-compat alias. These pin: the new int order, byte-identical params per renamed
/// member (ZERO numeric drift), the legacy-string → renamed-member migration on boot,
/// and the new-canonical-string write round-trip. The IsLessRestrictive order-INVERSION
/// guard lives in HabituationOverrideProcessorTests; the protection-floor-guard
/// member read (Reactive blocks weaker band moves) is covered by AdaptiveFitWiringTests.
/// </summary>
public sealed class HabituationProfileRenameMigrationTests
{
    private sealed class InMemoryProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Stored;
        public HabituationProfileSnapshot? Load() => Stored;
        public void Save(HabituationProfileSnapshot snapshot) => Stored = snapshot;
    }

    // ── The reorder: int order ASCENDS with elasticity (low → high) ──────────
    [Fact]
    public void EnumIntOrder_AscendsWithElasticity_LowToHigh()
    {
        Assert.Equal(0, (int)HabituationProfile.Reactive);
        Assert.Equal(1, (int)HabituationProfile.Adapting);
        Assert.Equal(2, (int)HabituationProfile.Resilient);
    }

    // ── ZERO numeric drift: params byte-identical per renamed member ─────────
    [Theory]
    [InlineData(HabituationProfile.Reactive,  360_000, 60_000, 1.0, 15, 6, 1.0)] // = old SENSITIVE
    [InlineData(HabituationProfile.Adapting,  180_000, 45_000, 1.5, 25, 4, 2.0)] // = old TYPICAL
    [InlineData(HabituationProfile.Resilient, 120_000, 30_000, 2.0, 30, 3, 2.0)] // unchanged
    public void For_RenamedMember_ReturnsByteIdenticalParams(
        HabituationProfile profile, int window, int interval, double step,
        int cap, int minSamples, double strikeLimit)
    {
        var p = HabituationProfileParams.For(profile);
        Assert.Equal(window,      p.StableWindowMs);
        Assert.Equal(interval,    p.StepIntervalMs);
        Assert.Equal(step,        p.StepMagnitudePp);
        Assert.Equal(cap,         p.HabOffsetCapPp);
        Assert.Equal(minSamples,  p.MinSamplesForFullWeight);
        Assert.Equal(strikeLimit, p.SevereStrikeLimit);
    }

    // ── Alias: legacy AND new canonical strings → the right member ───────────
    [Theory]
    [InlineData("SENSITIVE",  HabituationProfile.Reactive)]
    [InlineData("sensitive",  HabituationProfile.Reactive)]
    [InlineData("TYPICAL",    HabituationProfile.Adapting)]
    [InlineData("RESILIENT",  HabituationProfile.Resilient)]
    [InlineData("REACTIVE",   HabituationProfile.Reactive)]
    [InlineData("ADAPTING",   HabituationProfile.Adapting)]
    [InlineData("  adapting ", HabituationProfile.Adapting)]
    public void Alias_ParsesLegacyAndNewStrings(string raw, HabituationProfile expected)
    {
        Assert.True(HabituationProfileAlias.TryParse(raw, out var profile));
        Assert.Equal(expected, profile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    public void Alias_RejectsBlankOrUnknown(string? raw)
        => Assert.False(HabituationProfileAlias.TryParse(raw, out _));

    // ── Boot-restore migration: a legacy persisted string still loads ────────
    [Theory]
    [InlineData("SENSITIVE",  HabituationProfile.Reactive,  15, 1.0)]
    [InlineData("TYPICAL",    HabituationProfile.Adapting,  25, 2.0)]
    [InlineData("RESILIENT",  HabituationProfile.Resilient, 30, 2.0)]
    public void BootRestore_LegacySnapshotString_LoadsToRenamedMemberWithSameParams(
        string legacy, HabituationProfile expected, int expectedCap, double expectedStrikeLimit)
    {
        var store = new InMemoryProfileStore
        {
            Stored = new HabituationProfileSnapshot { Profile = legacy, ProfileSource = "calibration_demo" },
        };

        var engine = new MockInferenceEngine(profileStore: store);

        Assert.Equal(expected, engine.HabituationProfile);
        var p = HabituationProfileParams.For(engine.HabituationProfile);
        Assert.Equal(expectedCap, p.HabOffsetCapPp);
        Assert.Equal(expectedStrikeLimit, p.SevereStrikeLimit);
    }

    // ── Write side emits the NEW canonical string; reload returns the member ─
    [Theory]
    [InlineData(HabituationProfile.Reactive,  "REACTIVE")]
    [InlineData(HabituationProfile.Adapting,  "ADAPTING")]
    [InlineData(HabituationProfile.Resilient, "RESILIENT")]
    public void WriteThenReload_RoundTripsNewCanonicalString(HabituationProfile profile, string expectedString)
    {
        var store = new InMemoryProfileStore();
        var engine = new MockInferenceEngine(profileStore: store);

        engine.SetHabituationProfile(profile, "calibration_demo");

        Assert.NotNull(store.Stored);
        Assert.Equal(expectedString, store.Stored!.Profile);

        var reloaded = new MockInferenceEngine(profileStore: store);
        Assert.Equal(profile, reloaded.HabituationProfile);
    }

    // ── Fresh-user seed parity: seeds Adapting (= old TYPICAL params, no drift) ─
    [Fact]
    public void FreshUserSeed_LandsOnAdapting_WithTypicalParams()
    {
        var engine = new MockInferenceEngine();          // no snapshot ⇒ fresh user
        engine.ApplyAutoProfilingStartDefaults();

        Assert.Equal(HabituationProfile.Adapting, engine.HabituationProfile);
        var p = HabituationProfileParams.For(engine.HabituationProfile);
        Assert.Equal(25, p.HabOffsetCapPp);              // old TYPICAL cap, unchanged
        Assert.Equal(2.0, p.SevereStrikeLimit);
    }
}
