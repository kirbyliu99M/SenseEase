using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Backlog A7 (per-profile shader defaults) — PURE part. Pins
/// <see cref="HabituationProfileParams.ResolveDefaultsFor"/>: RESILIENT and
/// TYPICAL inherit the neutral <see cref="ShaderParams.Default"/> baseline,
/// while SENSITIVE returns a tightened-but-clamped opening overlay. The engine
/// re-seed wiring (MockInferenceEngine.SetHabituationProfile) is tested
/// separately; this file covers the resolver in isolation.</summary>
public class PerProfileShaderDefaultsTests
{
    // ── RESILIENT == TYPICAL == ShaderParams.Default ─────────────────────────

    [Theory]
    [InlineData(HabituationProfile.Resilient)]
    [InlineData(HabituationProfile.Typical)]
    public void ResolveDefaultsFor_ResilientAndTypical_MatchShaderDefault(HabituationProfile profile)
    {
        var resolved = HabituationProfileParams.ResolveDefaultsFor(profile);
        var baseline = ShaderParams.Default;

        Assert.Equal(baseline.MaskDepth,      resolved.MaskDepth);
        Assert.Equal(baseline.GridVisibility, resolved.GridVisibility);
        Assert.Equal(baseline.FeatherWidth,   resolved.FeatherWidth);
    }

    [Fact]
    public void ResolveDefaultsFor_ResilientEqualsTypical()
    {
        var resilient = HabituationProfileParams.ResolveDefaultsFor(HabituationProfile.Resilient);
        var typical   = HabituationProfileParams.ResolveDefaultsFor(HabituationProfile.Typical);

        Assert.Equal(resilient.MaskDepth,      typical.MaskDepth);
        Assert.Equal(resilient.GridVisibility, typical.GridVisibility);
        Assert.Equal(resilient.FeatherWidth,   typical.FeatherWidth);
    }

    [Fact]
    public void ResolveDefaultsFor_Resilient_ReadsBaselineConstants()
    {
        // Explicit pin to the named ShaderParams constants so a baseline retune
        // surfaces here too (the resolver must follow the single source of truth).
        var resolved = HabituationProfileParams.ResolveDefaultsFor(HabituationProfile.Resilient);
        Assert.Equal(ShaderParams.DefaultMaskDepth,      resolved.MaskDepth);
        Assert.Equal(ShaderParams.DefaultGridVisibility, resolved.GridVisibility);
        Assert.Equal(ShaderParams.DefaultFeatherWidth,   resolved.FeatherWidth);
    }

    // ── SENSITIVE differs from RESILIENT on MaskDepth ────────────────────────

    [Fact]
    public void ResolveDefaultsFor_Sensitive_DiffersFromResilientOnMaskDepth()
    {
        var sensitive = HabituationProfileParams.ResolveDefaultsFor(HabituationProfile.Sensitive);
        var resilient = HabituationProfileParams.ResolveDefaultsFor(HabituationProfile.Resilient);

        Assert.NotEqual(resilient.MaskDepth, sensitive.MaskDepth);
    }

    [Fact]
    public void ResolveDefaultsFor_Sensitive_ReturnsTightenedSpecValues()
    {
        var sensitive = HabituationProfileParams.ResolveDefaultsFor(HabituationProfile.Sensitive);
        Assert.Equal(0.80, sensitive.MaskDepth);
        Assert.Equal(0.60, sensitive.GridVisibility);
        Assert.Equal(0.55, sensitive.FeatherWidth);
    }

    // ── Resolver invariants ──────────────────────────────────────────────────

    [Theory]
    [InlineData(HabituationProfile.Resilient)]
    [InlineData(HabituationProfile.Typical)]
    [InlineData(HabituationProfile.Sensitive)]
    public void ResolveDefaultsFor_AlwaysInRange(HabituationProfile profile)
    {
        var resolved = HabituationProfileParams.ResolveDefaultsFor(profile);
        Assert.InRange(resolved.MaskDepth,      0.0, 1.0);
        Assert.InRange(resolved.GridVisibility, 0.0, 1.0);
        Assert.InRange(resolved.FeatherWidth,   0.0, 1.0);
    }

    [Theory]
    [InlineData(HabituationProfile.Resilient)]
    [InlineData(HabituationProfile.Typical)]
    [InlineData(HabituationProfile.Sensitive)]
    public void ResolveDefaultsFor_ReturnsFreshInstance_CallerOwnsIt(HabituationProfile profile)
    {
        // Pure resolver must hand back a private, mutable instance — never a
        // shared singleton the caller could corrupt for the next consumer.
        var first  = HabituationProfileParams.ResolveDefaultsFor(profile);
        var second = HabituationProfileParams.ResolveDefaultsFor(profile);
        Assert.NotSame(first, second);

        first.MaskDepth = 0.01; // mutate the first copy…
        var third = HabituationProfileParams.ResolveDefaultsFor(profile);
        Assert.NotEqual(first.MaskDepth, third.MaskDepth); // …third is unaffected
    }
}
