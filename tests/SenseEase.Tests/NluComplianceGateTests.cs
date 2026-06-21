using SenseEase.Core.Models;
using SenseEase.Infrastructure.Nlp;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Direct unit coverage of the pure <see cref="NluComplianceGate"/> — the single deterministic
/// floor both the LLM result and the lexicon fallback pass through. One rule per test (mirrors
/// AdaptationGateTests). Grown across Stages A→D; the convergence test (Model vs Lexicon source
/// produce the same compliant result) lands in Stage D.
/// </summary>
[Collection("ChatbotUiLanguage")] // Check → Scrub/BuildFallbackResponse read the UI-language pin — serialize
public class NluComplianceGateTests
{
    private static NluComplianceGate.RawNluDecision Raw(
        string intent, int severity = 3, string? reply = "ok",
        int? fov = null, double? md = null, double? gv = null,
        string? followupQ = null, string[]? followupOpts = null,
        NluComplianceGate.DecisionSource source = NluComplianceGate.DecisionSource.Model)
        => new(intent, severity, reply, fov, md, gv, followupQ, followupOpts, source);

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
    public void HedgedNamedSymptom_AsksInsteadOfActuatingOrDeflecting()
    {
        // The reported case: "I feel dizzy? I don't know" NAMES dizziness but HEDGES it. The old path let it
        // fall to the no-action deflection (HonestNoActionLine); now an uncertain symptom must ASK (a
        // clarifying card) and change nothing — even if the model wrongly proposed an actuation.
        var r = NluComplianceGate.Check(
            Raw("ReportDiscomfort", fov: -8, md: 0.06),
            "I feel dizzy? I don't know");

        Assert.Equal("FollowupRequest", r.Intent);   // promoted to a clarifying question
        Assert.Equal(0, r.FovDelta);                 // hedged → no actuation
        Assert.Equal(0.0, r.MaskDepthDelta, 6);
        Assert.NotNull(r.FollowupOptions);
        Assert.InRange(r.FollowupOptions!.Length, 2, 4);
    }

