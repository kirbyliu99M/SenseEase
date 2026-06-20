using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

/// <summary>Engine-state projection consumed by the persistence layer and the
/// chatbot prompt. Computed (not stored) from internal flags — see
/// <see cref="HabituationEngine.CurrentEngineState"/>. Parallel to (does not
/// replace) <see cref="HabituationPhase"/>, which UI keeps reading; the two
/// enums are two views of the same underlying state.</summary>
public enum EngineState
{
    /// <summary>Engine is observing or relaxing — no spike-pause active.</summary>
    Running,

    /// <summary>Engine is in the auto-recovery cooldown after a low/mid-tier
    /// spike. Will resume automatically when <c>_pauseCooldown</c> elapses.</summary>
    Cooldown,

    /// <summary>Engine is paused after a tier ≥ 8 spike. Will NOT auto-resume;
    /// the user (typically routed via the chatbot) must call
    /// <see cref="HabituationEngine.AcknowledgeAndResume"/> to continue.</summary>
    PausedAwaitingUserConfirm,
}

/// <summary>
/// Therapeutic graduated-desensitisation engine. After the user stays calm for a
/// sustained, contiguous window, it slowly widens the FOV (a positive offset) so the
/// user re-habituates to normal visual stimulus instead of depending on the overlay.
/// A discomfort recurrence immediately backs it off and pauses it.
/// Pure, deterministic logic — all time enters via the dtSeconds parameter.
/// NOT thread-safe: the owning inference engine must serialise access. SetProfile,
/// SetPaused, NotifyDiscomfort, and AcknowledgeAndResume share that contract —
/// caller holds the same gate.
///
/// Phase 3 introduces:
///  - Tier-aware spike handling via the new <see cref="Step"/> signature (4-arg)
///    and <see cref="HabituationProfileParams.GetPostSpikePauseMs"/>.
///  - Severe-strike counter with halve-up-to-limit-then-zero logic. Chatbot-
///    derived severe events are weighted 0.5 (per Q4); sensor-derived spikes
///    are weighted 1.0.
///  - <see cref="EngineState.PausedAwaitingUserConfirm"/> for tier ≥ 8 — engine
///    parks indefinitely until <see cref="AcknowledgeAndResume"/>.
///  - <see cref="EngineState"/> computed projection for chatbot + persistence.
/// </summary>
public sealed class HabituationEngine
{
    // Floating-point epsilon for strike-limit comparison. Chatbot weight is
    // 0.5 (per Q4); 4 × 0.5 == 2.0 holds bitwise on IEEE 754 today. The 1e-9
    // tolerance protects against future weight adjustments (e.g. 0.33, 0.66)
    // where exact equality would fail. Do NOT remove without re-validating
    // all weight × limit combinations — this is defensive-by-design, not
    // an accidental tolerance.
    private const double StrikeLimitEpsilon = 1e-9;

    private double _stableElapsed;    // contiguous calm time credited toward the window
    private double _stepAccumulator;  // time banked toward the next relaxation step
    private double _offset;           // current FOV relaxation, [0, _params.HabOffsetCapPp]
    private double _pauseCooldown;    // remaining forced-pause seconds; PositiveInfinity when awaiting user confirm
    private bool   _externallyPaused; // user-set pause from the dashboard
    private bool   _awaitingUserConfirm; // tier ≥ 8 spike — engine refuses to auto-resume
    private double _severeStrikeCount;   // weighted; sensor=1.0, chatbot=0.5 (Q4)
    private DateTime? _lastSpikeAtUtc;   // populated by RegisterSevereStrike; surfaced in persistence snapshot
    private bool _pressureWasAtRecurrence; // EM1: was pressure >= recurrence threshold on the previous tick? (edge-detect)
    // _lastSeenTier defaults to 1 (most conservative). This is INTENTIONAL,
    // not a placeholder. Callers using the no-tier NotifyDiscomfort(bool)
    // overload effectively get tier-1 behaviour — appropriate for legacy
    // paths and tests that don't simulate the upstream pressure-tier
    // pipeline. Production paths must use the explicit
    // NotifyDiscomfort(bool, int) overload (MockInferenceEngine does).
    private int    _lastSeenTier = 1;    // updated each Step call; consumed by NotifyDiscomfort default path
    private HabituationPhase _phase = HabituationPhase.Observing;

