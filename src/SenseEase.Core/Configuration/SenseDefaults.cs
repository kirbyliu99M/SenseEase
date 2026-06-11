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
    // Kirby ruling 2026-06-11 (UVHS live): grid cell spacing reduced 20% (40 → 32) —
    // 「目前網格的每格間距有點大，可以縮小20%」. DPI-scaled at draw time as before.
    public const int GridSizePx = 32;
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
    /// faster as the head speeds up (less lag on fast moves). Kirby round-3 (item 4.2,
    /// 2026-06-11): raised 0.007 → 0.015 — the plan's 0.007 under-reacted to fast head
    /// turns, so the mask lagged then caught up, read as 彈跳 (bounce). 0.015 keeps the
    /// heavy near-still smoothing (FcMin unchanged) while opening the cutoff sooner on
    /// fast moves (speed-adaptive: smooth when slow, responsive when fast). ROUND-4
    /// TUNABLE / UVHS-adjustable.</summary>
    public const double OverlayOneEuroBeta = 0.015;
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

    /// <summary>Banding round-5 (2026-06-11): the DEVICE-PIXEL field-resolution adaptive cap.
    /// <c>EnsureFieldBitmap</c> now sizes the field from PHYSICAL device pixels (DIP extent ×
    /// CompositionTarget.TransformToDevice) divided by <see cref="OverlayFieldDownscale"/>, so at
    /// 200% scaling the field is 720×450 instead of the DIP-sized 360×225 — 4× the pixels, which
    /// HALVES every banding band's on-screen width and DOUBLES the gradient sample density. For a
    /// VERY LARGE physical screen the band-path recompute would grow past the ~165 Hz live budget,
    /// so <see cref="OverlayWindow.ResolveFieldDownscale"/> steps the downscale UP (never below the
    /// base 4) whenever the field would otherwise exceed THIS many device pixels. 480_000 admits
    /// the full base downscale for every common panel up to true-4K at 200% (2880×1800/4 = 324_000;
    /// a 4K 3840×2160 physical /4 = 518_400 steps to /5 ⇒ 768×432 = 331_776), keeping the recompute
    /// band bounded (measured well under the ~3.5 ms band ceiling at ≤ this size). Raise only if a
    /// larger field profiles safely on the lowest-spec target. ROUND-5 TUNABLE.</summary>
    public const double OverlayFieldMaxFieldPixels = 480_000.0;

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

    // TOMBSTONE (UVHS round 2, 2026-06-11): OverlayGridLineMaxAlpha (= 130.0/255.0) and
    // OverlayFieldGridThreshold (= 0.04) were the additive A2-era constants that fed the
    // GRID-LINE rasterization INTO the quarter-res field (ResolveGridLineAlpha + the
    // peripheral-open-window threshold). The hybrid fix REMOVED grid rasterization from the
    // field — a 1.2 px world-locked hairline cannot survive the quarter-res downscale (it
    // became a fat bilinear band that popped in 4 px-quantized steps: the "grid FORM wrong /
    // grid JUMPS" verdict). The grid is now full-res WPF vector geometry (OverlayWindow.DrawGrid,
    // GridLineBrush A=130/255 used DIRECTLY as a frozen brush — not via a constant). Neither
    // value is read by any code or test anymore, so both are removed. The grid faintness lives
    // in GridLineBrush; the periphery reveal lives in the inverse-feather OpacityMask window.
    // These were additive-era constants (not locked values); the lock allows additive removal.

    /// <summary>Beta A2: how often (seconds) the rolling field-recompute frame-time stats
    /// (avg / p99) are flushed to wgc_diag while the overlay is active, for Kirby's live
    /// 165 Hz perf gate. One line a minute — negligible log volume.</summary>
    public const double OverlayFieldPerfLogIntervalSeconds = 60.0;

    /// <summary>Kirby round-9 ruling (2026-06-11): FREEZE the corner-approach field growth
    /// entirely (邊角縮放問題再現 → 0). Lineage: the A2 field normalizes each pixel's radial
    /// coordinate by the distance to the farthest screen corner FROM THE CURRENT CENTER (the
    /// CornerNormR denominator), so as the One-Euro center approached an edge/corner the clear
    /// hole + feather band ENLARGED — the "corner-approach zoom". Round-4 halved it (0.5,
    /// 放大的範圍砍半) while the grid-window brush stayed fully frozen (84d6fd3), which made the
    /// residual mask growth tolerable. The canvas-model unification (dae9879) then retired that
    /// frozen brush and generated the grid window from THIS field geometry — so the grid lines
    /// (high-frequency content) inherited the half growth and the zoom percept returned. The
    /// renderer blends the live denominator toward the REST denominator by this factor:
    /// <c>effectiveDenom = restDenom + (liveDenom − restDenom) · damping</c>, where restDenom is
    /// the farthest-corner distance from the REST center (0.5, 0.5) under the SAME aspect
    /// correction (a pure function of aspect, computed once per field-size change — never per
    /// pixel). damping = 0.0 pins effectiveDenom == restDenom ALWAYS: the hole/feather SIZE is
    /// constant everywhere and only the hole CENTER follows the head — the exact shape of the
    /// standing motion principle (「跟著User頭部追蹤已經是最大的限度」). At the rest center this
    /// is byte-identical to the 0.5 build (liveDenom == restDenom there for any damping). It
    /// NEVER weakens protection: with the center at an extreme, the smaller frozen denom makes
    /// the farthest screen corner's normalized r LARGER (≥ 1), so that corner saturates the
    /// feather to full dim even harder — both the mask and the unified grid window go SOLID
    /// there (canvas full). UVHS-adjustable (1.0 ⇒ pre-round-4 full growth; 0.5 ⇒ round-4
    /// halved growth; 0.0 ⇒ frozen size, center still head-follows).</summary>
    public const double OverlayFieldCornerGrowthDamping = 0.0;

    // TOMBSTONE (UVHS round-8 fix, 2026-06-11): GridWindowFadeOutStart (was 0.95) drove the
    // grid OpacityMask's band-pass FALL — the outer fade where the grid lines faded back to
    // fully transparent beyond the feather end. Kirby's clarified CANVAS MODEL REVOKES that
    // outer fade: the grid canvas is PRESENT everywhere outside the gaze hole (every corner, at
    // every gaze), so there is no fade-out band at all. The RadialGradientBrush window
    // (BuildGridOpacityMaskBrush / BuildLevelFeather) is retired with it; the grid OpacityMask is
    // now an ImageBrush over the _gridWindowBmp written in the field pass (clear hole →
    // smootherstep rise → SOLID beyond). No code or test reads this constant anymore; it is
    // removed (the band-pass FALL it parameterized no longer exists). Additive-era constant
    // (not a locked value) — the lock allows additive removal.

    // ── Kirby round-3 overlay-rendering tunables (2026-06-11) ─────────────────
    // ADDITIVE constants for the UVHS round-3 feedback (items 1, 2, 4). They do NOT
    // touch any locked rendered value: VimsLevels and the GridCeiling CURVE stay
    // byte-identical — these are a tier-ONSET gate + an OPACITY CAP that WRAP the
    // GridCeiling output, and a single One-Euro beta bump (responsiveness). All four
    // are flagged for Kirby's round-4 review.

    /// <summary>Kirby round-3 (item 1): grid tier-ONSET. The full-res vector grid
    /// contributes NOTHING below this tier — at L1..L(onset-1) the grid is fully
    /// invisible. From the onset the contribution ramps
    /// <c>gridTierFraction = clamp((tier − GridOnsetTier) / (12 − GridOnsetTier), 0, 1)</c>,
    /// reaching full strength at L12. Kirby found the crisp vector lines read hotter
    /// than the old blurry band and appeared too EARLY (the old tierFraction faded in
    /// from L2 ≈ 5%); onset L4 keeps L1..L3 grid-free. ROUND-4 TUNABLE.</summary>
    public const int GridOnsetTier = 4;

    /// <summary>Kirby round-3 (item 2): grid opacity CAP. The ApplyGridIntensity target
    /// is clamped to <c>min(computedTarget, GridOpacityCap)</c>. Effective max on-screen
    /// line alpha becomes cap × stroke(130/255) ≈ 0.40 × 0.51 ≈ 0.20 — clearly half-
    /// transparent (Kirby found the crisp lines too STRONG). The GridCeiling CURVE itself
    /// is UNTOUCHED (GridCeilingTests stay green); this cap WRAPS its output. ROUND-4
    /// TUNABLE.</summary>
    public const double GridOpacityCap = 0.40;

    /// <summary>Kirby round-3 (item 4): the CONSTANT intensity floor used for the grid +
    /// cell-tint strength while the persistent-overlay baseline is rendering steadily
    /// (保持遮罩開啟 ON, live tier &lt;= baseline). It is the SAME 0.55 floor the live path
    /// uses at zero smoothedIntensity, so the steady baseline matches a calm live frame
    /// WITHOUT the per-tick OFI/pressure jitter that made the grid pulse. ROUND-4 TUNABLE.</summary>
    public const double OverlaySteadyBaselineIntensity = 0.55;

    /// <summary>Kirby round-3 (item 4.1): field recompute / pose-mutation threshold used
    /// while head-tracking is ACTIVE. The coarser <c>OverlayWindow.FieldEpsilon</c> (0.0005)
    /// is sized for the static-screen "cost ~zero" gate; it can quantize the One-Euro filter's
    /// slow-settling glide (a sub-0.0005 per-frame delta freezes the field while the grid brush
    /// keeps moving → micro-stutter). This tighter value (sub-texel: ~1/40 px on the quarter-
    /// res field, &lt;&lt; 1 display px) lets the field follow ANY real head motion faithfully
    /// while still rejecting pure floating-point noise. Inactive/parked overlays keep the coarse
    /// epsilon. ROUND-4 TUNABLE / UVHS-adjustable.</summary>
    public const double OverlayFieldMotionEpsilon = 0.00005;

    // ── Parked-recompute storm fixes (2026-06-11, alpha1-w2-engine-integrity) ─
    // wgc_diag showed ~75 field recomputes/SECOND while head-tracking pose was active
    // (the ~165 Hz CompositionTarget.Rendering pump) but the mask read active=False and the
    // screen was static — every pump frame re-rendered a field whose 8-bit output was
    // byte-IDENTICAL to the last (the One-Euro filter creeps in sub-texel steps far below one
    // quarter-res field texel). At ~75/s the Parallel.For full-recompute spikes (19–28 ms when
    // a fullscreen game saturates the CPU) chain into the visible freeze. These constants gate
    // the redundant recomputes at the SOURCE without touching any locked rendered value or the
    // locked pose-tracking epsilon (the field center + grid-window mirror still track the head
    // on the exact OverlayFieldMotionEpsilon pose — only the redundant re-RENDER is skipped).

    /// <summary>Render-gate texel fraction for the pose-driven field recompute. The field
    /// center still TRACKS the head on the locked <see cref="OverlayFieldMotionEpsilon"/> (the
    /// grid-window mirror glides continuously), but <c>RenderFieldIfDirty</c> only recomputes +
    /// re-uploads the bitmap when the pose moved by at least 1/this of ONE field texel since the
    /// last RENDERED pose. A pose move below this changes the bilinear-upscaled low-frequency field
    /// by AT MOST one 8-bit code (≤1 LSB on the quarter-res buffer — the SAME invisibility bar the
    /// LUT pipeline already meets: ComposeFieldPixelLut_MatchesExactCompose_WithinOneByte; pinned by
    /// OverlayHeadPoseTests.ParkedPump_SubTexelPose_RendersWithinOneByte). 4 ⇒ a QUARTER-texel
    /// quantum: empirically the coarsest fraction that holds the field within 1 LSB across every
    /// tier/pose (a half-texel showed 2 LSB on the steepest tier-12 falloff). A quarter-texel is
    /// still ~14× the locked pose-tracking epsilon, so the ~165 Hz pump's sub-texel One-Euro creep
    /// (~0.05 texel) is converted from ~137 recomputes/s into ~0 when parked, while any real head
    /// turn still crosses the quantum within one frame and tracks faithfully.</summary>
    public const double OverlayFieldRenderTexelFraction = 4.0;

    /// <summary>Quantization step for the cell-tint strength + displayed mask alpha re-dirt gate.
    /// Both feed an 8-bit premultiplied buffer, so a change below 1/256 of full scale cannot flip
    /// any output byte. ApplyGridIntensity / ResolveMaskParams quantize their EMA outputs to this
    /// grid before deciding to mark the field dirty: the EMA chases live OFI/pressure noise and
    /// would otherwise re-dirt on sub-8-bit wobble every engine tick (the parked non-persistent
    /// storm). 1/256 is exactly one 8-bit code ⇒ the quantized value reaches the rendered byte
    /// identically; sub-code wobble is dropped. Invisible by definition (the buffer is 8-bit).</summary>
    public const double OverlayEightBitQuantum = 1.0 / 256.0;

    /// <summary>Max threads for the field-recompute <c>Parallel.For</c>. Capped at
    /// <c>max(1, ProcessorCount − 2)</c> so a fullscreen game saturating every core can never starve
    /// the WHOLE thread-pool out from under the render: two cores are always left for the
    /// UI/compositor (this is the real starvation fix — the 19–28 ms spikes in wgc_diag were the
    /// uncapped Parallel.For fighting the game for every core). With the parked-storm fixes a render
    /// only happens on a genuine visible change, so this cost is rare anyway.</summary>
    public static int OverlayFieldMaxParallelism => Math.Max(1, Environment.ProcessorCount - 2);

    /// <summary>Pixel-count threshold below which a field recompute runs SINGLE-THREADED (no
    /// Parallel.For dispatch). A small dirty band (a tiny pose nudge whose feather annulus does not
    /// span the field) is cheaper to compute inline than to fan out — and running it on the UI
    /// thread makes it immune to thread-pool starvation. Bands at or above this (and every full
    /// recompute) use the CAPPED Parallel.For. ~16 rows × a 720-wide field ≈ 11.5k px is well under
    /// 1 ms single-thread (measured ≈0.07 ms/row); above it the parallel fan-out wins even capped.
    /// NOTE: for THIS field geometry (feather disc reaches ±~0.66 normalized) most pose moves dirty
    /// a near-full-height band, so they correctly take the capped-parallel path — only genuinely
    /// tiny bands go single-threaded.</summary>
    public const int OverlayFieldSingleThreadPixelThreshold = 12_000;

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

    // ── Shape-axis render-character wiring (Kirby ruling 2026-06-11) ──────────────
    // The 50-set grid is now genuinely 2D: Band = protection depth (mask alpha / FOV
    // floor — UNCHANGED), Tier = the within-session 12-rung ramp (UNCHANGED), and Shape
    // (0..4) is the RENDER CHARACTER axis — it drives the per-set grid baseline strength
    // (GridVisibility) AND the per-set feather character (FeatherWidth). Q4 lock: Shape
    // touches SHADER / render params ONLY — never the LeakyIntegrator decay, never Scale,
    // never PeakMaskAlpha / FovFloorPercent (those stay Band-only). These are per-SET
    // baselines (constant across a set's 12 levels, re-seeded on a set move), NOT animated
    // values — nothing jumps mid-set (Kirby's standing motion principle).
    //
    // RATIONALE (baked-in): the GridCeiling formula saturates the rendered grid at
    // gv >= 0.75, so the three strongest shapes (2,3,4) cannot differentiate via grid
    // brightness alone — they instead differentiate via the FEATHER edge (D-2 made
    // FeatherWidth a REAL renderer input: it slides the smoothstep ramp start). So the
    // five shapes read as: 0/1 = progressively brighter grid at the SAME default edge;
    // 2 = the Set0 column (gv 0.75, default edge); 3/4 = the saturated grid with a
    // progressively SOFTER edge. All five are visually distinct. Render-only (Q4).
    //
    // ROUND-4 TUNABLE: the five mapped values per axis are round-4 design constants.
    // Set0 (Shape index 2) is LOCKED byte-identical to today: gv 0.75, feather offset 0.0
    // ⇒ FeatherWidth resolves to exactly the profile default (0.50). The feather RESULT is
    // clamped into the ShaderParams [0,1] valid range by the consumer.

    /// <summary>Shape-axis (Kirby 2026-06-11) per-Shape GridVisibility baseline, indexed by
    /// <c>GridCoord.Shape</c> (0..4). Shape 2 (the Set0 column) is exactly
    /// <see cref="Models.ShaderParams.DefaultGridVisibility"/> (0.75) so Set0 stays byte-identical.
    /// Shapes 2..4 share 0.75 because the GridCeiling render saturates at gv>=0.75 — they
    /// differentiate via <see cref="ShapeFeatherOffsetTable"/> instead. ROUND-4 TUNABLE.</summary>
    public static readonly double[] ShapeGridVisibilityTable = { 0.35, 0.55, 0.75, 0.75, 0.75 };

    /// <summary>Shape-axis (Kirby 2026-06-11) per-Shape FeatherWidth OFFSET added to the
    /// profile-default feather, indexed by <c>GridCoord.Shape</c> (0..4). Shape 2 (the Set0
    /// column) is 0.0 so the resolved feather equals the profile default
    /// (<see cref="Models.ShaderParams.DefaultFeatherWidth"/> = 0.50) byte-identically. The two
    /// strongest shapes soften the edge progressively (+0.15, +0.30) since the grid is already
    /// gv-saturated. The consumer adds the offset to the default and CLAMPS into the
    /// ShaderParams [0,1] range. ROUND-4 TUNABLE.</summary>
    public static readonly double[] ShapeFeatherOffsetTable = { 0.00, 0.00, 0.00, 0.15, 0.30 };
}
