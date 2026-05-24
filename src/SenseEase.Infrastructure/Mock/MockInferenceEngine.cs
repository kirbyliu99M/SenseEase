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
    private readonly IHabituationProfileStore? _profileStore;
    private double _personalizationAccumulator;
    private int _lastSavedOffset;
    private int      _vimsTier = 1;
    private int      _pendingTierCandidate = 1;
    private DateTime _pendingTierSinceUtc  = DateTime.MinValue;

    // ── Habituation-profile snapshot bookkeeping (Phase 4) ───────────────────
    // Tracks the engine state we persisted on the most recent successful
    // FileHabituationProfileStore.Save call. MaybePersistProfileSnapshot
    // compares current state to these markers and writes when any of the
    // user-spec'd triggers fire (profile change / level Δ > 5 / spike /
    // enter-or-exit awaiting / strike-count change). See
    // docs/HABITUATION-PROFILE.md §5.2.
    private double                _lastSavedLevel;
    private double                _lastSavedStrikeCount;
    private EngineState           _lastSavedEngineState = EngineState.Running;
    private HabituationProfile    _lastSavedProfile     = HabituationProfile.Resilient;
    private string                _lastSavedProfileSource = "default";
    private bool                  _profileSnapshotDirty;
    private DateTime?             _lastSavedSpikeAtUtc;

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
    /// <paramref name="habituationStore"/> persists habituation progress (offset only) across restarts;
    /// <paramref name="profileStore"/> persists the Phase 4 profile snapshot (profile + strikes +
    /// engine state) — a separate file from <paramref name="habituationStore"/> by design.
    /// All three are optional so test code can still use the parameterless form.
    /// </summary>
    public MockInferenceEngine(IAdjustmentLog? adjustmentLog = null,
                               IHabituationStore? habituationStore = null,
                               IHabituationProfileStore? profileStore = null)
    {
        _adjustmentLog = adjustmentLog;
        _habituationStore = habituationStore;
        _profileStore = profileStore;
        _habituation.TimeScale = SenseDefaults.HabituationTimeScale;

        // ── Restore precedence (per gotcha #1) ──────────────────────────────
        // 1. habituation.json → _offset (legacy file, untouched semantics)
        // 2. habituation_profile.json → profile + (eventually) strikes + state
        //    If habituation.json EXISTS but habituation_profile.json DOES NOT,
        //    treat as an upgrade scenario: default RESILIENT + source="default"
        //    and immediately write the new file. Do NOT force the user back
        //    into the calibration demo — that's a hostile UX for existing users.
        //    The two files are version-independent; a read failure on one
        //    does not roll back the other.
        HabituationState? savedOffset = habituationStore?.Load();
        if (savedOffset is not null)
            _habituation.Restore(savedOffset);

        HabituationProfileSnapshot? savedSnapshot = profileStore?.Load();
        if (savedSnapshot is not null)
        {
            if (Enum.TryParse<HabituationProfile>(savedSnapshot.Profile, ignoreCase: true,
                                                   out var prof))
            {
                _habituation.SetProfile(prof, savedSnapshot.ProfileSource);
            }
            // Note: we deliberately do NOT restore strike_count from the snapshot.
            // Strikes are a within-session safety counter — re-loading them on
            // startup would mean a user who hit the limit yesterday boots into
            // an immediately-zero state on the next severe event today, which
            // contradicts the "stable window resets strikes" semantics.
            // _offset comes from habituation.json (above); the new file's
            // habituation_offset_pp field is informational, not authoritative.
        }
        // else: file absent OR unreadable OR unknown schema (Q2 fallback).
        // Engine stays on RESILIENT default; if profileStore is non-null we'll
        // write a fresh snapshot on the next persist tick — see
        // _profileSnapshotDirty initial state below.

        if (adjustmentLog is not null)
        {
            var weight = adjustmentLog.WeightFor("FovRadius");
            _habituation.SetPersonalization(weight);
            // Re-seed the user-driven FOV offset from the persisted log so the
            // tightening the user accumulated last session (via chatbot feedback)
            // survives restarts. Without this, every restart silently reset the
            // direct user preference to 0 even though every adjustment was
            // dutifully appended to adjustments.jsonl. Clamp range matches the
            // accumulator's working range elsewhere in this file (-60..0).
            _userFovOffset = Math.Clamp((int)weight.NetDelta, -60, 0);
        }

        // Habituation defaults to PAUSED at startup — therapeutic relaxation requires
        // explicit user opt-in via the dashboard toggle. Persisted offset is still
        // restored (above) so resuming the engine picks up where the user left off.
        _habituation.SetPaused(true);

        // Subscribe to the engine's ProfileChanged event so the wrapper can
        // mark the snapshot dirty whenever someone (calibration demo, chatbot
        // override, ResetState path) flips the profile. The handler is a thin
        // flag-set — actual persistence happens inside MaybePersistProfileSnapshot.
        _habituation.ProfileChanged += (_, _) => _profileSnapshotDirty = true;

        _lastSavedOffset = _habituation.HabituationOffset;
        Current.HabituationLevel  = _habituation.HabituationLevel;
        Current.HabituationPaused = _habituation.IsPaused;
        Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);

        // Seed the persistence trackers to "no change since now". If the
        // snapshot file was absent at startup, MaybePersistProfileSnapshot will
        // detect that situation (dirty=true from ctor logic below) and write
        // an initial RESILIENT snapshot on the first Update tick.
        _lastSavedLevel       = _habituation.HabituationLevel;
        _lastSavedStrikeCount = _habituation.SevereStrikeCount;
        _lastSavedEngineState = _habituation.CurrentEngineState;
        _lastSavedProfile     = _habituation.CurrentProfile;
        _lastSavedProfileSource = _habituation.ProfileSource;
        _profileSnapshotDirty = savedSnapshot is null && profileStore is not null;
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
            // Pass the previous tick's _vimsTier — close enough for the spike-pause
            // tier selector (tier hysteresis debounces are 200 ms+, one Update tick
            // is typically 100 ms, so the staleness is at most one tier off). Doing
            // UpdateVimsTier first would be slightly more accurate but reorders
            // Pressure → Tier → Habituation in ways the existing tests may pin.
            _habituation.Step(pressure, Current.MaskActive, _vimsTier, dt);

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

            // Profile-snapshot persistence at the tail end of Update — after
            // both habituation state mutations AND tier update. Strike-count
            // / engine-state / level changes from this tick are now reflected
            // in the engine getters, so MaybePersistProfileSnapshot has the
            // final state to compare against the last-saved markers.
            MaybePersistProfileSnapshot();

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
                _habituation.NotifyDiscomfort(
                    Math.Abs(fovDelta) >= SenseDefaults.HabituationSevereFovThreshold,
                    _vimsTier);
            // Refresh immediately so the dashboard/overlay reflect the change even
            // when the sensing loop is stopped (no Update() tick will arrive).
            Current.FovRadiusPercent  = ComposeFovRadius(Current.Pressure);
            Current.HabituationLevel  = _habituation.HabituationLevel;
            Current.HabituationPaused = _habituation.IsPaused;
            PersistHabituation();
            MaybePersistProfileSnapshot();
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

    // ── Habituation profile pass-throughs (Phase 3) ──────────────────────────
    // Mirror the existing SetPaused / SetHabituationPaused naming convention so
    // calling-site code and tests can talk to either layer. THIN pass-throughs
    // only — no extra logic in the wrapper, per the "wrapper not a divergence
    // point" rule. Future engine behaviour changes propagate without needing
    // mirror updates here.

    /// <summary>Current habituation profile (RESILIENT / TYPICAL / SENSITIVE).</summary>
    public HabituationProfile HabituationProfile
    {
        get { lock (_gate) { return _habituation.CurrentProfile; } }
    }

    /// <summary>Origin of the current profile setting (default /
    /// calibration_demo / chatbot_override).</summary>
    public string HabituationProfileSource
    {
        get { lock (_gate) { return _habituation.ProfileSource; } }
    }

    /// <summary>Swap the habituation profile. See
    /// <see cref="HabituationEngine.SetProfile"/> for full semantics —
    /// resets strikes, preserves <c>_offset</c>, preserves any active pause.</summary>
    public void SetHabituationProfile(HabituationProfile profile, string source = "default")
    {
        lock (_gate)
        {
            _habituation.SetProfile(profile, source);
            Current.HabituationPaused = _habituation.IsPaused;
            MaybePersistProfileSnapshot();
        }
    }

    /// <summary>Resume from <see cref="EngineState.PausedAwaitingUserConfirm"/>.
    /// Routed via the chatbot when the user confirms they're ready to continue.</summary>
    public void AcknowledgeAndResumeHabituation()
    {
        lock (_gate)
        {
            _habituation.AcknowledgeAndResume();
            Current.HabituationPaused = _habituation.IsPaused;
            MaybePersistProfileSnapshot();
        }
    }

    // ── Profile-snapshot persistence (Phase 4) ────────────────────────────────
    // Compares the engine's current state against the markers from the most
    // recent successful Save. Writes when any of these conditions hold:
    //   - profile flipped (engine's ProfileChanged event sets _profileSnapshotDirty)
    //   - HabituationLevel delta > 5 since last save (per Q7: matches existing
    //     _lastSavedOffset cadence, "since last persisted level")
    //   - SevereStrikeCount changed at all (small delta still matters: rising
    //     toward limit OR resetting to zero are both signals the chatbot wants)
    //   - EngineState transitioned (Cooldown ↔ Running, into/out of
    //     PausedAwaitingUserConfirm)
    //   - LastSpikeAtUtc changed (spike fired since last save)
    //
    // Caller must hold _gate. Persistence is best-effort: a Save failure does
    // not roll back the trackers — the next change will retry. If _profileStore
    // is null, this is a no-op (test code path).
    private void MaybePersistProfileSnapshot()
    {
        if (_profileStore is null) return;

        double level    = _habituation.HabituationLevel;
        double strikes  = _habituation.SevereStrikeCount;
        EngineState es  = _habituation.CurrentEngineState;
        var profile     = _habituation.CurrentProfile;
        string source   = _habituation.ProfileSource;
        DateTime? spike = _habituation.LastSpikeAtUtc;

        bool dirty = _profileSnapshotDirty
                  || Math.Abs(level - _lastSavedLevel) > 5.0
                  || strikes != _lastSavedStrikeCount
                  || es != _lastSavedEngineState
                  || profile != _lastSavedProfile
                  || source != _lastSavedProfileSource
                  || spike != _lastSavedSpikeAtUtc;

        if (!dirty) return;

        var snap = BuildProfileSnapshot();
        _profileStore.Save(snap);

        _lastSavedLevel         = level;
        _lastSavedStrikeCount   = strikes;
        _lastSavedEngineState   = es;
        _lastSavedProfile       = profile;
        _lastSavedProfileSource = source;
        _lastSavedSpikeAtUtc    = spike;
        _profileSnapshotDirty   = false;
    }

    private HabituationProfileSnapshot BuildProfileSnapshot()
    {
        var p = _habituation.CurrentParams;
        return new HabituationProfileSnapshot
        {
            SchemaVersion           = 1,
            UpdatedAtIso            = DateTime.UtcNow.ToString("o"),
            Profile                 = _habituation.CurrentProfile.ToString().ToUpperInvariant(),
            ProfileSource           = _habituation.ProfileSource,
            HabituationLevel        = _habituation.HabituationLevel,
            HabituationOffsetPp     = _habituation.HabituationOffset,
            HabituationOffsetCapPp  = p.HabOffsetCapPp,
            CurrentVimsTier         = _vimsTier,
            SevereStrikeCount       = _habituation.SevereStrikeCount,
            SevereStrikeLimit       = p.SevereStrikeLimit,
            EngineState             = EngineStateToString(_habituation.CurrentEngineState),
            ExternallyPaused        = _habituation.IsExternallyPaused,
            AwaitingUserConfirm     = _habituation.IsAwaitingUserConfirm,
            LastSpikeAtIso          = _habituation.LastSpikeAtUtc?.ToString("o"),
            NextStepEligibleAtIso   = null, // Computed projection — deferred to a future phase
            ParamsSnapshot          = new HabituationParamsSnapshot
            {
                StableWindowMs  = p.StableWindowMs,
                StepIntervalMs  = p.StepIntervalMs,
                StepMagnitudePp = p.StepMagnitudePp,
            },
        };
    }

    /// <summary>Maps the <see cref="EngineState"/> enum to the spec's
    /// UPPER_SNAKE_CASE string form used in the JSON snapshot. Encapsulated
    /// here (not on the enum) so renaming the enum doesn't silently shift
    /// the wire contract.</summary>
    private static string EngineStateToString(EngineState s) => s switch
    {
        EngineState.Running                   => "RUNNING",
        EngineState.Cooldown                  => "COOLDOWN",
        EngineState.PausedAwaitingUserConfirm => "PAUSED_AWAITING_USER_CONFIRM",
        _                                     => "RUNNING",
    };

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
        // Mask alpha is driven by current VECTION RISK (pressure), not by how far the
        // user has narrowed the FOV. Earlier this used FovRadiusPercent, which meant a
        // user-initiated chatbot "I feel dizzy" (which legitimately shrinks FOV via
        // ApplyUserFeedback) would also make the peripheral mask appear on a static
        // screen — wrong, because there's no current motion to dim out.
        //
        // Pressure already gates MaskActive via TriggerPressure; using the same signal
        // here keeps mask onset / alpha ramp internally consistent. The user's narrower
        // FOV is preserved as a PREFERENCE that takes effect when pressure DOES spike,
        // but it no longer makes the mask appear on its own.
        double t      = Math.Clamp(Current.Pressure / SenseDefaults.TriggerPressure, 0.0, 1.0);
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

        // Mirror bypass for RELEASE: a sustained-low levelF that's already collapsed
        // past current tier by VimsTierFastReleaseGap (e.g. user alt-tabbed, OFI got
        // suppressed by the activity filter, pressure decayed quickly) snaps tier
        // straight down. Without this, tier walks down by 1 per debounce window, so
        // the user sees residual peripheral dimming for ~2 s after motion stops.
        if (-gap >= SenseDefaults.VimsTierFastReleaseGap)
        {
            int snap = Math.Clamp((int)Math.Round(levelF), 1, _vimsTier - 1);
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
