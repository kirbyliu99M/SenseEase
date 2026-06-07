namespace SenseEase.Core.Configuration;

/// <summary>
/// Phase-1 default parameters from the design draft §5.
/// Centralised here so Inference / Rendering / mocks all read one source of truth.
/// </summary>
public static class SenseDefaults
{
    public const int DefaultFovRadiusPercent = 35;
    // Tightest FOV reachable from natural pressure alone (no chatbot/habituation).
    // Decoupled from ComplaintFovDelta, which is a chatbot-feedback nudge — different policy knob.
    public const int TightestNaturalFovRadiusPercent = 15;
    public const int OuterTransitionRadiusPercent = 45;
    public const int GridSizePx = 40;
    public const double GridOpacity = 0.75;
    public const double NoiseGateThreshold = 8;
    public const double TriggerPressure = 85;
    /// <summary>Alpha-2 addendum (cancel explicit calibration → auto-profiling): the
    /// neutral starting level (1..12) a brand-new auto-profiling user is seeded at — L5
    /// 「中」, the Set0 median — from which the AdaptiveFit selector converges. Used only by
    /// <c>MockInferenceEngine.ApplyAutoProfilingStartDefaults</c> (flag-gated).</summary>
    public const int AutoProfilingStartLevel = 5;
    public const double PassiveFlowWeight = 2.0;
    public const double QuadraticTerm = 0.0008;
    // Base decay alpha for a 100 ms step (tau ~= 2.81s).
    public const double BaseDecayAlpha = 0.965;
    public const int ComplaintFovDelta = -15;
    public const int CooldownMs = 8000;
    // Retained Event Log entries (UI mirror + in-memory store). Raised 20 -> 200 so the
    // 事件記錄 / Event Log tab keeps a longer history for easier verification (user request).
    public const int EventLogMaxCount = 200;

    // Driver-mode passive-flow attenuation: in Driver mode the optical-flow index
    // is multiplied by this factor (does not hard-zero). Single source of truth for
    // the MockInferenceEngine driver-attenuation site. See SenseState.cs remarks.
    public const double DriverFlowAttenuation = 0.25;

    // ── Personal-pressure-scale bounds (calibration §7) ───────────────────────
    // Canonical [min, max] clamp for personal_pressure_scale. Mirrored by the
    // public CalibrationProcessor / HabituationOverrideProcessor constants, which
    // now reference these so there is one source of truth.
    public const double PersonalPressureScaleMin = 0.7;
    public const double PersonalPressureScaleMax = 1.5;

    // Phase 2.1 — cursor-velocity OFI normalization
    // 80 px/s-per-OFI-unit: full-screen sweep in 1s (~2000 px/s) ≈ OFI 25; fast gaming swipe ≈ OFI 80.
    // Higher factor => lower OFI per cursor speed (less sensitive).
    public const double CursorVelocityNormFactor = 120.0;
    public const int    CursorVelocityPollIntervalMs = 33; // ~30 Hz
    // EMA smoothing applied to both cursor speed (2.1) and WGC pixel-diff (2.2).
    // Higher alpha => heavier smoothing (less responsive to spikes).
    public const double CursorEmaAlpha = 0.55;

    // Phase 2.2 — WGC screen-capture pixel-diff OFI
    public const int    WgcCaptureIntervalMs = 66;  // ~15 Hz; 1 capture per 2 display frames
    public const int    WgcDownsampleWidth   = 160; // width of grayscale comparison frame
    public const int    WgcDownsampleHeight  = 90;  // height (preserves 16:9 aspect)
    // MAD (mean absolute difference per pixel, 0–255) × factor = raw OFI.
    // Tune after first real WGC data: idle desktop ≈ MAD 0-1, scrolling video ≈ MAD 15-40.
    // Lower factor => lower OFI from same MAD pixel-diff.
    public const double WgcMadNormFactor     = 4.0;

    // ── Phase 3 — Habituation Engine ──────────────────────────────────────────
    // Pressure bands are fractions of TriggerPressure: relax when calm, back off on recurrence.
    public const double HabituationStablePressureFraction     = 0.25; // calm  => pressure < 25% of TriggerPressure
    public const double HabituationRecurrencePressureFraction = 0.60; // spike => pressure >= 60% of TriggerPressure

