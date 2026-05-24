using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

/// <summary>
/// Therapeutic graduated-desensitisation engine. After the user stays calm for a
/// sustained, contiguous window, it slowly widens the FOV (a positive offset) so the
/// user re-habituates to normal visual stimulus instead of depending on the overlay.
/// A discomfort recurrence immediately backs it off and pauses it.
/// Pure, deterministic logic — all time enters via the dtSeconds parameter.
/// NOT thread-safe: the owning inference engine must serialise access. SetProfile
/// shares that contract — caller must hold the same gate.
///
/// Phase 2 rewire: the engine now reads its tuning constants from a
/// <see cref="HabituationProfileParams"/> instance instead of static
/// <see cref="SenseDefaults"/> fields. The default profile is RESILIENT, whose
/// numerics match the legacy SenseDefaults values exactly — so every existing
/// HabituationTests case passes one-line-unchanged. TYPICAL and SENSITIVE
/// stratification arrives via SetProfile from the calibration demo (Phase 5)
/// or chatbot override (Phase 7).
/// </summary>
public sealed class HabituationEngine
{
    private double _stableElapsed;    // contiguous calm time credited toward the window
    private double _stepAccumulator;  // time banked toward the next relaxation step
    private double _offset;           // current FOV relaxation, [0, _params.HabOffsetCapPp]
    private double _pauseCooldown;    // remaining forced-pause seconds after a recurrence
    private bool   _externallyPaused; // user-set pause from the dashboard
    private HabituationPhase _phase = HabituationPhase.Observing;

    // Active profile. Default is RESILIENT (matches legacy SenseDefaults
    // numerics) — see docs/HABITUATION-PROFILE.md §1.1 for rationale.
    private HabituationProfileParams _params = HabituationProfileParams.Resilient;
    private HabituationProfile       _currentProfile = HabituationProfile.Resilient;
    private string                   _profileSource  = "default";

    // Personalisation: linear interpolation by `caution` in [0,1] between the
    // profile's nominal window and the cautious-user upper bound (2× per
    // Q1 rule, see docs/HABITUATION-PROFILE.md §1.1). StepInterval ceiling
    // remains the legacy SenseDefaults value — it's profile-independent in
    // Phase 2 and can be promoted into the profile later if needed.
    private double _effectiveWindow;
    private double _effectiveStepInterval;

    public HabituationEngine()
    {
        _effectiveWindow       = _params.StableWindowSeconds;
        _effectiveStepInterval = _params.StepIntervalSeconds;
    }

    /// <summary>Debug accelerator for the demo loop; 1.0 = real time. Tests leave it at 1.0.</summary>
    public double TimeScale { get; set; } = 1.0;

    /// <summary>Current FOV relaxation in percent points, [0, _params.HabOffsetCapPp].</summary>
    public int HabituationOffset => (int)Math.Round(_offset);

    /// <summary>Progress toward full re-habituation, 0–100 (offset as a fraction of the cap).</summary>
    public int HabituationLevel =>
        (int)Math.Round(100.0 * _offset / _params.HabOffsetCapPp);

    /// <summary>True when habituation is paused (forced cooldown or manual pause).</summary>
    public bool IsPaused => _externallyPaused || _pauseCooldown > 0.0;

    /// <summary>Current lifecycle phase.</summary>
    public HabituationPhase Phase => _phase;

    /// <summary>Active reactivity profile. Default RESILIENT; switched by
    /// calibration demo or chatbot override via <see cref="SetProfile"/>.</summary>
    public HabituationProfile CurrentProfile => _currentProfile;

    /// <summary>Origin of the current profile setting. One of
    /// <c>"default"</c>, <c>"calibration_demo"</c>, <c>"chatbot_override"</c>.
    /// Surfaced in the Phase 4 JSON snapshot.</summary>
    public string ProfileSource => _profileSource;

    /// <summary>Raised whenever <see cref="SetProfile"/> changes the active
    /// profile. Phase 4's FileHabituationProfileStore consumer subscribes
    /// here to persist the new snapshot atomically.</summary>
    public event Action<HabituationProfile, string>? ProfileChanged;

