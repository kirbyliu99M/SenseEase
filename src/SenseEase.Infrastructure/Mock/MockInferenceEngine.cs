using SenseEase.Core.Abstractions;
using SenseEase.Core.AdaptiveFit;
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
    // A7: true once the user has personalised the shader knobs via ApplyShaderAdjustment.
    // While false, a profile CHANGE re-seeds _shader to the new profile's defaults
    // (SeedShaderFromProfile); once the user has tweaked them, their choices win.
    private bool _shaderUserModified;
    private readonly object _gate = new();
    private readonly HabituationEngine _habituation = new();
    private readonly IAdjustmentLog? _adjustmentLog;
    private readonly IHabituationStore? _habituationStore;
    private readonly IHabituationProfileStore? _profileStore;
    private double _personalizationAccumulator;
    private int _lastSavedOffset;
    private readonly bool _profileExistedAtLoad; // #2 (EM2 hardening): did a profile snapshot exist at boot?
    private int      _vimsTier = 1;
    private int      _pendingTierCandidate = 1;
    private DateTime _pendingTierSinceUtc  = DateTime.MinValue;

    // ── Adaptive Fit selector (Alpha-2 core GROUP A) ─────────────────────────
    // The engine's active set in the 50-set grid. PINNED to the median Set0
    // (Band 5, Shape 2) in this foundation — the selector that moves it lands in
    // a later group (C/D). At Set0 the resolved curve reproduces today's engine
    // exactly (set0 ≡ today), so even with the flag ON the mask/FOV are unchanged.
    private GridCoord _activeSet = GridCoord.Set0;

    // ── Adaptive Fit P5 (propose-confirm) ────────────────────────────────────
    // A cross-segment (felt-intensity) set move the selector wants to make but
    // CANNOT apply silently — it needs the user's explicit confirm. Queued here,
    // IN-MEMORY ONLY (never persisted, per locked constraint). The engine NEVER
    // opens a window for this; the App surfaces it only behind a userInitiated
    // card-open and routes Accept / Decline / expiry back through the Confirm /
    // Decline / Clear methods below. The selector tick owns expiry via the injected
    // clock (the suppression guard in MaybeRunAdaptiveFitSelector); the App filters
    // by wall-clock at display time.
    private (GridCoord Target, int TargetLevel, DateTime ExpiresAtUtc)? _pendingSetMove;

    // Engine-local mirror of AppConfig.AdaptiveFitSelectorEnabled. SenseEase.
    // Infrastructure does NOT (and must not) reference SenseEase.App, so the App
    // layer cannot be read here directly — the same architectural rule that keeps
    // HeadTrackingEnabled an App-only flag with the Infrastructure side gated via
    // DI. This instance flag is the Infrastructure-visible gate; it DEFAULTS OFF,
    // which is what guarantees flag-off byte-identity by construction. A later
    // group mirrors AppConfig.AdaptiveFitSelectorEnabled onto it at startup. While
    // false, NO new code path below is reachable and Update() runs today's path.
    private bool _adaptiveFitSelectorEnabled;

    // Engine-local mirror of AppConfig.AutoProfilingEnabled (Alpha-2 addendum:
    // cancel explicit calibration → auto-profiling). Same App-can't-be-read-from-
    // Infrastructure rule as _adaptiveFitSelectorEnabled — the App layer mirrors the
    // flag onto this at startup. DEFAULTS OFF → flag-off byte-identity by construction.
    // Read only by ApplyAutoProfilingStartDefaults (App-driven), so while OFF nothing
    // here changes today's behaviour.
    private bool _autoProfilingEnabled;

    // Alpha-2 addendum vestibular re-rooting: the PERSISTENT in-memory vestibular-
    // protection signal. Set by SetVestibularProtection (the auto-profiling one-time
    // field / Settings toggle) OR restored from the snapshot at load. The legacy
    // calibration path keeps signalling vestibular via _lastCalibrationResult.Medical
    // Advisory; the IsVestibularActive helper ORs both so EVERY vestibular consumer
    // keeps firing regardless of which world set it. Read by the selector guards
    // (ApplySetMove L9 floor / PassesVestibularGuard), which only run when the
    // selector flag is ON, so re-rooting them is byte-identical when the selector is OFF.
    private bool _vestibularProtection;

    // ── Adaptive Fit selector TICK (Alpha-2 core GROUP D) ────────────────────
    // Everything in this region is reachable ONLY inside an
    // 'if (_adaptiveFitSelectorEnabled)' guard in Update(); while the flag is
    // OFF (the default) none of it runs and Update() is byte-identical to today.
    //
    // The lifecycle/cooldown governor that decides WHEN a set move may be
    // evaluated. Pure Core type; it NEVER touches _offset (that invariant is the
    // whole reason it is a dedicated type). Advanced by dt every eval tick.
    private readonly AdaptiveFitStateMachine _adaptiveFitMachine = new();

    // Conservative evaluation cadence: accumulate dt and evaluate the selector at
    // most once per SettlingMinSeconds of accumulated time, so the durable-window
    // stats read + selector run is a low-frequency background concern, not a
    // per-Update cost. Reset to 0 on each evaluation.
    private double _adaptiveFitEvalAccumulator;

    // Recency tracking for recentManualLevelDelta (selector "manual authority"
    // term). The last ApplyUserFeedback delta is interpreted as a manual level
    // nudge: a NEGATIVE fovDelta (the user asked for more protection / a tighter
    // FOV) is a manual move toward a STRONGER band ⇒ a POSITIVE level delta; a
    // positive fovDelta (more openness) ⇒ negative. Only counts while within
    // AdaptiveFitManualRecencySeconds of the nudge; otherwise the selector sees 0
    // (no conflict). Conservative + simple per spec.
    private int      _lastManualLevelDeltaSign;
    private DateTime _lastManualFeedbackAtUtc = DateTime.MinValue;

    // Durable stats window (days) read back from _eventLog.ReadDurable. Matches
    // FileEventLogStore.RetentionDays (7) so we read the full rolling JSONL.
    private const double AdaptiveFitDurableWindowDays = 7.0;

    // Recency window (seconds) within which the last manual +/- still counts as a
    // selector manual-authority signal. Local to this file (no SenseDefaults edit
    // in scope). Kept conservative — a single SettlingMinSeconds window.
    private const double AdaptiveFitManualRecencySeconds = SenseDefaults.SettlingMinSeconds;

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

    // M1 (Phase 6-pre): event-log writer for habituation lifecycle. Parallel
    // path to the snapshot persistence (file IO via _profileStore) — this
    // emits one EventLogEntry per change so the chatbot's Phase 6c trajectory
    // builder can summarise the user's habituation arc by querying the event
    // store. Optional dependency — null in unit-test contexts.
    private readonly IEventLogStore? _eventLog;

    // Stage 7a: calibration_count the engine considers current. Initialised
    // from the loaded snapshot at construction; Stage 7f bumps it in
    // ApplyCalibrationResult on every non-Abandoned completion and
    // BuildProfileSnapshot persists the bumped value. Surfaced via the
    // LoadedCalibrationCount property below.
    // Wipe-recovery synthetics arrive with the loader-emitted default of 0,
    // which is the desired behaviour: recovered users start fresh.
    private int                   _loadedCalibrationCount;

    // Stage 7f: UTC timestamp of the most recent non-Abandoned calibration,
    // for the last_calibration_at_iso v2 snapshot field. Null until the
    // first ApplyCalibrationResult lands a Complete or Partial result.
    private DateTime?             _lastCalibrationAtUtc;

    // M3 Phase 7d: current personal_pressure_scale value. ApplyCalibrationResult
    // uses it to compute the effective trigger then discards; M3 keeps it as a
    // tracked field so BuildProfileSnapshot writes the right value AND
    // ApplyChatbotOverride(AdjustSensitivity) has somewhere to update.
    // Default 1.0 = "no personalisation" (matches the snapshot DTO default).
    private double                _personalPressureScale = 1.0;

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
                               IHabituationProfileStore? profileStore = null,
                               IEventLogStore? eventLog = null)
    {
        _adjustmentLog = adjustmentLog;
        _habituationStore = habituationStore;
        _profileStore = profileStore;
        _eventLog = eventLog;
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
        _profileExistedAtLoad = savedSnapshot is not null;
        if (savedSnapshot is not null)
        {
            if (Enum.TryParse<HabituationProfile>(savedSnapshot.Profile, ignoreCase: true,
                                                   out var prof))
            {
                _habituation.SetProfile(prof, savedSnapshot.ProfileSource);
            }
            // Stage 7a: surface the persisted calibration_count so upstream
            // code can branch on "has the user EVER completed calibration on
            // this machine" without re-reading the JSON. Wipe-recovery
            // snapshots arrive with CalibrationCount=0 (the loader synthesizes
            // a defaults-only snapshot), which is the desired behaviour:
            // recovered users get re-routed through the first-time calibration
            // card, not the LLM banner that assumes a baseline exists.
            // Stage 7f: ApplyCalibrationResult bumps from this seed value;
            // BuildProfileSnapshot persists the bumped value back.
            // H44 (anti-tamper): the on-disk count is user-editable JSON. A
            // hand-edited high value would fake a veteran calibrator and skip
            // first-run onboarding. Clamp to a sane range, then treat a positive
            // count with NO last_calibration_at_iso as a tamper signal (a genuine
            // non-zero count is always written alongside its timestamp by
            // BuildProfileSnapshot) and reset it to 0.
            int loadedCount = Math.Clamp(savedSnapshot.CalibrationCount, 0, 1000);
            if (loadedCount > 0 && string.IsNullOrEmpty(savedSnapshot.LastCalibrationAtIso))
                loadedCount = 0;
            _loadedCalibrationCount = loadedCount;
            // M3 Phase 7d: restore the persisted scale so a chatbot
            // AdjustSensitivity arriving early in the session has the right
            // baseline. Clamp defensively in case the on-disk value was
            // edited outside our range.
            _personalPressureScale = Math.Clamp(
                savedSnapshot.PersonalPressureScale,
                CalibrationProcessor.PersonalPressureScaleMin,
                CalibrationProcessor.PersonalPressureScaleMax);
            // Alpha-2 audit fix: re-DERIVE the engine trigger from the restored scale.
            // Restoring the FIELD alone wasn't enough — EffectiveTrigger is what the engine
            // actually reads, and nothing on the LOAD path re-applied it (only the runtime
            // ApplyCalibrationResult / ApplyChatbotOverride did). So a user who personalized
            // their sensitivity last session booted back at the DEFAULT trigger and silently
            // lost it until their next chatbot turn. Same arithmetic as both runtime paths
            // (effective_trigger = default / scale). The additive TriggerThresholdAdjustment
            // is not persisted (the snapshot carries only the scale), so the scale is the
            // best-effort reconstruction; scale==1.0 — which also covers the absolute-override
            // case that resets scale to 1.0 — leaves the engine default untouched. The
            // vestibular branch below runs AFTER this, so its TriggerPressure-50 hard-override
            // correctly wins for vestibular users.
            if (_personalPressureScale != 1.0)
                _habituation.EffectiveTrigger = SenseDefaults.TriggerPressure / _personalPressureScale;
            if (!string.IsNullOrEmpty(savedSnapshot.LastCalibrationAtIso) &&
                DateTime.TryParse(savedSnapshot.LastCalibrationAtIso,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var lastCalAt))
            {
                _lastCalibrationAtUtc = lastCalAt;
            }
            // Phase 7 write-durability: restore a user-confirmed chatbot strike override so the
            // AI's accepted tuning survives restart (was session-only). Restored BEFORE the
            // vestibular branch below so a vestibular user's hard-cap (=1) re-derived from the
            // flag intentionally OVERWRITES this. Clamp defensively to the processor's [1,5].
            if (savedSnapshot.SevereStrikeLimitOverride is int savedStrikeOverride)
                _habituation.SevereStrikeLimitOverride = Math.Clamp(savedStrikeOverride, 1, 5);
            // Alpha-2 addendum: restore the PERSISTENT vestibular protection so the
            // auto-profiling world's TriggerPressure-50 + SevereStrikeLimit-1 (and the
            // selector L9 floor / propose-stronger-only guards via IsVestibularActive)
            // survive restarts. Once explicit calibration is cancelled this field is the
            // ONLY vestibular source, so it must be re-applied at load. Field defaults
            // false ⇒ no-op for existing calibration-era snapshots (byte-identical).
            if (savedSnapshot.VestibularProtectionEnabled)
            {
                _vestibularProtection                  = true;
                _habituation.EffectiveTrigger          = CalibrationProcessor.VestibularTriggerPressureOverride;
                _habituation.SevereStrikeLimitOverride = 1;
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
        //
        // WAVE W2.0-E (fail-open observability): a store WAS wired but Load()
        // returned null. That null is indistinguishable here between three
        // causes — genuine first run (file truly absent), a CORRUPT/unparsable
        // file, or a REFUSED schema downgrade — and in the latter two the
        // engine has just SILENTLY discarded a calibrated profile (profile +
        // source + scale + advisory) and reverted to RESILIENT/source='default'.
        // That loss leaves no trace today. Emit ONE non-blocking durable record
        // to the same IEventLogStore sink the rest of this file uses (file-backed
        // ⇒ 7-day rolling JSONL, so it round-trips ReadDurable) so the silent
        // downgrade is auditable after the fact. No UI alarm; best-effort write.
        // Skipped when profileStore is null (headless / unit-test contexts where
        // there is no profile to lose).
        else if (profileStore is not null)
        {
            AppendRecoveryEvent("Recovery.ProfileLoadNull",
                "habituation profile Load() returned null (missing / corrupt / refused-downgrade); " +
                $"engine reverted to {_habituation.CurrentProfile.ToString().ToUpperInvariant()} " +
                $"source='{_habituation.ProfileSource}' (calibration state, if any, was not restored)");
        }

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
            double effectiveOfi = isDriverMode ? (opticalFlowIndex * SenseDefaults.DriverFlowAttenuation) : opticalFlowIndex;

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
            // Phase 5 follow-up (2026-05-31): also capture the raw sensor OFI
            // (pre-attenuation) so user-facing displays show what the user
            // intuitively means by "OFI" rather than the engine's internal
            // working value. OverlayWindow + the leaky integrator continue to
            // consume the effective OpticalFlowIndex above.
            Current.RawOpticalFlowIndex = opticalFlowIndex;
            Current.Pressure = pressure;
            // P1: personalized display stress — pressure as a % of THIS user's
            // effective trigger (lower trigger ⇒ higher % at equal pressure), so
            // personalization shows up in the hero metric without touching the tier.
            double effTrigger = _habituation.EffectiveTrigger;
            Current.DisplayStressPercent = effTrigger > 0
                ? (int)Math.Round(Math.Clamp(pressure / effTrigger * 100.0, 0, 100))
                : 0;
            Current.Mode = mode;

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

            // ── Adaptive Fit gated branch (Alpha-2 core GROUP A) ─────────────
            // FLAG-OFF (the default): this whole block is skipped — Current.
            // FovRadiusPercent keeps the value ComposeFovRadius produced above
            // and nothing reads the new grid (byte-identical to today).
            //
            // FLAG-ON at Set0 (the pinned median): ResolveLevel(_vimsTier) on
            // the Set0 curve yields FovFloorPercent == 15, which is exactly the
            // floor ComposeFovRadius already clamped to (SenseDefaults.
            // TightestNaturalFovRadiusPercent), so re-applying it changes
            // nothing — set0 ≡ today even with the flag ON. For a later,
            // selector-moved set the resolved (provisional) floor can be tighter,
            // and THIS is where it takes effect. The discretizer, hysteresis, and
            // every flag-off path are untouched; only the post-compose FOV floor
            // is re-clamped here, behind the flag.
            if (_adaptiveFitSelectorEnabled)
            {
                LevelParams lp = SetGrid.Curve(_activeSet).ForLevel(_vimsTier);
                Current.FovRadiusPercent = Math.Max(Current.FovRadiusPercent, lp.FovFloorPercent);
                // Alpha-2 (selector→renderer): surface the active set's mask DEPTH so the
                // selector's chosen envelope actually reaches the overlay. set0 ⇒ the raw
                // VimsLevels alpha (no visible change); a selector-moved (deformed) set ⇒ a
                // deeper/shallower mask at the same tier. A set move from the tick below lands
                // next frame — invisible given the overlay's ~250ms smoothing + 300s cooldown.
                Current.MaskPeakAlphaOverride = lp.PeakMaskAlpha;

                // ── Selector tick (GROUP D) ──────────────────────────────────
                // Conservative cadence: accumulate dt and only do the durable
                // stats read + state-machine advance + selector run at most once
                // per SettlingMinSeconds of accumulated time. Cheap on every
                // other Update; the heavy work is rate-limited here. Still gated
                // entirely by the flag, so OFF ⇒ none of this is reachable.
                _adaptiveFitEvalAccumulator += dt;
                if (_adaptiveFitEvalAccumulator >= SenseDefaults.SettlingMinSeconds)
                {
                    double evalDt = _adaptiveFitEvalAccumulator;
                    _adaptiveFitEvalAccumulator = 0.0;
                    MaybeRunAdaptiveFitSelector(now, evalDt);
                }
            }

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

    public void ApplyUserFeedback(int fovDelta, bool isDeliberatePreference = false)
    {
        lock (_gate)
        {
            // GROUP D — record the manual nudge as the selector's recentManual
            // signal. Sign convention: a NEGATIVE fovDelta (more protection /
            // tighter FOV) is a manual move toward a STRONGER band ⇒ POSITIVE
            // level delta. These fields are read ONLY inside the flag-gated
            // selector tick, so this is inert on the flag-off path (it just
            // writes two private fields nothing else consults).
            if (fovDelta != 0)
            {
                _lastManualLevelDeltaSign = -Math.Sign(fovDelta);
                _lastManualFeedbackAtUtc  = DateTime.UtcNow;
                // P5: a manual FOV nudge (EaseView / WidenView / chatbot FOV delta) defers
                // the selector AND retracts any stale pending suggestion. Fenced behind the
                // flag so the flag-off path stays byte-identical. This is the missing
                // OnManualAdjust wiring for the ApplyUserFeedback FOV-delta path.
                if (_adaptiveFitSelectorEnabled)
                {
                    _adaptiveFitMachine.OnManualAdjust();
                    _pendingSetMove = null;
                }
            }

            _userFovOffset = Math.Clamp(_userFovOffset + fovDelta, -60, 0);
            // D5: a negative (tightening) delta is a discomfort recurrence ONLY when it
            // is NOT an explicit standing preference. A deliberate "more protection"
            // request is NEUTRAL — it moves the FOV but must not erode the earned
            // habituation _offset (Q4.5) or trip a pause. Sensor-driven recurrence is
            // unaffected: it never reaches this method (see HabituationEngine.Step).
            if (fovDelta < 0 && !isDeliberatePreference)
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
            _shaderUserModified = true; // A7: user owns the shader knobs from here on
        }
    }

    /// <summary>G1: ABSOLUTE-set a single shader parameter from the chatbot slash-
    /// command / strict-language path. Unlike <see cref="ApplyShaderAdjustment"/>
    /// (delta-only, [-0.3,+0.3] capped upstream), this writes the field directly —
    /// the semantics are "the user told us exactly what they want". Mask/Grid/Feather
    /// write the [0,1] field then defensively <see cref="ShaderParams.Clamp"/>; FOV is
    /// a best-effort absolute via the user FOV offset. Marks the shader as
    /// user-modified (A7) so a later profile change won't re-seed over the choice, and
    /// mirrors onto <c>Current.Shader</c> so the overlay re-renders immediately even
    /// when the sensing loop is stopped (no Update() tick will arrive).</summary>
    public void SetShaderParam(ShaderParam param, double value)
    {
        lock (_gate)
        {
            switch (param)
            {
                case ShaderParam.MaskDepth:
                    _shader.MaskDepth = Math.Clamp(value, 0.0, 1.0);
                    break;
                case ShaderParam.GridVisibility:
                    _shader.GridVisibility = Math.Clamp(value, 0.0, 1.0);
                    break;
                case ShaderParam.FeatherWidth:
                    _shader.FeatherWidth = Math.Clamp(value, 0.0, 1.0);
                    break;
                case ShaderParam.FovRadius:
                {
                    // Best-effort ABSOLUTE FOV: the composed radius is
                    // ComputeFovRadiusPercent(pressure) + _userFovOffset + habituation
                    // offset (see ComposeFovRadius). Solve _userFovOffset so the
                    // composed result targets `value`, then clamp the offset's working
                    // range (-60..0; FOV is a tighten-only knob from chatbot feedback).
                    // The composed radius is finally clamped to
                    // [MinComposedFovRadius, 100] by ComposeFovRadius itself.
                    int target = (int)Math.Round(Math.Clamp(value, SenseDefaults.MinComposedFovRadius, 100));
                    int natural = ComputeFovRadiusPercent(Current.Pressure) + _habituation.HabituationOffset;
                    _userFovOffset = Math.Clamp(target - natural, -60, 0);
                    break;
                }
            }

            _shader.Clamp();
            _shaderUserModified = true; // A7: user owns the shader knobs from here on

            // Refresh Current via the SAME path SetLevel / ApplySetMove use so the
            // overlay re-renders without waiting for the next Update() tick.
            Current.Shader           = _shader;
            Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);
        }
    }

    /// <summary>G1: reset the shader knobs + user FOV offset to the CURRENT profile's
    /// defaults. Reuses the existing <see cref="SeedShaderFromProfile"/> (A7) so the
    /// per-profile baseline is the single source of truth. Q4.5 (locked): this clears
    /// transient chatbot shader state ONLY — it MUST NOT call <c>_habituation.Reset</c>
    /// or zero the earned habituation <c>_offset</c> (that conflation is the C5 bug).
    /// Mirrors onto <c>Current</c> via the same compose path so the overlay re-renders.</summary>
    public void ResetShaderToProfileDefault()
    {
        lock (_gate)
        {
            SeedShaderFromProfile(_habituation.CurrentProfile); // sets _shader + Current.Shader
            _userFovOffset      = 0;
            _shaderUserModified = false; // a later profile change may re-seed again

            // Q4.5: deliberately NO _habituation.Reset / ResetTransientState / _offset
            // zeroing here — the earned habituation offset (weeks of progress) survives.
            Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);
        }
    }

    public void ResetPersonalization()
    {
        lock (_gate)
        {
            _userFovOffset = 0;
            _shader.ResetToDefault();
            // A7: reset returns to the CURRENT profile's defaults (not just the global
            // default) and clears the personalisation flag so a later profile change re-seeds.
            SeedShaderFromProfile(_habituation.CurrentProfile);
            _shaderUserModified = false;
            // Q4.5: a shader / personalization reset clears transient session state
            // but MUST preserve the earned habituation offset (weeks of progress).
            // Use ResetTransientState, NOT the full ResetPostCalibration; the
            // displayed level therefore reflects the PRESERVED offset, not 0.
            _habituation.ResetTransientState();
            Current.HabituationLevel  = _habituation.HabituationLevel;
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

    // ── Adaptive Fit selector surface (Alpha-2 core GROUP A) ──────────────────
    // M1-gated; every behavioural effect here is fenced behind
    // _adaptiveFitSelectorEnabled (default OFF → today's behaviour byte-identical).
    // ResolveLevel is a pure query (no state mutation) so it is always safe to call.

    /// <summary>Engine-local mirror of <c>AppConfig.AdaptiveFitSelectorEnabled</c>.
    /// Defaults OFF — while false, the gated branch in <see cref="Update"/> is skipped
    /// and <see cref="SetLevel"/> is a no-op, so the engine is byte-identical to today.
    /// The App layer (a later group) sets this from the AppConfig flag at startup;
    /// SenseEase.Infrastructure cannot read SenseEase.App directly, mirroring the
    /// HeadTrackingEnabled App-only-flag rule.</summary>
    public bool AdaptiveFitSelectorEnabled
    {
        get { lock (_gate) { return _adaptiveFitSelectorEnabled; } }
        set { lock (_gate) { _adaptiveFitSelectorEnabled = value; } }
    }

    /// <summary>Engine-local mirror of <c>AppConfig.AutoProfilingEnabled</c> (Alpha-2
    /// addendum). Defaults OFF. The App layer sets it from the AppConfig flag at startup
    /// and then calls <see cref="ApplyAutoProfilingStartDefaults"/>. SenseEase.Infrastructure
    /// cannot read SenseEase.App directly — same App-only-flag rule as the selector.</summary>
    public bool AutoProfilingEnabled
    {
        get { lock (_gate) { return _autoProfilingEnabled; } }
        set { lock (_gate) { _autoProfilingEnabled = value; } }
    }

    /// <summary>Alpha-2 addendum: seed a BRAND-NEW auto-profiling user at the neutral
    /// TYPICAL profile + L5「中」 start (instead of RESILIENT/L1), from which the AdaptiveFit
    /// selector converges. No-op unless the flag is ON AND the user is genuinely fresh
    /// (no profile snapshot existed at load, zero prior calibrations, no persisted
    /// vestibular signal) — an existing user's profile is never overwritten. App-driven
    /// (called after the flag mirror is set), so flag-OFF construction stays byte-identical
    /// to today. Q4.5-safe: never touches the habituation <c>_offset</c>.</summary>
    public void ApplyAutoProfilingStartDefaults()
    {
        lock (_gate)
        {
            if (!_autoProfilingEnabled) return;
            if (_profileExistedAtLoad || _loadedCalibrationCount > 0 || _vestibularProtection) return;

            _habituation.SetProfile(HabituationProfile.Typical, "auto_profiling_default");
            _vimsTier             = SenseDefaults.AutoProfilingStartLevel; // L5 median seed
            _pendingTierCandidate = _vimsTier;
            _pendingTierSinceUtc  = DateTime.MinValue;
            Current.VimsTier         = _vimsTier;
            Current.HabituationLevel = _habituation.HabituationLevel;
            _profileSnapshotDirty    = true;
            MaybePersistProfileSnapshot();
        }
    }

    /// <summary>Alpha-2 addendum vestibular re-rooting: turn the PERSISTENT vestibular
    /// protection ON/OFF outside the (cancelled) calibration Q5. ON applies the SAME
    /// engine protection the vestibular calibration path applied — TriggerPressure-50
    /// hard-override + SevereStrikeLimit=1 — and arms the in-memory signal the selector
    /// guards (L9 floor / propose-stronger-only) and <c>HasVestibularDiagnosis</c> read.
    /// OFF (the Settings "regret" toggle) reverts the overrides to defaults. Persisted via
    /// <see cref="HabituationProfileSnapshot.VestibularProtectionEnabled"/> so it survives
    /// restarts. Q4.5-safe: never touches the habituation <c>_offset</c>.</summary>
    public void SetVestibularProtection(bool enabled, string source = "user")
    {
        lock (_gate)
        {
            _vestibularProtection = enabled;
            if (enabled)
            {
                _habituation.EffectiveTrigger          = CalibrationProcessor.VestibularTriggerPressureOverride; // 50
                _habituation.SevereStrikeLimitOverride = 1;
            }
            else
            {
                // Regret: the auto-profiling world has no calibration scale/override to
                // restore, so revert to engine defaults.
                _habituation.EffectiveTrigger          = SenseDefaults.TriggerPressure;
                _habituation.SevereStrikeLimitOverride = null;
            }
            _profileSnapshotDirty = true;
            MaybePersistProfileSnapshot();
            // NOTE: deliberately NO event-log entry here. The App-layer caller
            // (MainViewModel.AnswerVestibularPrompt / the Settings regret toggle) already
            // writes ONE localized, user-facing "Vestibular" line. Emitting an English/UTC
            // "Habituation.VestibularProtection" event here too produced the duplicate,
            // bilingual pair Kirby saw in the dashboard Event Log. The durable state still
            // round-trips via the persisted snapshot (VestibularProtectionEnabled), so no
            // forensic signal is lost by dropping the engine-side line.
        }
    }

    /// <summary>True when vestibular protection is active from EITHER world — the
    /// persistent auto-profiling signal (<see cref="SetVestibularProtection"/> / restored
    /// from snapshot) OR the legacy calibration advisory. The single predicate every
    /// vestibular consumer in this engine keys off, so re-rooting kept all consumers intact.</summary>
    private bool IsVestibularActive =>
        _vestibularProtection
        || string.Equals(_lastCalibrationResult?.MedicalAdvisory, "vestibular",
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>The engine's current discrete intensity level (1..12) — the same
    /// value surfaced as <c>SenseState.VimsTier</c>. Reading it does not depend on
    /// the flag; it just exposes the tier under the engine lock.</summary>
    public int CurrentLevel { get { lock (_gate) { return _vimsTier; } } }

    /// <summary>Resolve the per-level render envelope (peak mask alpha, FOV floor,
    /// discretizer scale, grid visibility) for a 1-based <paramref name="level"/>
    /// (1..12) on the engine's active set. At <see cref="GridCoord.Set0"/> (the
    /// pinned median, always active in this foundation) the resolved params equal
    /// today's engine: <c>PeakMaskAlpha == OverlayWindow.VimsLevels[L-1].Alpha</c>,
    /// <c>FovFloorPercent == 15</c>, <c>Scale == 11.0</c>, <c>GridVisibility ==
    /// 0.75</c> — so set0 ≡ today. Pure query; never mutates engine state, so it is
    /// safe to call regardless of the flag.</summary>
    public LevelParams ResolveLevel(int level)
    {
        lock (_gate) { return SetGrid.Curve(_activeSet).ForLevel(level); }
    }

    /// <summary>Set the engine's discrete level, tagging WHY via
    /// <paramref name="cause"/>. NO-OP when the selector flag is OFF (today's path
    /// has no notion of a directly-set level). When ON, sets the tier and — for
    /// <see cref="LevelChangeCause.SetChangeSnap"/> /
    /// <see cref="LevelChangeCause.FeltIntensitySnap"/> — clears the transient
    /// chatbot delta (FOV offset + shader knobs) BEFORE the snap (Q5 before Q4),
    /// reusing the <see cref="ResetPersonalization"/> shader path that PRESERVES the
    /// earned habituation <c>_offset</c> (it calls <c>ResetTransientState</c>, never
    /// the full post-calibration reset). The discretizer / hysteresis are not
    /// touched; the snapped tier is recomputed onto the active set's curve on the
    /// next Update via the gated branch.</summary>
    public void SetLevel(int level, LevelChangeCause cause)
    {
        lock (_gate)
        {
            // Flag-off: a direct SetLevel has no meaning on today's path. Skip
            // entirely so the OFF behaviour stays byte-identical.
            if (!_adaptiveFitSelectorEnabled) return;

            // Q4/Q5 ordering: for the two snap causes, clear the chatbot delta
            // (transient personalization) FIRST — preserving _offset — then snap.
            // Reuses the SAME transient-reset path as ResetPersonalization
            // (ResetTransientState preserves the earned habituation _offset, Q4.5);
            // it deliberately does NOT touch _vimsTier here so the snap below is
            // the single writer of the level.
            if (cause is LevelChangeCause.SetChangeSnap or LevelChangeCause.FeltIntensitySnap)
            {
                _userFovOffset = 0;
                _shader.ResetToDefault();
                _habituation.ResetTransientState();   // preserves _offset (Q4.5 / C5)
                Current.HabituationLevel  = _habituation.HabituationLevel;
                Current.HabituationPaused = _habituation.IsPaused;
            }

            // Snap the tier onto the new curve. Clamp to the native 12-rung span
            // and reset the pending-debounce bookkeeping so the snapped tier holds
            // (the selector's snap is authoritative — no debounce walk back).
            _vimsTier             = Math.Clamp(level, 1, SetCurve.LevelCount);
            _pendingTierCandidate = _vimsTier;
            _pendingTierSinceUtc  = DateTime.MinValue;
            Current.VimsTier      = _vimsTier;
            Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);

            // Apply the active set's FOV floor (set0 ⇒ 15, a no-op vs today).
            LevelParams lp = SetGrid.Curve(_activeSet).ForLevel(_vimsTier);
            Current.FovRadiusPercent = Math.Max(Current.FovRadiusPercent, lp.FovFloorPercent);
            // Alpha-2 (selector→renderer): refresh the mask-depth override so a level snap
            // re-renders the active set's alpha immediately (out-of-tick path). Gated on the
            // flag — OFF ⇒ -1 ⇒ overlay falls back to its static tier alpha (byte-identical).
            Current.MaskPeakAlphaOverride = _adaptiveFitSelectorEnabled ? lp.PeakMaskAlpha : -1.0;
            // Alpha-2 (selector→renderer): on a snap that reset the shader to default (above),
            // re-seed the grid baseline from the active set (mirrors ApplySetMove). Gated on the
            // snap cause so a non-snap SetLevel never overwrites a live chatbot grid adjustment.
            // The flag is already guaranteed ON here (early-return at the top). set0 ⇒ 0.75
            // (a no-op today); a future per-set GridVisibility would re-baseline here.
            if (cause is LevelChangeCause.SetChangeSnap or LevelChangeCause.FeltIntensitySnap)
                _shader.GridVisibility = lp.GridVisibility;
        }
    }

    // ── Adaptive Fit selector tick (Alpha-2 core GROUP D) ─────────────────────
    // Reachable ONLY from the flag-gated region in Update(); the caller already
    // holds _gate and has verified _adaptiveFitSelectorEnabled. While the flag is
    // OFF none of this runs and Update() is byte-identical to today.

    /// <summary>One conservative selector evaluation. Builds the user's own
    /// windowed <see cref="UserIntensityStats"/> from the durable event log,
    /// advances the cooldown state machine by <paramref name="evalDt"/>, and — when
    /// the machine <c>CanEvaluate</c> — runs the deterministic selector. A
    /// resulting <see cref="SelectorDecision.MoveTo"/> is applied SILENTLY only
    /// when the target is adjacent, stays in the SAME 5級 segment, and passes the
    /// vestibular guard; a CROSS-SEGMENT target HOLDs (P5 propose-confirm). Caller
    /// holds <c>_gate</c>.</summary>
    private void MaybeRunAdaptiveFitSelector(DateTime now, double evalDt)
    {
        // Build the single user's own durable stats. If we have no event log,
        // there is no durable history to read — skip (no stats ⇒ no move).
        if (_eventLog is null) return;

        DateTime since = now.AddDays(-AdaptiveFitDurableWindowDays);
        IReadOnlyList<EventLogEntry> durable;
        try { durable = _eventLog.ReadDurable(since); }
        catch { return; } // best-effort read; a failed read just means "no move this tick"

        UserIntensityStats stats = IntensityStatsBuilder.Build(
            durable, now, _habituation.EffectiveTrigger);

        // recentManualLevelDelta: the last manual +/- within the recency window,
        // else 0 (no conflict). Conservative + simple per spec.
        int recentManualLevelDelta = 0;
        if (_lastManualFeedbackAtUtc != DateTime.MinValue
            && (now - _lastManualFeedbackAtUtc).TotalSeconds <= AdaptiveFitManualRecencySeconds)
        {
            recentManualLevelDelta = _lastManualLevelDeltaSign;
        }

        // The state machine's Settling → Active gate wants to know whether a
        // confident pick currently exists; ask the selector once up front (pure,
        // no state change) and feed the boolean in.
        SelectorDecision peek = DeterministicSetSelector.Select(
            _activeSet, stats, SelectorWeights.Default, recentManualLevelDelta);
        bool haveConfidentPick = peek is SelectorDecision.MoveTo;

        _adaptiveFitMachine.Advance(evalDt, stats.SampleCount, haveConfidentPick);
        if (!_adaptiveFitMachine.CanEvaluate) return;

        // P5 (propose-confirm) suppression + expiry. While a cross-segment proposal
        // is pending the user's confirm, emit NO new move (don't overwrite/race the
        // queued one). The selector tick owns expiry via the injected clock: once
        // the 24h TTL elapses (the user never opened the card) clear it and allow
        // re-evaluation this same tick. The engine never surfaces it — that is
        // strictly an App-side, user-initiated concern.
        if (_pendingSetMove is { } pend)
        {
            if (now < pend.ExpiresAtUtc) return;   // a proposal is pending the user's confirm — emit no new move
            _pendingSetMove = null;                // expired (24h, user never opened the card) — clear + allow re-eval
        }

        // Re-run under the now-eligible machine (peek above may predate the
        // Advance that promoted us to Active). Pure; cheap.
        SelectorDecision decision = DeterministicSetSelector.Select(
            _activeSet, stats, SelectorWeights.Default, recentManualLevelDelta);

        if (decision is not SelectorDecision.MoveTo move) return;
        GridCoord target = move.Target;

        // Adjacency invariant (belt-and-suspenders: the selector only ever
        // proposes distance-1 cells, but we re-assert before applying).
        if (GridCoord.ManhattanDistance(_activeSet, target) != 1) return;

        // Vestibular propose-stronger-only applies to BOTH the silent same-segment
        // move AND the queued cross-segment proposal — a weaker-band target is never
        // moved-to OR queued. A blocked move HOLDs this tick. (Re-ordered ahead of the
        // segment branch in P5 so the guard gates the queue too.)
        if (!PassesVestibularGuard(_activeSet, target)) return;

        if (FiveBandSegmentForSet(target) != FiveBandSegmentForSet(_activeSet))
        {
            // CROSS-SEGMENT (felt-intensity change) ⇒ QUEUE a proposal; do NOT apply,
            // do NOT surface (the engine cannot open a window — locked constraint).
            // Only overwrite an existing pending with a STRONGER (higher-Band) target
            // (propose-stronger + decline-memory friendly). The App surfaces it only
            // behind a userInitiated card-open; Accept/Decline/expiry route back here.
            if (_pendingSetMove is not { } existing || target.Band >= existing.Target.Band)
            {
                _pendingSetMove = (target, FiveBandNominalLevel(target),
                                   now.AddSeconds(SenseDefaults.SetMoveProposalTtlSeconds));
                AppendAdaptiveFitEvent("AdaptiveFit.SetMoveProposed",
                    $"queued cross-segment proposal {FormatSet(_activeSet)} → {FormatSet(target)} (L{_pendingSetMove.Value.TargetLevel}, samples={stats.SampleCount})");
            }
            return;
        }

        // SAME-SEGMENT silent auto-move (UNCHANGED from today): apply the move silently
        // and notify the machine so it enters Cooldown (soft-定型) before the next
        // evaluation. Capture the source before ApplySetMove repoints _activeSet so the
        // log reads source→target.
        GridCoord from = _activeSet;
        ApplySetMove(target);
        _adaptiveFitMachine.OnSetChange();
        AppendAdaptiveFitEvent("AdaptiveFit.SetMove",
            $"silent adjacent move {FormatSet(from)} → {FormatSet(target)} (L{_vimsTier}, samples={stats.SampleCount})");
    }

    /// <summary>Apply an Adaptive Fit set move (SPEC item 3). Clears the chatbot
    /// delta FIRST via the SAME offset-preserving transient-reset path
    /// <see cref="SetLevel"/> uses (Q5: <c>_userFovOffset=0</c> +
    /// <c>ResetToDefault</c> + <c>ResetTransientState</c>, PRESERVING the earned
    /// habituation <c>_offset</c> / C5), THEN re-seeds the displayed level on the
    /// new curve (Q4 felt-snap). Never touches <c>_offset</c>. Caller holds
    /// <c>_gate</c>.</summary>
    private void ApplySetMove(GridCoord target)
    {
        _activeSet = target;

        // Q5 first: clear the transient chatbot delta, preserving _offset.
        _userFovOffset = 0;
        _shader.ResetToDefault();
        _habituation.ResetTransientState();   // preserves _offset (Q4.5 / C5)
        Current.HabituationLevel  = _habituation.HabituationLevel;
        Current.HabituationPaused = _habituation.IsPaused;

        // SPEC item 4 — vestibular L9 floor: an advisory user's resolved level is
        // clamped to >= 9 on any set move (propose-stronger-only safety stance).
        // Non-advisory users keep their current rung. Keep the pending-debounce
        // bookkeeping in sync so UpdateVimsTier does not immediately walk it back.
        // Re-rooted (Alpha-2 addendum): IsVestibularActive ORs the persistent
        // auto-profiling signal with the legacy calibration advisory.
        if (IsVestibularActive && _vimsTier < 9)
        {
            _vimsTier             = 9;
            _pendingTierCandidate = _vimsTier;
            _pendingTierSinceUtc  = DateTime.MinValue;
        }

        // Q4 then: re-seed the displayed level on the NEW set's curve. The tier
        // value itself is unchanged for non-advisory users (felt-snap keeps the
        // rung); we re-apply the new curve's FOV floor on top of the recomposed
        // radius.
        Current.VimsTier = _vimsTier;
        Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);
        LevelParams lp = SetGrid.Curve(_activeSet).ForLevel(_vimsTier);
        Current.FovRadiusPercent = Math.Max(Current.FovRadiusPercent, lp.FovFloorPercent);
        // Alpha-2 (selector→renderer): the NEW set's mask depth reaches the overlay
        // immediately on a silent move (this runs inside the flag-on selector tick).
        Current.MaskPeakAlphaOverride = lp.PeakMaskAlpha;
        // Alpha-2 (selector→renderer): re-seed the peripheral-grid baseline from the NEW set's
        // curve. The Q5 _shader.ResetToDefault() above already reset GridVisibility to the
        // hardcoded 0.75 default; replacing it with lp.GridVisibility makes the per-set value
        // the live baseline the chatbot then modulates. GridVisibility is a SHADER param, so it
        // flows to the overlay via Current.Shader (== _shader) — no separate override field is
        // needed, unlike mask depth (a tier-table value the overlay reads directly). set0 ⇒
        // 0.75 == ShaderParams.DefaultGridVisibility (a no-op today); a future per-set
        // GridVisibility re-baselines the grid on a set move — previously lp.GridVisibility was
        // resolved into every curve but read nowhere (the second latent drift the audit flagged).
        _shader.GridVisibility = lp.GridVisibility;
    }

    /// <summary>Vestibular guard (SPEC item 4): for a user carrying the vestibular
    /// advisory (the SAME in-memory signal the hard-cap path keys off —
    /// <c>LastCalibrationResult.MedicalAdvisory == "vestibular"</c>), BLOCK any move
    /// toward a WEAKER band (lower <see cref="GridCoord.Band"/>) — propose-stronger-
    /// only. A move that holds or raises Band is allowed. Non-advisory users are
    /// unaffected. (The L9 floor / 0.20 mask floor are enforced at resolve time by
    /// the grid's clamps; this method governs the directional move gate.)</summary>
    private bool PassesVestibularGuard(GridCoord current, GridCoord target)
    {
        // Re-rooted (Alpha-2 addendum): IsVestibularActive ORs the persistent
        // auto-profiling signal with the legacy calibration advisory.
        if (!IsVestibularActive) return true;

        // Propose-stronger-only: a move that LOWERS the intensity Band is blocked.
        return target.Band >= current.Band;
    }

    // ── Adaptive Fit P5 (propose-confirm) public surface ─────────────────────
    // The App reads the pending proposal ONLY behind a userInitiated card-open and
    // routes the user's Accept / Decline / defer / expiry back here. The engine
    // NEVER opens a window (locked constraint). All in-memory; nothing persisted.

    /// <summary>P5: pure read of the pending cross-segment proposal (null if none).
    /// Does NOT mutate or expire — the selector tick owns expiry via the injected
    /// clock; the App filters by wall-clock at display time. Caller does not need
    /// the flag set; a null pending just returns null.</summary>
    public (int Band, int Shape, int TargetLevel, DateTime ExpiresAtUtc)? PeekPendingSetMove()
    {
        lock (_gate)
        {
            if (_pendingSetMove is not { } p) return null;
            return (p.Target.Band, p.Target.Shape, p.TargetLevel, p.ExpiresAtUtc);
        }
    }

    /// <summary>P5: the user ACCEPTED the pending proposal. Re-asserts adjacency +
    /// the vestibular guard (defensive — state may have moved since the queue),
    /// applies the move via the offset-preserving <see cref="ApplySetMove"/> (Q4.5:
    /// never zeroes <c>_offset</c>), then snaps the felt level UP to the target
    /// level — RESPECTING the L9 floor for vestibular users — and enters Cooldown.
    /// Returns false (clearing the pending) if there is nothing pending or the
    /// re-assert fails.</summary>
    public bool ConfirmPendingSetMove()
    {
        lock (_gate)
        {
            if (_pendingSetMove is not { } pend) return false;
            GridCoord target = pend.Target;
            int targetLevel  = pend.TargetLevel;

            // Re-assert the move is still valid (adjacency + propose-stronger-only).
            if (GridCoord.ManhattanDistance(_activeSet, target) != 1
                || !PassesVestibularGuard(_activeSet, target))
            {
                _pendingSetMove = null;
                return false;
            }

            GridCoord from = _activeSet;
            ApplySetMove(target); // offset-preserving (Q4.5); never zeroes _offset

            // Snap the felt level UP to the target, respecting the vestibular L9 floor.
            // SetLevel also locks _gate — re-entrant, so this is safe.
            int confirmLevel = IsVestibularActive ? Math.Max(targetLevel, 9) : targetLevel;
            SetLevel(confirmLevel, LevelChangeCause.ProposalConfirm);

            _adaptiveFitMachine.OnSetChange();
            _pendingSetMove = null;
            AppendAdaptiveFitEvent("AdaptiveFit.SetMoveConfirmed",
                $"user-confirmed cross-segment move {FormatSet(from)} → {FormatSet(target)} (L{confirmLevel})");
            return true;
        }
    }

    /// <summary>P5: the user DECLINED the pending proposal. Backs the selector off via
    /// <see cref="AdaptiveFitStateMachine.OnProposalDeclined"/> (doubles the remaining
    /// cooldown) and clears the pending. Idempotent (no-op if nothing pending).</summary>
    public void DeclinePendingSetMove()
    {
        lock (_gate)
        {
            if (_pendingSetMove is null) return;
            _adaptiveFitMachine.OnProposalDeclined();
            GridCoord target = _pendingSetMove.Value.Target;
            _pendingSetMove = null;
            AppendAdaptiveFitEvent("AdaptiveFit.SetMoveDeclined",
                $"user-declined cross-segment proposal → {FormatSet(target)}");
        }
    }

    /// <summary>P5: clear the pending proposal WITHOUT any back-off — used for the
    /// card's 15s in-card expiry / defer path (neutral; the user may engage next
    /// time). Idempotent (no-op if nothing pending).</summary>
    public void ClearPendingSetMove()
    {
        lock (_gate) { _pendingSetMove = null; }
    }

    /// <summary>P5: a manual +/- nudge from the card path (which never reaches
    /// <see cref="ApplyUserFeedback"/>) defers the selector AND retracts a stale
    /// suggestion. Gated on the selector flag so it is inert while OFF.</summary>
    public void NotifyManualLevelNudge()
    {
        lock (_gate)
        {
            if (!_adaptiveFitSelectorEnabled) return;
            _adaptiveFitMachine.OnManualAdjust();
            _pendingSetMove = null;
        }
    }

    /// <summary>The set's NOMINAL 1-based level (1..12): its grid
    /// <see cref="GridCoord.Band"/> (0..9) mapped linearly onto the 12-rung ladder
    /// (round(1 + band·11/9), clamped 1..LevelCount). Factored out so both
    /// <see cref="FiveBandSegmentForSet"/> (segment bucketing) and the P5
    /// propose-confirm queue (the confirmed felt level) share one definition.</summary>
    private static int FiveBandNominalLevel(GridCoord set)
    {
        // Band 0..9 → nominal level 1..12 (linear, monotonic). round(1 + band·11/9).
        int nominalLevel = (int)Math.Round(
            1.0 + set.Band * (SetCurve.LevelCount - 1) / (double)GridCoord.MaxBand);
        return Math.Clamp(nominalLevel, 1, SetCurve.LevelCount);
    }

    /// <summary>Map a set to its 5級 segment via the set's nominal level (SPEC item
    /// 2 fallback — no <c>SetGrid.Segment</c> helper exists, and the canonical
    /// <c>TierColorPalette.BandForTier</c> lives in the App layer which
    /// Infrastructure cannot reference). The set's nominal level is its grid
    /// <see cref="GridCoord.Band"/> (0..9) mapped linearly onto the 12-rung ladder,
    /// then bucketed with the SAME ≤2/≤4/≤7/≤10/else mapping the App's BandForTier
    /// uses. Set0 (Band 5) ⇒ nominal L7 ⇒ Medium segment, matching its default-L5
    /// Medium 動態感.</summary>
    private static int FiveBandSegmentForSet(GridCoord set)
    {
        int nominalLevel = FiveBandNominalLevel(set);
        return nominalLevel switch
        {
            <= 2  => 0, // 極高
            <= 4  => 1, // 高
            <= 7  => 2, // 中  (Set0 lands here)
            <= 10 => 3, // 低
            _     => 4, // 極低
        };
    }

    private static string FormatSet(GridCoord c) => $"set(B{c.Band},S{c.Shape})";

    /// <summary>Emit an <c>AdaptiveFit.*</c> event-log entry (durable-prefixed, so
    /// it round-trips through <see cref="IEventLogStore.ReadDurable"/>). Best-effort;
    /// a logging failure never crashes the engine.</summary>
    private void AppendAdaptiveFitEvent(string category, string message)
    {
        if (_eventLog is null) return;
        try
        {
            _eventLog.Append(new EventLogEntry
            {
                Timestamp    = DateTime.UtcNow,
                Category     = category,
                Message      = message,
                Ofi          = Current.RawOpticalFlowIndex,
                Pressure     = Current.Pressure,
                FovRadius    = Current.FovRadiusPercent,
                MaskSnapshot = Current.MaskActive ? "ON" : "off",
            });
        }
        catch { /* best-effort; event-log writes must never crash the engine */ }
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
            var prev = _habituation.CurrentProfile;
            _habituation.SetProfile(profile, source);
            // A7: re-seed the shader to the new profile's defaults on a profile CHANGE,
            // UNLESS the user has personalised the shader knobs (then their choices win).
            if (profile != prev && !_shaderUserModified)
                SeedShaderFromProfile(profile);
            Current.HabituationPaused = _habituation.IsPaused;
            MaybePersistProfileSnapshot();
        }
    }

    /// <summary>A7 (per-profile shader defaults): copy a profile's default shader knobs into
    /// the live (readonly) <c>_shader</c> instance and mirror onto <c>Current.Shader</c>.
    /// RESILIENT/TYPICAL keep today's <c>ShaderParams.Default</c>; SENSITIVE gets tightened
    /// defaults (see <c>HabituationProfileParams.ResolveDefaultsFor</c>). Caller holds _gate.</summary>
    private void SeedShaderFromProfile(HabituationProfile profile)
    {
        var d = HabituationProfileParams.ResolveDefaultsFor(profile);
        _shader.MaskDepth      = d.MaskDepth;
        _shader.GridVisibility = d.GridVisibility;
        _shader.FeatherWidth   = d.FeatherWidth;
        _shader.Clamp();
        Current.Shader = _shader;
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

    // ── Calibration application (Stage 2 of post-test calibration feature) ──
    // The CalibrationProcessor (Stage 1) produces a pure CalibrationResult.
    // This is where that result becomes engine state: effective trigger,
    // strike-limit override, and profile. The engine itself stays ignorant
    // of "scale" / "personal_pressure_scale" — those concepts live here in
    // the wrapper.
    //
    // Math priority (per user supplement):
    //   override exists -> trigger = override  (scale + adjustment IGNORED)
    //   else            -> trigger = (defaultTrigger / scale) + adjustment

    private CalibrationResult? _lastCalibrationResult;

    /// <summary>The most recent calibration result the wrapper applied via
    /// <see cref="ApplyCalibrationResult"/>. Null until calibration runs at
    /// least once. Read by the Stage 6 JSON-snapshot writer to surface the
    /// scale + overrides + advisory + dominance fields in habituation_profile.json.</summary>
    public CalibrationResult? LastCalibrationResult
    {
        get { lock (_gate) { return _lastCalibrationResult; } }
    }

    /// <summary>The <c>calibration_count</c> value the engine considers
    /// current. Initialised from the loaded snapshot at construction
    /// (Stage 7a), bumped in <see cref="ApplyCalibrationResult"/> for every
    /// non-Abandoned completion (Stage 7f), and round-tripped through
    /// <see cref="BuildProfileSnapshot"/> so the bumped value lands in the
    /// next on-disk write. Returns 0 when no snapshot was loaded (genuine
    /// first-run) or the loaded snapshot is a wipe-recovery synthetic — both
    /// mean the engine has no PERSISTED evidence of a prior calibration on
    /// this machine.
    /// <para>Distinct from <c>AppConfig.IsFirstRun</c> (machine-level first-run for
    /// the whole app): calibration history is tracked by THIS counter — they're
    /// different concepts and shouldn't share a signal.</para></summary>
    public int LoadedCalibrationCount
    {
        get { lock (_gate) { return _loadedCalibrationCount; } }
    }

    /// <summary>The engine's current effective spike-trigger pressure.
    /// Equals <see cref="SenseDefaults.TriggerPressure"/> until calibration
    /// applies an override / scale. Pass-through getter so Stage 6's snapshot
    /// writer and Stage 6/7's chatbot context block can include this value
    /// without poking at the engine field directly.</summary>
    public double HabituationEffectiveTrigger
    {
        get { lock (_gate) { return _habituation.EffectiveTrigger; } }
    }

    /// <summary>Active strike-limit-override, or null when the engine should
    /// use the profile default. Pass-through getter.</summary>
    public int? HabituationSevereStrikeLimitOverride
    {
        get { lock (_gate) { return _habituation.SevereStrikeLimitOverride; } }
    }

    /// <summary>UM1 telemetry: the engine's current habituation offset (pp),
    /// read-only. Pass-through getter used by the daily-behaviour collector to
    /// track net adaptation over the day — it only READS the offset and never
    /// resets or mutates it (Q4.5-safe).</summary>
    public int HabituationOffsetPp
    {
        get { lock (_gate) { return _habituation.HabituationOffset; } }
    }

    /// <summary>Alpha-2 addendum: the current state of the PERSISTENT vestibular protection
    /// (the auto-profiling signal toggled by the one-time field / Settings). Source of truth
    /// for the Settings "regret" toggle's checked state. Does NOT reflect the legacy
    /// calibration advisory (that path is flag-off and uses its own snapshot fields).</summary>
    public bool IsVestibularProtectionEnabled
    {
        get { lock (_gate) { return _vestibularProtection; } }
    }

    /// <summary>Apply a post-test calibration result. Computes the effective
    /// trigger (override > scale + adjustment) and pushes it to the engine,
    /// pushes the strike-limit override (vestibular case), and swaps the
    /// profile if it differs. Fires a profile-snapshot persist on the way
    /// out so the change lands in habituation_profile.json.</summary>
    public void ApplyCalibrationResult(CalibrationResult result)
    {
        lock (_gate)
        {
            _lastCalibrationResult = result;

            // Stage 7f: bump persisted calibration_count for Complete/Partial.
            // Abandoned runs (no Q3 set, user cancelled the card before card 3)
            // do NOT increment per spec §3 — they represent a user who
            // started the form and walked away, not a calibration the system
            // has any data from. Without this bump, Stage 7a's branch reads
            // a forever-zero count and routes every demo end to the buttons
            // UI even after the user has calibrated dozens of times.
            if (result.Completeness != CalibrationCompleteness.Abandoned)
            {
                _loadedCalibrationCount++;
                _lastCalibrationAtUtc = DateTime.UtcNow;
                _profileSnapshotDirty = true;
            }

            // Compute effective trigger. Override wins outright (no further
            // arithmetic); otherwise scale-then-adjust.
            double trigger;
            if (result.TriggerThresholdOverride is int ov)
            {
                trigger = ov;
            }
            else
            {
                // Defensive clamp belt-and-suspenders — processor already clamps,
                // but we don't trust callers to come exclusively through it.
                double scale = Math.Clamp(result.PersonalPressureScale,
                                          CalibrationProcessor.PersonalPressureScaleMin,
                                          CalibrationProcessor.PersonalPressureScaleMax);
                trigger = (SenseDefaults.TriggerPressure / scale)
                        + result.TriggerThresholdAdjustment;
            }

            _habituation.EffectiveTrigger          = trigger;
            _habituation.SevereStrikeLimitOverride = result.SevereStrikeLimitOverride;
            // M3 Phase 7d: track the applied scale so the snapshot reflects it
            // and a subsequent chatbot AdjustSensitivity has the right baseline
            // to compare against.
            _personalPressureScale = result.TriggerThresholdOverride is null
                ? Math.Clamp(result.PersonalPressureScale,
                             CalibrationProcessor.PersonalPressureScaleMin,
                             CalibrationProcessor.PersonalPressureScaleMax)
                : 1.0;

            // Profile change is idempotent on no-change. Source tagged as
            // "calibration_demo" so the persistence snapshot reflects the
            // origin. NOTE: an in-place re-calibration to the SAME profile
            // does NOT reset strikes — strikes are only profile-scoped per Q6.
            if (_habituation.CurrentProfile != result.Profile)
                _habituation.SetProfile(result.Profile, "calibration_demo");

            // Refresh bindable state so the dashboard reflects the change
            // immediately without waiting for the next Update tick.
            Current.HabituationLevel  = _habituation.HabituationLevel;
            Current.HabituationPaused = _habituation.IsPaused;
            Current.FovRadiusPercent  = ComposeFovRadius(Current.Pressure);

            MaybePersistProfileSnapshot();
        }
    }

    /// <summary>M3 Phase 7d: apply a chatbot-proposed engine override that
    /// has already passed the Processor's validation (7c) and the Applicator's
    /// defensive intercept (7f). The caller is responsible for those gates;
    /// this method just performs the write atomically under the engine lock.
    /// <para>Returns true iff the write actually happened. False for unknown
    /// intent / unparsable value (defensive; processor should have rejected
    /// earlier).</para></summary>
    public bool ApplyChatbotOverride(OverrideIntent intent, object proposedValue)
    {
        lock (_gate)
        {
            bool applied;
            switch (intent)
            {
                case OverrideIntent.AdjustSensitivity:
                {
                    if (!TryUnwrapDouble(proposedValue, out double scale))
                    {
                        applied = false;
                        break;
                    }
                    scale = Math.Clamp(scale,
                                       CalibrationProcessor.PersonalPressureScaleMin,
                                       CalibrationProcessor.PersonalPressureScaleMax);
                    _personalPressureScale = scale;
                    // Same arithmetic as the calibration path:
                    //   effective_trigger = default_trigger / scale
                    // so lower scale (less sensitive) raises the trigger.
                    _habituation.EffectiveTrigger = SenseDefaults.TriggerPressure / scale;
                    applied = true;
                    break;
                }

                case OverrideIntent.SwitchProfile:
                {
                    // Reading A (2026-06-06): chatbot profile-switch removed — profile is
                    // calibration-only. Unreachable in normal flow (Processor rejects
                    // SwitchProfile); kept as a defensive no-op. Do NOT call
                    // _habituation.SetProfile; leave the profile unchanged.
                    applied = false;
                    break;
                }

                case OverrideIntent.AdjustStrikeLimit:
                {
                    if (!TryUnwrapInt(proposedValue, out int limit))
                    {
                        applied = false;
                        break;
                    }
                    _habituation.SevereStrikeLimitOverride = limit;
                    applied = true;
                    break;
                }

                default:
                    applied = false;
                    break;
            }

            if (applied)
            {
                _profileSnapshotDirty = true;
                Current.HabituationLevel = _habituation.HabituationLevel;
                Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);
                MaybePersistProfileSnapshot();
            }
            return applied;
        }
    }

    private static bool TryUnwrapDouble(object value, out double result)
    {
        switch (value)
        {
            case double d:  result = d;        return true;
            case float f:   result = f;        return true;
            case long l:    result = l;        return true;
            case int i:     result = i;        return true;
            case string s when double.TryParse(s,
                              System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture,
                              out double parsed):
                            result = parsed;   return true;
            default:        result = 0;        return false;
        }
    }

    private static bool TryUnwrapInt(object value, out int result)
    {
        switch (value)
        {
            case int i:    result = i;          return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                           result = (int)l;     return true;
            case double d when d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue:
                           result = (int)d;     return true;
            case string s when int.TryParse(s,
                              System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture,
                              out int parsed):
                           result = parsed;     return true;
            default:       result = 0;          return false;
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
        // M1 (Phase 6-pre): change-detection + event-log emission run
        // INDEPENDENT of profile-store persistence. The original early
        // exit on `_profileStore is null` only protected the file-IO
        // Save() call; it shouldn't gate the event-log writes (which go
        // to a separate IEventLogStore for the chatbot's trajectory
        // builder). Trackers (_lastSavedX) are also updated regardless
        // so the next dirty-check has a fresh baseline.

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

        // Emit habituation lifecycle events BEFORE updating the trackers
        // so WriteHabituationLifecycleEvents can compare old vs new.
        WriteHabituationLifecycleEvents(level, strikes, es, profile, source, spike);

        // Profile-store persistence is a separate, optional concern.
        if (_profileStore is not null)
        {
            // W2.0-C: persist through the store's atomic Update so the EM2 re-read
            // (carry-forward of the chatbot/calibration-owned fields) and the Save
            // happen under ONE store-lock span. Previously BuildProfileSnapshot did
            // its own unsynchronised Load and the Save was separate, so the
            // chatbot's concurrent LongitudinalObservationWriter could interleave
            // and clobber an appended observation (or have its write reverted).
            // BuildProfileSnapshot returns null to skip the write on a transient
            // read failure (#2: don't clobber), which Update honours.
            _profileStore.Update(BuildProfileSnapshot);
        }

        _lastSavedLevel         = level;
        _lastSavedStrikeCount   = strikes;
        _lastSavedEngineState   = es;
        _lastSavedProfile       = profile;
        _lastSavedProfileSource = source;
        _lastSavedSpikeAtUtc    = spike;
        _profileSnapshotDirty   = false;
    }

    /// <summary>M1 (Phase 6-pre): emit one EventLogEntry per kind of
    /// habituation change detected by <see cref="MaybePersistProfileSnapshot"/>.
    /// Categories use the <c>Habituation.*</c> prefix so the Phase 6c
    /// trajectory builder can grep:
    /// <list type="bullet">
    ///   <item><c>Habituation.Profile</c> — profile / source flip</item>
    ///   <item><c>Habituation.LevelChanged</c> — level Δ &gt; 5 pp</item>
    ///   <item><c>Habituation.Spike</c> — strike fired (sensor or chatbot)</item>
    ///   <item><c>Habituation.StateChange</c> — Running ↔ Cooldown ↔
    ///         PausedAwaitingUserConfirm</item>
    /// </list>
    /// The state-snapshot fields (Ofi, Pressure, FovRadius, MaskSnapshot)
    /// are filled from <see cref="Current"/> at write-time so the trajectory
    /// builder sees the sensor context surrounding each event.</summary>
    private void WriteHabituationLifecycleEvents(
        double level, double strikes, EngineState es,
        HabituationProfile profile, string source, DateTime? spike)
    {
        if (_eventLog is null) return;

        if (profile != _lastSavedProfile || source != _lastSavedProfileSource)
        {
            AppendHabituationEvent("Habituation.Profile",
                $"{_lastSavedProfile} → {profile} (source: {source})");
        }

        if (Math.Abs(level - _lastSavedLevel) > 5.0)
        {
            string direction = level > _lastSavedLevel ? "rose" : "fell";
            AppendHabituationEvent("Habituation.LevelChanged",
                $"level {direction} {_lastSavedLevel:F1} → {level:F1}");
        }

        if (spike != _lastSavedSpikeAtUtc && spike is not null)
        {
            AppendHabituationEvent("Habituation.Spike",
                $"severe spike registered; strike_count {strikes:F1}");
        }
        else if (strikes != _lastSavedStrikeCount)
        {
            // Strike count moved without a NEW spike timestamp — this is the
            // reset path (strikes zeroed by full-stable-window OR by hitting
            // the limit). Still worth logging as a strike-counter event.
            AppendHabituationEvent("Habituation.Spike",
                $"strike_count {_lastSavedStrikeCount:F1} → {strikes:F1}");
        }

        if (es != _lastSavedEngineState)
        {
            AppendHabituationEvent("Habituation.StateChange",
                $"{_lastSavedEngineState} → {es}");
        }
    }

    /// <summary>WAVE W2.0-E: emit the boot-time fail-open recovery record when a
    /// wired profile store returns a null snapshot and the engine silently reverts
    /// to the RESILIENT default. Routes through the SAME durable
    /// <see cref="IEventLogStore"/> sink as the habituation lifecycle events so the
    /// silent calibration-state loss round-trips <see cref="IEventLogStore.ReadDurable"/>.
    /// Best-effort and null-safe so it is callable from the constructor: a missing
    /// event log or a logging failure never blocks engine boot (fail-open).</summary>
    private void AppendRecoveryEvent(string category, string message)
    {
        if (_eventLog is null) return;
        try
        {
            _eventLog.Append(new EventLogEntry
            {
                Timestamp    = DateTime.UtcNow,
                Category     = category,
                Message      = message,
                Ofi          = Current.RawOpticalFlowIndex,
                Pressure     = Current.Pressure,
                FovRadius    = Current.FovRadiusPercent,
                MaskSnapshot = Current.MaskActive ? "ON" : "off",
            });
        }
        catch { /* best-effort; recovery diagnostics must never crash engine boot */ }
    }

    private void AppendHabituationEvent(string category, string message)
    {
        try
        {
            _eventLog!.Append(new EventLogEntry
            {
                Timestamp    = DateTime.UtcNow,
                Category     = category,
                Message      = message,
                Ofi          = Current.RawOpticalFlowIndex,
                Pressure     = Current.Pressure,
                FovRadius    = Current.FovRadiusPercent,
                MaskSnapshot = Current.MaskActive ? "ON" : "off",
            });
        }
        catch { /* best-effort; event-log writes must never crash the engine */ }
    }

    /// <summary>EM2 carry-forward snapshot builder, now invoked as the mutator of
    /// <see cref="IHabituationProfileStore.Update"/> (W2.0-C). <paramref name="existing"/>
    /// is the snapshot the store read for us INSIDE its lock — a consistent view,
    /// not a separate unsynchronised Load — so the carry-forward of fields owned by
    /// OTHER writers of the same habituation_profile.json (the chatbot's
    /// LongitudinalObservationWriter + the calibration applicator) can no longer
    /// race against their concurrent writes.</summary>
    private HabituationProfileSnapshot? BuildProfileSnapshot(HabituationProfileSnapshot? existing)
    {
        var p = _habituation.CurrentParams;
        // EM2: read-modify-write. This builds a FRESH snapshot from in-memory
        // engine state, but several fields are owned by OTHER writers of the SAME
        // habituation_profile.json (the chatbot's LongitudinalObservationWriter and
        // the calibration applicator). Carry those fields forward from <existing>
        // so an engine persist no longer silently wipes them (the chatbot's
        // longitudinal memory + the calibration_* enums).
        // #2 (EM2 hardening, review): if a profile EXISTED at boot but the in-lock
        // re-read is null, that is a transient read failure (file briefly locked /
        // corrupt), NOT a first run. Rebuilding-and-overwriting here would null-fill
        // MedicalAdvisoryShown + the calibration_* fields and wipe the vestibular
        // safety advisory. Signal "skip this persist" by returning null.
        if (existing is null && _profileExistedAtLoad) return null;
        return new HabituationProfileSnapshot
        {
            // UM2: carry the loaded schema version FORWARD. The store hands us a
            // post-load `existing`: a v1 file is null-filled to v2, a v2 file is
            // migrated to v3, a v3 file stays v3 — so this preserves the documented
            // v1 → v2 → v3 progression instead of jumping a v1-loaded profile straight
            // to v3 on the engine's next persist. For a genuine first write (no existing
            // file) fall back to the DTO's current default (no hard-coded literal, so it
            // can't silently desync from the snapshot DTO).
            SchemaVersion           = existing?.SchemaVersion ?? new HabituationProfileSnapshot().SchemaVersion,
            UpdatedAtIso            = DateTime.UtcNow.ToString("o"),
            Profile                 = _habituation.CurrentProfile.ToString().ToUpperInvariant(),
            ProfileSource           = _habituation.ProfileSource,
            HabituationLevel        = _habituation.HabituationLevel,
            HabituationOffsetPp     = _habituation.HabituationOffset,
            HabituationOffsetCapPp  = p.HabOffsetCapPp,
            CurrentVimsTier         = _vimsTier,
            SevereStrikeCount       = _habituation.SevereStrikeCount,
            SevereStrikeLimit       = p.SevereStrikeLimit,
            // Phase 7 write-durability: persist a NON-vestibular user-confirmed chatbot strike
            // override so it survives restart. For a vestibular user we persist null and let the
            // vestibular hard-cap (=1) be re-derived from VestibularProtectionEnabled on load —
            // that path intentionally WINS, so we must not double-source it here.
            SevereStrikeLimitOverride = IsVestibularActive ? (int?)null : _habituation.SevereStrikeLimitOverride,
            EngineState             = EngineStateToString(_habituation.CurrentEngineState),
            ExternallyPaused        = _habituation.IsExternallyPaused,
            AwaitingUserConfirm     = _habituation.IsAwaitingUserConfirm,
            LastSpikeAtIso          = _habituation.LastSpikeAtUtc?.ToString("o"),
            NextStepEligibleAtIso   = null, // Computed projection — deferred to a future phase
            // Stage 7f: persist the calibration_count + last_calibration_at_iso
            // pair so a future engine boot sees the bumped value. The other v2
            // calibration_* fields (completeness, path, responses, confidence,
            // advisory) remain at CLR defaults — Appendix B items #2-#4 own
            // those.
            CalibrationCount        = _loadedCalibrationCount,
            LastCalibrationAtIso    = _lastCalibrationAtUtc?.ToString("o"),
            // M3 Phase 7d: persist the tracked scale so the chatbot's next
            // context-build sees the current value (previously the DTO
            // default 1.0 was emitted regardless of the applied scale).
            PersonalPressureScale   = _personalPressureScale,
            ParamsSnapshot          = new HabituationParamsSnapshot
            {
                StableWindowMs  = p.StableWindowMs,
                StepIntervalMs  = p.StepIntervalMs,
                StepMagnitudePp = p.StepMagnitudePp,
            },
            // EM2 — fields owned by OTHER writers of this file; preserved from the
            // existing on-disk snapshot so the engine's persist does not wipe them.
            LongitudinalObservations = existing?.LongitudinalObservations ?? new(),
            // UM2: the monthly observation buckets are owned by LongitudinalObservation
            // Writer; carry them forward too or an engine persist would erase them.
            LongitudinalObservationsV2 = existing?.LongitudinalObservationsV2 ?? new(),
            CalibrationCompleteness  = existing?.CalibrationCompleteness,
            CalibrationPath          = existing?.CalibrationPath,
            CalibrationResponses     = existing?.CalibrationResponses,
            SymptomDominancePrimary  = existing?.SymptomDominancePrimary,
            // Alpha-2 addendum: an auto-profiling vestibular user (no calibration) must
            // still surface the advisory to the chatbot's [habituation_state] block,
            // which reads MedicalAdvisoryShown. Otherwise carry the calibration value forward.
            MedicalAdvisoryShown     = _vestibularProtection
                                         ? MedicalAdvisory.Vestibular
                                         : existing?.MedicalAdvisoryShown,
            CalibrationConfidence    = existing?.CalibrationConfidence,
            // Alpha-2 addendum: persist the auto-profiling vestibular signal so it
            // survives restarts (restored in the ctor load path).
            VestibularProtectionEnabled = _vestibularProtection,
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
        // Alpha-2 (selector→renderer): the discretizer GAIN comes from the ACTIVE set's curve
        // when the selector is ON. set0.Scale == 11.0, so flag-ON at set0 stays byte-identical
        // to the old literal; a future per-set Scale would (now) actually change the
        // pressure→level mapping. Previously lp.Scale was resolved into every curve but the
        // discretizer ignored it (hardcoded 11.0), so a non-default set silently kept the native
        // span — the latent drift the conformance audit flagged. Flag-OFF keeps the native
        // 12-rung span literal, so every flag-off path is unchanged.
        double scale  = _adaptiveFitSelectorEnabled
            ? SetGrid.Curve(_activeSet).ForLevel(_vimsTier).Scale
            : (SetCurve.LevelCount - 1);     // 11.0 — native 12-level span
        double levelF = 1.0 + (scale * t);   // 1..12 (native 12-level scale — Adaptive Fit Q2=B)

        // ── Fast-onset bypass ────────────────────────────────────────────────
        // A genuine pressure surge can jump levelF by several units in one Update tick.
        // The strict ±1/tick limiter would then need 1.8 s of debounce ticks to catch up
        // (longer than the natural pressure surge itself). When the gap exceeds
        // VimsTierFastOnsetGap we therefore snap straight to the nearest integer tier.
        double gap = levelF - _vimsTier;
        if (gap >= SenseDefaults.VimsTierFastOnsetGap)
        {
            int snap = Math.Clamp((int)Math.Round(levelF), _vimsTier + 1, 12);
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
            candidate  = Math.Min(12, _vimsTier + 1);
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