    // Active profile. Default is RESILIENT (matches legacy SenseDefaults
    // numerics) — see docs/HABITUATION-PROFILE.md §1.1 for rationale.
    private HabituationProfileParams _params = HabituationProfileParams.Resilient;
    private HabituationProfile       _currentProfile = HabituationProfile.Resilient;
    private string                   _profileSource  = "default";

    // Personalisation: linear interpolation by `caution` in [0,1] between the
    // profile's nominal window and the cautious-user upper bound (2× per Q1).
    // StepInterval ceiling remains the legacy SenseDefaults value — profile-
    // independent in Phase 2, may be promoted into the profile later.
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

    /// <summary>True when habituation is paused (forced cooldown OR awaiting user confirm OR manual pause).</summary>
    public bool IsPaused => _externallyPaused || _pauseCooldown > 0.0 || _awaitingUserConfirm;

    /// <summary>True iff the engine is in <see cref="EngineState.PausedAwaitingUserConfirm"/>.</summary>
    public bool IsAwaitingUserConfirm => _awaitingUserConfirm;

    /// <summary>Current weighted strike count. Sensor spikes contribute 1.0;
    /// chatbot severe reports contribute 0.5. Zeros when the limit is reached
    /// (offset zeros at the same time) OR when a full stable-window passes
    /// OR when the profile changes (Q6).</summary>
    public double SevereStrikeCount => _severeStrikeCount;

    /// <summary>Current lifecycle phase. UI projection — kept stable for the
    /// dashboard's habituation widget. See <see cref="CurrentEngineState"/>
    /// for the chatbot / persistence projection.</summary>
    public HabituationPhase Phase => _phase;

    /// <summary>Active reactivity profile. Default RESILIENT; switched by
    /// calibration demo or chatbot override via <see cref="SetProfile"/>.</summary>
    public HabituationProfile CurrentProfile => _currentProfile;

    /// <summary>Origin of the current profile setting. One of
    /// <c>"default"</c>, <c>"calibration_demo"</c>, <c>"chatbot_override"</c>.
    /// Surfaced in the Phase 4 JSON snapshot.</summary>
    public string ProfileSource => _profileSource;

    /// <summary>Computed projection of internal state — does NOT replace
    /// <see cref="Phase"/>. The two enums are different views of the same
    /// underlying flags; storing both separately would invite drift.</summary>
    public EngineState CurrentEngineState
    {
        get
        {
            // Per Q8 (modified): externally_paused does NOT mask Cooldown —
            // it's surfaced as a parallel boolean elsewhere (Phase 4 JSON).
            // The chatbot needs to see the underlying engine state even when
            // the user has manually paused.
            if (_awaitingUserConfirm) return EngineState.PausedAwaitingUserConfirm;
            if (_pauseCooldown > 0.0)  return EngineState.Cooldown;
            return EngineState.Running;
        }
    }

    /// <summary>Mirror of <c>_externallyPaused</c> for the Phase 4 JSON
    /// snapshot's parallel <c>externally_paused</c> field (Q8 modified).</summary>
    public bool IsExternallyPaused => _externallyPaused;

    /// <summary>UTC of the most recent severe-strike event (sensor or chatbot).
    /// Null when no spike has ever fired. Surfaced in the Phase 4 JSON
    /// snapshot's <c>last_spike_at_iso</c> field.</summary>
    public DateTime? LastSpikeAtUtc => _lastSpikeAtUtc;

    /// <summary>Active profile's parameter set. Exposed for the persistence
    /// snapshot's <c>params_snapshot</c> sub-record and any chatbot prompt
    /// path that needs cadence numbers without re-resolving the enum.</summary>
    public HabituationProfileParams CurrentParams => _params;

