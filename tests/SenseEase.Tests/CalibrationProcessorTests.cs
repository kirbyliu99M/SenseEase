using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Stage 1 of the post-test calibration feature. Pins every
/// decision-matrix case from spec §9 (first-calibration / buttons path)
/// plus the boundary cases for personal_pressure_scale and the SSQ-style
/// symptom-dominance grouping. LLM-path tests live elsewhere (Stage 4+).
///
/// Test helpers (<see cref="MakeResponses"/>) keep the per-test arrange
/// block minimal so each test reads as "if these answers come in, expect
/// this result". Reviewers should be able to verify each test maps to
/// exactly one §9 bullet without cross-referencing.</summary>
public class CalibrationProcessorTests
{
    // ── Helper: build a minimally-valid CalibrationResponses with Q3 set ──
    private static CalibrationResponses MakeResponses(
        int intensity,
        UncomfortableSegment? q1 = UncomfortableSegment.Middle,
        BaselineComparison? q4 = BaselineComparison.Similar,
        IReadOnlyList<RiskFactor>? q5 = null,
        IReadOnlyList<SymptomType>? q2 = null)
    => new()
    {
        Q1Segment      = q1,
        Q2Symptoms     = q2 ?? new[] { SymptomType.Dizziness },
        Q3Intensity    = intensity,
        Q4Comparison   = q4,
        Q5RiskFactors  = q5 ?? new[] { RiskFactor.None },
    };

    private static readonly CalibrationSensorData NeutralSensor =
        new(PeakPressure: 50.0, MaskActivations: 1, PeakPressureAtSec: 25.0);

    // ── §9 Case 1: Q3=5 + Q4=worse → SENSITIVE ───────────────────────────────

    [Fact]
    public void Q3_5_Q4_Worse_GivesSensitive()
    {
        var r = MakeResponses(intensity: 5, q4: BaselineComparison.Worse);
        var result = CalibrationProcessor.Process(r, NeutralSensor);
        Assert.Equal(HabituationProfile.Sensitive, result.Profile);
    }

    // ── §9 Case 2: Vestibular diagnosed → SENSITIVE + override 50 + limit 1 ──

    [Fact]
    public void VestibularDiagnosed_HardLocksToSensitiveWithOverrides()
    {
        var r = MakeResponses(intensity: 1,
            q5: new[] { RiskFactor.VestibularDiagnosed });
        var result = CalibrationProcessor.Process(r, NeutralSensor);

        Assert.Equal(HabituationProfile.Sensitive, result.Profile);
        Assert.Equal(50, result.TriggerThresholdOverride);
        Assert.Equal(1, result.SevereStrikeLimitOverride);
        Assert.Equal("vestibular", result.MedicalAdvisory);
    }

    // ── §9 Case 3: Q3=2 + 2 risk factors → TYPICAL escalates to SENSITIVE ──

    [Fact]
    public void Q3_2_With_TwoRiskFactors_EscalatesTypicalToSensitive()
    {
        var r = MakeResponses(intensity: 2,
            q5: new[] { RiskFactor.MotionSickness, RiskFactor.Migraine });
        var result = CalibrationProcessor.Process(r, NeutralSensor);

        // Q3=2 base → TYPICAL; +2 risks → escalate → SENSITIVE.
        Assert.Equal(HabituationProfile.Sensitive, result.Profile);
        // intensity != 5 → no high_risk_combo advisory; vestibular not present.
        Assert.Null(result.MedicalAdvisory);
    }

    // ── §9 Case 4: Q3=4 + 1 risk factor → SENSITIVE stays SENSITIVE + threshold-10 ──

    [Fact]
    public void Q3_4_With_OneRiskFactor_KeepsSensitive_AppliesThresholdAdjustment()
    {
        var r = MakeResponses(intensity: 4,
            q5: new[] { RiskFactor.MotionSickness });
        var result = CalibrationProcessor.Process(r, NeutralSensor);

        Assert.Equal(HabituationProfile.Sensitive, result.Profile);
        Assert.Equal(-10, result.TriggerThresholdAdjustment);
        Assert.Null(result.TriggerThresholdOverride); // adjustment, not override
    }

    // ── §9 Case 5: Q5=Skipped → profile only from Q3+Q4 ─────────────────────

    [Fact]
    public void Q5_Skipped_DoesNotEscalateOrTriggerAdvisory()
    {
        var r = MakeResponses(intensity: 3, q4: BaselineComparison.Similar,
            q5: new[] { RiskFactor.Skipped });
        var result = CalibrationProcessor.Process(r, NeutralSensor);

        // Q3=3 + Q4=similar → TYPICAL (no escalation, no adjustment).
        Assert.Equal(HabituationProfile.Typical, result.Profile);
        Assert.Equal(0, result.TriggerThresholdAdjustment);
        Assert.Null(result.TriggerThresholdOverride);
        Assert.Null(result.MedicalAdvisory);
    }

