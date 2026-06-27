using SenseEase.Core.Inference;
using SenseEase.Infrastructure.Nlp;
using Xunit;

namespace SenseEase.Tests;

/// <summary>U2 honesty guard (eval 2026-06-27): the model can tag an app HOW-TO question
/// ("how do I make the dark edges less strong?") as ReportDiscomfort and narrate a change it never made.
/// Two deterministic backstops: the gate reroutes a no-symptom how-to to Unknown (no actuation, no picker),
/// and the scrubber's broadened claim-detection rewrites a change-claim to the honest no-op line whenever
/// nothing actuated. A named-symptom help request is untouched and still actuates.</summary>
public class U2HonestyGuardTests
{
    const bool En = true;   // pin English for determinism

    // ── InterrogativeDetector.IsHowToInquiry ─────────────────────────────────────────────────────
    [Theory]
    [InlineData("how do I make the dark edges less strong?", true)]
    [InlineData("How can I reduce the brightness?", true)]
    [InlineData("how to turn off the grid", true)]
    [InlineData("Is there a way to soften the borders?", true)]
    [InlineData("怎麼讓邊緣不要那麼深?", true)]
    [InlineData("如何調整保護?", true)]
    [InlineData("I'm dizzy, how do I get relief?", false)]   // symptom-LED → not start-anchored → not a how-to
    [InlineData("I feel nauseous", false)]
    [InlineData("what is VIMS?", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHowToInquiry_Matrix(string? text, bool expected)
        => Assert.Equal(expected, InterrogativeDetector.IsHowToInquiry(text));

    // ── Gate: a no-symptom how-to is rerouted to Unknown, zero deltas, honest reply ───────────────
    [Fact]
    public void Gate_HowToNoSymptom_RoutesToUnknown_NoActuation_HonestReply()
    {
        var d = new NluComplianceGate.RawNluDecision(
            "ReportDiscomfort", 2, "I've softened the dark edges a little so they're less strong.",
            0, 0.0, 0.0, null, null);   // model claimed discomfort but set all deltas to 0
        var r = NluComplianceGate.Check(d, "how do I make the dark edges less strong?", En);

        Assert.Equal("Unknown", r.Intent);
        Assert.Equal(0, r.FovDelta);
        Assert.Equal(0.0, r.MaskDepthDelta);
        Assert.Equal(0.0, r.GridVisibilityDelta);
        Assert.Contains("haven't changed anything", r.Response);   // the false "I've softened" claim is gone
    }

    [Fact]
    public void Gate_SymptomPlusHelpRequest_StillActuates()
    {
        var d = new NluComplianceGate.RawNluDecision(
            "ReportDiscomfort", 3, "I've eased the visuals a little.",
            -8, 0.1, 0.0, null, null);
        var r = NluComplianceGate.Check(d, "I'm dizzy, can you help?", En);

        Assert.Equal("ReportDiscomfort", r.Intent);
        Assert.Equal(-8, r.FovDelta);          // a named symptom + help request is untouched by the how-to reroute
        Assert.Equal(0.1, r.MaskDepthDelta);
    }

    [Fact]
    public void Gate_HowToWithNamedSymptom_NotRerouted()
    {
        // "how do I make the dizziness stop?" names a symptom → the !MentionsSpecificSymptom gate blocks the
        // reroute, so it is NOT forced to Unknown (a named symptom keeps its discomfort handling).
        var d = new NluComplianceGate.RawNluDecision(
            "ReportDiscomfort", 3, "Let me ease the motion for you.",
            -6, 0.1, 0.0, null, null);
        var r = NluComplianceGate.Check(d, "how do I make the dizziness stop?", En);

        Assert.NotEqual("Unknown", r.Intent);
    }

    // ── Scrubber: paraphrase change-claims caught when !actuated ──────────────────────────────────
    [Theory]
    [InlineData("I've softened the dark edges a little so they're less strong.")]  // substring (already covered)
    [InlineData("I softened the dark edges a little.")]                            // no "'ve" → regex
    [InlineData("I just dimmed the edges for you.")]                               // "i just" + past → regex
    [InlineData("Let me ease that for you.")]                                      // promise → regex
    [InlineData("I'll reduce the grid now.")]                                      // promise → regex
    [InlineData("The dark edges are now softer.")]                                 // stative → regex
    [InlineData("The overlay looks a little gentler now.")]                        // stative → regex
    public void Scrub_ZeroActuation_ChangeClaim_BecomesHonestNoOp(string reply)
    {
        string s = ReplyComplianceScrubber.Scrub(reply, "Unknown", actuated: false, userText: "ok", uiEnglishPin: En);
        Assert.Contains("haven't changed anything", s);
    }

    [Theory]
    [InlineData("You can soften the dark edges by dragging the strength control.")]  // advice — NOT a claim
    [InlineData("To make the edges less strong, drag the control on the bar.")]
    [InlineData("I can ease the visuals if you'd like — just say the word.")]        // an OFFER, not a claim
    public void Scrub_ZeroActuation_Advice_IsPreserved(string reply)
    {
        string s = ReplyComplianceScrubber.Scrub(reply, "Unknown", actuated: false, userText: "ok", uiEnglishPin: En);
        Assert.DoesNotContain("haven't changed anything", s);   // advice/offers survive verbatim
    }

    [Fact]
    public void Scrub_RealActuation_ClaimSurvives()
    {
        // When a change really happened, "I softened the edges" is TRUE and must pass through untouched.
        string s = ReplyComplianceScrubber.Scrub("I softened the edges a little.", "ReportDiscomfort",
            actuated: true, userText: "my eyes hurt", uiEnglishPin: En);
        Assert.Contains("softened", s);
        Assert.DoesNotContain("haven't changed anything", s);
    }
}
