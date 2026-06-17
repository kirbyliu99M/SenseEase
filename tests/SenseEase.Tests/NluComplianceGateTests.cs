using SenseEase.Infrastructure.Nlp;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Direct unit coverage of the pure <see cref="NluComplianceGate"/> — the single deterministic
/// floor both the LLM result and the lexicon fallback pass through. One rule per test (mirrors
/// AdaptationGateTests). Grown across Stages A→D; the convergence test (Model vs Lexicon source
/// produce the same compliant result) lands in Stage D.
/// </summary>
public class NluComplianceGateTests
{
    private static NluComplianceGate.RawNluDecision Raw(
        string intent, int severity = 3, string? reply = "ok",
        int? fov = null, double? md = null, double? gv = null,
        string? followupQ = null, string[]? followupOpts = null)
        => new(intent, severity, reply, fov, md, gv, followupQ, followupOpts);

    [Fact]
    public void Promote_FollowupWithNamedSymptom_BecomesReportDiscomfort_NoCard()
    {
        var r = NluComplianceGate.Check(
            Raw("FollowupRequest", followupQ: "Which symptom?", followupOpts: new[] { "Dizzy", "Nausea" }),
            "I'm dizzy"); // names a specific symptom → veto the "which?" question

        Assert.Equal("ReportDiscomfort", r.Intent);
        Assert.Null(r.FollowupQuestion);
        Assert.Null(r.FollowupOptions);
    }

    [Fact]
    public void Demote_DiscomfortWithoutNamedSymptom_BecomesFollowup_ZeroesDeltas()
    {
        var r = NluComplianceGate.Check(
            Raw("ReportDiscomfort", fov: -5, md: 0.1), // engine proposed actuation…
            "I feel weird with my eyes");              // …but no specific symptom → ASK first

        Assert.Equal("FollowupRequest", r.Intent);
        Assert.Equal(0, r.FovDelta);              // non-actuating intent → deltas zeroed
        Assert.Equal(0.0, r.MaskDepthDelta, 6);
        Assert.NotNull(r.FollowupOptions);
        Assert.InRange(r.FollowupOptions!.Length, 2, 4);
    }

    [Fact]
    public void AllZeros_ReportedDiscomfort_FallsBackToLexiconDefaults()
    {
        // v0.9.1 hotfix: ReportDiscomfort + severity>=2 + all-zero explicit deltas is treated
        // as "not engaged" → the lexicon's severity-bucket defaults apply instead of nothing.
        var r = NluComplianceGate.Check(
            Raw("ReportDiscomfort", severity: 3, fov: 0, md: 0.0, gv: 0.0),
            "I'm dizzy"); // named symptom → no demote; exercises the all-zeros branch

        Assert.Equal("ReportDiscomfort", r.Intent);
        Assert.True(r.FovDelta != 0 || r.MaskDepthDelta != 0.0 || r.GridVisibilityDelta != 0.0,
            "all-zero model deltas should fall back to non-zero lexicon defaults");
    }

    [Fact]
    public void EngagedDeltas_AreClampedToSafeRange()
    {
        var r = NluComplianceGate.Check(
            Raw("ReportDiscomfort", fov: -100, md: 0.9, gv: -0.9),
            "I'm dizzy"); // named symptom → no demote; model "engaged" with out-of-range values

        Assert.Equal(-30, r.FovDelta);            // FOV clamp [-30, 0]
        Assert.Equal(0.3, r.MaskDepthDelta, 6);   // mask clamp [-0.3, 0.3]
        Assert.Equal(-0.3, r.GridVisibilityDelta, 6);
    }

    [Fact]
    public void NonActuatingIntent_NeverDerivesDeltas()
    {
        var r = NluComplianceGate.Check(Raw("AnalyzeRequest", fov: -10, md: 0.2), "how am I doing?");
        Assert.Equal(0, r.FovDelta);
        Assert.Equal(0.0, r.MaskDepthDelta, 6);
        Assert.Equal(0.0, r.GridVisibilityDelta, 6);
    }
}