    // ── §9 Case 7: Q3 unanswered → Abandoned + RESILIENT default ────────────

    [Fact]
    public void Q3_Unanswered_GivesAbandonedCompletenessAndResilientDefault()
    {
        var r = new CalibrationResponses { Q3Intensity = null };
        var result = CalibrationProcessor.Process(r, NeutralSensor);

        Assert.Equal(CalibrationCompleteness.Abandoned, result.Completeness);
        Assert.Equal(HabituationProfile.Resilient, result.Profile);
        Assert.Equal(1.0, result.PersonalPressureScale);
        Assert.Equal(CalibrationConfidence.Low, result.CalibrationConfidence);
    }

    // ── §9 Case 8: Q3 answered, others partial → Partial + still acts ──────

    [Fact]
    public void Q3_AnsweredOthersMissing_GivesPartialAndStillProcessesProfile()
    {
        var r = new CalibrationResponses
        {
            Q3Intensity = 3,
            // Q1/Q2/Q4/Q5 all unanswered.
        };
        var result = CalibrationProcessor.Process(r, NeutralSensor);

        Assert.Equal(CalibrationCompleteness.Partial, result.Completeness);
        // Q3=3 alone → TYPICAL per Step 1 ("Q3 ∈ {2,3} → TYPICAL").
        Assert.Equal(HabituationProfile.Typical, result.Profile);
        Assert.Equal(CalibrationConfidence.Medium, result.CalibrationConfidence);
    }

    // ── §9 Case 9: scale floor enforcement (Q3=1 + peak 70 → scale=0.7) ────

    [Fact]
    public void ScaleLookup_LowIntensityHighPeak_FloorsToMin()
    {
        // intensity=1, peak >= 60 → table value 0.7 (also the clamp floor).
        var r = MakeResponses(intensity: 1);
        var sensor = new CalibrationSensorData(70.0, 1, 30.0);
        var result = CalibrationProcessor.Process(r, sensor);

        Assert.Equal(0.7, result.PersonalPressureScale);
    }

    // ── §9 Case 10: vestibular hard cap on scale ───────────────────────────

    [Fact]
    public void Vestibular_CapsScaleToOne_EvenWhenLookupWouldYieldHigher()
    {
        // Q3=5 + peak < 70 → table scale 1.5. Vestibular forces ≤ 1.0.
        var r = MakeResponses(intensity: 5,
            q5: new[] { RiskFactor.VestibularDiagnosed });
        var sensor = new CalibrationSensorData(40.0, 5, 10.0);
        var result = CalibrationProcessor.Process(r, sensor);

        Assert.Equal(1.0, result.PersonalPressureScale);
    }

    // ── Q5 internal: None alone doesn't escalate ───────────────────────────

    [Fact]
    public void Q5_NoneOnly_DoesNotCountAsRiskFactor()
    {
        var r = MakeResponses(intensity: 2,
            q5: new[] { RiskFactor.None });
        var result = CalibrationProcessor.Process(r, NeutralSensor);

        Assert.Equal(HabituationProfile.Typical, result.Profile);   // base, no escalation
        Assert.Equal(0, result.TriggerThresholdAdjustment);
    }

    // ── High-risk combo advisory ───────────────────────────────────────────

    [Fact]
    public void Q3_5_With_TwoNonVestibularRisks_TriggersHighRiskComboAdvisory()
    {
        var r = MakeResponses(intensity: 5,
            q5: new[] { RiskFactor.MotionSickness, RiskFactor.Migraine });
        var result = CalibrationProcessor.Process(r, NeutralSensor);

        Assert.Equal("high_risk_combo", result.MedicalAdvisory);
        Assert.Equal(HabituationProfile.Sensitive, result.Profile); // already at top, stays
    }

    [Fact]
    public void Q3_4_With_TwoRisks_NoHighRiskComboAdvisory_Q3MustBe5()
    {
        var r = MakeResponses(intensity: 4,
            q5: new[] { RiskFactor.MotionSickness, RiskFactor.Migraine });
        var result = CalibrationProcessor.Process(r, NeutralSensor);

        // Escalation still applies (≥2 risks), but advisory requires Q3 == 5.
        Assert.Null(result.MedicalAdvisory);
    }