    // ── Personal-calibration trigger overrides (Stage 2 of calibration feature) ──
    // The engine is INTENTIONALLY ignorant of "personal_pressure_scale" as a
    // concept. It only sees an effective trigger value — the wrapper computes
    // it (defaultTrigger / scale, or a direct override) and pushes it here.
    // Keeps the engine's "three canonical profiles" mental model clean: a
    // SENSITIVE engine with EffectiveTrigger = 50 is still semantically a
    // SENSITIVE engine, just with a per-user threshold.
    //
    // Math direction is "divide trigger" (not "multiply pressure"). Sensor
    // readings stay pristine in raw form; only the threshold the user must
    // cross to trigger a spike moves per user. This matters when raw pressure
    // gets logged or referenced by the chatbot — multiplied pressure would
    // mislead every downstream consumer.

    /// <summary>The pressure threshold this engine uses for spike-recurrence
    /// detection and the calm-pressure fraction comparison. Defaults to
    /// <see cref="SenseDefaults.TriggerPressure"/>. Calibration overrides this
    /// via <c>MockInferenceEngine.ApplyCalibrationResult</c>; the wrapper
    /// computes the value, this property just stores + serves it.</summary>
    public double EffectiveTrigger { get; set; } = SenseDefaults.TriggerPressure;

    /// <summary>Optional override for <see cref="HabituationProfileParams.SevereStrikeLimit"/>.
    /// When non-null, <see cref="RegisterSevereStrike"/> compares the strike
    /// counter against THIS value instead of the profile's default. Set by
    /// the calibration result for the vestibular-diagnosed branch (= 1).
    /// Null = use the profile default. Like <see cref="EffectiveTrigger"/>,
    /// the engine just stores + serves; the wrapper decides when to set it.</summary>
    public int? SevereStrikeLimitOverride { get; set; }

    /// <summary>Raised whenever <see cref="SetProfile"/> changes the active
    /// profile. Phase 4's FileHabituationProfileStore consumer subscribes
    /// here to persist the new snapshot atomically.</summary>
    public event Action<HabituationProfile, string>? ProfileChanged;

    /// <summary>Swap the active profile. Caller (typically the owning
    /// <see cref="SenseEase.Infrastructure.Mock.MockInferenceEngine"/>) must
    /// serialise this against <see cref="Step"/> via the same gate.
    /// <para>Behaviour per Q6 + Phase 3 supplement #2:</para>
    /// <list type="bullet">
    ///   <item><c>_severeStrikeCount</c> is RESET (strikes are profile-scoped).</item>
    ///   <item><c>_offset</c> is PRESERVED (long-term habituation progress
    ///         survives a profile shift), but re-clamped if the new cap is lower.</item>
    ///   <item><c>_awaitingUserConfirm</c> is NOT cleared. Pause is a CURRENT
    ///         safety requirement orthogonal to strike history. A user who
    ///         needs to confirm before resuming still needs to confirm; the
    ///         profile of WHAT they'll resume INTO is what changed.</item>
    ///   <item><c>_pauseCooldown</c> is NOT cleared either — same reason.</item>
    ///   <item><c>_effectiveWindow</c> / <c>_effectiveStepInterval</c> rebase to
    ///         the new profile floor; SetPersonalization re-applies caution
    ///         on the next adjustment-log tick.</item>
    /// </list></summary>
    public void SetProfile(HabituationProfile profile, string source = "default")
    {
        if (profile == _currentProfile && source == _profileSource) return;

        _params = HabituationProfileParams.For(profile);
        _currentProfile = profile;
        _profileSource  = source;

        // Strikes reset — profile-scoped per Q6.
        _severeStrikeCount = 0.0;

        // Re-baseline caution-derived state. Don't touch _offset (Q6) and DO
        // NOT touch _awaitingUserConfirm / _pauseCooldown (supplement #2).
        _effectiveWindow       = _params.StableWindowSeconds;
        _effectiveStepInterval = _params.StepIntervalSeconds;

        if (_offset > _params.HabOffsetCapPp)
            _offset = _params.HabOffsetCapPp;

        ProfileChanged?.Invoke(profile, source);
    }