    // ── DEPRECATED (Phase 2): superseded by HabituationProfileParams ──────────
    // The 7 constants below were the engine's only tuning source before the
    // three-profile system landed. They are retained as the RESILIENT-profile
    // defaults (verified bit-for-bit identical to HabituationProfileParams.Resilient).
    // The engine no longer reads them; new code should reference
    // HabituationProfileParams instead. See docs/HABITUATION-PROFILE.md §1, §2.
    // Kept (not removed) for backward compat — Phase 3+ may relocate them
    // entirely once we're confident no out-of-tree consumer depends on them.
    public const double HabituationStableWindowSeconds        = 120.0; // -> HabituationProfileParams.Resilient.StableWindowSeconds
    public const double HabituationMaxWindowSeconds           = 240.0; // -> _params.StableWindowSeconds * 2.0 (per Q1 rule)
    public const double HabituationStepIntervalSeconds        = 30.0;  // -> HabituationProfileParams.Resilient.StepIntervalSeconds
    public const double HabituationStepPercent                = 2.0;   // -> HabituationProfileParams.Resilient.StepMagnitudePp
    public const int    HabituationMaxOffset                  = 30;    // -> HabituationProfileParams.Resilient.HabOffsetCapPp
    public const double HabituationPauseCooldownSeconds       = 90.0;  // -> HabituationProfileParams.Resilient.PostSpikePauseLowSeconds
    public const int    HabituationPersonalizationMinSamples  = 3;     // -> HabituationProfileParams.Resilient.MinSamplesForFullWeight

    // ── Still actively read (not in HabituationProfileParams) ─────────────────
    // These remain SenseDefaults-owned because they're either profile-independent
    // engine behaviour or candidates for Phase 3+ promotion.
    public const double HabituationMaxStepIntervalSeconds     = 75.0;  // cautious-user cadence upper bound (cap of caution-scaling)
    public const double HabituationMidBandDecayRate            = 0.5;   // non-calm time bleeds calm credit at this rate
    public const double HabituationSpikeBackoffMultiplier      = 0.5;   // Phase 2 spike-halve mult; Phase 3 replaces with strike counter
    public const double HabituationMildBackoffPercent          = 4.0;   // mild discomfort report reduces offset by this
    public const int    HabituationSevereFovThreshold          = 18;    // |fovDelta| >= this => severe; Phase 3 wires into strike counter
    public const double HabituationCautionMeanDeltaDivisor     = 15.0;  // mean FOV delta that saturates "caution" to 1.0
    public const double HabituationTimeScale = 1.0;  // demo accelerator for habituation dt; 1.0 = real time
    public const double HabituationPersonalizationRefreshSeconds = 30.0; // re-pull personalization this often

    // ── Phase 5 — Anti-flicker & tier hysteresis ──────────────────────────────
    // 3-tap median pre-filter on raw OFI rejects single-frame outliers (scene cuts, cursor
    // flicks) at the cost of ~133 ms onset latency. 5-tap was tested first but added 333 ms
    // of WGC-only delay, making screen content respond visibly slower than cursor input.
    public const int VimsMedianTaps = 3;

    // Visual intensity level (1..12, native scale — Adaptive Fit Q2=B) hysteresis. Within a level the displayed mask does not move.
    // To transition UP requires levelF to exceed currentTier + 0.5 + deadband for `UpDebounceMs`.
    // To transition DOWN requires falling below currentTier - 0.5 - deadband for `DownDebounceMs`.
    // Asymmetric attack/release mirrors audio compressor design — fast tighten, slow release.
    public const double VimsTierDeadband     = 0.15;   // half-band width in level units
    public const int    VimsTierUpDebounceMs   = 200;  // confirm rise (~3 frames @ 15 Hz)
    public const int    VimsTierDownDebounceMs = 500;  // confirm fall — halved from 1200 ms
                                                       // after user feedback that overlay fade-out
                                                       // felt sluggish on rapid OFI drops.
    // Fast-release knob for lower tiers (≤ VimsLowTierReleaseCutoff). At low tiers we are
    // mostly relaxed already, so the mask should clear briskly.
    public const int    VimsTierDownDebounceLowMs = 250;  // tiers ≤ cutoff release at this rate
    // Rescaled 7→9 for the native 12-level scale: the top 3 levels (10,11,12)
    // keep the slow "dangerous top" release; levels ≤ 9 clear briskly — same
    // top-3 proportion as the prior 1..10 scale's 8,9,10.
    public const int    VimsLowTierReleaseCutoff  = 9;    // level transition target ≤ this uses low-ms

    // Fast-onset bypass: when levelF outruns the current tier by this many levels in a single
    // tick, snap straight to the new tier (no debounce).
    public const double VimsTierFastOnsetGap = 1.5;

