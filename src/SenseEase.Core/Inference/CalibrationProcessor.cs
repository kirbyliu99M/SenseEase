namespace SenseEase.Core.Inference;

/// <summary>
/// Pure-logic post-demo calibration processor. Maps the user's 5-question
/// responses + sensor observations to a <see cref="CalibrationResult"/>
/// (profile + personal_pressure_scale + trigger overrides + medical advisory).
///
/// No I/O, no UI, no engine state. The result is a record; the caller is
/// responsible for applying it (writing the JSON snapshot, calling
/// <see cref="HabituationEngine.SetProfile"/>, surfacing the advisory).
///
/// Encodes the decision flow from docs/CALIBRATION.md / the spec §5:
///   Step 1: Q3+Q4 → base profile
///   Step 2: Q5 risk-factor escalation (one-way: only tightens, never loosens)
///   Step 3: personal_pressure_scale lookup from §7's table
///   Step 4: vestibular hard cap (scale ≤ 1.0)
/// Plus §6's medical advisory selection and §8's confidence rating.
///
/// Repeat-calibration weighted-averaging of personal_pressure_scale
/// (new × 0.4 + old × 0.6) is the CALLER's responsibility — this processor
/// only produces the fresh scale from the current session.
/// </summary>
public static class CalibrationProcessor
{
    public const double PersonalPressureScaleMin = 0.7;
    public const double PersonalPressureScaleMax = 1.5;
    public const double VestibularPersonalPressureScaleCap = 1.0;

