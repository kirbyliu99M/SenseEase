using SenseEase.Core.Abstractions;
using SenseEase.Core.Adaptation;
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
    private readonly IProfileEvalHistoryStore? _profileEvalHistory;   // B6 Stage 4b streak store (clear-on-reset)
    private readonly IHabituationStore? _habituationStore;
    private readonly IHabituationProfileStore? _profileStore;
    private double _personalizationAccumulator;
    private int _lastSavedOffset;
    private readonly bool _profileExistedAtLoad; // #2 (EM2 hardening): did a profile snapshot exist at boot?
    private int      _vimsTier = 1;
    private int      _pendingTierCandidate = 1;
    private DateTime _pendingTierSinceUtc  = DateTime.MinValue;

    // ── LOGIC-13 / D-5: fresh-seed onboarding protection ──────────────────────
    // ApplyAutoProfilingStartDefaults seeds a fresh user at the L5「中」 median; without
    // protection the first calm Update tick fast-releases it straight to L1 (gap 4 >=
    // VimsTierFastReleaseGap), so the "fresh users start at L5" intent is never felt.
    // While _onboardingProtectionActive, UpdateVimsTier HOLDS the seeded tier against
    // downward movement until EITHER a genuine pressure-driven INCREASE occurs (the user
    // actually needs more protection — normal projection resumes) OR a fixed settling
    // period elapses (whichever first). This is deliberate onboarding protection per D-5,
    // NOT a change to tier projection. Defaults inert (active=false) so a non-auto-profiled
    // engine — every existing test that does not call the seeder — is byte-identical.
    private bool   _onboardingProtectionActive;
    private double _onboardingElapsedSeconds;

    // UI "Overlay Always ON" / Auto-Protect hard-latch: while true, UpdateVimsTier FREEZES the displayed
    // tier at the pinned level (set via SetAutoLevelHold). Pressure / the habituation offset+strike loops /
    // the B5/B6 auto-adaptation all keep running — only the pressure→tier PROJECTION is suspended. Inert
    // by default (false) so every non-toggled path is byte-identical.
    private bool   _autoLevelHold;

    // Fixed settling period (seconds of accumulated Update dt) the seeded tier is held
    // against fast-release before normal projection resumes. NEW local constant (no
    // SenseDefaults value change, per the wave rules) — kept conservative at one
    // SettlingMinSeconds window so the median is felt for a meaningful onboarding moment
    // without overriding a genuine pressure surge (which ends protection early regardless).
    private const double OnboardingProtectionSeconds = SenseDefaults.SettlingMinSeconds;

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
    // field / Settings toggle) OR restored from the snapshot at load. D-13: the legacy
    // calibration advisory path is deleted, so this field is now the SOLE source the
    // IsVestibularActive helper reads — EVERY vestibular consumer keys off it. Read by
    // the selector guards (ApplySetMove L9 floor / PassesVestibularGuard), which only
    // run when the selector flag is ON, so re-rooting them is byte-identical when the
    // selector is OFF.
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

    // B1: the App layer pushes the recent measured-FMS aggregate (mean 0..20 + report count over the
    // same durable window) here via UpdateRecentFmsContext, because symptom_log.jsonl lives in the App
    // data root (out of this Infrastructure layer's reach). The selector consumes it as the OUTCOME
    // term, gated on count > 0. (0,0) ⇒ no FMS bias until the App supplies data.
    private double _recentFmsMean;
    private int    _recentFmsCount;

    // B3 (per-app learning): the App layer pushes the coarse, consent-gated foreground key
    // ("category/app", or null when D-16 consent is off / app unknown) here via
    // NotifyForegroundAppCategory, because the foreground filter lives in the App layer. The
    // durable-event appenders stamp it onto EventLogEntry.ForegroundKey so the per-app stats
    // pipeline can bucket history by app. null ⇒ the global bucket (today's behaviour).
    // Set/read only on the dispatcher thread (engine tick + the App push), like _recentFms*,
    // so no lock is needed.
    private string? _currentForegroundKey;

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

    // ── LOGIC-03: off-dispatcher profile persistence ──────────────────────────
    // MaybePersistProfileSnapshot runs on the UI dispatcher under _gate; the
    // _profileStore.Update call there does synchronous file IO whose AtomicJsonWriter
    // .TryRead can Thread.Sleep up to ~400 ms under AV file locks — a real UI freeze on
    // this machine. To move that IO off the dispatcher we capture the snapshot inputs under
    // _gate (no live-state race) and run _profileStore.Update on a SINGLE-WRITER serialized
    // background queue (_persistTail chains one Task after another so writes never overlap
    // and ORDER is preserved). W2.0-C atomicity is intact: the store's own per-path lock
    // still spans Load→mutate→Save, and the mutate closure reads ONLY the captured snapshot
    // + the store's in-lock `existing` (never live engine state).
    //
    // OPT-IN (default OFF → fully synchronous). While OFF the persist runs inline exactly as
    // before, so every existing read-after-write test (e.g. HabituationProfileStoreTests'
    // construct-engine → engine.Update → store.Load asserts) stays byte-identical and
    // deterministic. The App flips this ON at startup for the production dispatcher; tests
    // that want the async path can flip it then call FlushPendingPersists() for determinism.
    private bool                  _backgroundPersistEnabled;
    private Task                  _persistTail = Task.CompletedTask;
    private readonly object       _persistQueueGate = new();

    // M1 (Phase 6-pre): event-log writer for habituation lifecycle. Parallel
    // path to the snapshot persistence (file IO via _profileStore) — this
    // emits one EventLogEntry per change so the chatbot's Phase 6c trajectory
    // builder can summarise the user's habituation arc by querying the event
    // store. Optional dependency — null in unit-test contexts.
    private readonly IEventLogStore? _eventLog;

    // Stage 7a: calibration_count the engine considers current. Initialised from the
    // loaded snapshot at construction and round-tripped by BuildProfileSnapshot.
    // D-13: the old runtime bump in ApplyCalibrationResult is gone (explicit
    // calibration cancelled), so this is now LOAD-SEEDED ONLY — a machine that
    // calibrated under the old flow keeps its persisted count across upgrades.
    // Surfaced via the LoadedCalibrationCount property below.
    // Wipe-recovery synthetics arrive with the loader-emitted default of 0,
    // which is the desired behaviour: recovered users start fresh.
    private int                   _loadedCalibrationCount;

    // Stage 7f: UTC timestamp of the most recent non-Abandoned calibration, for the
    // last_calibration_at_iso v2 snapshot field. D-13: now set only on the snapshot
    // LOAD path (the runtime ApplyCalibrationResult that stamped it is deleted).
    private DateTime?             _lastCalibrationAtUtc;

    // M3 Phase 7d: current personal_pressure_scale value. D-13: the old
    // ApplyCalibrationResult writer is gone; the value is now restored on the load
    // path and updated by ApplyChatbotOverride(AdjustSensitivity). BuildProfileSnapshot
    // writes the tracked value out.
    // Default 1.0 = "no personalisation" (matches the snapshot DTO default).
    private double                _personalPressureScale = 1.0;

    // B5: the deterministic AdaptationGate's signed contribution to EffectiveTrigger, in pressure points
    // (negative = tighten/more protection, positive = relax/less protection). A DEDICATED channel,
    // separate from _personalPressureScale (the user/load channel): composed as EffectiveTrigger =
    // base + this. Persisted in the profile snapshot; re-applied at boot on top of the restored base;
    // zeroed by a Reset; clamped to ±AutoAdaptTriggerSpanPp at the engine boundary (mirrors
    // AdaptationGateConfig.Default.AutoTriggerSpanPp).
    private double                _autoAdaptTriggerDeltaPp = 0.0;
    private const double          AutoAdaptTriggerSpanPp   = 12.0;

    /// <summary>Audit-log Parameter the trigger auto-channel writes (shared so the App's budget derive
    /// matches the literal at <see cref="ApplyAutoAdaptDelta"/>).</summary>
    public const string AutoAdaptTriggerParameter      = "AutoAdaptTrigger";
    /// <summary>B6 Stage 4b: audit-log Parameter for a profile UP-move (Delta=0). Counts toward the shared
    /// weekly relax budget but NEVER toward net-relax/HasEverAutoRelaxed. Shared with the App's derive.</summary>
    public const string AutoReclassifyProfileParameter = "AutoReclassifyProfile";
    /// <summary>B6 Stage 4b: the profile_source an AUTO elasticity move stamps. Used by the Reset revert
    /// guard + the boot-load source whitelist.</summary>
    public const string AutoElasticitySource           = "auto_elasticity";

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
                               IEventLogStore? eventLog = null,
                               IProfileEvalHistoryStore? profileEvalHistory = null)
    {
        _adjustmentLog = adjustmentLog;
        _profileEvalHistory = profileEvalHistory;   // B6 Stage 4b: cleared on ResetPersonalization
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
            // B6 Stage 4a: alias-parse so legacy persisted strings (SENSITIVE/TYPICAL) still load
            // to the renamed members; new files already hold REACTIVE/ADAPTING/RESILIENT.
            if (HabituationProfileAlias.TryParse(savedSnapshot.Profile, out var prof))
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
            // D-13: nothing bumps this at runtime any more (ApplyCalibrationResult is
            // deleted); BuildProfileSnapshot round-trips the loaded value back out.
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
            // B5: restore the persisted auto-adaptation delta (clamped defensively). Re-applied on top
            // of the restored base trigger AFTER the vestibular branch below, via ApplyAutoDeltaToFreshBase.
            _autoAdaptTriggerDeltaPp = Math.Clamp(
                savedSnapshot.AutoAdaptTriggerDeltaPp, -AutoAdaptTriggerSpanPp, AutoAdaptTriggerSpanPp);
            // Alpha-2 audit fix: re-DERIVE the engine trigger from the restored scale.
            // Restoring the FIELD alone wasn't enough — EffectiveTrigger is what the engine
            // actually reads, and nothing on the LOAD path re-applied it (only the runtime
            // ApplyChatbotOverride does now that D-13 deleted the calibration path). So a
            // user who personalized their sensitivity last session booted back at the DEFAULT
            // trigger and silently lost it until their next chatbot turn. Same arithmetic as
            // the chatbot runtime path (effective_trigger = default / scale). The additive
            // TriggerThresholdAdjustment is not persisted (the snapshot carries only the scale),
            // so the scale is the best-effort reconstruction; scale==1.0 — which also covers the
            // absolute-override case that resets scale to 1.0 — leaves the engine default
            // untouched. The vestibular branch below runs AFTER this, so its TriggerPressure-50
            // hard-override correctly wins for vestibular users.
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
            // Vestibular-removal migration: the vestibular-protection feature is retired (the
            // app is now scoped to general comfort, not clinical vestibular disorders). An
            // existing user who persisted VestibularProtectionEnabled=true must NOT silently
            // drop to the default trigger-85/Typical; instead, gracefully re-home them into the
            // most-cautious surviving profile (Reactive = the B6 Stage 4a rename of SENSITIVE,
            // i.e. Cohort.Sensitive's no-net-relax floor + strike-limit-1 + the
            // PassesProtectionFloorGuard Reactive leg). They start STRONG and the B5/B6 loop
            // refines from there. _vestibularProtection stays permanently false (field default)
            // so the feature can NEVER re-arm. _profileSnapshotDirty forces the false/null
            // overwrite to persist this session, making the migration one-shot (the carried-
            // forward MedicalAdvisory.Vestibular is neutralized to null in MergeCarryForward).
            // MUST run before ApplyAutoDeltaToFreshBase() so the restored auto-delta composes on
            // the Reactive base. Field defaults false ⇒ no-op for snapshots that never had it.
            if (savedSnapshot.VestibularProtectionEnabled)
            {
                _habituation.SetProfile(HabituationProfile.Reactive, "vestibular_migration");
                // Clear any restored strike-override so the migrated user's strike limit comes SOLELY from
                // the profile (Reactive=1, later Resilient=2 if the loop moves them up) — never a stale
                // vestibular-grade hard cap lingering from an atypical/hand-edited snapshot.
                _habituation.SevereStrikeLimitOverride = null;
                _profileSnapshotDirty = true;
                AppendRecoveryEvent("Migration.VestibularRetired",
                    "vestibular protection retired (app scoped to general comfort); seeded " +
                    "most-cautious Reactive profile, auto-loop will refine");
            }

            // B5: re-apply the restored auto-adaptation delta on top of the base trigger the lines above
            // just established (scale-derived or vestibular-50), then re-assert the vestibular cap. Runs
            // once at boot, after the base is set, so the gate's contribution survives restarts.
            ApplyAutoDeltaToFreshBase();

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
            _habituation.SetPersonalization(CombinedFovCautionWeight(adjustmentLog));   // B2: FovRadius + LevelNudge
            // Re-seed the user-driven FOV offset from the persisted log so the
            // tightening the user accumulated last session (via chatbot feedback)
            // survives restarts. Without this, every restart silently reset the
            // direct user preference to 0 even though every adjustment was
            // dutifully appended to adjustments.jsonl. Clamp range matches the
            // accumulator's working range elsewhere in this file (-60..0).
            _userFovOffset = Math.Clamp((int)weight.NetDelta, -60, 0);
        }

        // CW-7 / LOGIC-10 (carried NE2): replay the user's persisted SHADER
        // personalization (MaskDepth / GridVisibility / FeatherWidth) the SAME way
        // _userFovOffset is replayed above — otherwise every restart silently reset a
        // user-accepted "darker mask" / "/grid off" / softer-edge choice back to the
        // profile default even though every adjustment was dutifully appended to
        // adjustments.jsonl. Seeds the per-profile base first (so untouched knobs keep
        // their profile defaults) THEN layers the user deltas (A7 _shaderUserModified
        // pattern: a touched knob marks the shader user-owned so a later profile change
        // won't re-seed over the user's choice).
        ReplayShaderPersonalizationFromLog();

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
        Current.UserFovOffsetPp  = _userFovOffset; // honest display signal — mirror beside the compose

        // Seed the persistence trackers to "no change since now". If the
        // snapshot file was absent at startup, MaybePersistProfileSnapshot will
        // detect that situation (dirty=true from ctor logic below) and write
        // an initial RESILIENT snapshot on the first Update tick.
        _lastSavedLevel       = _habituation.HabituationLevel;
        _lastSavedStrikeCount = _habituation.SevereStrikeCount;
        _lastSavedEngineState = _habituation.CurrentEngineState;
        _lastSavedProfile     = _habituation.CurrentProfile;
        _lastSavedProfileSource = _habituation.ProfileSource;
        // Persist on the first Update tick when EITHER there was no snapshot to begin with
        // (write an initial default snapshot) OR a load-time mutation already marked the
        // snapshot dirty — notably the vestibular-removal migration, which sets
        // _profileSnapshotDirty=true so its neutralized false/null overwrites the stale on-disk
        // true/Vestibular. OR-in the existing flag so that intent is not clobbered here.
        _profileSnapshotDirty = (savedSnapshot is null && profileStore is not null)
                                || _profileSnapshotDirty;
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

            // Hysteresis: trigger at the user's EFFECTIVE trigger (default 85; a vestibular
            // user is 50), release below 85% of it to avoid sticky behavior/flicker.
            // Beta-1 #5 (review): visible protection (mask on/off, FOV, tier) was wired to the
            // FIXED 85, so a vestibular user's lowered trigger only moved DisplayStressPercent
            // + strike sensitivity — the actual mask/FOV/tier were byte-identical to a
            // non-vestibular user, silently negating the vestibular envelope. Driving these
            // off effTrigger fixes that; for the default user effTrigger == TriggerPressure,
            // so non-vestibular behavior is byte-identical.
            bool shouldMaskOn = pressure >= effTrigger;
            bool shouldMaskOff = pressure < (effTrigger * 0.85);
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
            Current.UserFovOffsetPp   = _userFovOffset; // honest display signal — mirror beside the compose
            Current.HabituationLevel  = _habituation.HabituationLevel;
            Current.HabituationPaused = _habituation.IsPaused;

            RefreshPersonalization(dt);
            PersistHabituation();

            UpdateVimsTier(now, dt);
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
            Current.UserFovOffsetPp   = _userFovOffset; // honest display signal — mirror beside the compose
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
                    int natural = ComputeFovRadiusPercent(Current.Pressure, _habituation.EffectiveTrigger) + _habituation.HabituationOffset;
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
            Current.UserFovOffsetPp  = _userFovOffset; // honest display signal — mirror beside the compose
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
            Current.UserFovOffsetPp  = _userFovOffset; // honest display signal — mirror beside the compose (back to 0 here)
        }
    }

    public void ResetPersonalization()
    {
        lock (_gate)
        {
            // B6 Stage 4b: an AUTO-moved profile must return toward the fresh-user seed on Reset, BEFORE the
            // shader is seeded (so the shader reflects the reverted profile) and BEFORE the auto-delta is
            // zeroed (so the cohort is already at-seed when the delta is removed — no permissive-cohort +
            // nonzero-delta transient). Explicit-user (user_dashboard) / calibration profiles are LEFT
            // untouched (a deliberate pick survives a personalization reset). The streak store is cleared
            // unconditionally so a stale UP-streak can never fire right after a Reset.
            if (_habituation.ProfileSource == AutoElasticitySource)
                _habituation.AutoReclassifyProfile(HabituationProfile.Adapting, "auto_profiling_default");
            _profileEvalHistory?.Clear();

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
            // B5: a Reset removes the auto-adaptation contribution entirely (return to the user's own
            // base trigger), so an auto-relax can NEVER survive a Reset — the under-protection-undo gap
            // the red-team flagged. EffectiveTrigger holds base + delta here, so subtracting the delta
            // returns it to base; persisted below so the zero survives a restart.
            if (_autoAdaptTriggerDeltaPp != 0.0)
            {
                _habituation.EffectiveTrigger -= _autoAdaptTriggerDeltaPp;
                _autoAdaptTriggerDeltaPp = 0.0;
                _profileSnapshotDirty = true;
                MaybePersistProfileSnapshot();   // persist ONLY when the auto-delta actually changed
            }
            PersistHabituation(force: true);
            _vimsTier = 1;
            _pendingTierCandidate = 1;
            _pendingTierSinceUtc  = DateTime.MinValue;
            // Refresh the composed radius + mirror the (now-zeroed) offset so the dashboard
            // reflects the reset immediately, without waiting for the next Update() tick —
            // mirror beside the compose (single source = _userFovOffset, now 0).
            Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);
            Current.UserFovOffsetPp  = _userFovOffset;
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

    /// <summary>LOGIC-03: opt-in to OFF-dispatcher profile persistence. Defaults OFF →
    /// the <c>_profileStore.Update</c> call in <see cref="MaybePersistProfileSnapshot"/>
    /// runs SYNCHRONOUSLY inline exactly as before (every read-after-write test stays
    /// deterministic). When the App flips it ON at startup, each dirty persist captures its
    /// snapshot inputs under <c>_gate</c> then runs the file IO on a single-writer serialized
    /// background queue, so the dispatcher never blocks on AtomicJsonWriter's AV-retry sleeps.
    /// Write ORDER and W2.0-C atomicity are preserved (see the field comment). Pair with
    /// <see cref="FlushPendingPersists"/> at shutdown (or in a test) to drain pending writes.</summary>
    public bool BackgroundPersistEnabled
    {
        get { lock (_gate) { return _backgroundPersistEnabled; } }
        set { lock (_gate) { _backgroundPersistEnabled = value; } }
    }

    /// <summary>LOGIC-03: block until every queued background profile persist has completed.
    /// A no-op when background persistence is OFF (writes were already synchronous). Call at
    /// shutdown so the final snapshot lands before the process exits, or in a test that
    /// enabled the async path before asserting on the persisted file. Never throws: a failed
    /// background write is already swallowed by the store's best-effort Save.</summary>
    public void FlushPendingPersists()
    {
        Task tail;
        lock (_persistQueueGate) { tail = _persistTail; }
        try { tail.Wait(); } catch { /* best-effort; persistence failures must not crash */ }
    }

    /// <summary>LOGIC-03: append one profile-store persist to the SINGLE-WRITER serialized
    /// background queue. Chaining each write onto <c>_persistTail</c> (under
    /// <c>_persistQueueGate</c>) guarantees writes never overlap and run in ENQUEUE ORDER —
    /// two concurrent persists to the same file could otherwise lose an update or interleave
    /// the store's Load→mutate→Save span. The mutate closure was captured from in-memory
    /// state under <c>_gate</c> by the caller, so the background body touches no live engine
    /// state. Best-effort: a failed write is swallowed so the chain never faults.</summary>
    private void EnqueueProfilePersist(
        IHabituationProfileStore store,
        Func<HabituationProfileSnapshot?, HabituationProfileSnapshot?> mutate)
    {
        lock (_persistQueueGate)
        {
            _persistTail = _persistTail.ContinueWith(
                _ =>
                {
                    try { store.Update(mutate); }
                    catch { /* best-effort; persistence must never crash the engine */ }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>Alpha-2 addendum: seed a BRAND-NEW user at the neutral TYPICAL profile +
    /// L5「中」 start (instead of RESILIENT/L1), from which the AdaptiveFit selector
    /// converges. No-op unless the user is genuinely fresh (no profile snapshot existed at
    /// load, zero prior calibrations, no persisted vestibular signal) — an existing user's
    /// profile is never overwritten.
    /// <para>FEAT-07 / D-8 (2026-06-11): the TYPICAL/L5 seeding now runs regardless of
    /// <see cref="AutoProfilingEnabled"/>. The flag-off path no longer "restores the
    /// explicit-calibration flow" — commit 3131387 deleted that subsystem, so flag-off can
    /// only ever strand a fresh user at RESILIENT/L1 with no onboarding. Flag-off semantics
    /// are now "fresh users still seed at TYPICAL/L5; ONLY the vestibular prompt, console
    /// visibility, and selector-adjacent UI signals react to the flag" — all of which live
    /// in the App layer, not here. Hence the previous <c>if (!_autoProfilingEnabled) return;</c>
    /// guard is gone; the fresh-user guard below is the sole gate.</para>
    /// Q4.5-safe: never touches the habituation <c>_offset</c>.</summary>
    public void ApplyAutoProfilingStartDefaults()
    {
        lock (_gate)
        {
            // FEAT-07 / D-8: NO _autoProfilingEnabled gate here — fresh users seed ADAPTING/L5
            // whether or not the flag is on (the flag gates only App-side vestibular/console/
            // selector-UI). The fresh-user guard is the only thing that may early-return.
            if (_profileExistedAtLoad || _loadedCalibrationCount > 0 || _vestibularProtection) return;

            _habituation.SetProfile(HabituationProfile.Adapting, "auto_profiling_default");
            _vimsTier             = SenseDefaults.AutoProfilingStartLevel; // L5 median seed
            _pendingTierCandidate = _vimsTier;
            _pendingTierSinceUtc  = DateTime.MinValue;
            // LOGIC-13 / D-5 (onboarding protection): arm the seed-protection window so the
            // very first calm Update tick does NOT immediately fast-release the L5 median down
            // to L1 (gap 4 >= VimsTierFastReleaseGap snaps within ~66 ms otherwise) — the
            // documented "fresh users start at L5「中」" would never be user-visible. See
            // UpdateVimsTier for the deliberate exemption.
            _onboardingProtectionActive  = true;
            _onboardingElapsedSeconds    = 0.0;
            Current.VimsTier         = _vimsTier;
            Current.HabituationLevel = _habituation.HabituationLevel;
            _profileSnapshotDirty    = true;
            MaybePersistProfileSnapshot();
        }
    }

    /// <summary>UI "Overlay Always ON" / Auto-Protect: pin the displayed protection tier at
    /// <paramref name="level"/> and FREEZE the auto-level (pressure→tier) projection while held, so the
    /// user gets an immediate, stable overlay. The habituation engine (offset / strike / state machine) +
    /// the B5/B6 auto-adaptation keep running untouched — only <see cref="UpdateVimsTier"/>'s projection is
    /// suspended, and the engine's severe-spike pause still fires on raw pressure. Releasing (hold=false)
    /// resumes projection from live pressure on the next tick. Idempotent; Q4.5-safe (never touches the
    /// earned offset).</summary>
    public void SetAutoLevelHold(bool hold, int level = 6)
    {
        lock (_gate)
        {
            _autoLevelHold = hold;
            if (hold)
            {
                int pinned = Math.Clamp(level, 1, 12);
                _vimsTier             = pinned;
                _pendingTierCandidate = pinned;
                _pendingTierSinceUtc  = DateTime.MinValue;
                Current.VimsTier      = pinned;
                // The manual hold supersedes the fresh-seed onboarding hold (both freeze the tier — avoid a fight).
                _onboardingProtectionActive = false;
            }
            // Release: leave _vimsTier where it is; the next UpdateVimsTier projects from live pressure.
        }
    }

    /// <summary>True when vestibular protection is active. Vestibular-removal: there is no
    /// longer any path that sets <c>_vestibularProtection</c> true (the App activation surface
    /// and the engine setter were removed; a persisted-true user is migrated to Reactive at
    /// load), so this is permanently false. The single predicate every vestibular consumer in
    /// this engine keys off — kept as a named member so the (now statically-dead, protective)
    /// consumer branches stay intact and correct-by-construction.</summary>
    private bool IsVestibularActive => _vestibularProtection;

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
    /// chatbot delta (FOV offset + shader knobs) BEFORE the snap (Q5 before Q4)
    /// via the pause-PRESERVING transient reset
    /// (<c>HabituationEngine.ResetTransientStatePreservingPause</c>, CW-1): a
    /// silent snap PRESERVES the earned habituation <c>_offset</c> (Q4.5) AND the
    /// active pause contract (it must not un-pause an opt-out user nor exit the
    /// tier ≥ 8 awaiting-confirm park — unlike <see cref="ResetPersonalization"/>,
    /// the explicit user path, which calls the full <c>ResetTransientState</c>).
    /// The discretizer / hysteresis are not touched; the snapped tier is
    /// recomputed onto the active set's curve on the next Update via the gated
    /// branch.</summary>
    public void SetLevel(int level, LevelChangeCause cause)
    {
        lock (_gate)
        {
            // Flag-off: a direct SetLevel has no meaning on today's path. Skip
            // entirely so the OFF behaviour stays byte-identical.
            if (!_adaptiveFitSelectorEnabled) return;

            // Q4/Q5 ordering: for the two snap causes, clear the chatbot delta
            // (transient personalization) FIRST — preserving _offset — then snap.
            // Uses the pause-PRESERVING transient reset (CW-1): this is a SILENT
            // selector snap, so it must NOT un-pause opted-out habituation nor exit
            // the tier ≥ 8 awaiting-user-confirm park (only AcknowledgeAndResume may
            // — whitepaper §7.4 / SAFETY-PROVISIONS §8). It still preserves the
            // earned habituation _offset (Q4.5 / C5) and does NOT touch _vimsTier
            // here so the snap below is the single writer of the level.

            // A7 user-authority guard (Shape-axis wiring, Kirby 2026-06-11): capture the
            // user-owned gv/feather BEFORE the Q5 ResetToDefault wipes them, so an explicit
            // /grid or /feather choice survives a silent set-change snap (mirrors ApplySetMove).
            bool   userOwnsShader      = _shaderUserModified;
            double userGridVisibility  = _shader.GridVisibility;
            double userFeatherWidth    = _shader.FeatherWidth;

            if (cause is LevelChangeCause.SetChangeSnap or LevelChangeCause.FeltIntensitySnap)
            {
                _userFovOffset = 0;
                _shader.ResetToDefault();
                _habituation.ResetTransientStatePreservingPause(); // preserves _offset (Q4.5 / C5) + pause (CW-1)
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
            Current.UserFovOffsetPp  = _userFovOffset; // honest display signal — mirror beside the compose

            // Apply the active set's FOV floor (set0 ⇒ 15, a no-op vs today).
            LevelParams lp = SetGrid.Curve(_activeSet).ForLevel(_vimsTier);
            Current.FovRadiusPercent = Math.Max(Current.FovRadiusPercent, lp.FovFloorPercent);
            // Alpha-2 (selector→renderer): refresh the mask-depth override so a level snap
            // re-renders the active set's alpha immediately (out-of-tick path). Gated on the
            // flag — OFF ⇒ -1 ⇒ overlay falls back to its static tier alpha (byte-identical).
            Current.MaskPeakAlphaOverride = _adaptiveFitSelectorEnabled ? lp.PeakMaskAlpha : -1.0;
            // Shape-axis render character (Kirby 2026-06-11): on a snap that reset the shader to
            // default (above), re-seed BOTH the grid baseline AND the feather character from the
            // active set (mirrors ApplySetMove — Shape drives both). Gated on the snap cause so a
            // non-snap SetLevel never overwrites a live chatbot grid/feather adjustment. The flag
            // is already guaranteed ON here (early-return at the top). Set0 ⇒ gv 0.75 / feather
            // 0.50 (a no-op today); off-Set0 shapes re-baseline both here. A7 guard: if the user
            // OWNS the shader, RESTORE their gv/feather instead of the per-set baseline.
            if (cause is LevelChangeCause.SetChangeSnap or LevelChangeCause.FeltIntensitySnap)
            {
                if (userOwnsShader)
                {
                    _shader.GridVisibility = userGridVisibility;
                    _shader.FeatherWidth   = userFeatherWidth;
                    _shaderUserModified    = true;
                }
                else
                {
                    _shader.GridVisibility = lp.GridVisibility;
                    _shader.FeatherWidth   = lp.FeatherWidth;
                }
            }
        }
    }

    /// <summary>B1: receive the recent measured-FMS aggregate (mean 0..20 + report count) over the
    /// selector's durable window, pushed by the App layer (symptom_log.jsonl lives in the App data
    /// root, out of this layer's reach — mirrors how <see cref="NotifyManualLevelNudge"/> is pushed in).
    /// The selector consumes it as the OUTCOME term, gated on count &gt; 0. Runs on the dispatcher thread
    /// like the engine tick, so no lock is needed; bad values guard to neutral.</summary>
    public void UpdateRecentFmsContext(double mean, int count)
    {
        // A bad mean (NaN/negative) zeroes BOTH count and mean, so the selector sees "no data"
        // (count and mean stay consistent — never count>0 with a meaningless mean).
        bool valid = count > 0 && !double.IsNaN(mean) && mean >= 0.0;
        _recentFmsCount = valid ? count : 0;
        _recentFmsMean  = valid ? Math.Clamp(mean, 0.0, 20.0) : 0.0;
    }

    /// <summary>B3 (per-app learning): receive the coarse, consent-gated foreground key
    /// (<c>"category/app"</c>, or null when D-16 per-app consent is off / the app is unknown),
    /// pushed by the App layer (the foreground filter lives there, out of this layer's reach —
    /// mirrors <see cref="UpdateRecentFmsContext"/> / <see cref="NotifyManualLevelNudge"/>). The
    /// durable-event appenders stamp it onto <see cref="EventLogEntry.ForegroundKey"/> so the
    /// per-app stats pipeline can later bucket history by app. A blank/whitespace key is
    /// normalised to null (the global bucket). Runs on the dispatcher thread like the engine tick,
    /// so no lock is needed.</summary>
    public void NotifyForegroundAppCategory(string? foregroundKey)
        => _currentForegroundKey = string.IsNullOrWhiteSpace(foregroundKey) ? null : foregroundKey;

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
            durable, now, _habituation.EffectiveTrigger,
            fmsMean: _recentFmsMean, fmsCount: _recentFmsCount);

        // recentManualLevelDelta: the last manual +/- within the recency window,
        // else 0 (no conflict). Conservative + simple per spec.
        int recentManualLevelDelta = 0;
        if (_lastManualFeedbackAtUtc != DateTime.MinValue
            && (now - _lastManualFeedbackAtUtc).TotalSeconds <= AdaptiveFitManualRecencySeconds)
        {
            recentManualLevelDelta = _lastManualLevelDeltaSign;
        }

        // B6 Stage 3: the B5 gate's standing auto-delta as a band vote, so the selector won't drift the band
        // OPPOSITE the gate's direction (one coordinated loop — not two channels fighting). Sign convention: a
        // NEGATIVE auto-delta is a TIGHTEN (more protection ⇒ prefer a HIGHER band ⇒ +1); a POSITIVE delta is
        // a RELAX (⇒ −1); 0 = no standing auto-delta ⇒ no brake (the default user is byte-identical). A soft
        // brake read off the gate lock is fine — a one-tick-stale sign at worst defers a move, never forces one.
        int gateLevelDirection = -Math.Sign(_autoAdaptTriggerDeltaPp);

        // The state machine's Settling → Active gate wants to know whether a
        // confident pick currently exists; ask the selector once up front (pure,
        // no state change) and feed the boolean in.
        SelectorDecision peek = DeterministicSetSelector.Select(
            _activeSet, stats, SelectorWeights.Default, recentManualLevelDelta, gateLevelDirection);
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
            _activeSet, stats, SelectorWeights.Default, recentManualLevelDelta, gateLevelDirection);

        if (decision is not SelectorDecision.MoveTo move) return;
        GridCoord target = move.Target;

        // Adjacency invariant (belt-and-suspenders: the selector only ever
        // proposes distance-1 cells, but we re-assert before applying).
        if (GridCoord.ManhattanDistance(_activeSet, target) != 1) return;

        // Propose-stronger-only guard applies to BOTH the silent same-segment move AND the
        // queued cross-segment proposal — a weaker-band target is never moved-to OR queued
        // for a protected cohort. A blocked move HOLDs this tick. (Re-ordered ahead of the
        // segment branch in P5 so the guard gates the queue too.) Beta-1 #7 extends this from
        // vestibular-only to ALSO cover the SENSITIVE habituation profile.
        if (!PassesProtectionFloorGuard(_activeSet, target)) return;

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
    /// <c>ResetToDefault</c> + <c>ResetTransientStatePreservingPause</c>,
    /// PRESERVING the earned habituation <c>_offset</c> / C5 AND the active pause
    /// contract / CW-1 — this is a SILENT background move that must not un-pause
    /// opted-out habituation nor exit the tier ≥ 8 awaiting-user-confirm park),
    /// THEN re-seeds the displayed level on the new curve (Q4 felt-snap). Never
    /// touches <c>_offset</c>. Caller holds <c>_gate</c>.</summary>
    private void ApplySetMove(GridCoord target)
    {
        _activeSet = target;

        // A7 user-authority guard (Shape-axis wiring, Kirby 2026-06-11): if the user has
        // EXPLICITLY adjusted the shader knobs (/grid, /feather — _shaderUserModified set by
        // ApplyShaderAdjustment / SetShaderParam), their gv + feather must SURVIVE this set
        // move — a silent background move must never clobber a user's render choice (the SAME
        // contract SetHabituationProfile honours for a profile change). The Q5 ResetToDefault
        // below wipes ALL knobs, so capture the user-owned gv/feather here and restore them
        // after the reset INSTEAD of re-seeding the per-set baseline. (MaskDepth is NOT a
        // Shape-axis output, so it keeps the existing Q5 transient-reset behaviour either way.)
        bool userOwnsShader      = _shaderUserModified;
        double userGridVisibility = _shader.GridVisibility;
        double userFeatherWidth   = _shader.FeatherWidth;

        // Q5 first: clear the transient chatbot delta, preserving _offset + pause.
        _userFovOffset = 0;
        _shader.ResetToDefault();
        _habituation.ResetTransientStatePreservingPause(); // preserves _offset (Q4.5 / C5) + pause (CW-1)
        Current.HabituationLevel  = _habituation.HabituationLevel;
        Current.HabituationPaused = _habituation.IsPaused;

        // SPEC item 4 — vestibular L9 floor: an advisory user's resolved level is
        // clamped to >= 9 on any set move (propose-stronger-only safety stance).
        // Non-advisory users keep their current rung. Keep the pending-debounce
        // bookkeeping in sync so UpdateVimsTier does not immediately walk it back.
        // D-13: IsVestibularActive is the persistent auto-profiling signal only.
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
        Current.UserFovOffsetPp  = _userFovOffset; // honest display signal — mirror beside the compose
        LevelParams lp = SetGrid.Curve(_activeSet).ForLevel(_vimsTier);
        Current.FovRadiusPercent = Math.Max(Current.FovRadiusPercent, lp.FovFloorPercent);
        // Alpha-2 (selector→renderer): the NEW set's mask depth reaches the overlay
        // immediately on a silent move (this runs inside the flag-on selector tick).
        Current.MaskPeakAlphaOverride = lp.PeakMaskAlpha;
        // Shape-axis render character (Kirby 2026-06-11): re-seed the peripheral-grid baseline
        // AND the feather character from the NEW set's curve — Shape drives BOTH per-set render
        // outputs. The Q5 _shader.ResetToDefault() above reset gv/feather to the hardcoded
        // defaults; replacing them with lp.GridVisibility / lp.FeatherWidth makes the per-set
        // values the live baselines the chatbot then modulates. Both are SHADER params, so they
        // flow to the overlay via Current.Shader (== _shader) — no separate override field is
        // needed (unlike mask depth, a tier-table value the overlay reads directly). Set0 ⇒
        // gv 0.75 / feather 0.50 == the ShaderParams defaults (a byte-identical no-op today);
        // off-Set0 shapes now genuinely re-baseline the grid + feather on a set move.
        //
        // A7 guard: if the user OWNS the shader, RESTORE their gv/feather instead of the per-set
        // baseline (a silent set move must not clobber an explicit /grid or /feather choice).
        if (userOwnsShader)
        {
            _shader.GridVisibility = userGridVisibility;
            _shader.FeatherWidth   = userFeatherWidth;
            _shaderUserModified    = true; // ResetToDefault did not clear the flag, but keep the invariant explicit
        }
        else
        {
            _shader.GridVisibility = lp.GridVisibility;
            _shader.FeatherWidth   = lp.FeatherWidth;
        }
    }

    /// <summary>Protection-floor guard: BLOCK any selector move toward a WEAKER band
    /// (lower <see cref="GridCoord.Band"/>) for the cohorts that must never have their
    /// protection silently reduced. A move that holds or raises Band is always allowed.
    /// <list type="bullet">
    ///   <item><b>Vestibular</b> (SPEC item 4) — <see cref="IsVestibularActive"/>, which is
    ///   permanently false post vestibular-removal (this leg is statically dead but protective).
    ///   Propose-stronger-only.</item>
    ///   <item><b>Beta-1 #7 — SENSITIVE habituation profile.</b> The selector previously
    ///   applied a same-segment Band-DOWN SILENTLY for sensitive users (the two flags are
    ///   independent: vestibular vs profile), quietly lowering the whole mask-alpha envelope
    ///   for the cohort least able to tolerate it. Never auto-weaken: a sensitive user's
    ///   protection only reduces through explicit user action, not the silent selector.</item>
    /// </list>
    /// (The L9 floor / 0.20 mask floor are enforced at resolve time by the grid's clamps;
    /// this method governs the directional move gate.)</summary>
    private bool PassesProtectionFloorGuard(GridCoord current, GridCoord target)
    {
        // A move that holds or strengthens the band is always allowed.
        if (target.Band >= current.Band) return true;

        // A weaker (protection-reducing) move is blocked for the protected cohorts.
        if (IsVestibularActive) return false;
        if (_habituation.CurrentProfile == SenseEase.Core.Inference.HabituationProfile.Reactive) return false;
        return true;
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
                || !PassesProtectionFloorGuard(_activeSet, target))
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
    /// <see cref="ApplyUserFeedback"/>) defers the selector AND retracts a stale suggestion.
    /// <para>B2: it now ALSO registers the manual-authority SIGN for the card path. Previously only
    /// the chatbot FOV path (<see cref="ApplyUserFeedback"/>) set <c>_lastManualLevelDeltaSign</c>, so
    /// a card ±1 nudge merely reset the cooldown without deferring a conflicting auto-move. Now the
    /// user's manual direction defers an opposing selector move via <c>W5ManualConflict</c> for the
    /// recency window. <paramref name="direction"/> &gt;= 0 = the user asked for MORE protection
    /// (a stronger band ⇒ +1); &lt; 0 = less (−1). Gated on the selector flag so it is inert while
    /// OFF. (The nudge's protection itself persists via the baseline tier, so it is deliberately NOT
    /// logged to WeightFor("FovRadius") here — that would double-apply the resting FOV offset.)</summary>
    public void NotifyManualLevelNudge(int direction = +1)
    {
        lock (_gate)
        {
            if (!_adaptiveFitSelectorEnabled) return;
            _lastManualLevelDeltaSign = direction >= 0 ? +1 : -1;
            _lastManualFeedbackAtUtc  = DateTime.UtcNow;
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
                ForegroundKey = _currentForegroundKey,   // B3: per-app bucketing (null = global)
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

    /// <summary>CW-7 / LOGIC-10 (carried NE2): restore the user's persisted shader
    /// personalization at boot, mirroring the <c>_userFovOffset</c> replay in the ctor.
    /// First seeds the shader from the CURRENTLY-restored profile so untouched knobs keep
    /// their per-profile defaults (RESILIENT/TYPICAL → neutral baseline; SENSITIVE →
    /// tightened), THEN layers each knob's persisted net delta from the adjustment log on
    /// top (SAME taring semantics as <c>WeightFor("FovRadius")</c> — the log's reset
    /// sentinel zeroes prior history). Shader deltas are stored in normalized [0,1] space
    /// (see <c>ShaderParams</c>), so the restored value is base + NetDelta, clamped. Any
    /// knob with a non-zero net delta means the user OWNED the shader, so we set the A7
    /// <c>_shaderUserModified</c> flag — a later profile change then won't re-seed over the
    /// user's choice (same contract <see cref="ApplyShaderAdjustment"/> / <see cref="SetShaderParam"/>
    /// establish at runtime). No adjustment log ⇒ leave the profile-seeded base untouched.
    /// Caller is the ctor (single-threaded, pre-publication), so no _gate is taken.</summary>
    private void ReplayShaderPersonalizationFromLog()
    {
        if (_adjustmentLog is null) return;

        // Establish the per-profile base for UNTOUCHED knobs (the spec's
        // "profile re-seeding still wins for untouched knobs"). For RESILIENT/TYPICAL
        // this equals ShaderParams.Default, so it is a no-op vs the field initialiser;
        // for a restored SENSITIVE profile it applies that profile's tightened defaults.
        SeedShaderFromProfile(_habituation.CurrentProfile);

        bool anyUserDelta = false;
        anyUserDelta |= ReplayOneShaderKnob("MaskDepth",      v => _shader.MaskDepth      = v, _shader.MaskDepth);
        anyUserDelta |= ReplayOneShaderKnob("GridVisibility", v => _shader.GridVisibility = v, _shader.GridVisibility);
        anyUserDelta |= ReplayOneShaderKnob("FeatherWidth",   v => _shader.FeatherWidth   = v, _shader.FeatherWidth);

        if (anyUserDelta)
        {
            _shader.Clamp();
            Current.Shader      = _shader;
            _shaderUserModified = true; // A7: user owned the shader → profile re-seed must not win
        }
    }

    /// <summary>Replay one shader knob's persisted net delta onto its profile-seeded base.
    /// Returns true iff the user had a non-zero net delta for this knob (so the caller can
    /// set the A7 user-modified flag). The setter writes <c>base + NetDelta</c> clamped to
    /// [0,1]; a zero net delta leaves the profile default exactly in place.</summary>
    private bool ReplayOneShaderKnob(string parameter, Action<double> set, double baseValue)
    {
        double net = _adjustmentLog!.WeightFor(parameter).NetDelta;
        if (net == 0.0) return false;
        set(Math.Clamp(baseValue + net, 0.0, 1.0));
        return true;
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

    // ── Calibration state read-surface (Stage 2 remnant) ──────────────────────
    // D-13 (2026-06-11): the application method (ApplyCalibrationResult) and the
    // _lastCalibrationResult / LastCalibrationResult read-back it fed were DELETED
    // together with CalibrationProcessor.Process — explicit calibration is
    // cancelled and nothing applies a CalibrationResult to the engine any more. The
    // calibration_count / last_calibration_at fields below remain because they are
    // still RESTORED from the persisted snapshot at load (and round-tripped back
    // out), so a machine that calibrated under the old flow keeps a stable,
    // non-zero count across upgrades. The personal-pressure scale is now driven
    // only by the load path + the chatbot's ApplyChatbotOverride(AdjustSensitivity).

    /// <summary>The <c>calibration_count</c> value the engine considers
    /// current. Initialised from the loaded snapshot at construction
    /// (Stage 7a) and round-tripped through
    /// <see cref="BuildProfileSnapshotCore"/> so the loaded value lands in the
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
    /// for the Settings "regret" toggle's checked state. Since D-13 deleted the calibration
    /// advisory path, this is the engine's only vestibular signal.</summary>
    public bool IsVestibularProtectionEnabled
    {
        get { lock (_gate) { return _vestibularProtection; } }
    }

    // ── D-13 (2026-06-11): ApplyCalibrationResult DELETED ────────────────────
    // The wrapper method that turned a CalibrationProcessor.Process result into
    // engine state (effective trigger / strike-limit override / profile swap /
    // calibration_count bump) is gone, along with the processor's Process entry
    // point. Explicit calibration is cancelled (AppConfig.AutoProfilingEnabled),
    // so the ONLY remaining sources of the vestibular safety envelope are the
    // PERSISTENT auto-profiling signal (SetVestibularProtection — EffectiveTrigger
    // = 50 + SevereStrikeLimitOverride = 1) and its snapshot restore on load. The
    // personal-pressure scale is still set on the LOAD path and by the chatbot's
    // ApplyChatbotOverride(AdjustSensitivity); _loadedCalibrationCount /
    // _lastCalibrationAtUtc are now seeded only from the loaded snapshot. To
    // restore the explicit-calibration application flow, git revert 3131387.

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
                    // B5: re-compose the auto-adaptation delta on top of the new user base, so a user
                    // sensitivity change never erases the gate's contribution (the v1 overwrite bug).
                    ApplyAutoDeltaToFreshBase();
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
                Current.UserFovOffsetPp  = _userFovOffset; // honest display signal — mirror beside the compose
                MaybePersistProfileSnapshot();
            }
            return applied;
        }
    }

    /// <summary>B5 — apply the deterministic AdaptationGate's decision to the dedicated auto-channel.
    /// <paramref name="newDelta"/> is the new SIGNED contribution to EffectiveTrigger (negative =
    /// tighten/more protection, positive = relax/less protection), clamped to ±AutoAdaptTriggerSpanPp at
    /// this boundary (the gate already clamps — belt-and-suspenders). Applied as the DIFFERENCE from the
    /// current auto-delta so the user/load base is preserved; the vestibular cap is re-asserted; the
    /// snapshot is persisted; an AUDIT-ONLY AdjustmentRecord (Source="system", which WeightFor ignores)
    /// records the move. No-op on a non-finite or unchanged delta. The composed visual slew is bounded by
    /// the gate's one-step (≤3pp) limit + the existing UpdateVimsTier hysteresis/EMA, so no separate
    /// per-window slew clamp is needed for ≤3pp steps (a larger step would warrant one).</summary>
    public void ApplyAutoAdaptDelta(double newDelta)
    {
        lock (_gate)
        {
            if (!double.IsFinite(newDelta)) return;
            newDelta = Math.Clamp(newDelta, -AutoAdaptTriggerSpanPp, AutoAdaptTriggerSpanPp);
            // Vestibular: the auto-channel may never RELAX (positive). The gate already rejects vestibular
            // relax; enforce it at the engine boundary too so no path can carry a positive delta here.
            if (_vestibularProtection && newDelta > 0.0) newDelta = 0.0;
            double diff = newDelta - _autoAdaptTriggerDeltaPp;
            if (Math.Abs(diff) < 1e-9) return;

            _habituation.EffectiveTrigger += diff;
            if (_vestibularProtection &&
                _habituation.EffectiveTrigger > CalibrationProcessor.VestibularTriggerPressureOverride)
                _habituation.EffectiveTrigger = CalibrationProcessor.VestibularTriggerPressureOverride;

            _autoAdaptTriggerDeltaPp = newDelta;
            _profileSnapshotDirty = true;
            _adjustmentLog?.Append(new AdjustmentRecord
            {
                Parameter = "AutoAdaptTrigger",
                Delta     = diff,
                Source    = "system", // AUDIT-ONLY: WeightFor sums Source=="user", so this never feeds the FOV offset / caution math
            });
            Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);
            MaybePersistProfileSnapshot();
        }
    }

    /// <summary>B6 Stage 4b — the SINGLE locked commit for one adaptation pass: it folds the profile flip
    /// (the FOV-offset elasticity axis) AND the trigger auto-delta (the EffectiveTrigger axis) under ONE
    /// <c>_gate</c> acquisition, re-reading live state inside the lock and aborting to Hold on any protective
    /// change between the off-lock snapshot and the commit. The pure gate + classifier run OFF-lock; this is
    /// the only mutate. Deliberately does NOT call the self-locking/self-persisting public
    /// <see cref="ApplyAutoAdaptDelta"/> / <c>SetHabituationProfile</c> (that would double-lock + double-
    /// persist); the trigger arithmetic is inlined from <see cref="ApplyAutoAdaptDelta"/> without the re-lock,
    /// and the profile flip routes through <see cref="HabituationEngine.AutoReclassifyProfile"/> (NO shader
    /// re-seed — F6).</summary>
    public AdaptationCommitResult ApplyGateOutcome(AdaptationCommitRequest req)
    {
        if (req is null) return AdaptationCommitResult.Hold("null-request");

        // Cheap pre-lock no-op — keep the consent-ON / nothing-to-do cost identical to today.
        if (req.ProfileMove == ProfileMove.Hold && req.TriggerDecision is GateDecision.Hold)
            return AdaptationCommitResult.Hold("no-op");

        lock (_gate)
        {
            // (1) LIVE RE-READ — the verdict was computed off-lock; re-read what it depended on.
            var    liveProfile = _habituation.CurrentProfile;
            double liveDelta   = _autoAdaptTriggerDeltaPp;
            bool   liveVest    = _vestibularProtection;
            int    liveLevel   = _habituation.HabituationLevel;
            double liveStrk    = _habituation.SevereStrikeCount;

            // (2) MISMATCH ABORT (fail-closed): the snapshot the verdict assumed must still hold.
            if (liveProfile != req.SnapProfile
                || Math.Abs(liveDelta - req.SnapAutoDelta) > 1e-9
                || liveVest != req.SnapVestibular)
                return AdaptationCommitResult.Hold("snapshot-vs-live-mismatch");

            // (2b) UP-ONLY PROTECTIVE-RACE ABORT: an UP verdict's justification is "at/near cap +
            // demonstrated tolerance". A severe strike between snapshot and commit collapses _offset /
            // bumps the strike count WITHOUT changing profile/delta/vestibular, so (2) passes. Abort UP
            // (never DOWN — DOWN fails toward protection) on any protective drift.
            if (req.ProfileMove == ProfileMove.Up
                && (liveLevel < req.SnapHabituationLevel || liveStrk > req.SnapStrikeCount + 1e-9))
                return AdaptationCommitResult.Hold("up-stale-protective-change");

            // (3) LIVE BUDGET RE-DERIVE inside the lock (catches a budget/eval row racing the off-lock read).
            var liveTiming = AdaptationEvidence.DeriveAutoChannelTiming(
                _adjustmentLog?.All(), DateTime.UtcNow, AutoAdaptTriggerParameter, AutoReclassifyProfileParameter);
            bool budgetSpent = liveTiming.AutoChangesThisWeek >= AdaptationGateConfig.Default.MaxAutoChangesPerWeek;

            // (4) PROFILE FLIP — vestibular hard-hold (never auto-move a vestibular user); an UP move
            // consumes a relax slot (blocked when the budget is spent), a DOWN move is protective + exempt.
            bool profileMoved = false;
            bool profileWouldRelax = req.ProfileMove == ProfileMove.Up;
            if (req.ProfileMove != ProfileMove.Hold && !liveVest
                && !(profileWouldRelax && budgetSpent))
            {
                _habituation.AutoReclassifyProfile(req.ProfileTarget, AutoElasticitySource);
                profileMoved = true;
            }

            // (4b) DOWN-INTO-REACTIVE RETRACT: a DOWN into Reactive (→ Cohort.Sensitive) lowers the floor a
            // standing positive (relaxed) delta was never permitted under. DOWN must tighten NET protection,
            // so retract any standing positive auto-delta in the same locked body.
            if (profileMoved && req.ProfileMove == ProfileMove.Down
                && req.ProfileTarget == HabituationProfile.Reactive && _autoAdaptTriggerDeltaPp > 0.0)
            {
                _habituation.EffectiveTrigger -= _autoAdaptTriggerDeltaPp;
                _adjustmentLog?.Append(new AdjustmentRecord
                {
                    Parameter = AutoAdaptTriggerParameter, Delta = -_autoAdaptTriggerDeltaPp, Source = "system",
                });
                _autoAdaptTriggerDeltaPp = 0.0;
            }

            // (5) PROFILE BUDGET AUDIT ROW — only a relax-direction (UP) profile move consumes the shared
            // weekly budget (DOWN is protective, exempt). Delta=0 + a distinct Parameter so it never pollutes
            // net-relax / HasEverAutoRelaxed.
            if (profileMoved && req.ProfileMove == ProfileMove.Up)
                _adjustmentLog?.Append(new AdjustmentRecord
                {
                    Parameter = AutoReclassifyProfileParameter, Delta = 0.0, Source = "system",
                });

            // (6) COHERENCE GUARD for the trigger axis.
            var apt = req.TriggerDecision as GateDecision.Apply;
            bool wantTriggerApply = apt is not null;
            // A TRUE net-relax only (NewAutoDeltaPp > 0 AND above the current delta) — a benign unwind of a
            // prior tighten (positive diff but still ≤ 0) keeps the user MORE protected, so it may stack.
            bool triggerIsNetRelax = apt is not null && apt.Direction == AdaptDirection.Relax
                                     && apt.NewAutoDeltaPp > 0.0 && apt.NewAutoDeltaPp > _autoAdaptTriggerDeltaPp;
            // §5.3 never-both-NET-relax (UP) + §5.4 down-move-no-auto-unlock (DOWN): suppress a net-relax
            // trigger whenever the profile also moved this pass.
            if (wantTriggerApply && triggerIsNetRelax && req.ProfileMove != ProfileMove.Hold)
                wantTriggerApply = false;
            // A relax-direction trigger move also consumes a relax slot — block on the live budget.
            if (wantTriggerApply && triggerIsNetRelax && budgetSpent)
                wantTriggerApply = false;

            // (7) TRIGGER DELTA inline (mirror ApplyAutoAdaptDelta, NO re-lock, NO self-persist).
            bool triggerMoved = false;
            if (wantTriggerApply)
            {
                double nd = Math.Clamp(apt!.NewAutoDeltaPp, -AutoAdaptTriggerSpanPp, AutoAdaptTriggerSpanPp);
                if (liveVest && nd > 0.0) nd = 0.0;
                double diff = nd - _autoAdaptTriggerDeltaPp;
                if (Math.Abs(diff) >= 1e-9)
                {
                    _habituation.EffectiveTrigger += diff;
                    if (liveVest && _habituation.EffectiveTrigger > CalibrationProcessor.VestibularTriggerPressureOverride)
                        _habituation.EffectiveTrigger = CalibrationProcessor.VestibularTriggerPressureOverride;
                    _autoAdaptTriggerDeltaPp = nd;
                    _adjustmentLog?.Append(new AdjustmentRecord
                    {
                        Parameter = AutoAdaptTriggerParameter, Delta = diff, Source = "system",
                    });
                    Current.FovRadiusPercent = ComposeFovRadius(Current.Pressure);
                    triggerMoved = true;
                }
            }

            // (8) PERSIST ONCE for both axes.
            if (profileMoved || triggerMoved)
            {
                Current.HabituationPaused = _habituation.IsPaused;
                _profileSnapshotDirty = true;
                MaybePersistProfileSnapshot();
            }

            return new AdaptationCommitResult(profileMoved, triggerMoved,
                triggerMoved ? "applied" : (profileMoved ? "profile-only" : "hold"));
        }
    }

    /// <summary>B5 — re-apply the auto-adaptation delta on top of a freshly-set BASE EffectiveTrigger
    /// (call right after boot restore / AdjustSensitivity / SetVestibularProtection set the base), then
    /// re-assert the vestibular cap so a positive (relax) delta can never push a vestibular user's
    /// composed trigger above the 50 floor. Keeps the invariant EffectiveTrigger == base + delta.</summary>
    private void ApplyAutoDeltaToFreshBase()
    {
        // True no-op when the auto-channel is unused (consent never engaged): the cohort cap below must
        // NOT fire at delta==0, or it would silently change an existing path (e.g. a vestibular user's
        // chatbot AdjustSensitivity result) — keep every pre-B5 EffectiveTrigger path byte-identical.
        if (_autoAdaptTriggerDeltaPp == 0.0) return;

        // A vestibular user may NEVER carry a RELAX (positive) auto-delta. Retire it outright rather than
        // letting the cohort cap merely HIDE it — otherwise a later SetVestibularProtection(false) would
        // resurrect the relax on top of the (higher) non-vestibular base.
        if (_vestibularProtection && _autoAdaptTriggerDeltaPp > 0.0)
            _autoAdaptTriggerDeltaPp = 0.0;

        _habituation.EffectiveTrigger += _autoAdaptTriggerDeltaPp;
        if (_vestibularProtection &&
            _habituation.EffectiveTrigger > CalibrationProcessor.VestibularTriggerPressureOverride)
            _habituation.EffectiveTrigger = CalibrationProcessor.VestibularTriggerPressureOverride;
    }

    /// <summary>B5: an ATOMIC snapshot of the adaptation-relevant engine state, taken under the gate lock
    /// so the (base = effective − delta) pairing the App computes can never tear against a concurrent
    /// mutate (the App's tuning pass runs off the UI thread). Replaces several lock-free reads.</summary>
    public (double EffectiveTrigger, double AutoDeltaPp, double PersonalPressureScale, bool Vestibular,
            HabituationProfile Profile, int HabituationLevel, double OffsetPp, double CapPp, double StrikeCount)
            SnapshotForAdaptation()
    {
        lock (_gate)
        {
            return (_habituation.EffectiveTrigger, _autoAdaptTriggerDeltaPp, _personalPressureScale,
                    _vestibularProtection, _habituation.CurrentProfile,
                    // B6 Stage 4b: profile-axis inputs for the classifier + the in-lock protective-race recheck.
                    _habituation.HabituationLevel, (double)_habituation.HabituationOffset,
                    _habituation.CurrentParams.HabOffsetCapPp, _habituation.SevereStrikeCount);
        }
    }

    /// <summary>B5 test/diagnostics accessor: the engine's current effective trigger (base + auto delta).
    /// The App reads adaptation state via <see cref="SnapshotForAdaptation"/> (atomic), so this getter is
    /// consumed only by the AutoAdaptTriggerChannel tests.</summary>
    public double EffectiveTrigger => _habituation.EffectiveTrigger;

    /// <summary>B5 test/diagnostics accessor: the current auto-adaptation channel contribution
    /// (pp; negative = tighten). Production reads it via <see cref="SnapshotForAdaptation"/>.</summary>
    public double AutoAdaptTriggerDeltaPp => _autoAdaptTriggerDeltaPp;

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
            // happen under ONE store-lock span. The mutate closure reads ONLY the
            // captured core snapshot + the store's in-lock `existing` (never live
            // engine state), so it is safe to run on a background thread.
            //
            // LOGIC-03: capture the snapshot inputs HERE (under _gate) so the file IO can
            // move off the dispatcher without racing live engine state. BuildProfileSnapshot
            // CORE is built from in-memory state now; MergeCarryForward folds in the
            // `existing`-owned fields inside the store lock and may return null to SKIP the
            // write on a transient read failure (#2: don't clobber), which Update honours.
            HabituationProfileSnapshot core = BuildProfileSnapshotCore();
            bool vestibularPersist = _vestibularProtection;
            IHabituationProfileStore store = _profileStore;
            Func<HabituationProfileSnapshot?, HabituationProfileSnapshot?> mutate =
                existing => MergeCarryForward(core, existing, vestibularPersist);

            if (_backgroundPersistEnabled)
                EnqueueProfilePersist(store, mutate);
            else
                store.Update(mutate); // synchronous (default) — preserves read-after-write
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
                ForegroundKey = _currentForegroundKey,   // B3: per-app bucketing (null = global)
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
                ForegroundKey = _currentForegroundKey,   // B3: per-app bucketing (null = global)
            });
        }
        catch { /* best-effort; event-log writes must never crash the engine */ }
    }

    /// <summary>LOGIC-03: build the ENGINE-OWNED half of the profile snapshot from live
    /// in-memory state. Called under <c>_gate</c> at persist time and captured into the
    /// mutate closure so the actual file IO (which folds in the <c>existing</c>-owned fields)
    /// can run off the dispatcher without racing live engine state. The vestibular-dependent
    /// fields are resolved HERE from the live <see cref="IsVestibularActive"/> /
    /// <c>_vestibularProtection</c> so the captured snapshot is internally consistent; the
    /// non-vestibular <c>MedicalAdvisoryShown</c> fallback to <c>existing</c> is finalized in
    /// <see cref="MergeCarryForward"/>. Fields owned by OTHER writers of the file (chatbot
    /// longitudinal memory + calibration_* enums + SchemaVersion) are left at defaults and
    /// overlaid by the merge.</summary>
    private HabituationProfileSnapshot BuildProfileSnapshotCore()
    {
        var p = _habituation.CurrentParams;
        return new HabituationProfileSnapshot
        {
            // SchemaVersion left at the DTO default; MergeCarryForward overlays `existing`'s
            // so the documented v1 → v2 → v3 progression is preserved.
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
            // B5: persist the auto-adaptation delta so the gate's contribution survives restarts
            // (re-applied in the ctor load path on top of the restored base trigger).
            AutoAdaptTriggerDeltaPp = _autoAdaptTriggerDeltaPp,
            ParamsSnapshot          = new HabituationParamsSnapshot
            {
                StableWindowMs  = p.StableWindowMs,
                StepIntervalMs  = p.StepIntervalMs,
                StepMagnitudePp = p.StepMagnitudePp,
            },
            // Alpha-2 addendum: an auto-profiling vestibular user (no calibration) must still
            // surface the advisory to the chatbot's [habituation_state] block. The vestibular
            // case is final here; the non-vestibular fallback to existing is done in the merge.
            MedicalAdvisoryShown        = _vestibularProtection ? MedicalAdvisory.Vestibular : null,
            // Alpha-2 addendum: persist the auto-profiling vestibular signal so it
            // survives restarts (restored in the ctor load path).
            VestibularProtectionEnabled = _vestibularProtection,
        };
    }

    /// <summary>EM2 carry-forward merge, invoked as the mutator of
    /// <see cref="IHabituationProfileStore.Update"/> (W2.0-C). <paramref name="existing"/>
    /// is the snapshot the store read for us INSIDE its lock — a consistent view, not a
    /// separate unsynchronised Load — so the carry-forward of fields owned by OTHER writers
    /// of the same habituation_profile.json (the chatbot's LongitudinalObservationWriter +
    /// the calibration applicator) can no longer race against their concurrent writes.
    /// <para>LOGIC-03: this runs inside the store lock (possibly on the background persist
    /// queue) and reads ONLY <paramref name="core"/> (captured under <c>_gate</c>),
    /// <paramref name="existing"/>, and the captured vestibular flags — never live engine
    /// state — so it is safe off the dispatcher.</para></summary>
    private HabituationProfileSnapshot? MergeCarryForward(
        HabituationProfileSnapshot core, HabituationProfileSnapshot? existing,
        bool vestibularPersist)
    {
        // #2 (EM2 hardening, review): if a profile EXISTED at boot but the in-lock re-read is
        // null, that is a transient read failure (file briefly locked / corrupt), NOT a first
        // run. Rebuilding-and-overwriting here would null-fill MedicalAdvisoryShown + the
        // calibration_* fields and wipe the vestibular safety advisory. Skip this persist.
        if (existing is null && _profileExistedAtLoad) return null;

        // UM2: carry the loaded schema version FORWARD (v1→v2→v3 progression). For a genuine
        // first write (no existing file) fall back to the DTO's current default.
        core.SchemaVersion = existing?.SchemaVersion ?? new HabituationProfileSnapshot().SchemaVersion;

        // EM2 — fields owned by OTHER writers of this file; preserved from the existing
        // on-disk snapshot so the engine's persist does not wipe them.
        core.LongitudinalObservations   = existing?.LongitudinalObservations ?? new();
        core.LongitudinalObservationsV2 = existing?.LongitudinalObservationsV2 ?? new();
        core.CalibrationCompleteness    = existing?.CalibrationCompleteness;
        core.CalibrationPath            = existing?.CalibrationPath;
        core.CalibrationResponses       = existing?.CalibrationResponses;
        core.SymptomDominancePrimary    = existing?.SymptomDominancePrimary;
        core.CalibrationConfidence      = existing?.CalibrationConfidence;

        // Non-vestibular: carry the calibration advisory forward. (vestibularPersist is now
        // permanently false post-removal, so this branch always runs.) Vestibular-removal
        // migration: a pre-upgrade snapshot may still carry a stale MedicalAdvisory.Vestibular;
        // carrying it forward unchanged would RESURRECT it forever and keep the three chatbot
        // override interceptors firing against a now-non-vestibular user. Map a carried-forward
        // Vestibular advisory to null so the disk-durable second signal is cleared alongside the
        // bool — both vestibular signals are neutralized on the same persist.
        if (!vestibularPersist)
        {
            var carried = existing?.MedicalAdvisoryShown;
            core.MedicalAdvisoryShown =
                carried == MedicalAdvisory.Vestibular ? null : carried;
        }

        return core;
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
        => Math.Clamp(ComputeFovRadiusPercent(pressure, _habituation.EffectiveTrigger) + _userFovOffset + _habituation.HabituationOffset,
                      15, 100);

    // Periodically re-pull the user's FOV-adjustment history so habituation personalisation
    // stays fresh as the adjustment log grows. Throttled to once per step interval.
    private void RefreshPersonalization(double dt)
    {
        if (_adjustmentLog is null) return;
        _personalizationAccumulator += dt;
        if (_personalizationAccumulator < SenseDefaults.HabituationPersonalizationRefreshSeconds) return;
        _personalizationAccumulator = 0.0;
        _habituation.SetPersonalization(CombinedFovCautionWeight(_adjustmentLog));   // B2: FovRadius + LevelNudge
    }

    /// <summary>B2: the habituation-CAUTION input = the user's FOV-preference history (chatbot FOV
    /// adjustments, Parameter "FovRadius") COMBINED with their manual level nudges (Parameter
    /// "LevelNudge"). Both express "I want more/less protection", so both shape the relaxation cadence.
    /// Kept SEPARATE from the FOV-offset restore (which uses "FovRadius" only) so a level nudge feeds
    /// the caution WITHOUT double-applying the resting FOV offset — the nudge's protection already
    /// persists via the baseline tier. Both channels are tared by the same most-recent Reset
    /// (WeightFor is reset-aware). Null log ⇒ a zero weight. Static + internal for unit cover.</summary>
    internal static PersonalWeight CombinedFovCautionWeight(IAdjustmentLog? log)
    {
        if (log is null) return new PersonalWeight("FovRadius", 0, 0.0, 0.0);
        var fov   = log.WeightFor("FovRadius");
        var nudge = log.WeightFor("LevelNudge");
        int count = fov.Count + nudge.Count;
        if (count == 0) return new PersonalWeight("FovRadius", 0, 0.0, 0.0);
        // Caution MUST be monotone in protection-seeking: appending a fixed-magnitude (−8) nudge
        // channel must NEVER dilute a steeper FovRadius mean. A count-weighted average WOULD — adding
        // −8 nudges to a −20 FOV mean pulls it toward −8, relaxing faster = LESS protection after the
        // user pressed "more protection" (the B2-caution review HIGH). So take the MOST-CAUTIOUS
        // (most-negative) per-channel mean for the caution magnitude; sum Count for the confidence
        // ramp. (NetDelta is summed for completeness but unused downstream: the FOV-offset restore
        // reads WeightFor("FovRadius").NetDelta directly; SetPersonalization consumes MeanDelta+Count.)
        double fovMean   = fov.Count   > 0 ? fov.MeanDelta   : 0.0;
        double nudgeMean = nudge.Count > 0 ? nudge.MeanDelta : 0.0;
        double cautionMean = (fov.Count > 0 && nudge.Count > 0) ? Math.Min(fovMean, nudgeMean)
                           : (fov.Count > 0 ? fovMean : nudgeMean);
        return new PersonalWeight("FovRadius", count, fov.NetDelta + nudge.NetDelta, cautionMean);
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

    // Beta-1 #5: the FOV-radius projection now keys off the user's EFFECTIVE trigger
    // (default 85; vestibular 50) instead of the literal TriggerPressure, so a vestibular
    // user's FOV tightens earlier. For the default user trigger == TriggerPressure so the
    // output is byte-identical. The 1.6× post-trigger saturation is preserved as a fraction
    // of the effective trigger (0.6× trigger past the trigger point).
    private static int ComputeFovRadiusPercent(double pressure, double trigger)
    {
        double trig = trigger > 0 ? trigger : SenseDefaults.TriggerPressure;
        double pre = Math.Clamp(pressure / trig, 0.0, 1.0);
        if (pressure <= trig)
        {
            // Linear: 100% (fully open at rest) → DefaultFovRadiusPercent (35%) at the trigger
            double radius = 100.0 - (100.0 - SenseDefaults.DefaultFovRadiusPercent) * pre;
            return (int)Math.Round(radius);
        }

        int tightestRadius = SenseDefaults.TightestNaturalFovRadiusPercent;
        // Steeper post-trigger slope: saturate at 1.6× trigger (not 2.0×)
        // so realistic sustained OFI actually drives FOV to the floor.
        double post = Math.Clamp(
            (pressure - trig) / (0.6 * trig),
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
    private void UpdateVimsTier(DateTime nowUtc, double dt)
    {
        // UI Auto-Protect hard-latch: while the user's "Overlay Always ON" toggle pins the level, FREEZE
        // the pressure→tier projection at the held tier. Everything else keeps running (pressure, the
        // habituation offset/strike loops, the B5/B6 auto-adaptation, and the severe-spike pause which
        // still fires on raw pressure) — only this tier projection is suspended. Released on toggle-off.
        if (_autoLevelHold)
        {
            _pendingTierCandidate = _vimsTier;
            _pendingTierSinceUtc  = DateTime.MinValue;
            return;
        }

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
        // Beta-1 #5: tier discretizes against the EFFECTIVE trigger (default 85; vestibular
        // 50) so a vestibular user climbs the ladder earlier. Default user == byte-identical.
        double effTrig = _habituation.EffectiveTrigger > 0 ? _habituation.EffectiveTrigger : SenseDefaults.TriggerPressure;
        double t      = Math.Clamp(Current.Pressure / effTrig, 0.0, 1.0);
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

        double gap = levelF - _vimsTier;

        // ── LOGIC-13 / D-5: fresh-seed onboarding protection ──────────────────
        // Deliberate onboarding protection (NOT a change to tier projection): while the
        // freshly-seeded L5「中」 median is protected, HOLD it against any downward movement
        // so a fresh user actually sees/feels the median before the engine takes over. The
        // window ends — and normal projection resumes THIS tick — on EITHER a genuine
        // pressure-driven INCREASE (the user needs more protection: levelF crosses the up
        // boundary / fast-onset gap) OR a fixed settling period of accumulated calm time,
        // whichever first. Inert unless ApplyAutoProfilingStartDefaults armed it.
        if (_onboardingProtectionActive)
        {
            _onboardingElapsedSeconds += dt;
            bool genuineIncrease =
                gap >= SenseDefaults.VimsTierFastOnsetGap
                || levelF >= _vimsTier + 0.5 + SenseDefaults.VimsTierDeadband;
            if (genuineIncrease || _onboardingElapsedSeconds >= OnboardingProtectionSeconds)
            {
                // Protection over — fall through to normal projection (an increase applies
                // immediately below; an elapsed-window calm tick now releases normally).
                _onboardingProtectionActive = false;
            }
            else
            {
                // Still protected and not rising: hold the seeded tier, keep the pending-
                // debounce bookkeeping pinned so the held tier does not walk down.
                _pendingTierCandidate = _vimsTier;
                _pendingTierSinceUtc  = DateTime.MinValue;
                return;
            }
        }

        // ── Fast-onset bypass ────────────────────────────────────────────────
        // A genuine pressure surge can jump levelF by several units in one Update tick.
        // The strict ±1/tick limiter would then need 1.8 s of debounce ticks to catch up
        // (longer than the natural pressure surge itself). When the gap exceeds
        // VimsTierFastOnsetGap we therefore snap straight to the nearest integer tier.
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
