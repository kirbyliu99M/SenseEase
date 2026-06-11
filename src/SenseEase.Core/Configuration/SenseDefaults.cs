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

    // ── C7: single-source step-magnitude table + per-call delta clamps ────────────
    /// <summary>C7: per-severity (1..5) FOV-tightening step magnitudes (pp; negative =
    /// tighter). Single source consumed by FeedbackLexicon.DeriveAdjustments.</summary>
    public static readonly int[] FovStepBySeverity = { -6, -10, -15, -22, -30 };
    /// <summary>C7: per-severity (1..5) mask-depth step magnitudes.</summary>
    public static readonly double[] MaskDepthStepBySeverity = { 0.05, 0.08, 0.12, 0.18, 0.25 };
    /// <summary>C7: max FOV delta per single chatbot/NLU call (pp; negative bound).</summary>
    public const int MaxFovDeltaPerCall = -30;
    /// <summary>C7: max accumulated user FOV offset (pp; negative floor for _userFovOffset).</summary>
    public const int MaxUserFovOffsetAccumulated = -60;
    /// <summary>C7: max shader-knob delta per single call (mask/grid/feather, ± this).</summary>
    public const double MaxShaderDeltaPerCall = 0.3;
    /// <summary>C7/G1: minimum composed FOV radius (pp) — tightest the FOV can compose to.
    /// Equal to <see cref="TightestNaturalFovRadiusPercent"/>; named separately for the
    /// composed-radius clamp + the G1 <c>/fov</c> absolute-set lower bound.</summary>
    public const int MinComposedFovRadius = 15;

    // ── F2: tuning constants referenced by 2+ files (SSOT rule: any tuning constant
    //        used by 2+ files MUST live here, not as a per-file literal) ─────────────
    /// <summary>F2: dashboard chart trigger-line — duplicates <see cref="TriggerPressure"/>;
    /// reference this so the chart cannot drift from the engine trigger.</summary>
    public const double ChartTriggerLine = TriggerPressure;
    /// <summary>F2: overlay mask smoothing EMA factor (OverlayWindow cross-fade).</summary>
    public const double MaskSmoothingFactor = 0.25;
    /// <summary>F2: overlay radial brush baseline radius (RelativeToBoundingBox).</summary>
    public const double OverlayBrushRadius = 0.85;
    /// <summary>F2: overlay brush corner-margin multiplier for the dynamic radius.</summary>
    public const double OverlayBrushRadiusCornerMargin = 1.10;
    /// <summary>F2: feathering-halo fix — overlay dynamic-radius ASYMMETRIC envelope half-lives (ms),
    /// consumed by <c>OverlayWindow.ApplyHeadPoseToBrush</c> and asserted by OverlayHeadPoseTests.
    /// The dynamic radius (corner reach) is otherwise recomputed raw every render frame; applied
    /// to a RelativeToBoundingBox brush that makes the feather band translate AND scale in screen
    /// pixels as the head moves → a "breathing" edge perceived as a halo/comet tail. The envelope
    /// damps the radius: fast ATTACK (grow ≈ instantly so a newly-fixated corner stays covered —
    /// preserves the M5 UX-iter 3 corner reach) + slow RELEASE (shrink slowly so the edge never
    /// pumps while the head moves). Center follow is left fully responsive — only the radius is
    /// smoothed. Inactive snaps to <see cref="OverlayBrushRadius"/> (byte-identical v0.9 baseline).</summary>
    public const double OverlayRadiusAttackHalfLifeMs  = 25.0;   // grow toward a larger radius — near-instant
    public const double OverlayRadiusReleaseHalfLifeMs = 600.0;  // shrink back — slow, kills the breathing edge

    // ── Beta A1: one-clock motion model — single One-Euro pose filter ─────────
    // (docs/BETA_FEATHERING_COHERENCE_PLAN.md §A1.) The old design ran four
    // divergent clocks (center 15 ms half-life lerp · radius 25/600 ms asymmetric
    // envelope · grid-opacity EMA+280 ms anim · mask alpha 0.25 EMA). During head
    // motion these strata moved semi-independently → a Gestalt common-fate
    // violation, perceived as a "multilayer halo / swim". A1 funnels the raw
    // head pose through ONE smoothing pass — a single One-Euro filter — producing
    // one smoothed (cx,cy) per render frame; center, radius and the grid hole all
    // derive from THAT single value in the SAME frame on the SAME time-constant,
    // so there is one moving boundary instead of three.
    //
    // One-Euro (Casiez et al. 2012) is an adaptive low-pass: cutoff
    //   fc = FcMin + Beta·|d(pose)/dt|
    // so it smooths hard when the head is near-still (kills sensor-jitter breathing)
    // and opens up when the head moves fast (low lag → responsive follow). That
    // adaptive behaviour IS the asymmetric "fast-attack / slow-settle" semantics the
    // halo fix validated — now a property of the ONE clock rather than a separate
    // radius envelope. Start values per the plan; tune in UVHS.
    /// <summary>Beta A1: One-Euro minimum cutoff frequency (Hz). Lower ⇒ heavier
    /// smoothing of slow/near-still motion (less jitter); the responsiveness floor.</summary>
    public const double OverlayOneEuroFcMin = 1.0;
    /// <summary>Beta A1: One-Euro speed coefficient. Higher ⇒ the cutoff opens up
    /// faster as the head speeds up (less lag on fast moves). 0.007 per the plan.</summary>
    public const double OverlayOneEuroBeta = 0.007;
    /// <summary>Beta A1: One-Euro derivative-estimate cutoff (Hz). Low-passes the
    /// speed estimate that drives the adaptive cutoff so a single noisy frame
    /// cannot momentarily un-smooth the position. 1.0 Hz is the canonical default.</summary>
    public const double OverlayOneEuroDCutoff = 1.0;

    // ── Beta A2: single-pass field renderer (mask + grid as ONE per-pixel field) ─
    // (docs/BETA_FEATHERING_COHERENCE_PLAN.md §A2.) The mask, the cell-fill and the
    // grid are no longer three independently-shaped translucent strata composited on
    // top of one another (the Gestalt common-fate / scission "multilayer halo"). The
    // field is computed once per frame into a premultiplied-BGRA WriteableBitmap at
    // REDUCED resolution and presented through ONE Image with smooth bilinear upscale
    // — the plan states no perceptual loss for a low-frequency gradient + faint grid,
    // and reduced-res keeps the CPU software-composition cost low (AllowsTransparency
    // pins the overlay off the GPU). The ShaderEffect path remains a documented
    // follow-up if the live 165 Hz perf gate fails.
    //
    // These are ADDITIVE constants (SenseDefaults values lock allows additive
    // constants where specified). None of them change a locked rendered value: the
    // field reproduces the VimsLevels depth/peak/floor and the 0.35→0.95 feather
    // envelope byte-identically at the knob defaults (see OverlayWindow field math).

    /// <summary>Beta A2: field render downscale factor. The field bitmap is computed
    /// at 1/this of the screen resolution and bilinear-upscaled by the one Image.
    /// 4 ⇒ quarter-res per axis ⇒ ~1/16 the pixel work. A low-frequency gradient +
    /// faint grid carries no visible loss at this factor; raise if banding shows.</summary>
    public const int OverlayFieldDownscale = 4;

    /// <summary>Beta A2: gamma applied to the falloff parameter before 8-bit encode
    /// (banding mitigation, best-effort, no dithering — dithering is locked out). The
    /// dark-ramp interpolation is shaped in gamma-space so the limited 8-bit codes are
    /// distributed where the eye is most sensitive in the near-black band. ~2.2 per the
    /// plan. 1.0 would be linear (the un-shaped baseline).</summary>
    public const double OverlayFieldGamma = 2.2;

    /// <summary>Beta A2 (D-2): MaskDepth → effective-peak multiplier span. MaskDepth is
    /// normalised [0,1] with default <c>ShaderParams.DefaultMaskDepth</c> (0.74). The
    /// renderer maps it to a bounded multiplier on the tier-resolved peak alpha:
    /// <c>mult = 1 + (maskDepth − 0.74) · span</c>, then clamps the EFFECTIVE alpha so it
    /// is never below the 0.20 floor (when the mask is commanded on) and never above the
    /// VimsLevels table value for the tier. At the 0.74 default the multiplier is exactly
    /// 1.0 ⇒ byte-identical to the table. Span chosen so the full knob travel reaches the
    /// bounds: driving MaskDepth to 1.0 saturates the multiplier into the table-ceiling
    /// clamp, and driving it to 0 collapses a high-tier peak all the way down into the
    /// 0.20 floor clamp — the knob is meaningful across its whole range yet still bounded
    /// by the ceiling above and the always-on floor below.</summary>
    public const double OverlayMaskDepthMultiplierSpan = 1.15;

    /// <summary>Beta A2 (D-2): always-on mask depth floor enforced IN the field. When the
    /// mask is commanded on, the effective peak alpha can never resolve below this even if
    /// MaskDepth is driven to 0 — the core VIMS protection floor (deliberately stricter
    /// than VR norms; clinical choice). Mirrors <c>ShaderCommandLexicon.MaskDepthSafetyFloor</c>.</summary>
    public const double OverlayMaskEffectiveAlphaFloor = 0.20;

    /// <summary>Beta A2 (D-2): FeatherWidth → feather-band-width scale. FeatherWidth is
    /// normalised [0,1] with default <c>ShaderParams.DefaultFeatherWidth</c> (0.50). The
    /// renderer keeps the ramp END pinned at 0.95 (corner hold) and slides the ramp START
    /// so a wider feather pulls the start inward (a longer, softer band) and a narrower
    /// feather pushes it outward (a tighter band). At the 0.50 default the start resolves
    /// to exactly 0.35 ⇒ the locked 0.35→0.95 envelope, byte-identical. This span bounds
    /// how far the start can travel either way within safe clamps.</summary>
    public const double OverlayFeatherStartSpan = 0.20;

    /// <summary>Beta A2: faint screen-locked grid line alpha ceiling (0..1) at full grid
    /// brightness. Matches the legacy GridLineBrush A=130/255 ≈ 0.51 so the field grid
    /// reads at the same faintness the separate grid Lines did. The per-frame
    /// GridCeiling(gridVisibility) curve still drives the brightness; this is the value it
    /// scales toward. Grid stays WORLD-LOCKED (mod(px, GridSizePx)) — never moves with the
    /// mask center.</summary>
    public const double OverlayGridLineMaxAlpha = 130.0 / 255.0;

    /// <summary>Beta A2 (UVHS 2026-06-11 corrected semantics): the open-window threshold for
    /// the PERIPHERAL grid reveal. The faint world-locked grid lines share the FOV mask's
    /// radial window (clean centre, revealed periphery); a line pixel only shows where that
    /// shared window is open PAST this value — i.e. outside the genuinely-clean centre. This
    /// keeps the centre grid-free while the grid fills the dimmed periphery (the 3762a42
    /// presentation). Low so the clean centre stays clean and the grid begins right at the
    /// feather. (Previously misread as "draw grid where mask alpha is BELOW this", which
    /// inverted the layout into a centre grid — see BETA_FEATHERING_COHERENCE_PLAN §A2.)
    /// Still one alpha per output pixel: the periphery line blends OVER the mask dim.</summary>
    public const double OverlayFieldGridThreshold = 0.04;

    /// <summary>Beta A2: how often (seconds) the rolling field-recompute frame-time stats
    /// (avg / p99) are flushed to wgc_diag while the overlay is active, for Kirby's live
    /// 165 Hz perf gate. One line a minute — negligible log volume.</summary>
    public const double OverlayFieldPerfLogIntervalSeconds = 60.0;

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

    /// <summary>D-4 (ratified 2026-06-11; withdrawn Wave-12 "doc-cleanup only" triage):
    /// cursor-move OFI attenuation. While <c>WindowsActivityFilter.ShouldSuppressOfi</c>
    /// holds (300 ms after any pointer delta / chrome event) the WGC pixel-diff OFI is
    /// multiplied by this factor instead of being hard-zeroed, so a user who keeps the
    /// mouse moving over genuinely-moving video/scroll content still accumulates SOME
    /// pressure (LOGIC-05/NE9) rather than none. Intentionally mirrors the Driver
    /// attenuation magnitude (<see cref="DriverFlowAttenuation"/>) but is a SEPARATE,
    /// independently-named knob so the two semantics can diverge later.</summary>
    public const double CursorMoveOfiAttenuation = 0.25;

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

    // DEAD-09: The 7 DEPRECATED (Phase 2) habituation constants
    // (HabituationStableWindowSeconds, HabituationMaxWindowSeconds,
    //  HabituationStepIntervalSeconds, HabituationStepPercent,
    //  HabituationMaxOffset, HabituationPauseCooldownSeconds,
    //  HabituationPersonalizationMinSamples) were removed 2026-06-11.
    // Successors: HabituationProfileParams.Resilient (e.g. .HabOffsetCapPp = 30).

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
    // norm-factors. All values are read behind AppConfig.AdaptiveFitSelectorEnabled
    // (default ON since c1461a9) — selector and head-tracking are both live.
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

    /// <summary>P5 (propose-confirm): time-to-live for an UNCONFIRMED cross-segment set-move
    /// proposal queued by the selector. The proposal is surfaced ONLY when the user next opens
    /// the pop-up; if they never do within this window it decays and the selector is free to
    /// re-evaluate. 24h. In-memory only (never persisted).</summary>
    public const double SetMoveProposalTtlSeconds = 86400; // 24h; unconfirmed system suggestion decays
}
