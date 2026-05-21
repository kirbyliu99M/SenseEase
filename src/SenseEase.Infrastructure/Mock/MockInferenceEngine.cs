using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Mock;

/// <summary>
/// Phase-1 inference engine: wraps the real <see cref="LeakyIntegrator"/> with
/// Driver/Passenger weighting, mask thresholding, and a cooldown so the mask does
/// not flicker around the trigger boundary.
/// </summary>
public sealed class MockInferenceEngine : IInferenceEngine
{
    private readonly LeakyIntegrator _integrator = new();
    private DateTime _lastMaskChange = DateTime.MinValue;
    private DateTime _lastUpdate     = DateTime.MinValue;
    private int _userFovOffset;   // accumulated from chatbot feedback, clamped ≤ 0
    private readonly ShaderParams _shader = ShaderParams.Default;
    private readonly object _gate = new();
    private readonly HabituationEngine _habituation = new();
    private readonly IAdjustmentLog? _adjustmentLog;
    private readonly IHabituationStore? _habituationStore;
    private double _personalizationAccumulator;
    private int _lastSavedOffset;
    private int      _vimsTier = 1;
    private int      _pendingTierCandidate = 1;
    private DateTime _pendingTierSinceUtc  = DateTime.MinValue;

    public SenseState Current { get; } = new()
    {
        FovRadiusPercent = 100,
        GridSizePx = SenseDefaults.GridSizePx,
        GridOpacity = SenseDefaults.GridOpacity,
        Mode = "Passenger",
        LastUpdatedAt = DateTime.Now,
    };