    /// <summary>Swap the active profile. Caller (typically the owning
    /// <see cref="SenseEase.Infrastructure.Mock.MockInferenceEngine"/>) must
    /// serialise this against <see cref="Step"/> via the same gate.
    /// <para>Profile change resets <c>_effectiveWindow</c> /
    /// <c>_effectiveStepInterval</c> to the new baseline; the next
    /// <see cref="SetPersonalization"/> tick re-applies caution on top.
    /// The accumulated <c>_offset</c> is preserved (long-term progress
    /// survives a profile shift per Q6).</para></summary>
    public void SetProfile(HabituationProfile profile, string source = "default")
    {
        if (profile == _currentProfile && source == _profileSource) return;

        _params = HabituationProfileParams.For(profile);
        _currentProfile = profile;
        _profileSource  = source;

        // Re-baseline caution-derived state. Don't touch _offset —
        // earned habituation progress persists across profile changes.
        _effectiveWindow       = _params.StableWindowSeconds;
        _effectiveStepInterval = _params.StepIntervalSeconds;

        // Re-clamp _offset in case the new cap is lower than the old one
        // (e.g. RESILIENT → SENSITIVE drops cap from 30 to 15).
        if (_offset > _params.HabOffsetCapPp)
            _offset = _params.HabOffsetCapPp;

        ProfileChanged?.Invoke(profile, source);
    }

    /// <summary>
    /// Advance one tick. <paramref name="dtSeconds"/> is the owning engine's real
    /// elapsed dt; <paramref name="pressure"/> and <paramref name="maskActive"/> are
    /// the current inference state.
    /// </summary>
    public void Step(double pressure, bool maskActive, double dtSeconds)
    {
        if (dtSeconds <= 0.0) return;
        double dt = dtSeconds * TimeScale;

        if (_pauseCooldown > 0.0)
            _pauseCooldown = Math.Max(0.0, _pauseCooldown - dt);

        double trigger = SenseDefaults.TriggerPressure;

        // Pressure-spike recurrence: back off and pause immediately.
        // Phase 3 will replace this branch with the strike-counter logic
        // (halve up to SevereStrikeLimit, then zero + reset).
        if (pressure >= trigger * SenseDefaults.HabituationRecurrencePressureFraction)
        {
            _offset = Math.Max(0.0, _offset * SenseDefaults.HabituationSpikeBackoffMultiplier);
            EnterPause();
            return;
        }

        if (_externallyPaused || _pauseCooldown > 0.0)
        {
            _phase = HabituationPhase.Paused;
            return;
        }

        // Stability accounting. Calm credit accrues only while genuinely calm AND the
        // protective mask is not engaged; otherwise the accumulated credit decays
        // (so non-contiguous calm cannot ratchet up to a relaxation).
        bool calm = pressure < trigger * SenseDefaults.HabituationStablePressureFraction
                    && !maskActive;
        if (calm)
            _stableElapsed = Math.Min(_effectiveWindow, _stableElapsed + dt);
        else
            _stableElapsed = Math.Max(0.0,
                _stableElapsed - (dt * SenseDefaults.HabituationMidBandDecayRate));

        if (_stableElapsed >= _effectiveWindow)
        {
            _phase = HabituationPhase.Relaxing;
            _stepAccumulator += dt;
            while (_stepAccumulator >= _effectiveStepInterval
                   && _offset < _params.HabOffsetCapPp)
            {
                _stepAccumulator -= _effectiveStepInterval;
                _offset = Math.Min(_params.HabOffsetCapPp,
                                   _offset + _params.StepMagnitudePp);
            }
            if (_offset >= _params.HabOffsetCapPp)
                _stepAccumulator = 0.0; // do not bank time once capped
        }
        else
        {
            _phase = HabituationPhase.Observing;
        }
    }

    /// <summary>
    /// Report a chatbot-detected discomfort. A severe report zeroes the offset; a mild
    /// report applies a small back-off. Both pause habituation and restart the window.
    /// Phase 3 will fold severe reports into the strike counter (weighted 0.5 per Q4).
    /// </summary>
    public void NotifyDiscomfort(bool severe)
    {
        _offset = severe
            ? 0.0
            : Math.Max(0.0, _offset - SenseDefaults.HabituationMildBackoffPercent);
        EnterPause();
    }