    /// <summary>B6 Stage 4b — AUTO bidirectional elasticity profile move. Distinct from
    /// <see cref="SetProfile"/> on three points, all to keep a self-correcting classifier safe:
    /// <list type="bullet">
    ///   <item>It TARES (does NOT zero) the in-flight strike count, so a relax-direction move cannot wipe
    ///         near-threshold strike evidence (SetProfile zeros it — correct for a deliberate switch).</item>
    ///   <item>It does NOT hard-clamp <c>_offset</c> to a lower cap; the unconditional cap-bleed near the top
    ///         of <see cref="Step"/> converges an over-cap offset gradually instead of truncating earned
    ///         progress (so a noisy classifier can't ratchet habituation down).</item>
    ///   <item>It re-checks the tared count against the NEW profile's limit (honoring
    ///         <see cref="SevereStrikeLimitOverride"/> + the epsilon, exactly as
    ///         <see cref="RegisterSevereStrike"/>), zeroing the offset+count if already at/over the new limit.</item>
    /// </list>
    /// The caller (<c>MockInferenceEngine.ApplyGateOutcome</c>) holds the gate AND enforces the vestibular
    /// hold — this method is never invoked for a vestibular user. Does NOT touch <c>_awaitingUserConfirm</c>,
    /// <c>_pauseCooldown</c>, <c>_externallyPaused</c>, <c>_stableElapsed</c>, <c>_stepAccumulator</c>,
    /// <c>_phase</c>, or <see cref="EffectiveTrigger"/>.</summary>
    public void AutoReclassifyProfile(HabituationProfile target, string source = "auto_elasticity")
    {
        if (target == _currentProfile && source == _profileSource) return;   // no-op parity (cf. SetProfile)

        _params         = HabituationProfileParams.For(target);
        _currentProfile = target;
        _profileSource  = source;
        // SetProfile zeros _severeStrikeCount here; this AUTO path TARES-NOT-ZEROS it on purpose.

        _effectiveWindow       = _params.StableWindowSeconds;
        _effectiveStepInterval = _params.StepIntervalSeconds;

        // Re-check the tared count against the NEW limit — same comparison as RegisterSevereStrike
        // (override + epsilon). This is the ONLY place this method writes _offset.
        double effectiveLimit = SevereStrikeLimitOverride ?? _params.SevereStrikeLimit;
        if (_severeStrikeCount >= effectiveLimit - StrikeLimitEpsilon)
        {
            _offset = 0.0;
            _severeStrikeCount = 0.0;
        }
        // NO hard clamp (cf. SetProfile's `if (_offset > cap) _offset = cap`): an over-cap _offset is bled
        // toward the cap by Step()'s unconditional cap-bleed, never truncated.

        ProfileChanged?.Invoke(target, source);
    }

    /// <summary>Resume from <see cref="EngineState.PausedAwaitingUserConfirm"/>.
    /// No-op if the engine is not in that state. Clears the await flag and
    /// the (infinite) cooldown; phase returns to Observing. The user / chatbot
    /// path is responsible for confirming the user is genuinely ready —
    /// the engine has no way to verify.</summary>
    public void AcknowledgeAndResume()
    {
        if (!_awaitingUserConfirm) return;
        _awaitingUserConfirm = false;
        _pauseCooldown = 0.0;
        _stableElapsed = 0.0;
        _stepAccumulator = 0.0;
        _phase = HabituationPhase.Observing;
    }

