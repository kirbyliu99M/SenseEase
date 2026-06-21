using System;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M3 (Phase 7c): contract tests for HabituationOverrideProcessor. Each
/// of the five validation rules per spec §4.3.3 has a positive case
/// (proposal accepted) and a negative case (proposal rejected with reason).
/// </summary>
public class HabituationOverrideProcessorTests
{
    // ── Test fixture helpers ─────────────────────────────────────────────────

    private static DateTime NowFixed => new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc);

    private static HabituationProfileSnapshot MakeSnapshot(
        string profile = "TYPICAL",
        double scale = 1.0,
        int strikeLimit = 2,
        MedicalAdvisory? advisory = null)
    {
        return new HabituationProfileSnapshot
        {
            SchemaVersion = 2,
            Profile = profile,
            ProfileSource = "calibration_demo",
            PersonalPressureScale = scale,
            SevereStrikeLimit = strikeLimit,
            MedicalAdvisoryShown = advisory,
        };
    }

    private static ProfileOverrideProposal MakeProposal(
        OverrideIntent intent,
        object value,
        DateTime? expires = null)
    {
        return new ProfileOverrideProposal(
            Intent: intent,
            TargetField: intent switch
            {
                OverrideIntent.AdjustSensitivity  => "personal_pressure_scale",
                OverrideIntent.SwitchProfile      => "profile",
                OverrideIntent.AdjustStrikeLimit  => "severe_strike_limit",
                _                                 => "unknown",
            },
            ProposedValue: value,
            Rationale: "test rationale",
            ExpiresAtUtc: expires ?? NowFixed.AddDays(2));
    }

    // ── Rule 1: expires_at_iso window ────────────────────────────────────────

    [Fact]
    public void Validate_ExpiresInFuture_Within7Days_Accepts()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1, expires: NowFixed.AddDays(2));
        var result = HabituationOverrideProcessor.Validate(proposal, MakeSnapshot(), now: NowFixed);
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void Validate_ExpiresInPast_NowAccepts_CallerFloors()
    {
        // Chatbot-tuning change (2026-06-19): a proposal is generated THIS turn, so a past expiry only
        // means the LLM got the absolute date wrong (Qwen copies an in-prompt example date / can't compute
        // "today + 1 day"). Freshness is no longer a validation gate — the host floors a past expiry to
        // now + 1 day when storing the active proposal rather than discarding a well-formed proposal.
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1, expires: NowFixed.AddHours(-1));
        var result = HabituationOverrideProcessor.Validate(proposal, MakeSnapshot(), now: NowFixed);
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void Validate_ExpiresMoreThan7DaysOut_NowAccepts_CallerClamps()
    {
        // M3 reliability change: the over-cap-expiry case no longer rejects.
        // Mistral 3B reliably overshoots the 48h target; the host's
        // MainViewModel clamps the expiry to +7 days when storing the active
        // proposal rather than discarding an otherwise-valid block over a
        // few-hours date arithmetic miss.
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1, expires: NowFixed.AddDays(10));
        var result = HabituationOverrideProcessor.Validate(proposal, MakeSnapshot(), now: NowFixed);
        Assert.True(result.IsAccepted);
    }

    // ── Rule 2: value-in-range per intent ────────────────────────────────────

    [Theory]
    [InlineData(0.7)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Validate_AdjustSensitivity_ScaleInRange_Accepts(double scale)
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, scale);
        var result = HabituationOverrideProcessor.Validate(proposal, MakeSnapshot(), now: NowFixed);
        Assert.True(result.IsAccepted);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.6)]
    [InlineData(2.0)]
    public void Validate_AdjustSensitivity_ScaleOutOfRange_Rejects(double scale)
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, scale);
        var result = HabituationOverrideProcessor.Validate(proposal, MakeSnapshot(), now: NowFixed);
        Assert.False(result.IsAccepted);
        Assert.Contains("out of range", result.RejectionReason);
    }

    [Fact]
    public void Validate_AdjustSensitivity_NonNumericValue_Rejects()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, "not a number");
        var result = HabituationOverrideProcessor.Validate(proposal, MakeSnapshot(), now: NowFixed);
        Assert.False(result.IsAccepted);
        Assert.Contains("numeric", result.RejectionReason);
    }

    [Theory]
    [InlineData("RESILIENT")]
    [InlineData("TYPICAL")]
    [InlineData("SENSITIVE")]
    [InlineData("typical")] // case-insensitive
    public void Validate_SwitchProfile_KnownProfile_Rejected_NotSupported(string profile)
    {
        // Reading A (2026-06-06): the habituation profile is set ONLY at
        // calibration. The chatbot can no longer switch it live, so an
        // early gate rejects EVERY SwitchProfile proposal — even a known,
        // well-formed profile value.
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, profile);
        var result = HabituationOverrideProcessor.Validate(proposal, MakeSnapshot(profile: "RESILIENT"), now: NowFixed);
        Assert.False(result.IsAccepted);
        Assert.Contains("calibration", result.RejectionReason!.ToLowerInvariant());
    }

    [Fact]
    public void Validate_SwitchProfile_UnknownProfile_Rejects()
    {
        // Still rejected — but via the Reading A early gate (calibration-only),
        // which fires before the unknown-profile range check. Don't pin the
        // exact reason; just confirm it's rejected.
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, "EXTREME");
        var result = HabituationOverrideProcessor.Validate(proposal, MakeSnapshot(), now: NowFixed);
        Assert.False(result.IsAccepted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Validate_AdjustStrikeLimit_InRange_Accepts(int limit)
    {
        var proposal = MakeProposal(OverrideIntent.AdjustStrikeLimit, (long)limit);
        var result = HabituationOverrideProcessor.Validate(proposal, MakeSnapshot(), now: NowFixed);
        Assert.True(result.IsAccepted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(100)]
    public void Validate_AdjustStrikeLimit_OutOfRange_Rejects(int limit)
    {
        var proposal = MakeProposal(OverrideIntent.AdjustStrikeLimit, (long)limit);
        var result = HabituationOverrideProcessor.Validate(proposal, MakeSnapshot(), now: NowFixed);
        Assert.False(result.IsAccepted);
        Assert.Contains("out of range", result.RejectionReason);
    }

    // ── Rule 3: advisory + relaxation = HARD REJECT ──────────────────────────
    // (Vestibular-removal: the vestibular Rule-3 facts were stripped; the processor's
    //  vestibular leg is kept as dead-but-protective. The non-vestibular accepts-relaxation
    //  baseline below is the surviving Rule-3 coverage.)

    [Fact]
    public void Validate_NonVestibularUser_RelaxationProposal_Accepts()
    {
        // No advisory, scale relaxation is fine.
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 0.8);
        var snapshot = MakeSnapshot(scale: 1.0, advisory: null);
        var result = HabituationOverrideProcessor.Validate(proposal, snapshot, now: NowFixed);
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void Validate_RetainedVestibularAdvisoryBranch_RejectsRelaxation_AcceptsTighten()
    {
        // The vestibular FEATURE is removed, but the Processor's Rule-3 advisory leg is KEPT as
        // dead-but-protective (the MedicalAdvisory.Vestibular enum survives for back-compat, and a
        // pre-migration on-disk advisory could still reach this gate). Pin the branch directly so it stays
        // correct: an advisory + a RELAXATION proposal is hard-rejected WITH a safety note; a TIGHTENING is
        // allowed. (No production user reaches this post-migration; this guards the retained safety logic.)
        var snap = MakeSnapshot(scale: 1.0, advisory: MedicalAdvisory.Vestibular);

        var rejected = HabituationOverrideProcessor.Validate(
            MakeProposal(OverrideIntent.AdjustSensitivity, 0.8), snap, now: NowFixed);   // scale ↓ = relax
        Assert.False(rejected.IsAccepted);
        Assert.NotNull(rejected.SafetyNote);

        var accepted = HabituationOverrideProcessor.Validate(
            MakeProposal(OverrideIntent.AdjustSensitivity, 1.2), snap, now: NowFixed);   // scale ↑ = tighten
        Assert.True(accepted.IsAccepted);
    }

    // ── Rule 4: one proposal at a time ───────────────────────────────────────

    [Fact]
    public void Validate_AnotherProposalPending_Rejects()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);
        var result = HabituationOverrideProcessor.Validate(
            proposal,
            MakeSnapshot(),
            isAnotherProposalPending: true,
            now: NowFixed);
        Assert.False(result.IsAccepted);
        Assert.Contains("pending", result.RejectionReason);
    }

    [Fact]
    public void Validate_NoOtherProposalPending_Accepts()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);
        var result = HabituationOverrideProcessor.Validate(
            proposal,
            MakeSnapshot(),
            isAnotherProposalPending: false,
            now: NowFixed);
        Assert.True(result.IsAccepted);
    }

    // ── Rule 5: 7-day cooldown after decline ─────────────────────────────────

    [Fact]
    public void Validate_RecentDeclineOfSameIntent_WithinCooldown_Rejects()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);
        DateTime? Decline(OverrideIntent intent) =>
            intent == OverrideIntent.AdjustSensitivity ? NowFixed.AddDays(-3) : null;
        var result = HabituationOverrideProcessor.Validate(
            proposal,
            MakeSnapshot(),
            mostRecentDeclineFor: Decline,
            now: NowFixed);
        Assert.False(result.IsAccepted);
        Assert.Contains("cooldown", result.RejectionReason);
    }

    [Fact]
    public void Validate_OldDeclineOfSameIntent_OutsideCooldown_Accepts()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);
        DateTime? Decline(OverrideIntent intent) =>
            intent == OverrideIntent.AdjustSensitivity ? NowFixed.AddDays(-10) : null;
        var result = HabituationOverrideProcessor.Validate(
            proposal,
            MakeSnapshot(),
            mostRecentDeclineFor: Decline,
            now: NowFixed);
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void Validate_DeclineOfDifferentIntent_DoesNotBlock()
    {
        // User declined SwitchProfile 3 days ago, now chatbot proposes AdjustSensitivity.
        // Cooldown only applies to the SAME intent.
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);
        DateTime? Decline(OverrideIntent intent) =>
            intent == OverrideIntent.SwitchProfile ? NowFixed.AddDays(-3) : null;
        var result = HabituationOverrideProcessor.Validate(
            proposal,
            MakeSnapshot(),
            mostRecentDeclineFor: Decline,
            now: NowFixed);
        Assert.True(result.IsAccepted);
    }

    // ── IsRelaxationProposal (Phase 7f intercept reuse) ──────────────────────

    [Fact]
    public void IsRelaxationProposal_LowerScale_True()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 0.8);
        var snapshot = MakeSnapshot(scale: 1.0);
        Assert.True(HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot));
    }

    [Fact]
    public void IsRelaxationProposal_HigherScale_False()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.2);
        var snapshot = MakeSnapshot(scale: 1.0);
        Assert.False(HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot));
    }

    [Fact]
    public void IsRelaxationProposal_LessRestrictiveProfile_True()
    {
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, "RESILIENT");
        var snapshot = MakeSnapshot(profile: "SENSITIVE");
        Assert.True(HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot));
    }

    [Fact]
    public void IsRelaxationProposal_MoreRestrictiveProfile_False()
    {
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, "SENSITIVE");
        var snapshot = MakeSnapshot(profile: "RESILIENT");
        Assert.False(HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot));
    }

    // B6 Stage 4a: pin the restrictiveness rank with the NEW names + the middle tier, and
    // explicitly guard the INVERSION trap — Order() ranks restrictiveness (the INVERSE of the
    // enum's int order), so a naive (int)p would wrongly call Reactive less-restrictive than Resilient.

    [Fact]
    public void IsRelaxationProposal_ResilientOverAdapting_IsRelaxation_True()
    {
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, "RESILIENT");
        var snapshot = MakeSnapshot(profile: "ADAPTING");
        Assert.True(HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot));
    }

    [Fact]
    public void IsRelaxationProposal_AdaptingOverReactive_IsRelaxation_True()
    {
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, "ADAPTING");
        var snapshot = MakeSnapshot(profile: "REACTIVE");
        Assert.True(HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot));
    }

    [Fact]
    public void IsRelaxationProposal_ReactiveOverResilient_NotRelaxation_False()
    {
        // The inversion guard: Reactive (most restrictive) over Resilient (least) is NOT a relaxation.
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, "REACTIVE");
        var snapshot = MakeSnapshot(profile: "RESILIENT");
        Assert.False(HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot));
    }

    [Fact]
    public void IsRelaxationProposal_HigherStrikeLimit_True()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustStrikeLimit, (long)4);
        var snapshot = MakeSnapshot(strikeLimit: 2);
        Assert.True(HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot));
    }

    [Fact]
    public void IsRelaxationProposal_LowerStrikeLimit_False()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustStrikeLimit, (long)1);
        var snapshot = MakeSnapshot(strikeLimit: 2);
        Assert.False(HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot));
    }
}
