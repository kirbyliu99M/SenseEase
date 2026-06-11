using System;
using SenseEase.App.ViewModels;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UX-21 (Wave B2): the safety-intercept / apply-block results carry a stable, language-
/// neutral reason CODE alongside the English forensic <c>Reason</c>. The Card maps the
/// CODE to localized zh/en copy; the English <c>Reason</c> stays the durable event-log /
/// action-log string and MUST NOT drift (the existing HabituationOverrideApplicatorTests
/// assert on those substrings). These tests pin the CODE on every block path WITHOUT
/// touching the forensic strings.
/// </summary>
public class HabituationOverrideApplicatorReasonCodeTests
{
    private static ProfileOverrideProposal MakeProposal(OverrideIntent intent, object value)
        => new ProfileOverrideProposal(
            Intent: intent,
            TargetField: intent.ToString().ToLowerInvariant(),
            ProposedValue: value,
            Rationale: "test rationale",
            ExpiresAtUtc: DateTime.UtcNow.AddDays(2));

    private static HabituationProfileSnapshot MakeSnapshot(
        double scale = 1.0, int strikeLimit = 2, MedicalAdvisory? advisory = null)
        => new HabituationProfileSnapshot
        {
            SchemaVersion = 2,
            Profile = "TYPICAL",
            ProfileSource = "calibration_demo",
            PersonalPressureScale = scale,
            SevereStrikeLimit = strikeLimit,
            MedicalAdvisoryShown = advisory,
        };

    [Fact]
    public void NullSnapshot_Blocks_WithSnapshotUnavailableCode_StableReason()
    {
        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(
            MakeProposal(OverrideIntent.AdjustSensitivity, 1.1), null);

        Assert.True(result.Blocked);
        Assert.Equal(HabituationInterceptCode.SnapshotUnavailable, result.Code);
        // Forensic English string unchanged (the existing H15 tests assert "verify safety").
        Assert.Contains("verify safety", result.Reason!);
    }

    [Fact]
    public void VestibularHardSetStrikeLimit_Blocks_WithHardSetCode_StableReason()
    {
        var snapshot = MakeSnapshot(strikeLimit: 1, advisory: MedicalAdvisory.Vestibular);
        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(
            MakeProposal(OverrideIntent.AdjustStrikeLimit, (long)3), snapshot);

        Assert.True(result.Blocked);
        Assert.Equal(HabituationInterceptCode.VestibularHardSetStrikeLimit, result.Code);
        Assert.Contains("hard-set", result.Reason!);
    }

    [Fact]
    public void VestibularRelaxation_Blocks_WithVestibularRelaxationCode_StableReason()
    {
        var snapshot = MakeSnapshot(scale: 1.0, advisory: MedicalAdvisory.Vestibular);
        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(
            MakeProposal(OverrideIntent.AdjustSensitivity, 0.8), snapshot);

        Assert.True(result.Blocked);
        Assert.Equal(HabituationInterceptCode.VestibularRelaxation, result.Code);
        Assert.Contains("vestibular", result.Reason!);
    }

    [Fact]
    public void HighRiskComboSignificant_Blocks_WithHighRiskCode_StableReason()
    {
        var snapshot = MakeSnapshot(scale: 1.0, advisory: MedicalAdvisory.HighRiskCombo);
        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(
            MakeProposal(OverrideIntent.AdjustSensitivity, 0.8), snapshot);

        Assert.True(result.Blocked);
        Assert.Equal(HabituationInterceptCode.HighRiskComboSignificant, result.Code);
        Assert.Contains("high-risk-combo", result.Reason!);
    }

    [Fact]
    public void Passed_HasNoneCode()
    {
        var snapshot = MakeSnapshot(scale: 1.0, advisory: null);
        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(
            MakeProposal(OverrideIntent.AdjustSensitivity, 0.8), snapshot);

        Assert.False(result.Blocked);
        Assert.Equal(HabituationInterceptCode.None, result.Code);
    }

    [Fact]
    public void ApplyResult_LegacyBlockedOverload_DefaultsToNoneCode()
    {
        // The reason-only overload (used by tests / non-coded callers) defaults Code=None,
        // so the Card falls back to rendering the literal Reason — nothing is lost.
        var r = ApplyResult.Blocked("vestibular advisory active");
        Assert.True(r.BlockedBySafety);
        Assert.Equal(HabituationInterceptCode.None, r.Code);
        Assert.Equal("vestibular advisory active", r.Reason);
    }
}
