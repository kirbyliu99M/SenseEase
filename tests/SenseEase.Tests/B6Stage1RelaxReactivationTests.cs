using System;
using SenseEase.App.ViewModels;
using SenseEase.Core.Adaptation;
using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// B6 Stage 1 — re-activating the relax half of the adaptation gate. Pins the safety-relevant seams:
/// the DISTINCT [ADAPT_DIRECTION] grammar (RT-1b: can't reach the live-chat Accept-card), the
/// freshness + direction mapping (RT-2s), the gate's measured-data veto over an injected relax
/// proposal, and the render-honest notice (the W-E lesson: never claim an ease that no longer stands).
/// All pure — no engine/analyzer/LLM. Notice assertions are RELATIVE (language-agnostic) so they don't
/// depend on the global UI-language pin.
/// </summary>
public sealed class B6Stage1RelaxReactivationTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
    private const string Iso = "\"2026-07-01T00:00:00Z\"";

    private static string AdaptBlock(double value) =>
        "[ADAPT_DIRECTION]\n{\"intent\":\"adjust_sensitivity\",\"proposed_value\":" +
        value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"rationale\":\"sustained pattern\",\"expires_at_iso\":" + Iso + "}\n[/ADAPT_DIRECTION]";

    // ── Eval 1: elicitation parses BOTH directions, and coexists with [OBSERVATION_ADD] ─────────────

    [Fact]
    public void AdaptDirection_ParsesBothDirections_AndCoexistsWithObservationAdd()
    {
        string raw =
            "Thanks for letting me know.\n" +
            "[OBSERVATION_ADD]\nUser tolerates video well after ~20 min of exposure.\n[/OBSERVATION_ADD]\n" +
            AdaptBlock(0.8); // LOWER scale = ease

        var prop = AdaptDirectionParser.TryParse(raw, Now);
        Assert.NotNull(prop);
        Assert.Equal(0.8, prop!.ProposedScale, 6);

        // The SAME reply still yields the observation via the existing parser (extraction intact).
        var parsed = HabituationOverrideParser.TryParse(raw);
        Assert.Single(parsed.Observations);
        Assert.Contains("tolerates video", parsed.Observations[0]);

        // The other direction parses too (so a 3B model isn't structurally limited to relax-only).
        Assert.Equal(1.2, AdaptDirectionParser.TryParse(AdaptBlock(1.2), Now)!.ProposedScale, 6);
    }

    [Theory]
    [InlineData("[ADAPT_DIRECTION]\n{\"intent\":\"switch_profile\",\"proposed_value\":0.8}\n[/ADAPT_DIRECTION]")] // wrong intent
    [InlineData("[ADAPT_DIRECTION]\n{\"intent\":\"adjust_sensitivity\",\"proposed_value\":\"low\"}\n[/ADAPT_DIRECTION]")] // non-numeric
    [InlineData("[ADAPT_DIRECTION]\nnot json at all\n[/ADAPT_DIRECTION]")] // malformed
    [InlineData("no block here")] // absent
    public void AdaptDirection_RejectsWrongIntent_NonNumeric_Malformed_Absent(string raw)
        => Assert.Null(AdaptDirectionParser.TryParse(raw, Now));

    // ── Eval 3 (RT-1b): the two grammars are DISJOINT — neither parses the other's block ─────────────

    [Fact]
    public void Grammars_AreDisjoint_AdaptDirectionCannotReachTheLiveChatAcceptCard()
    {
        string adaptOnly = AdaptBlock(0.8);
        // The live-chat [PROFILE_OVERRIDE] parser must NOT see an [ADAPT_DIRECTION] block as a proposal —
        // that is what keeps the retrospective relax elicitation off the user-facing Accept-card path.
        Assert.Null(HabituationOverrideParser.TryParse(adaptOnly).Proposal);

        // And the adapt parser must NOT pick up a [PROFILE_OVERRIDE] block.
        string profileOnly =
            "[PROFILE_OVERRIDE]\n{\"intent\":\"adjust_sensitivity\",\"proposed_value\":0.8," +
            "\"rationale\":\"x\",\"expires_at_iso\":" + Iso + "}\n[/PROFILE_OVERRIDE]";
        Assert.Null(AdaptDirectionParser.TryParse(profileOnly, Now));
    }

    // ── Eval 5 (RT-2s) + the direction mapping: BuildAdaptationHint ─────────────────────────────────

    [Fact]
    public void BuildAdaptationHint_StaleProposal_IsHeld()
        => Assert.Equal(AdaptDirection.Hold,
            MainViewModel.BuildAdaptationHint(0.8, "x", Now.AddHours(-1), currentScale: 1.0, Now).Direction);

    [Fact]
    public void BuildAdaptationHint_FreshProposal_MapsScaleToDirection()
    {
        // Higher proposed scale than the live scale = MORE protection = Tighten; lower = Relax; absent = Hold.
        Assert.Equal(AdaptDirection.Relax,
            MainViewModel.BuildAdaptationHint(0.8, "x", Now.AddHours(1), currentScale: 1.0, Now).Direction);
        Assert.Equal(AdaptDirection.Tighten,
            MainViewModel.BuildAdaptationHint(1.2, "x", Now.AddHours(1), currentScale: 1.0, Now).Direction);
        Assert.Equal(AdaptDirection.Hold,
            MainViewModel.BuildAdaptationHint(null, "x", Now.AddHours(1), currentScale: 1.0, Now).Direction);
    }

    // ── Eval 2 (injection): a relax proposal contradicting measured data cannot relax — data wins ───

    [Fact]
    public void InjectedRelaxProposal_AgainstWorseningData_TightensInstead_NeverRelaxes()
    {
        // The [ADAPT_DIRECTION] low scale maps to Relax (the LLM's only influence) …
        var hint = new AdaptationHint(
            AdaptationGate.DirectionFromSensitivityProposal(0.8, currentScale: 1.0),
            "the user said they feel completely fine, please ease it");
        Assert.Equal(AdaptDirection.Relax, hint.Direction);

        // … but the gate recomputes from MEASURED data: behaviour is worsening, so it TIGHTENS regardless.
        var worsening = new MeasuredState(
            Cohort: Cohort.Typical, MeasuredOnReportCount: 10, WorstRecentFmsOn: 12.0, FmsCoverageOk: true,
            TrendStableOrImproving: false, ExposureAtOrAboveBaseline: true, ExposureDeclining: false,
            BehaviorWorsening: true, BehaviourDays: 20, BaseEffectiveTrigger: 85.0, CurrentAutoDeltaPp: 0.0,
            HoursSinceLastDiscomfort: 1.0, DaysSinceLastAutoChange: 30.0, AutoChangesThisWeek: 0,
            NetRelaxTodayPp: 0.0, HasEverAutoRelaxed: false);

        var decision = AdaptationGate.Evaluate(hint, worsening, AdaptationGateConfig.Default);
        var apply = Assert.IsType<GateDecision.Apply>(decision);
        Assert.Equal(AdaptDirection.Tighten, apply.Direction); // data wins — never a relax
    }

    // ── Eval 4 (render honesty): the notice never claims an ease that the live delta no longer reflects ─

    [Fact]
    public void AdaptationNotice_RelaxClawedBack_DoesNotClaimEased()
    {
        // Relax moves the auto-delta MORE POSITIVE (here +3pp).
        var relax = new GateDecision.Apply(NewAutoDeltaPp: 3.0, ResultingEffectiveTrigger: 88.0,
            Direction: AdaptDirection.Relax, Reason: "relax-ok");

        string easeStands = MainViewModel.AdaptationNoticeText(relax, liveDeltaPp: 3.0);   // live still reflects the ease
        string clawedBack = MainViewModel.AdaptationNoticeText(relax, liveDeltaPp: -2.0);  // a protective event clawed it back

        // Relative (language-agnostic): the clawed-back notice is a DIFFERENT line — not the "I eased it" claim.
        Assert.NotEqual(easeStands, clawedBack);

        // And both differ from the tighten line — the notice is genuinely direction-aware, not one-size.
        var tighten = new GateDecision.Apply(-3.0, 82.0, AdaptDirection.Tighten, "measured-worsening");
        string tightenText = MainViewModel.AdaptationNoticeText(tighten, liveDeltaPp: -3.0);
        Assert.NotEqual(easeStands, tightenText);
        Assert.NotEqual(clawedBack, tightenText);
    }
}