    public static CalibrationResult Process(
        CalibrationResponses responses,
        CalibrationSensorData sensor)
    {
        var completeness = ClassifyCompleteness(responses);
        if (completeness == CalibrationCompleteness.Abandoned)
        {
            // Per spec §3: Q3 not answered → revert to RESILIENT default;
            // calibration_count does NOT increment (caller's responsibility).
            // We still return a result so the caller has a uniform code path.
            return new CalibrationResult
            {
                Profile                  = HabituationProfile.Resilient,
                PersonalPressureScale    = 1.0,
                TriggerThresholdOverride = null,
                TriggerThresholdAdjustment = 0,
                SevereStrikeLimitOverride = null,
                MedicalAdvisory          = null,
                SymptomDominancePrimary  = null,
                CalibrationConfidence    = "low",
                Completeness             = CalibrationCompleteness.Abandoned,
            };
        }

        int intensity = responses.Q3Intensity!.Value; // non-null guaranteed by completeness check

        // ── Step 1: Q3+Q4 → base profile ──
        HabituationProfile profile = DeriveBaseProfile(intensity, responses.Q4Comparison);

        // ── Step 2: Q5 risk-factor escalation ──
        int riskCount = CountActiveRiskFactors(responses.Q5RiskFactors);
        bool vestibular = responses.Q5RiskFactors?.Contains(RiskFactor.VestibularDiagnosed) == true;

        int?    triggerOverride  = null;
        int     triggerAdjust    = 0;
        int?    strikeLimitOverride = null;
        string? medicalAdvisory  = null;

        if (vestibular)
        {
            // Spec §5 step 2 first branch: vestibular hard-locks.
            profile = HabituationProfile.Sensitive;
            triggerOverride     = 50;
            strikeLimitOverride = 1;
            medicalAdvisory     = "vestibular";
        }
        else if (riskCount >= 2)
        {
            profile = EscalateOneLevel(profile);
            // Spec §5: high-risk combo medical advisory fires when Q3==5 AND
            // ≥2 risk factors. Vestibular branch already returned above.
            if (intensity == 5)
                medicalAdvisory = "high_risk_combo";
        }
        else if (riskCount == 1)
        {
            triggerAdjust = -10;
        }

        // ── Step 3: personal_pressure_scale from §7 lookup ──
        double scale = LookupPersonalPressureScale(intensity, sensor.PeakPressure);

        // ── Step 4: vestibular hard cap on scale (§5 step 4) ──
        if (vestibular && scale > VestibularPersonalPressureScaleCap)
            scale = VestibularPersonalPressureScaleCap;

        // ── Final clamp belt-and-suspenders ──
        scale = Math.Clamp(scale, PersonalPressureScaleMin, PersonalPressureScaleMax);

        // ── Derived: symptom dominance primary (SSQ-aligned) ──
        string? dominance = ComputeSymptomDominancePrimary(responses.Q2Symptoms);

        // ── Confidence rating (§8 rules) ──
        string confidence = ComputeCalibrationConfidence(responses, sensor, completeness);

        return new CalibrationResult
        {
            Profile                   = profile,
            PersonalPressureScale     = scale,
            TriggerThresholdOverride  = triggerOverride,
            TriggerThresholdAdjustment = triggerAdjust,
            SevereStrikeLimitOverride = strikeLimitOverride,
            MedicalAdvisory           = medicalAdvisory,
            SymptomDominancePrimary   = dominance,
            CalibrationConfidence     = confidence,
            Completeness              = completeness,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Q3 mandatory; missing → Abandoned. All 5 answered (non-null
    /// primary, freetext optional) → Complete. Q3 present, others missing →
    /// Partial.</summary>
    private static CalibrationCompleteness ClassifyCompleteness(CalibrationResponses r)
    {
        if (r.Q3Intensity is null) return CalibrationCompleteness.Abandoned;

        bool allOthersAnswered =
            r.Q1Segment is not null &&
            r.Q2Symptoms is not null &&
            (r.Q4Comparison is not null || r.Q4NoBaseline) &&
            r.Q5RiskFactors is not null;
        return allOthersAnswered
            ? CalibrationCompleteness.Complete
            : CalibrationCompleteness.Partial;
    }

    /// <summary>Spec §5 Step 1 base mapping.
    /// Q3 ∈ {4,5} → SENSITIVE. Q3=3 + Q4=worse → SENSITIVE.
    /// Q3 ∈ {2,3} → TYPICAL. Q3=1 + Q4=worse → TYPICAL.
    /// Else (Q3=1 + Q4 ≠ worse) → RESILIENT.</summary>
    private static HabituationProfile DeriveBaseProfile(int intensity, BaselineComparison? q4)
    {
        if (intensity >= 4) return HabituationProfile.Sensitive;
        if (intensity == 3 && q4 == BaselineComparison.Worse) return HabituationProfile.Sensitive;
        if (intensity == 2 || intensity == 3) return HabituationProfile.Typical;
        // intensity == 1
        if (q4 == BaselineComparison.Worse) return HabituationProfile.Typical;
        return HabituationProfile.Resilient;
    }

    /// <summary>RESILIENT → TYPICAL → SENSITIVE → SENSITIVE (stays at top).</summary>
    private static HabituationProfile EscalateOneLevel(HabituationProfile p) => p switch
    {
        HabituationProfile.Resilient => HabituationProfile.Typical,
        HabituationProfile.Typical   => HabituationProfile.Sensitive,
        HabituationProfile.Sensitive => HabituationProfile.Sensitive,
        _                            => p,
    };

    /// <summary>Counts risk factors that contribute to escalation. "None" and
    /// "Skipped" sentinels are EXCLUDED — neither counts as a risk. So
    /// <c>[Skipped]</c> = 0, <c>[None]</c> = 0, <c>[MotionSickness]</c> = 1,
    /// <c>[MotionSickness, Migraine]</c> = 2.</summary>
    private static int CountActiveRiskFactors(IReadOnlyList<RiskFactor>? factors)
    {
        if (factors is null) return 0;
        int count = 0;
        foreach (var f in factors)
        {
            if (f != RiskFactor.None && f != RiskFactor.Skipped) count++;
        }
        return count;
    }

    /// <summary>Personal-pressure-scale lookup from spec §7's table.
    /// Higher scale = more sensitive user = trigger pressure fires earlier
    /// (via <c>effectiveTrigger = defaultTrigger / scale</c> at the engine
    /// boundary).</summary>
    private static double LookupPersonalPressureScale(int intensity, double peak)
    {
        return intensity switch
        {
            1 => peak switch
            {
                <  40 => 1.0,
                <  60 => 0.85,
                _     => 0.7,
            },
            2 => peak switch
            {
                <  40 => 1.1,
                <  60 => 1.0,
                _     => 0.9,
            },
            3 => peak switch
            {
                <  50 => 1.3,
                <  80 => 1.0,
                _     => 0.9,
            },
            4 => peak switch
            {
                <  60 => 1.4,
                <  90 => 1.1,
                _     => 1.0,
            },
            5 => peak switch
            {
                <  70 => 1.5,
                _     => 1.2,
            },
            _ => 1.0, // unreachable — intensity clamped 1-5 upstream
        };
    }

    /// <summary>SSQ three-dimension grouping per spec §3:
    ///   nausea_score        = (Nausea ? 1 : 0) + (Dizziness ? 0.5 : 0)
    ///   oculomotor_score    = (EyeStrain ? 1 : 0) + (Headache ? 0.5 : 0)
    ///   disorientation_score = (Disorientation ? 1 : 0) + (Imbalance ? 0.5 : 0)
    /// Returns argmax if unique, "mixed" if multiple tied at top, null if all zero.</summary>
    public static string? ComputeSymptomDominancePrimary(IReadOnlyList<SymptomType>? symptoms)
    {
        if (symptoms is null || symptoms.Count == 0) return null;

        double nausea = (symptoms.Contains(SymptomType.Nausea)      ? 1.0 : 0.0)
                     + (symptoms.Contains(SymptomType.Dizziness)    ? 0.5 : 0.0);
        double oculo  = (symptoms.Contains(SymptomType.EyeStrain)   ? 1.0 : 0.0)
                     + (symptoms.Contains(SymptomType.Headache)     ? 0.5 : 0.0);
        double disori = (symptoms.Contains(SymptomType.Disorientation) ? 1.0 : 0.0)
                     + (symptoms.Contains(SymptomType.Imbalance)    ? 0.5 : 0.0);

        if (nausea == 0 && oculo == 0 && disori == 0) return null;

        double max = Math.Max(nausea, Math.Max(oculo, disori));
        int countAtMax = (nausea == max ? 1 : 0) + (oculo == max ? 1 : 0) + (disori == max ? 1 : 0);
        if (countAtMax > 1) return "mixed";

        if (nausea == max) return "nausea";
        if (oculo  == max) return "oculomotor";
        return "disorientation";
    }

    /// <summary>Spec §8 confidence rules:
    /// <list type="bullet">
    ///   <item><c>high</c>: all 5 questions answered AND Q1 segment aligns with
    ///         the sensor's peak-pressure timestamp (±5 s — see segment-bucket
    ///         heuristic in <see cref="Q1AlignsWithSensorPeak"/>).</item>
    ///   <item><c>medium</c>: Q3 answered but at least one other missing OR Q1
    ///         segment doesn't align.</item>
    ///   <item><c>low</c>: Q3 answered but internal contradictions (e.g. Q3=1
    ///         with ≥2 risk factors).</item>
    /// </list>
    /// </summary>
    private static string ComputeCalibrationConfidence(
        CalibrationResponses r, CalibrationSensorData sensor, CalibrationCompleteness completeness)
    {
        if (completeness == CalibrationCompleteness.Abandoned) return "low";

        // Internal-contradiction check: low intensity report combined with
        // multiple risk factors suggests user is either downplaying or hasn't
        // calibrated their self-report scale. Flag as low confidence so the
        // chatbot can probe in subsequent sessions.
        int riskCount = CountActiveRiskFactors(r.Q5RiskFactors);
        if (r.Q3Intensity == 1 && riskCount >= 2) return "low";

        // Q1 signal conflict (user said "全程都還好" but freetext mentioned a
        // symptom) is also a low-confidence indicator.
        if (r.Q1SignalConflict) return "low";

        if (completeness == CalibrationCompleteness.Partial) return "medium";

        // Complete + no contradictions → check Q1-vs-sensor alignment.
        return Q1AlignsWithSensorPeak(r.Q1Segment, sensor) ? "high" : "medium";
    }

    /// <summary>Q1-segment-vs-sensor alignment heuristic for the §8 confidence
    /// rule "Q1 時段與 sensor 峰值吻合 (±5 秒)". We need a demo duration to
    /// turn segments into time windows; using a conservative 60 s assumed-
    /// duration (the bundled FPV-vid.mp4 is 59 s, matches). Segments split
    /// the duration into thirds. Sensor peak's <c>PeakPressureAtSec</c> in
    /// the matching third (with ±5 s slack across boundaries) counts as aligned.</summary>
    private static bool Q1AlignsWithSensorPeak(UncomfortableSegment? seg, CalibrationSensorData sensor)
    {
        if (seg is null) return false;
        if (seg == UncomfortableSegment.None) return true; // no claim to verify

        const double assumedDurationSec = 60.0;
        const double slack = 5.0;
        double third = assumedDurationSec / 3.0;
        double peakAt = sensor.PeakPressureAtSec;

        return seg switch
        {
            UncomfortableSegment.Start  => peakAt < third + slack,
            UncomfortableSegment.Middle => peakAt > third - slack && peakAt < 2 * third + slack,
            UncomfortableSegment.End    => peakAt > 2 * third - slack,
            _                           => false,
        };
    }

    /// <summary>Repeat-calibration scale blending (spec §7): new × 0.4 + old × 0.6.
    /// Helper exposed for the caller to apply at the wrapper layer — the
    /// processor itself never sees the prior session, by design (keeps the
    /// pure-logic surface free of cross-session state).</summary>
    public static double BlendScaleWithPrior(double freshScale, double priorScale)
    {
        double blended = (freshScale * 0.4) + (priorScale * 0.6);
        return Math.Clamp(blended, PersonalPressureScaleMin, PersonalPressureScaleMax);
    }
}