    /// <summary>
    /// Update per-user personalisation from FOV-adjustment history. A user who
    /// habitually tightens (negative MeanDelta) habituates more gently: a longer
    /// observation window and a slower relaxation cadence.
    /// <para>Ceiling rules (per Q1): StableWindow caps at 2× the profile floor
    /// (e.g. RESILIENT 120s → 240s cap; SENSITIVE 360s → 720s cap).
    /// StepInterval keeps the legacy <see cref="SenseDefaults.HabituationMaxStepIntervalSeconds"/>
    /// ceiling for Phase 2 — profile-independent, can be promoted later.</para>
    /// </summary>
    public void SetPersonalization(PersonalWeight fovHistory)
    {
        double rawCaution = Math.Clamp(
            -fovHistory.MeanDelta / SenseDefaults.HabituationCautionMeanDeltaDivisor,
            0.0, 1.0);
        double confidence = Math.Clamp(
            fovHistory.Count / (double)_params.MinSamplesForFullWeight,
            0.0, 1.0);
        double caution = rawCaution * confidence;

        double windowFloor   = _params.StableWindowSeconds;
        double windowCeiling = _params.StableWindowSeconds * 2.0;
        _effectiveWindow = windowFloor + (caution * (windowCeiling - windowFloor));

        double stepFloor    = _params.StepIntervalSeconds;
        double stepCeiling  = SenseDefaults.HabituationMaxStepIntervalSeconds;
        // Clamp the ceiling above the floor in case a profile with a high
        // StepIntervalSeconds (e.g. SENSITIVE 60s vs legacy ceiling 75s) still
        // leaves room to scale up. If the ceiling has been overshot by the
        // floor, no caution-scaling applies.
        if (stepCeiling <= stepFloor) stepCeiling = stepFloor;
        _effectiveStepInterval = stepFloor + (caution * (stepCeiling - stepFloor));
    }

    /// <summary>Set or clear the user's manual pause.</summary>
    public void SetPaused(bool paused) => _externallyPaused = paused;

    /// <summary>
    /// Restore persisted progress (the earned offset). Stability counters restart so
    /// calm must be re-confirmed before the next relaxation step.
    /// </summary>
    public void Restore(HabituationState state)
    {
        _offset = Math.Clamp(state.Offset, 0.0, _params.HabOffsetCapPp);
        _stableElapsed = 0.0;
        _stepAccumulator = 0.0;
        _pauseCooldown = 0.0;
        _phase = HabituationPhase.Observing;
    }

    /// <summary>Snapshot the persistable progress.</summary>
    public HabituationState Snapshot() => new()
    {
        Offset = _offset,
        SavedAtUtc = DateTime.UtcNow,
    };

    /// <summary>Reset all habituation state to a fresh, neutral baseline.
    /// Does NOT reset the active profile — call <see cref="SetProfile"/> if you
    /// also want to clear the profile back to RESILIENT.</summary>
    public void Reset()
    {
        _stableElapsed = 0.0;
        _stepAccumulator = 0.0;
        _offset = 0.0;
        _pauseCooldown = 0.0;
        _externallyPaused = false;
        _phase = HabituationPhase.Observing;
        _effectiveWindow       = _params.StableWindowSeconds;
        _effectiveStepInterval = _params.StepIntervalSeconds;
    }

    private void EnterPause()
    {
        _stableElapsed = 0.0;
        _stepAccumulator = 0.0;
        // Tier-aware pause length lands in Phase 3 (Step gets a vimsTier
        // parameter, GetPostSpikePauseMs picks low/mid/-1 + requireConfirm).
        // For Phase 2 we use the profile's low-tier pause as the constant
        // fallback — for RESILIENT this is 90s, identical to the legacy
        // SenseDefaults.HabituationPauseCooldownSeconds.
        _pauseCooldown = _params.PostSpikePauseLowSeconds;
        _phase = HabituationPhase.Paused;
    }
}
