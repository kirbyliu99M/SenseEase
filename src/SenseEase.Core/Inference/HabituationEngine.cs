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
/// NOT thread-safe: the owning inference engine must serialise access.
/// </summary>
public sealed class HabituationEngine
{
    private double _stableElapsed;    // contiguous calm time credited toward the window
    private double _stepAccumulator;  // time banked toward the next relaxation step
    private double _offset;           // current FOV relaxation, [0, HabituationMaxOffset]
    private double _pauseCooldown;    // remaining forced-pause seconds after a recurrence
    private bool   _externallyPaused; // user-set pause from the dashboard
    private HabituationPhase _phase = HabituationPhase.Observing;

    // Personalisation: linear interpolation by `caution` in [0,1] between the
    // nominal and the cautious-user bounds.
    private double _effectiveWindow       = SenseDefaults.HabituationStableWindowSeconds;
    private double _effectiveStepInterval = SenseDefaults.HabituationStepIntervalSeconds;

    /// <summary>Debug accelerator for the demo loop; 1.0 = real time. Tests leave it at 1.0.</summary>
    public double TimeScale { get; set; } = 1.0;

    /// <summary>Current FOV relaxation in percent points, [0, HabituationMaxOffset].</summary>
    public int HabituationOffset => (int)Math.Round(_offset);

    /// <summary>Progress toward full re-habituation, 0–100 (offset as a fraction of the cap).</summary>
    public int HabituationLevel =>
        (int)Math.Round(100.0 * _offset / SenseDefaults.HabituationMaxOffset);

    /// <summary>True when habituation is paused (forced cooldown or manual pause).</summary>
    public bool IsPaused => _externallyPaused || _pauseCooldown > 0.0;

    /// <summary>Current lifecycle phase.</summary>
    public HabituationPhase Phase => _phase;

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
                   && _offset < SenseDefaults.HabituationMaxOffset)
            {
                _stepAccumulator -= _effectiveStepInterval;
                _offset = Math.Min(SenseDefaults.HabituationMaxOffset,
                                   _offset + SenseDefaults.HabituationStepPercent);
            }
            if (_offset >= SenseDefaults.HabituationMaxOffset)
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
    /// </summary>
    public void SetPersonalization(PersonalWeight fovHistory)
    {
        double rawCaution = Math.Clamp(
            -fovHistory.MeanDelta / SenseDefaults.HabituationCautionMeanDeltaDivisor,
            0.0, 1.0);
        double confidence = Math.Clamp(
            fovHistory.Count / (double)SenseDefaults.HabituationPersonalizationMinSamples,
            0.0, 1.0);
        double caution = rawCaution * confidence;

        _effectiveWindow = SenseDefaults.HabituationStableWindowSeconds
            + (caution * (SenseDefaults.HabituationMaxWindowSeconds
                          - SenseDefaults.HabituationStableWindowSeconds));
        _effectiveStepInterval = SenseDefaults.HabituationStepIntervalSeconds
            + (caution * (SenseDefaults.HabituationMaxStepIntervalSeconds
                          - SenseDefaults.HabituationStepIntervalSeconds));
    }

    /// <summary>Set or clear the user's manual pause.</summary>
    public void SetPaused(bool paused) => _externallyPaused = paused;

    /// <summary>
    /// Restore persisted progress (the earned offset). Stability counters restart so
    /// calm must be re-confirmed before the next relaxation step.
    /// </summary>
    public void Restore(HabituationState state)
    {
        _offset = Math.Clamp(state.Offset, 0.0, SenseDefaults.HabituationMaxOffset);
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

    /// <summary>Reset all habituation state to a fresh, neutral baseline.</summary>
    public void Reset()
    {
        _stableElapsed = 0.0;
        _stepAccumulator = 0.0;
        _offset = 0.0;
        _pauseCooldown = 0.0;
        _externallyPaused = false;
        _phase = HabituationPhase.Observing;
        _effectiveWindow = SenseDefaults.HabituationStableWindowSeconds;
        _effectiveStepInterval = SenseDefaults.HabituationStepIntervalSeconds;
    }

    private void EnterPause()
    {
        _stableElapsed = 0.0;
        _stepAccumulator = 0.0;
        _pauseCooldown = SenseDefaults.HabituationPauseCooldownSeconds;
        _phase = HabituationPhase.Paused;
    }
}