    [Fact]
    public void Vestibular_Plus_HighIntensity_AdvisoryIsVestibularNotComboPerSpec()
    {
        // Spec §6: when both conditions hold, prefer the more specific advisory.
        var r = MakeResponses(intensity: 5,
            q5: new[] { RiskFactor.VestibularDiagnosed, RiskFactor.MotionSickness });
        var result = CalibrationProcessor.Process(r, NeutralSensor);

        Assert.Equal("vestibular", result.MedicalAdvisory);
    }

    // ── Base-profile mapping ────────────────────────────────────────────────

    [Theory]
    [InlineData(1, BaselineComparison.Similar, HabituationProfile.Resilient)]
    [InlineData(1, BaselineComparison.Better,  HabituationProfile.Resilient)]
    [InlineData(1, BaselineComparison.Worse,   HabituationProfile.Typical)]
    [InlineData(2, BaselineComparison.Similar, HabituationProfile.Typical)]
    [InlineData(3, BaselineComparison.Similar, HabituationProfile.Typical)]
    [InlineData(3, BaselineComparison.Worse,   HabituationProfile.Sensitive)]
    [InlineData(4, BaselineComparison.Similar, HabituationProfile.Sensitive)]
    [InlineData(5, BaselineComparison.Better,  HabituationProfile.Sensitive)]
    public void BaseProfileMapping(int intensity, BaselineComparison q4, HabituationProfile expected)
    {
        var r = MakeResponses(intensity: intensity, q4: q4);
        var result = CalibrationProcessor.Process(r, NeutralSensor);
        Assert.Equal(expected, result.Profile);
    }

    // ── Personal pressure scale §7 table (full) ─────────────────────────────

    [Theory]
    // Q3=1 boundaries
    [InlineData(1, 30.0, 1.0)]
    [InlineData(1, 50.0, 0.85)]
    [InlineData(1, 70.0, 0.7)]
    // Q3=2 boundaries
    [InlineData(2, 30.0, 1.1)]
    [InlineData(2, 50.0, 1.0)]
    [InlineData(2, 70.0, 0.9)]
    // Q3=3 boundaries
    [InlineData(3, 40.0, 1.3)]
    [InlineData(3, 60.0, 1.0)]
    [InlineData(3, 90.0, 0.9)]
    // Q3=4 boundaries
    [InlineData(4, 50.0, 1.4)]
    [InlineData(4, 70.0, 1.1)]
    [InlineData(4, 95.0, 1.0)]
    // Q3=5 boundaries (clamped to 1.5 floor)
    [InlineData(5, 50.0, 1.5)]
    [InlineData(5, 80.0, 1.2)]
    public void PersonalPressureScaleTable(int intensity, double peak, double expectedScale)
    {
        var r = MakeResponses(intensity: intensity);
        var sensor = new CalibrationSensorData(peak, 0, 0);
        var result = CalibrationProcessor.Process(r, sensor);

        Assert.Equal(expectedScale, result.PersonalPressureScale, precision: 2);
    }

    // ── SSQ symptom-dominance grouping ──────────────────────────────────────

    [Theory]
    [InlineData(new[] { SymptomType.Nausea },                          SymptomDominance.Nausea)]
    [InlineData(new[] { SymptomType.EyeStrain },                       SymptomDominance.Oculomotor)]
    [InlineData(new[] { SymptomType.Disorientation },                  SymptomDominance.Disorientation)]
    [InlineData(new[] { SymptomType.Nausea, SymptomType.EyeStrain },   SymptomDominance.Mixed)]
    [InlineData(new[] { SymptomType.Dizziness },                       SymptomDominance.Nausea)]   // dizziness=0.5 on nausea axis, others 0
    [InlineData(new[] { SymptomType.Headache },                        SymptomDominance.Oculomotor)]
    public void SymptomDominancePrimary_GroupsBySSQAxes(SymptomType[] symptoms, SymptomDominance expected)
    {
        var r = MakeResponses(intensity: 2, q2: symptoms);
        var result = CalibrationProcessor.Process(r, NeutralSensor);
        Assert.Equal(expected, result.SymptomDominancePrimary);
    }

    [Fact]
    public void SymptomDominancePrimary_AllNoneOrEmpty_ReturnsNull()
    {
        var r = MakeResponses(intensity: 2, q2: Array.Empty<SymptomType>());
        var result = CalibrationProcessor.Process(r, NeutralSensor);
        Assert.Null(result.SymptomDominancePrimary);
    }

    // ── Confidence rating ───────────────────────────────────────────────────

    [Fact]
    public void Confidence_Low_WhenQ3IsOneAndTwoRiskFactors()
    {
        var r = MakeResponses(intensity: 1,
            q5: new[] { RiskFactor.MotionSickness, RiskFactor.Migraine });
        var result = CalibrationProcessor.Process(r, NeutralSensor);
        Assert.Equal(CalibrationConfidence.Low, result.CalibrationConfidence);
    }