    /// <summary>
    /// <paramref name="adjustmentLog"/> drives habituation personalisation;
    /// <paramref name="habituationStore"/> persists habituation progress across restarts.
    /// Both optional so test code can still use the parameterless form.
    /// </summary>
    public MockInferenceEngine(IAdjustmentLog? adjustmentLog = null,
                               IHabituationStore? habituationStore = null)
    {
        _adjustmentLog = adjustmentLog;
        _habituationStore = habituationStore;
        _habituation.TimeScale = SenseDefaults.HabituationTimeScale;

        HabituationState? saved = habituationStore?.Load();
        if (saved is not null)
            _habituation.Restore(saved);
        if (adjustmentLog is not null)
            _habituation.SetPersonalization(adjustmentLog.WeightFor("FovRadius"));

        // Habituation defaults to PAUSED at startup — therapeutic relaxation requires
        // explicit user opt-in via the dashboard toggle. Persisted offset is still
        // restored (above) so resuming the engine picks up where the user left off.
        _habituation.SetPaused(true);

        _lastSavedOffset = _habituation.HabituationOffset;
        Current.HabituationLevel  = _habituation.HabituationLevel;
        Current.HabituationPaused = _habituation.IsPaused;
        Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);
    }

    public SenseState Update(double opticalFlowIndex, string mode, DateTime? sampledAt = null)
    {
        lock (_gate)
        {
            // Compute dt: real elapsed seconds when sampledAt is provided; 1.0 for test / unit-step.
            DateTime now = sampledAt ?? DateTime.UtcNow;
            double dt = (_lastUpdate == DateTime.MinValue || sampledAt == null)
                ? 1.0
                : Math.Clamp((now - _lastUpdate).TotalSeconds, 0.001, 2.0);
            _lastUpdate = now;

            bool isDriverMode = string.Equals(mode, "Driver", StringComparison.OrdinalIgnoreCase);

            // Driver mode attenuates passive-flow impact (does not hard-zero).
            double effectiveOfi = isDriverMode ? (opticalFlowIndex * 0.25) : opticalFlowIndex;

            // After mask activation, attenuate OFI to reduce ping-pong oscillation.
            if (!isDriverMode && Current.MaskActive)
            {
                double tightness = ComputeMaskTightness(Current.FovRadiusPercent);
                double attenuation = 0.45 - (tightness * 0.30); // 0.45 -> 0.15
                effectiveOfi *= Math.Clamp(attenuation, 0.15, 0.45);
            }

            // Passenger mode weighs passive optical flow more heavily than Driver mode.
            double weight = isDriverMode ? 1.0 : SenseDefaults.PassiveFlowWeight;
            double pressure = _integrator.Step(effectiveOfi, weight, dt);

            Current.OpticalFlowIndex = effectiveOfi;
            Current.Pressure = pressure;
            Current.Mode = mode;
            Current.FmsScore = (int)Math.Clamp(pressure / 10.0, 0, 20);

            // Hysteresis: trigger at 100, release at 85 to avoid sticky behavior/flicker.
            bool shouldMaskOn = pressure >= SenseDefaults.TriggerPressure;
            bool shouldMaskOff = pressure < (SenseDefaults.TriggerPressure * 0.85);
            bool cooldownPassed =
                (now - _lastMaskChange).TotalMilliseconds >= SenseDefaults.CooldownMs;

            if (!Current.MaskActive && shouldMaskOn && cooldownPassed)
            {
                Current.MaskActive = true;
                _lastMaskChange = now;
            }
            else if (Current.MaskActive && shouldMaskOff)
            {
                Current.MaskActive = false;
                _lastMaskChange = now;
            }

            // Habituation: gradually relax FOV after sustained calm (ticks under _gate).
            _habituation.Step(pressure, Current.MaskActive, dt);

            // Pressure-driven clear-zone radius + chatbot feedback offset + habituation relaxation.
            Current.FovRadiusPercent  = ComposeFovRadius(pressure);
            Current.HabituationLevel  = _habituation.HabituationLevel;
            Current.HabituationPaused = _habituation.IsPaused;

            RefreshPersonalization(dt);
            PersistHabituation();

            UpdateVimsTier(now);
            Current.VimsTier = _vimsTier;

            Current.Shader = _shader;
            Current.LastUpdatedAt = DateTime.Now;
            return Current;
        }
    }

    public void ApplyUserFeedback(int fovDelta)
    {
        lock (_gate)
        {
            _userFovOffset = Math.Clamp(_userFovOffset + fovDelta, -60, 0);
            // A negative (tightening) chatbot feedback is a discomfort recurrence:
            // back off habituation — severe reports zero it, mild reports nudge it down.
            if (fovDelta < 0)
                _habituation.NotifyDiscomfort(Math.Abs(fovDelta) >= SenseDefaults.HabituationSevereFovThreshold);
            // Refresh immediately so the dashboard/overlay reflect the change even
            // when the sensing loop is stopped (no Update() tick will arrive).
            Current.FovRadiusPercent  = ComposeFovRadius(Current.Pressure);
            Current.HabituationLevel  = _habituation.HabituationLevel;
            Current.HabituationPaused = _habituation.IsPaused;
            PersistHabituation();
        }
    }

    public void ApplyShaderAdjustment(double maskDepthDelta, double gridVisibilityDelta, double featherWidthDelta)
    {
        lock (_gate)
        {
            _shader.MaskDepth      += maskDepthDelta;
            _shader.GridVisibility += gridVisibilityDelta;
            _shader.FeatherWidth   += featherWidthDelta;
            _shader.Clamp();
        }
    }

    public void ResetPersonalization()
    {
        lock (_gate)
        {
            _userFovOffset = 0;
            _shader.ResetToDefault();
            _habituation.Reset();
            Current.HabituationLevel  = 0;
            Current.HabituationPaused = false;
            PersistHabituation(force: true);
            _vimsTier = 1;
            _pendingTierCandidate = 1;
            _pendingTierSinceUtc  = DateTime.MinValue;
        }
    }

    public void SetHabituationPaused(bool paused)
    {
        lock (_gate)
        {
            _habituation.SetPaused(paused);
            Current.HabituationPaused = _habituation.IsPaused;
        }
    }

    public int UserFovOffset { get { lock (_gate) { return _userFovOffset; } } }

    public ShaderParams BaselineShader => ShaderParams.Default;

    // Compose the final clear-zone radius from the three contributors and clamp to [15,100].
    // _userFovOffset is <= 0 (user wants more protection); HabituationOffset is >= 0 (engine
    // relaxes). They are summed once here so there is a single writer of FovRadiusPercent.
    private int ComposeFovRadius(double pressure)
        => Math.Clamp(ComputeFovRadiusPercent(pressure) + _userFovOffset + _habituation.HabituationOffset,
                      15, 100);

    // Periodically re-pull the user's FOV-adjustment history so habituation personalisation
    // stays fresh as the adjustment log grows. Throttled to once per step interval.
    private void RefreshPersonalization(double dt)
    {
        if (_adjustmentLog is null) return;
        _personalizationAccumulator += dt;
        if (_personalizationAccumulator < SenseDefaults.HabituationPersonalizationRefreshSeconds) return;
        _personalizationAccumulator = 0.0;
        _habituation.SetPersonalization(_adjustmentLog.WeightFor("FovRadius"));
    }

    // Persist habituation progress only when the earned offset actually changes,
    // unless force=true (e.g. after a reset where offset may already be 0).
    private void PersistHabituation(bool force = false)
    {
        if (_habituationStore is null) return;
        int offset = _habituation.HabituationOffset;
        if (!force && offset == _lastSavedOffset) return;
        _lastSavedOffset = offset;
        _habituationStore.Save(_habituation.Snapshot());
    }

    private static int ComputeFovRadiusPercent(double pressure)
    {
        double pre = Math.Clamp(pressure / SenseDefaults.TriggerPressure, 0.0, 1.0);
        if (pressure <= SenseDefaults.TriggerPressure)
        {
            // Linear: 100% (fully open at rest) → DefaultFovRadiusPercent (35%) at TriggerPressure
            double radius = 100.0 - (100.0 - SenseDefaults.DefaultFovRadiusPercent) * pre;
            return (int)Math.Round(radius);
        }

        int tightestRadius = SenseDefaults.TightestNaturalFovRadiusPercent;
        // Steeper post-trigger slope: saturate at 1.6× TriggerPressure (not 2.0×)
        // so realistic sustained OFI actually drives FOV to the floor.
        double post = Math.Clamp(
            (pressure - SenseDefaults.TriggerPressure) / (0.6 * SenseDefaults.TriggerPressure),
            0.0, 1.0);
        double activeRadius = SenseDefaults.DefaultFovRadiusPercent
            - ((SenseDefaults.DefaultFovRadiusPercent - tightestRadius) * post);
        return (int)Math.Round(activeRadius);
    }

    private static double ComputeMaskTightness(int fovRadiusPercent)
    {
        double span = Math.Max(1, SenseDefaults.OuterTransitionRadiusPercent - SenseDefaults.DefaultFovRadiusPercent);
        return Math.Clamp((SenseDefaults.OuterTransitionRadiusPercent - fovRadiusPercent) / span, 0.0, 1.0);
    }

    /// <summary>
    /// VIMS tier with level-space hysteresis + asymmetric debounce.
    /// Within a tier (and inside ±deadband of its boundaries) the displayed tier holds,
    /// eliminating per-tick mask flicker. A real sustained rise still confirms within
    /// VimsTierUpDebounceMs; falls are deliberately slower (VimsTierDownDebounceMs).
    ///
    /// Two upgrades over the original ±1-per-tick state machine:
    ///   1) Fast-onset bypass — if levelF outruns the current tier by ≥ VimsTierFastOnsetGap,
    ///      snap directly to round(levelF) without debounce. This eliminates the prior
    ///      1.8 s climb on genuine pressure surges (a tick can leap multiple levels because
    ///      WGC pixel-diff is bursty and the post-trigger slope saturates at 1.6× trigger).
    ///   2) Tiered release rate — high tiers (> VimsLowTierReleaseCutoff) keep the slow
    ///      VimsTierDownDebounceMs for comfort; lower tiers use VimsTierDownDebounceLowMs
    ///      so the mask actually clears when discomfort has passed.
    /// </summary>
    private void UpdateVimsTier(DateTime nowUtc)
    {
        double t      = Math.Clamp((100.0 - Current.FovRadiusPercent) / 85.0, 0.0, 1.0);
        double levelF = 1.0 + (9.0 * t); // 1..10

        // ── Fast-onset bypass ────────────────────────────────────────────────
        // A genuine pressure surge can jump levelF by several units in one Update tick.
        // The strict ±1/tick limiter would then need 1.8 s of debounce ticks to catch up
        // (longer than the natural pressure surge itself). When the gap exceeds
        // VimsTierFastOnsetGap we therefore snap straight to the nearest integer tier.
        double gap = levelF - _vimsTier;
        if (gap >= SenseDefaults.VimsTierFastOnsetGap)
        {
            int snap = Math.Clamp((int)Math.Round(levelF), _vimsTier + 1, 10);
            _vimsTier             = snap;
            _pendingTierCandidate = snap;
            _pendingTierSinceUtc  = DateTime.MinValue;
            return;
        }

        // Boundaries with deadband. UP at currentTier + 0.5 + deadband; DOWN at currentTier - 0.5 - deadband.
        double upBoundary   = _vimsTier + 0.5 + SenseDefaults.VimsTierDeadband;
        double downBoundary = _vimsTier - 0.5 - SenseDefaults.VimsTierDeadband;

        int candidate;
        int debounceMs;
        if (levelF >= upBoundary)
        {
            candidate  = Math.Min(10, _vimsTier + 1);
            debounceMs = SenseDefaults.VimsTierUpDebounceMs;
        }
        else if (levelF <= downBoundary)
        {
            candidate  = Math.Max(1, _vimsTier - 1);
            // Tiered release rate: slow at the dangerous top, brisk at low tiers.
            // `candidate` is the tier we are dropping INTO — once it is ≤ cutoff, we have
            // already left the high-comfort-risk region and can clear faster.
            debounceMs = candidate <= SenseDefaults.VimsLowTierReleaseCutoff
                ? SenseDefaults.VimsTierDownDebounceLowMs
                : SenseDefaults.VimsTierDownDebounceMs;
        }
        else
        {
            // Inside the held tier — reset any pending candidate; nothing changes.
            _pendingTierCandidate = _vimsTier;
            _pendingTierSinceUtc  = DateTime.MinValue;
            return;
        }

        // Track the pending transition; commit only if it stays past the debounce window.
        if (candidate != _pendingTierCandidate)
        {
            _pendingTierCandidate = candidate;
            _pendingTierSinceUtc  = nowUtc;
            return;
        }
        if (_pendingTierSinceUtc == DateTime.MinValue)
        {
            _pendingTierSinceUtc = nowUtc;
            return;
        }
        if ((nowUtc - _pendingTierSinceUtc).TotalMilliseconds >= debounceMs)
        {
            _vimsTier = candidate;
            _pendingTierCandidate = candidate;
            _pendingTierSinceUtc  = DateTime.MinValue;
        }
    }
}
