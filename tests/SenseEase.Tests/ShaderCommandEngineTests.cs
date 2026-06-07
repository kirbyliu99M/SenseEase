using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>G1 (Alpha-2) — engine-side wiring for the absolute shader-set API
/// (<see cref="MockInferenceEngine.SetShaderParam"/> /
/// <see cref="MockInferenceEngine.ResetShaderToProfileDefault"/>). Pins the absolute
/// set, the engine-side defensive clamp, and the LOCKED Q4.5 invariant that a shader
/// reset never touches the earned habituation offset.</summary>
public class ShaderCommandEngineTests
{
    /// <summary>Minimal in-memory habituation store so a non-zero earned offset can be
    /// seeded into a fresh engine (mirrors HabituationTests' private helper).</summary>
    private sealed class InMemoryHabituationStore : IHabituationStore
    {
        private HabituationState? _stored;
        public HabituationState? Load() => _stored;
        public void Save(HabituationState state) => _stored = state;
    }

    // ── SetShaderParam: absolute set ──────────────────────────────────────────

    [Fact]
    public void SetShaderParam_GridVisibilityZero_TurnsGridOff()
    {
        var engine = new MockInferenceEngine();
        engine.SetShaderParam(ShaderParam.GridVisibility, 0.0);
        Assert.Equal(0.0, engine.Current.Shader.GridVisibility, 6);
    }

    [Fact]
    public void SetShaderParam_GridVisibility_AbsoluteSet_NotDelta()
    {
        var engine = new MockInferenceEngine();
        // Default grid is 0.75; an absolute set to 0.10 must LAND at 0.10 in one call
        // (the old delta path was capped at ±0.3 and would only reach 0.45).
        engine.SetShaderParam(ShaderParam.GridVisibility, 0.10);
        Assert.Equal(0.10, engine.Current.Shader.GridVisibility, 6);
    }

    [Fact]
    public void SetShaderParam_MaskDepth_SetsValue()
    {
        var engine = new MockInferenceEngine();
        engine.SetShaderParam(ShaderParam.MaskDepth, 0.20);
        Assert.Equal(0.20, engine.Current.Shader.MaskDepth, 6);
    }

    [Fact]
    public void SetShaderParam_FeatherWidth_SetsValue()
    {
        var engine = new MockInferenceEngine();
        engine.SetShaderParam(ShaderParam.FeatherWidth, 0.35);
        Assert.Equal(0.35, engine.Current.Shader.FeatherWidth, 6);
    }

    // ── Engine-side defensive clamp ───────────────────────────────────────────
    // The 0.20 MASK FLOOR is enforced in the LEXICON (ShaderCommandLexiconTests pins
    // it). The ENGINE does a defensive [0,1] clamp only — documented in the
    // SetShaderParam contract. These pin the engine clamp, not the lexicon floor.

    [Theory]
    [InlineData(ShaderParam.MaskDepth)]
    [InlineData(ShaderParam.GridVisibility)]
    [InlineData(ShaderParam.FeatherWidth)]
    public void SetShaderParam_Above1_ClampedTo1(ShaderParam param)
    {
        var engine = new MockInferenceEngine();
        engine.SetShaderParam(param, 5.0);
        double v = param switch
        {
            ShaderParam.MaskDepth      => engine.Current.Shader.MaskDepth,
            ShaderParam.GridVisibility => engine.Current.Shader.GridVisibility,
            _                          => engine.Current.Shader.FeatherWidth,
        };
        Assert.Equal(1.0, v, 6);
    }

    [Fact]
    public void SetShaderParam_Negative_ClampedToZero()
    {
        var engine = new MockInferenceEngine();
        engine.SetShaderParam(ShaderParam.MaskDepth, -3.0);
        Assert.Equal(0.0, engine.Current.Shader.MaskDepth, 6);
    }

    // ── FOV best-effort absolute set ──────────────────────────────────────────

    [Fact]
    public void SetShaderParam_Fov_TargetsRequestedRadius()
    {
        var engine = new MockInferenceEngine();
        // At rest (pressure 0) the natural radius is 100; requesting 60 must compose
        // down toward 60 via the user FOV offset.
        engine.SetShaderParam(ShaderParam.FovRadius, 60);
        Assert.Equal(60, engine.Current.FovRadiusPercent);
    }

    [Fact]
    public void SetShaderParam_Fov_ClampsToMinComposedFloor()
    {
        var engine = new MockInferenceEngine();
        // Even a request below the floor cannot drive the composed radius under 15.
        engine.SetShaderParam(ShaderParam.FovRadius, 5);
        Assert.True(engine.Current.FovRadiusPercent >= 15);
    }

    // ── ResetShaderToProfileDefault ───────────────────────────────────────────

    [Fact]
    public void ResetShaderToProfileDefault_RestoresProfileDefault()
    {
        var engine = new MockInferenceEngine();
        engine.SetShaderParam(ShaderParam.GridVisibility, 0.0); // user turned grid off
        Assert.Equal(0.0, engine.Current.Shader.GridVisibility, 6);

        engine.ResetShaderToProfileDefault();

        // Default profile (RESILIENT) inherits ShaderParams.Default grid visibility.
        Assert.Equal(ShaderParams.DefaultGridVisibility, engine.Current.Shader.GridVisibility, 6);
    }

    [Fact]
    public void ResetShaderToProfileDefault_UsesCurrentProfileDefaults()
    {
        var engine = new MockInferenceEngine();
        engine.SetHabituationProfile(HabituationProfile.Sensitive, "test");
        engine.SetShaderParam(ShaderParam.MaskDepth, 0.30); // user tweak
        Assert.Equal(0.30, engine.Current.Shader.MaskDepth, 6);

        engine.ResetShaderToProfileDefault();

        // SENSITIVE's tightened default MaskDepth is 0.80 (A7 / ResolveDefaultsFor).
        var sensitiveDefault = HabituationProfileParams.ResolveDefaultsFor(HabituationProfile.Sensitive);
        Assert.Equal(sensitiveDefault.MaskDepth, engine.Current.Shader.MaskDepth, 6);
    }

    // ── LOCKED Q4.5: shader reset must NOT touch the earned habituation offset ─

    [Fact]
    public void ResetShaderToProfileDefault_PreservesHabituationOffset_Q45()
    {
        // Seed a non-zero earned offset into a fresh engine via its store, tweak the
        // shader, then run the G1 shader-only reset. The offset MUST survive (Q4.5):
        // ResetShaderToProfileDefault clears transient shader state ONLY.
        var store = new InMemoryHabituationStore();
        store.Save(new HabituationState { Offset = 18 });
        var engine = new MockInferenceEngine(habituationStore: store);
        Assert.Equal(18, engine.HabituationOffsetPp); // precondition: offset seeded

        engine.SetShaderParam(ShaderParam.MaskDepth, 0.30);
        engine.ResetShaderToProfileDefault();

        Assert.Equal(18, engine.HabituationOffsetPp);        // in-memory offset intact
        Assert.Equal(18.0, store.Load()!.Offset, 3);          // persisted offset intact
    }
}