    [Fact]
    public void Followup_WithUnusableOptions_RebuildsCase1Picker_NotDeadEndUnknown()
    {
        // Finding #3: a FollowupRequest with no usable options must rebuild the Case-1
        // "Unknown → symptom picker" (the sanitize passes "Unknown" to TryBuildFollowup) rather than
        // dead-ending to a pickerless Unknown — TryBuildFollowup never builds one for "FollowupRequest".
        var r = NluComplianceGate.Check(
            Raw("FollowupRequest", followupQ: "?", followupOpts: new[] { "only one option" }), // <2 → unusable
            "hmm, not really sure what's going on"); // vague, no specific symptom, non-empty

        Assert.Equal("FollowupRequest", r.Intent);          // stays a clarify, not a dead-end Unknown
        Assert.NotNull(r.FollowupQuestion);
        Assert.NotNull(r.FollowupOptions);
        Assert.InRange(r.FollowupOptions!.Length, 2, 4);    // a real picker was rebuilt
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

    // ── Stage B: the source-gated Case-1 clarify ────────────────────────────────────────

    [Fact]
    public void LexiconSource_UnknownVagueInput_OffersSymptomPicker()
    {
        // The lexicon couldn't classify "I feel off" → it ASKS (Case 1) rather than reply generically.
        var r = NluComplianceGate.Check(
            Raw("Unknown", reply: null, source: NluComplianceGate.DecisionSource.Lexicon),
            "I feel off");

        Assert.Equal("FollowupRequest", r.Intent);
        Assert.NotNull(r.FollowupOptions);
        Assert.InRange(r.FollowupOptions!.Length, 2, 4);
        Assert.Equal(0, r.FovDelta); // non-actuating
    }

    [Fact]
    public void ModelSource_Unknown_KeepsReply_NoPicker()
    {
        // The model handles Unknown by replying — Case 1 must NOT fire for the model path, or
        // its conversational answer would be discarded for a symptom picker.
        var r = NluComplianceGate.Check(
            Raw("Unknown", reply: "SenseEase reduces visual motion.",
                source: NluComplianceGate.DecisionSource.Model),
            "what is SenseEase?", uiEnglishPin: true);

        Assert.Equal("Unknown", r.Intent);
        Assert.Null(r.FollowupOptions);
        Assert.Equal("SenseEase reduces visual motion.", r.Response);
    }

    // ── Stage C: the question-guard (a question never silently actuates) ─────────────────

    [Theory]
    [InlineData("do you think I'm dizzy now?")]
    [InlineData("你覺得我現在頭暈嗎?")]
    public void QuestionGuard_InterrogativeReportDiscomfort_ZeroesDeltas_AndFlags(string utterance)
    {
        // A named symptom survives the demote (intent stays ReportDiscomfort), but the
        // interrogative framing means it must NOT actuate. Deltas zeroed, flag set, reply kept.
        var r = NluComplianceGate.Check(Raw("ReportDiscomfort", fov: -8, md: 0.06, gv: 0.04), utterance);

        Assert.Equal("ReportDiscomfort", r.Intent); // intent unchanged — only actuation suppressed
        Assert.Equal(0, r.FovDelta);
        Assert.Equal(0.0, r.MaskDepthDelta, 6);
        Assert.Equal(0.0, r.GridVisibilityDelta, 6);
        Assert.True(r.SuppressedAsQuestion);
    }

    [Fact]
    public void QuestionGuard_GenuineReport_PreservesDeltas_NoFlag()
    {
        var r = NluComplianceGate.Check(Raw("ReportDiscomfort", fov: -8, md: 0.06), "I'm dizzy");

        Assert.True(r.FovDelta != 0); // a real report still actuates
        Assert.False(r.SuppressedAsQuestion);
    }

    [Fact]
    public void QuestionGuard_HelpSeekingReport_StillActuates()
    {
        // "I'm dizzy, can you help?" is a help-seeking REPORT, not an asking-the-bot inquiry —
        // IsSymptomInquiry is deliberately narrow, so this must still actuate.
        var r = NluComplianceGate.Check(Raw("ReportDiscomfort", fov: -8), "I'm dizzy, can you help?");

        Assert.True(r.FovDelta != 0);
        Assert.False(r.SuppressedAsQuestion);
    }

    // Kirby 2026-06-20: a screen/state question the model mislabels ReportDiscomfort + answers
    // "已記錄"/"recorded" with NO actuation (record-only) must NOT read as recorded — broadened
    // guard fires regardless of delta and the "recorded" claim is replaced.
    [Theory]
    [InlineData("現在看的影片是不是容易暈眩")]
    [InlineData("是不是會頭暈")]
    public void QuestionGuard_RecordOnlyInquiry_DropsRecordedClaim_ZeroesDeltas(string utterance)
    {
        var r = NluComplianceGate.Check(
            Raw("ReportDiscomfort", reply: "了解，已記錄。如再有不適請告知。"), utterance, uiEnglishPin: false);

        Assert.Equal(0, r.FovDelta);
        Assert.Equal(0.0, r.MaskDepthDelta, 6);
        Assert.Equal(0.0, r.GridVisibilityDelta, 6);
        Assert.DoesNotContain("已記錄", r.Response);      // the misleading "recorded" is gone
        Assert.Contains("沒有變更", r.Response);          // replaced with the honest clarify
    }

    [Fact]
    public void QuestionGuard_RecordOnlyInquiry_En_DropsRecordedClaim()
    {
        var r = NluComplianceGate.Check(
            Raw("ReportDiscomfort", reply: "Got it — recorded. Tell me if anything else comes up."),
            "do you think this video is making me dizzy?");

        Assert.Equal(0, r.FovDelta);
        Assert.DoesNotContain("recorded", r.Response);
    }

    // ── Stage D: convergence — the SAME deterministic floor governs BOTH decision engines ──
    //    The model PROPOSES actuation; the lexicon proposes nothing. For "ask, don't guess"
    //    and "don't answer a question by acting" inputs, the one gate must zero the actuation
    //    for BOTH sources. The lexicon path is run exactly as BuildFallbackResult does it
    //    (Classify + SeverityBucket → gate, deltas null, Source=Lexicon).

    private static NluResult ViaModel(string utterance) => NluComplianceGate.Check(
        Raw("ReportDiscomfort", fov: -10, md: 0.2, gv: 0.1,
            source: NluComplianceGate.DecisionSource.Model),
        utterance);

    private static NluResult ViaLexicon(string utterance)
    {
        var (intent, _) = FeedbackLexicon.Classify(utterance);
        int severity = FeedbackLexicon.SeverityBucket(utterance);
        return NluComplianceGate.Check(
            Raw(intent, severity: severity, reply: null,
                source: NluComplianceGate.DecisionSource.Lexicon),
            utterance);
    }

    [Theory]
    [InlineData("do you think I'm dizzy now?")] // interrogative → question-guard zeroes
    [InlineData("你覺得我現在頭暈嗎?")]
    [InlineData("I feel weird with my eyes")]   // vague discomfort, no named symptom → demote
    public void Convergence_MustNotActuate_BothSourcesZeroDeltas(string utterance)
    {
        var model = ViaModel(utterance);
        var lexicon = ViaLexicon(utterance);

        Assert.Equal(0, model.FovDelta);
        Assert.Equal(0.0, model.MaskDepthDelta, 6);
        Assert.Equal(0.0, model.GridVisibilityDelta, 6);

        Assert.Equal(0, lexicon.FovDelta);
        Assert.Equal(0.0, lexicon.MaskDepthDelta, 6);
        Assert.Equal(0.0, lexicon.GridVisibilityDelta, 6);
    }

    [Theory]
    [InlineData("I'm dizzy")] // a clearly-named symptom report → BOTH engines actuate (no over-suppression)
    [InlineData("頭暈")]
    public void Convergence_GenuineReport_BothSourcesActuate(string utterance)
    {
        Assert.True(ViaModel(utterance).FovDelta < 0,   "model path should actuate a real report");
        Assert.True(ViaLexicon(utterance).FovDelta < 0, "lexicon path should actuate a real report");
    }
}