    /// <summary>
    /// Advance one tick. <paramref name="dtSeconds"/> is the owning engine's real
    /// elapsed dt; <paramref name="pressure"/> and <paramref name="maskActive"/> are
    /// the current inference state; <paramref name="vimsTier"/> is the upstream
    /// inference-engine's current VIMS tier (1–10), used to scale post-spike
    /// pause length and gate the tier ≥ 8 user-confirm path.
    /// </summary>
    public void Step(double pressure, bool maskActive, int vimsTier, double dtSeconds)
    {
        if (dtSeconds <= 0.0) return;
        _lastSeenTier = vimsTier;
        double dt = dtSeconds * TimeScale;

        // Infinite cooldown (awaiting user confirm) never ticks down here.
        if (_pauseCooldown > 0.0 && !_awaitingUserConfirm)
            _pauseCooldown = Math.Max(0.0, _pauseCooldown - dt);

        // Use the per-user effective trigger (defaults to
        // SenseDefaults.TriggerPressure when no calibration has applied an
        // override / scale). Engine doesn't know whether this value is the
        // OS default or a calibration-derived per-user number — that's the
        // wrapper's concern.
        double trigger = EffectiveTrigger;
        bool atRecurrence = pressure >= trigger * SenseDefaults.HabituationRecurrencePressureFraction;

        // While paused / cooling down, the pause IS the active safety state: do
        // not accrue strikes or stability. (EM1: this guard now precedes the
        // recurrence check, so an active pause genuinely suppresses re-strikes —
        // the behaviour the whitepaper already claims.) Keep the edge tracker
        // current so the pause lifting on still-high pressure is not mistaken for
        // a fresh rising edge.
        if (_externallyPaused || _pauseCooldown > 0.0 || _awaitingUserConfirm)
        {
            _phase = HabituationPhase.Paused;
            _pressureWasAtRecurrence = atRecurrence;
            return;
        }

        // B6 Stage 4b: lazy convergence after an AUTO down-move (AutoReclassifyProfile) deliberately left
        // _offset above the lowered cap. Explicit/calibration SetProfile hard-clamps on the flip, so this
        // only ever bleeds the auto-deferred case. Gradual (one StepMagnitudePp/tick), never an instant
        // truncate, so a noisy classifier cannot ratchet earned habituation down. Placed BEFORE the
        // recurrence/stability branches (which can early-return) so it is calm-INDEPENDENT — a worsening
        // user, the only one who triggers an auto DOWN, still converges to the new cap.
        if (_offset > _params.HabOffsetCapPp)
            _offset = Math.Max(_params.HabOffsetCapPp, _offset - _params.StepMagnitudePp);

        // Pressure-spike recurrence: sensor-derived severe event (weight 1.0).
        // EM1 — EDGE-triggered: fire only on the rising crossing of the recurrence
        // threshold, NOT every tick the level stays high. Level-triggering fired
        // ~15x/sec during ordinary video playback in Passenger mode, storming the
        // strike counter, the event log, and the profile-snapshot disk writes,
        // and shredding _offset via the repeated back-off.
        // B5 — No separate SevereDebounceMs constant by design: severe strikes are
        // de-facto debounced by _pauseCooldown. RegisterSevereStrike -> EnterPause
        // sets _pauseCooldown (a finite post-spike pause, or infinite while awaiting
        // user confirm), and the guard at the top of Step (the _pauseCooldown > 0.0
        // branch) returns early on every subsequent tick until that cooldown elapses,
        // so no further strike can fire inside the pause window. Do NOT add a
        // redundant debounce timer here — it would double-suppress strikes and skew
        // the post-spike pause length.
        if (atRecurrence && !_pressureWasAtRecurrence)
        {
            _pressureWasAtRecurrence = true;
            RegisterSevereStrike(weight: 1.0, vimsTier);
            return;
        }
        _pressureWasAtRecurrence = atRecurrence;

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
            // A full stable window pass means the user has demonstrated calm
            // for the entire profile-defined cool-off. Strikes reset — the
            // user has earned a clean slate for the next severe-event count.
            _severeStrikeCount = 0.0;

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

    /// <summary>Phase 2-era overload retained for HabituationTests.cs and any
    /// out-of-tree caller. Forwards to the primary <see cref="Step"/> with
    /// <paramref name="dtSeconds"/> mapping to tier 1 (lowest tier — most
    /// conservative pause length, semantically closest to "no tier
    /// context"). Production code must use the 4-arg signature.</summary>
    [Obsolete("Use Step(pressure, maskActive, vimsTier, dtSeconds). Test-only fallback.")]
    public void Step(double pressure, bool maskActive, double dtSeconds)
        => Step(pressure, maskActive, vimsTier: 1, dtSeconds);

    /// <summary>
    /// Report a chatbot-detected discomfort. Severe is weighted 0.5 in the
    /// strike counter (per Q4: LLM signals are softer ground truth than
    /// sensor-derived spikes). Mild applies a small back-off and pauses but
    /// does not count as a strike. Uses the last-seen VIMS tier from Step
    /// (default 1 if Step has never been called).
    /// </summary>
    public void NotifyDiscomfort(bool severe) => NotifyDiscomfort(severe, _lastSeenTier);

    /// <summary>Same as <see cref="NotifyDiscomfort(bool)"/> but with an
    /// explicit tier. Production callers (MockInferenceEngine) pass the
    /// current <c>Current.VimsTier</c>; tests use the no-tier overload.</summary>
    public void NotifyDiscomfort(bool severe, int vimsTier)
    {
        if (severe)
        {
            RegisterSevereStrike(weight: 0.5, vimsTier);
            return;
        }
        _offset = Math.Max(0.0, _offset - SenseDefaults.HabituationMildBackoffPercent);
        EnterPause(vimsTier);
    }

    /// <summary>Core severe-event handler. Halves <c>_offset</c> + increments
    /// the weighted strike counter. When the counter reaches the profile's
    /// <see cref="HabituationProfileParams.SevereStrikeLimit"/> (with epsilon
    /// per supplement #1), zeroes <c>_offset</c> instead AND resets the
    /// counter. Always calls <see cref="EnterPause"/> for the appropriate
    /// tier-scaled cooldown.</summary>
    private void RegisterSevereStrike(double weight, int vimsTier)
    {
        _lastSpikeAtUtc = DateTime.UtcNow;
        _severeStrikeCount += weight;
        // Per-user override (Stage 2 calibration) trumps the profile default.
        // Vestibular calibration sets this to 1 to zero the offset on the very
        // first severe event regardless of profile-default tolerance.
        double effectiveLimit = SevereStrikeLimitOverride ?? _params.SevereStrikeLimit;
        // Epsilon belt — see StrikeLimitEpsilon field comment for the
        // "do not remove" rationale. Compares with -1e-9 slack so future
        // non-binary weights (e.g. 1/3) don't silently miss the limit.
        if (_severeStrikeCount >= effectiveLimit - StrikeLimitEpsilon)
        {
            _offset = 0.0;
            _severeStrikeCount = 0.0;
        }
        else
        {
            _offset = Math.Max(0.0, _offset * SenseDefaults.HabituationSpikeBackoffMultiplier);
        }
        EnterPause(vimsTier);
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
            fovHistory.Count / (double)_params.MinSamplesForFullWeight,
            0.0, 1.0);
        double caution = rawCaution * confidence;

        double windowFloor   = _params.StableWindowSeconds;
        double windowCeiling = _params.StableWindowSeconds * 2.0;
        _effectiveWindow = windowFloor + (caution * (windowCeiling - windowFloor));

        double stepFloor    = _params.StepIntervalSeconds;
        double stepCeiling  = SenseDefaults.HabituationMaxStepIntervalSeconds;
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

    /// <summary>Clear transient session state — the stability/step accumulators,
    /// the severe-strike count, any pause, the phase, and the effective windows —
    /// while PRESERVING <c>_offset</c>. This is the reset a shader / personalization
    /// reset uses (e.g. the chatbot "/reset shader" path via
    /// <see cref="SenseEase.Infrastructure.Mock.MockInferenceEngine.ResetPersonalization"/>):
    /// the user's earned long-term habituation progress (weeks of <c>_offset</c>)
    /// MUST survive it — locked decision Q4.5. Every field cleared here is
    /// session-scoped; <c>_offset</c> is the only long-term value and is left
    /// untouched.</summary>
    public void ResetTransientState()
    {
        _stableElapsed = 0.0;
        _stepAccumulator = 0.0;
        _pauseCooldown = 0.0;
        _externallyPaused = false;
        _awaitingUserConfirm = false;
        _severeStrikeCount = 0.0;
        _lastSeenTier = 1;
        _phase = HabituationPhase.Observing;
        _pressureWasAtRecurrence = false;
        _effectiveWindow       = _params.StableWindowSeconds;
        _effectiveStepInterval = _params.StepIntervalSeconds;
        // _offset is deliberately NOT cleared (Q4.5).
    }

    /// <summary>Clear ONLY the chatbot-delta / stability bookkeeping a set move
    /// needs cleared, while PRESERVING the active pause contract. A silent
    /// background AdaptiveFit set move (<c>MockInferenceEngine.ApplySetMove</c>)
    /// and the snap-cause branches of <c>SetLevel</c> reuse this in place of the
    /// full <see cref="ResetTransientState"/>: those paths run under the
    /// production selector flag WITHOUT the user, so they must NOT silently
    /// un-pause opted-out habituation nor exit the tier ≥ 8
    /// <see cref="EngineState.PausedAwaitingUserConfirm"/> park — only
    /// <see cref="AcknowledgeAndResume"/> (the explicit user path) may do that
    /// (whitepaper §7.4, SAFETY-PROVISIONS §8). Preserves <c>_externallyPaused</c>,
    /// <c>_awaitingUserConfirm</c>, <c>_pauseCooldown</c>, <c>_severeStrikeCount</c>,
    /// the <c>_phase</c> (so a paused engine keeps reporting Paused), and the
    /// last-seen tier. Resets exactly the session-scoped stability/step
    /// accumulators + the recurrence edge tracker and rebases the effective
    /// windows — the new-curve snap re-confirms calm from scratch. <c>_offset</c>
    /// is never touched (Q4.5). The explicit user reset
    /// (<c>MockInferenceEngine.ResetPersonalization</c>) deliberately keeps
    /// calling the full <see cref="ResetTransientState"/>.</summary>
    public void ResetTransientStatePreservingPause()
    {
        _stableElapsed = 0.0;
        _stepAccumulator = 0.0;
        _pressureWasAtRecurrence = false;
        _effectiveWindow       = _params.StableWindowSeconds;
        _effectiveStepInterval = _params.StepIntervalSeconds;
        // PRESERVED (the pause contract + strike history a silent selector move
        // must not disturb): _externallyPaused, _awaitingUserConfirm,
        // _pauseCooldown, _severeStrikeCount, _lastSeenTier, _phase.
        // _offset is deliberately NOT cleared (Q4.5).
    }

    /// <summary>Full re-baseline to a fresh, neutral state, INCLUDING zeroing
    /// <c>_offset</c>. Only legitimate after a deliberate re-calibration that
    /// re-derives the profile from scratch — never from a shader / personalization
    /// reset (use <see cref="ResetTransientState"/> for that). Does NOT reset the
    /// active profile — call <see cref="SetProfile"/> too if you also want to
    /// clear the profile back to RESILIENT.</summary>
    public void ResetPostCalibration()
    {
        ResetTransientState();
        _offset = 0.0;
    }

    /// <summary>Enter the post-spike pause. Tier &le; 5 → PostSpikePauseLow,
    /// auto-resumes. Tier 6 or 7 → PostSpikePauseMid, auto-resumes. Tier ≥ 8 →
    /// PausedAwaitingUserConfirm, <see cref="AcknowledgeAndResume"/> required.</summary>
    private void EnterPause(int vimsTier)
    {
        _stableElapsed = 0.0;
        _stepAccumulator = 0.0;

        int pauseMs = _params.GetPostSpikePauseMs(vimsTier, out bool requireConfirm);
        if (requireConfirm)
        {
            _awaitingUserConfirm = true;
            _pauseCooldown = double.PositiveInfinity;
        }
        else
        {
            _pauseCooldown = pauseMs / 1000.0;
        }
        _phase = HabituationPhase.Paused;
    }
}