    // Fast-RELEASE bypass: mirror of fast-onset for sudden pressure drops (e.g. user
    // alt-tabs and OFI is suppressed). Without this, tier release walks down by 1 per
    // VimsTierDownDebounceMs even when levelF has already collapsed. With it, a 1.5-level
    // gap drop snaps tier straight down in one tick, killing the residual "stuck darkness"
    // when motion ends abruptly.
    public const double VimsTierFastReleaseGap = 1.5;

    // Display Comfort lux→VCP defaults removed at v0.11 (UI Consolidation Sprint
    // Phase 4) — feature decommissioned along with the entire DDC/CI pipeline.
    // See CHANGELOG.md v0.11 "Removed".

    // ── Adaptive Fit selector (F1/F2 governance) ──────────────────────────────
    // Alpha-2 P3/P4 GROUP C. Provisional, deliberately CONSERVATIVE weights/margins:
    // the selector should prefer Hold over chasing noise until M1 calibrates the OFI
    // norm-factors. All values are read ONLY behind AppConfig.AdaptiveFitSelectorEnabled
    // (default OFF) — nothing here is on today's hot path.
    //
    // score(candidate) = W1·fit(pressure,band) + W2·fit(spike,shape) + W3·fit(calm,band)
    //                    − W4·ManhattanDistance(current,candidate) − W5·manualConflict.
    // The fit-term weights (W1..W3) dominate; the distance penalty (W4) only breaks ties
    // toward the nearer cell; the manual-conflict penalty (W5) is large enough to suppress
    // any single-step move that opposes a recent user adjustment (user authority defers).

    /// <summary>Selector weight on pressure→Band fit (the primary intensity signal).</summary>
    public const double SelectorW1Pressure = 1.0;

    /// <summary>Selector weight on spike-recurrence→Shape fit (response-shape responsiveness).</summary>
    public const double SelectorW2Spike = 0.6;

    /// <summary>Selector weight on calm-ratio→Band fit (the relax-when-calm signal).</summary>
    public const double SelectorW3Calm = 0.6;

    /// <summary>Selector distance penalty per unit Manhattan step. Small: it only breaks
    /// near-ties toward the closer adjacent cell; it must not by itself veto a move that the
    /// fit terms clearly prefer (every candidate is at distance 0 or 1 by construction).</summary>
    public const double SelectorW4Distance = 0.1;

    /// <summary>Selector manual-conflict penalty. Large enough that one conflicting unit of
    /// fit-advantage cannot overcome it — a candidate moving OPPOSITE a recent manual
    /// adjustment is effectively suppressed (user authority defers the auto-move).</summary>
    public const double SelectorW5ManualConflict = 2.0;

    /// <summary>Minimum score advantage argmax must hold over "stay" before any move fires
    /// (anti-overfit confidence gate). Conservative: small fit wobbles do not move the set.</summary>
    public const double SelectorConfidenceMargin = 0.15;

    /// <summary>Half-width (in normalised [0,1] feature units) of the linear fit kernel:
    /// fit = 1 − clamp(|featureNorm − cellTargetNorm| / FitTolerance, 0, 1). Larger ⇒ a
    /// gentler, more forgiving fit (cells further from the ideal still score &gt; 0).
    /// 0.7 is wide enough that a feature near a [0,1] extreme still produces a usable
    /// per-band gradient from mid-grid (so a clearly-hot/clearly-calm window can move),
    /// yet narrow enough that a neutral mid-grid window's best neighbour stays under the
    /// confidence margin (so noise Holds). Provisional until M1 anchors the OFI norms.</summary>
    public const double SelectorFitTolerance = 0.7;

    /// <summary>Window-sample floor: the selector returns Hold until at least this many
    /// samples back the stats (thin data must never move the set).</summary>
    public const int SelectorMinSamplesBeforeAnyMove = 12;

    /// <summary>Seconds the state machine stays in Cooldown after a set change (soft-定型);
    /// no further auto-move is evaluated until it elapses back to Active.</summary>
    public const double SetCooldownSeconds = 300.0;

    /// <summary>Minimum seconds in Settling before the machine may advance to Active
    /// (paired with the min-samples + confident-pick condition).</summary>
    public const double SettlingMinSeconds = 60.0;

    /// <summary>Multiplier applied to the REMAINING cooldown when a proposal is declined —
    /// each decline doubles the wait, so repeated rejection backs the selector off fast.</summary>
    public const double CooldownDeclineMultiplier = 2.0;
}