    [Fact]
    public void Confidence_Low_WhenQ1ConflictDetectedFromFreetext()
    {
        // User picked "全程都還好" but mentioned 暈 in freetext → conflict.
        // After the Stage 1 review changes, Q1SignalConflict is derived inside
        // the processor (via DetectQ1SignalConflict), NOT set on the response.
        var r = MakeResponses(intensity: 2) with
        {
            Q1Segment  = UncomfortableSegment.None,
            Q1Freetext = "其實還是有點暈，但不嚴重",
        };
        var result = CalibrationProcessor.Process(r, NeutralSensor);
        Assert.Equal(CalibrationConfidence.Low, result.CalibrationConfidence);
        Assert.True(result.Q1SignalConflict);
    }

    [Fact]
    public void DetectQ1SignalConflict_EnglishFreetext()
    {
        // English-language detection — "feeling a bit dizzy still" should match
        // the "dizz" keyword.
        var r = MakeResponses(intensity: 2) with
        {
            Q1Segment  = UncomfortableSegment.None,
            Q1Freetext = "Feeling a bit dizzy still",
        };
        Assert.True(CalibrationProcessor.DetectQ1SignalConflict(r));
    }

    [Fact]
    public void DetectQ1SignalConflict_NoConflict_WhenQ1SegmentIsNotNone()
    {
        // Q1=Middle + freetext mentions symptom is NOT a conflict — user already
        // told us there's a worst segment, freetext just elaborates.
        var r = MakeResponses(intensity: 2) with
        {
            Q1Segment  = UncomfortableSegment.Middle,
            Q1Freetext = "中段最暈",
        };
        Assert.False(CalibrationProcessor.DetectQ1SignalConflict(r));
    }

    [Fact]
    public void DetectQ1SignalConflict_NoConflict_WhenFreetextEmpty()
    {
        var r = MakeResponses(intensity: 2) with
        {
            Q1Segment  = UncomfortableSegment.None,
            Q1Freetext = null,
        };
        Assert.False(CalibrationProcessor.DetectQ1SignalConflict(r));
    }

    [Fact]
    public void Confidence_High_WhenAllAnsweredAndQ1AlignsWithSensorPeak()
    {
        // Sensor peak at 25s, demo ~60s → "middle" third [20s, 40s]. Q1=Middle aligns.
        var r = MakeResponses(intensity: 3, q1: UncomfortableSegment.Middle);
        var sensor = new CalibrationSensorData(75.0, 2, 25.0);
        var result = CalibrationProcessor.Process(r, sensor);

        Assert.Equal(CalibrationConfidence.High, result.CalibrationConfidence);
    }

    [Fact]
    public void Confidence_Medium_WhenQ1DoesNotAlignWithSensorPeak()
    {
        // Q1=Start but sensor peak at 50s (end third).
        var r = MakeResponses(intensity: 3, q1: UncomfortableSegment.Start);
        var sensor = new CalibrationSensorData(75.0, 2, 50.0);
        var result = CalibrationProcessor.Process(r, sensor);

        Assert.Equal(CalibrationConfidence.Medium, result.CalibrationConfidence);
    }

    // ── Repeat-calibration scale blending (spec §7 last bullet) ─────────────

    [Theory]
    // Spec example: new 0.8, prior 1.2 → 0.6*1.2 + 0.4*0.8 = 1.04
    [InlineData(0.8, 1.2, 1.04)]
    [InlineData(1.5, 1.0, 1.20)] // 0.4*1.5 + 0.6*1.0 = 1.2
    [InlineData(0.7, 0.9, 0.82)] // 0.4*0.7 + 0.6*0.9 = 0.82
    public void BlendScaleWithPrior_Weights_NewAsPointFour_PriorAsPointSix(
        double fresh, double prior, double expected)
    {
        double blended = CalibrationProcessor.BlendScaleWithPrior(fresh, prior);
        Assert.Equal(expected, blended, precision: 2);
    }

    [Fact]
    public void BlendScaleWithPrior_ClampsToValidRange()
    {
        // Even if both fresh and prior pin a tail-edge, the blend stays in [0.7, 1.5].
        Assert.Equal(0.7, CalibrationProcessor.BlendScaleWithPrior(0.5, 0.5)); // would compute 0.5 → clamped
        Assert.Equal(1.5, CalibrationProcessor.BlendScaleWithPrior(2.0, 2.0)); // would compute 2.0 → clamped
    }
}
