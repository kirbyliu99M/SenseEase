using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SenseEase.App.Interop;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;

namespace SenseEase.App.Views;

/// <summary>
/// Transparent, topmost, click-through overlay. Beta A2 (single-pass field):
/// renders the peripheral FOV dimming mask AND the screen-locked Chang-Hain anchor
/// grid as ONE per-pixel field into a single premultiplied-BGRA
/// <see cref="WriteableBitmap"/>, presented through one <c>Image</c> with smooth
/// bilinear upscale from reduced resolution.
///
/// <para>This replaces the prior three-stratum composition (a center-following
/// <c>RadialGradientBrush</c> FOV mask + a full-screen near-black cell-fill
/// <c>Rectangle</c> + white grid <c>Line</c>s under a separate radial OpacityMask)
/// — the Gestalt common-fate / perceptual-scission "multilayer halo" the
/// BETA_FEATHERING_COHERENCE_PLAN diagnosed. One alpha per pixel ⇒ no stacked
/// layers, no separate cell-fill, no scalar-radius "breathing".</para>
///
/// <para>The A1 One-Euro-smoothed (cx,cy) handed in by the MainWindow pump remains
/// the single pose input; one set of field params per frame. MaskDepth and
/// FeatherWidth are now REAL inputs (D-2, closing CW-2): MaskDepth bounds the peak
/// alpha (clamped to the ≥0.20 floor / VimsLevels ceiling), FeatherWidth slides the
/// feather-band start — both byte-identical to the prior baseline at their defaults.</para>
/// </summary>
public partial class OverlayWindow : Window, IOverlayRenderer
{
    private const double BrushRadius = SenseDefaults.OverlayBrushRadius; // F2: single-source
    private const double BrushRadiusCornerMargin = SenseDefaults.OverlayBrushRadiusCornerMargin; // F2: single-source

    /// <summary>
    /// VIMS 12-level table (native scale, Adaptive Fit Q2=B). Each row is
    /// (R, G, B, peak-alpha fraction 0–1). L1–L7 are pure gray (#666 → #222); the
    /// cool-shift begins at L8 (a subtle blue tint, Blue channel up to +6 above R/G) —
    /// a pure-black mask reads as a "uniform overlay" on bright backgrounds, but a
    /// cool-shifted near-black reads as a deliberate "out-of-focus dimming" effect,
    /// drawing the eye to the clear center even on white content. Engine hysteresis
    /// (state.VimsTier) controls discrete tier identity; OverlayWindow's per-tick
    /// smoothing cross-fades color+alpha between tiers.
    /// </summary>
    // internal (not private) so D6DayOneProtectionTests / AdaptiveFit* tests can assert
    // against the canonical depth table. Native 12-level scale (Adaptive Fit Q2=B).
    // L1 stays 0% alpha (D6 day-1 protection floor). Alpha climbs monotonically
    // 0.00 → 0.92; hue shifts neutral gray → cool-shifted near-black (+blue tint from
    // L8 up so the mask reads as deliberate dimming on bright bg). PRESERVED byte-for-byte
    // by Beta A2 — the single-pass field reproduces these rows exactly at the knob defaults.
    internal static readonly (byte R, byte G, byte B, double Alpha)[] VimsLevels =
    {
        (102, 102, 102, 0.00), // L1  #666666  0%   neutral gray (day-1 floor)
        (102, 102, 102, 0.14), // L2  #666666  14%
        ( 85,  85,  85, 0.25), // L3  #555555  25%
        ( 68,  68,  68, 0.34), // L4  #444444  34%
        ( 68,  68,  68, 0.42), // L5  #444444  42%
        ( 51,  51,  51, 0.50), // L6  #333333  50%
        ( 34,  34,  34, 0.58), // L7  #222222  58%
        ( 20,  20,  24, 0.66), // L8  #141418  66%  cool-shift begins
        (  8,   8,  14, 0.73), // L9  #08080E  73%
        (  4,   4,  10, 0.80), // L10 #04040A  80%
        (  2,   2,   8, 0.86), // L11 #020208  86%
        (  0,   0,   4, 0.92), // L12 #000004  92%  peak — slight blue read on bright bg
    };

    // ── Smoothed mask color + alpha (tier cross-fade) ────────────────────────
    // Tracks the engine's discrete tier target via per-tick exponential approach so
    // tier transitions cross-fade visually over ~250 ms. Preserves the continuous-
    // looking transitions WITHOUT giving up the engine's tier hysteresis.
    private double _displayedR, _displayedG, _displayedB;
    private double _displayedAlpha;
    private bool   _displayedSeeded;
    private const double MaskSmoothingFactor = SenseDefaults.MaskSmoothingFactor; // F2: single-source

    // Grid brightness EMA (same signal that drove GridCanvas.Opacity before A2).
    private double _smoothedIntensity;
    private double _gridBrightness; // 0..1, eased toward the per-frame ceiling target

    // ── A2 single field state (one moving boundary, one alpha per pixel) ─────
    // The A1 One-Euro-smoothed pose is the SINGLE input; center + radius derive from
    // it in the SAME frame on the SAME time-constant. The grid is WORLD-LOCKED
    // (mod(px, GridSizePx)) and never moves with the center.
    private double _fieldCx = 0.5;
    private double _fieldCy = 0.5;
    private double _fieldRadius = BrushRadius;

    // Resolved per-pixel field parameters (recomputed only on real input change).
    private byte   _maskR, _maskG, _maskB;
    private double _maskPeakAlpha;   // effective peak (after MaskDepth + floor/ceiling), 0..1
    private double _featherStart;    // ramp start, normalized radial coord
    private bool   _maskActive;

    // ── The single-pass field bitmap (reduced-res, premultiplied BGRA) ───────
    private WriteableBitmap? _fieldBmp;
    private int    _fieldW, _fieldH;             // reduced-res field dimensions
    private double _lastScreenW = -1, _lastScreenH = -1;
    private byte[]? _fieldBuf;                    // BGRA scratch buffer

    // Recompute gate: only redraw when an input moved beyond epsilon (a static
    // screen must cost ~zero). Packs the inputs that change the rendered field.
    private bool   _fieldDirty = true;
    private double _lastCx = double.NaN, _lastCy = double.NaN, _lastRadius = double.NaN;
    private double _lastPeak = double.NaN, _lastFeatherStart = double.NaN, _lastGrid = double.NaN;
    private int    _lastColorKey = -1;
    private bool   _lastFieldActive;
    private const double FieldEpsilon = 0.0005;

    // ── Perf instrumentation (Kirby's 165 Hz live gate) ──────────────────────
    private double _recompAvgMs;
    private double _recompP99Ms;       // cheap rolling high-water estimate
    private long   _recompCount;
    private double _recompSumMs;
    private double _recompMaxMs;
    private DateTime _lastPerfFlushUtc = DateTime.MinValue;

    /// <summary>Debug property for the live perf gate: rolling average field-recompute
    /// time (ms) since the last one-minute flush. ~0 while the screen is static (the
    /// field is not recomputed when inputs are unchanged). Exposed for the UVHS soak.</summary>
    public double FieldRecomputeAvgMs => _recompAvgMs;

    /// <summary>Debug property for the live perf gate: rolling worst-case (p99-ish)
    /// field-recompute time (ms) since the last one-minute flush.</summary>
    public double FieldRecomputeP99Ms => _recompP99Ms;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded            += (_, _) => { EnsureFieldBitmap(); MarkFieldDirty(); RenderFieldIfDirty(); };
        SizeChanged       += (_, _) => { EnsureFieldBitmap(); MarkFieldDirty(); RenderFieldIfDirty(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ClickThrough.Enable(this);

        // Fade the overlay in so it doesn't pop harshly.
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(
                0, 1, new Duration(TimeSpan.FromMilliseconds(380)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
    }

    // ── IOverlayRenderer ─────────────────────────────────────────────────────

    /// <summary>Update the overlay from the current SenseState.</summary>
    public void ApplyState(SenseState state)
    {
        EnsureFieldBitmap();
        ResolveMaskParams(state);
        ResolveGridBrightness(state);
        // Head pose comes from the SAME state on the engine-tick path; the ~165 Hz pump
        // path uses UpdateHeadGazeCenter. Both funnel into ApplyHeadPose.
        ApplyHeadPose(state.HeadGazeX, state.HeadGazeY, state.HeadTrackingActive);
        RenderFieldIfDirty();
    }

    /// <summary>High-frequency entry point for the head-pose pump (~165 Hz
    /// <c>CompositionTarget.Rendering</c>, MainWindow). Bypasses the full mask/grid
    /// parameter recomputation and updates ONLY the pose-derived center + radius, then
    /// re-renders the field if anything moved beyond epsilon.
    ///
    /// <para>Beta A1 (one-clock): the pose handed in is ALREADY the single One-Euro-
    /// smoothed value (the pump owns the one filter); this method does NOT re-smooth.
    /// <paramref name="dtMs"/> is retained for signature compatibility but no longer
    /// drives a separate radius envelope — radius derives directly from the smoothed
    /// center. Must run on the UI thread (mutates a WriteableBitmap).</para></summary>
    public void UpdateHeadGazeCenter(double headGazeX, double headGazeY, bool active, double dtMs = 0.0)
    {
        if (_fieldBmp is null) return;
        ApplyHeadPose(headGazeX, headGazeY, active);
        RenderFieldIfDirty();
    }

    // ── Mask + grid parameter resolution (engine-tick cadence) ───────────────

    /// <summary>Resolve the per-frame mask color + EFFECTIVE peak alpha + feather-band
    /// start from the state. MaskDepth and FeatherWidth (D-2) are wired here:
    /// <list type="bullet">
    ///   <item><b>MaskDepth</b> → bounded multiplier on the tier-resolved peak alpha,
    ///   then the EFFECTIVE alpha is clamped so it is never below the 0.20 floor (when the
    ///   mask is commanded on) and never above the VimsLevels table value for the tier;
    ///   MaskPeakAlphaOverride precedence is preserved. At MaskDepth's default the
    ///   multiplier is 1.0 ⇒ byte-identical to the table.</item>
    ///   <item><b>FeatherWidth</b> → slides the smoothstep ramp start within safe clamps
    ///   (end pinned at 0.95). At FeatherWidth's default the start is 0.35 ⇒ the locked
    ///   0.35→0.95 envelope.</item>
    /// </list></summary>
    private void ResolveMaskParams(SenseState state)
    {
        int tier = Math.Clamp(state.VimsTier, 1, 12);
        var (tR, tG, tB, tableAlpha) = VimsLevels[tier - 1];

        // Selector→renderer DEPTH override (Alpha-2): the active AdaptiveFit set's curve
        // overrides the tier alpha when set (>= 0); hue stays tier-keyed. -1 keeps the
        // static tier alpha. set0 resolves to exactly tableAlpha (default set unchanged).
        double tierAlpha = state.MaskPeakAlphaOverride >= 0.0
            ? state.MaskPeakAlphaOverride
            : tableAlpha;

        // Seed the cross-fade on first tick so we don't ease in from black at startup.
        if (!_displayedSeeded)
        {
            _displayedR = tR; _displayedG = tG; _displayedB = tB; _displayedAlpha = tierAlpha;
            _displayedSeeded = true;
        }
        else
        {
            _displayedR     += (tR        - _displayedR)     * MaskSmoothingFactor;
            _displayedG     += (tG        - _displayedG)     * MaskSmoothingFactor;
            _displayedB     += (tB        - _displayedB)     * MaskSmoothingFactor;
            _displayedAlpha += (tierAlpha - _displayedAlpha) * MaskSmoothingFactor;
        }

        byte r = (byte)Math.Round(Math.Clamp(_displayedR, 0, 255));
        byte g = (byte)Math.Round(Math.Clamp(_displayedG, 0, 255));
        byte b = (byte)Math.Round(Math.Clamp(_displayedB, 0, 255));

        // D-2: MaskDepth → bounded effective peak alpha. The locked guarantees are
        // enforced IN the field (see ResolveEffectivePeakAlpha): floor 0.20 when the
        // mask is on, ceiling = the (smoothed) table/override alpha, identity at default.
        double effectivePeak = ResolveEffectivePeakAlpha(
            _displayedAlpha, state.Shader.MaskDepth, state.MaskActive);

        // D-2: FeatherWidth → ramp start (band-width slide), end pinned at 0.95.
        double featherStart = ResolveFeatherStart(state.Shader.FeatherWidth);

        int colorKey = (r << 16) | (g << 8) | b;
        if (colorKey != _lastColorKey
            || Math.Abs(effectivePeak - _lastPeak) > FieldEpsilon
            || Math.Abs(featherStart - _lastFeatherStart) > FieldEpsilon
            || state.MaskActive != _maskActive)
        {
            _maskR = r; _maskG = g; _maskB = b;
            _maskPeakAlpha = effectivePeak;
            _featherStart  = featherStart;
            _maskActive    = state.MaskActive;
            _lastColorKey = colorKey;
            _lastPeak = effectivePeak;
            _lastFeatherStart = featherStart;
            MarkFieldDirty();
        }
    }

    /// <summary>Grid brightness target — the SAME tier/pressure-driven signal that drove
    /// GridCanvas.Opacity before A2. EMA-smoothed; GridCeiling stays the brightness
    /// ceiling driver (and the gv=0 → ceiling 0 "/grid off" behaviour). The world-locked
    /// grid lines are revealed in the dimmed PERIPHERY (sharing the FOV mask's window); the
    /// centre stays clean — this brightness scales the faint line alpha there.</summary>
    private void ResolveGridBrightness(SenseState state)
    {
        double intensity = ComputeAdaptiveIntensity(state);
        _smoothedIntensity = 0.85 * _smoothedIntensity + 0.15 * intensity;

        int    tier         = Math.Clamp(state.VimsTier, 1, 12);
        double tierFraction = (tier - 1) / 11.0;                 // 0 at L1, 1 at L12
        double gv           = Math.Clamp(state.Shader.GridVisibility, 0.0, 1.0);
        double ceiling      = GridCeiling(gv);
        double target       = Math.Clamp(tierFraction * ceiling * (0.55 + (0.45 * _smoothedIntensity)),
                                         0.0, ceiling);

        // Ease toward target on the SAME tick cadence (replaces the 280 ms DoubleAnimation
        // + GridCanvas.Opacity EMA — folded onto one clock per A1). MaskSmoothingFactor
        // gives ~250 ms convergence, matching the prior perceived grid fade timing.
        _gridBrightness += (target - _gridBrightness) * MaskSmoothingFactor;

        if (Math.Abs(_gridBrightness - _lastGrid) > FieldEpsilon)
        {
            _lastGrid = _gridBrightness;
            MarkFieldDirty();
        }
    }

    private static double ComputeAdaptiveIntensity(SenseState state)
    {
        double flowTerm     = Math.Clamp(state.OpticalFlowIndex / 120.0, 0.0, 1.0);
        double pressureTerm = Math.Clamp(state.Pressure / SenseDefaults.TriggerPressure, 0.0, 1.0);
        return Math.Clamp((flowTerm * 0.35) + (pressureTerm * 0.85), 0.0, 1.0);
    }

    /// <summary>
    /// Maps grid-visibility knob → max grid opacity (the per-frame ceiling). Extracted as
    /// a pure static so the curve is unit-testable. gv=0 ⇒ ceiling 0 exactly ("/grid off"
    /// is truly off); for gv>0 the curve is 0.40 + 0.80·gv (default gv=0.75 ⇒ ceiling 1.00).
    /// PRESERVED byte-for-byte (GridCeilingTests).
    /// </summary>
    internal static double GridCeiling(double gv)
        => gv > 0.0 ? Math.Min(1.0, 0.40 + (0.80 * gv)) : 0.0;

    // ── Head pose → field center + radius (one clock, one boundary) ──────────

    /// <summary>Shared head-pose → field-center mutation. Both the state-driven path
    /// (ApplyState, engine tick) and the pump path (UpdateHeadGazeCenter, ~165 Hz) call
    /// this. Active=false snaps to the v0.9 baseline (center 0.5/0.5, radius BrushRadius).
    ///
    /// <para>Beta A1 (one-clock): center, radius and the grid hole all derive from the
    /// SAME (cx,cy) in the SAME frame — the pose was smoothed ONCE upstream by the pump's
    /// One-Euro filter, so radius is a pure function of the smoothed center (no separate
    /// asymmetric radius clock). The corner-normalized radial field (ResolveFieldAlpha)
    /// uses this center directly; there is no animated GLOBAL radius — corner coverage is
    /// guaranteed per-pixel (kills the scalar-radius "breathing").</para></summary>
    private void ApplyHeadPose(double headGazeX, double headGazeY, bool active)
    {
        // M5.1-B: mode-based clamp. Balanced (default) restricts travel to [0.10, 0.90]
        // so the periphery is always partially dimmed; FullFollow (opt-in) allows [0, 1].
        double low, high;
        if (Configuration.AppConfig.HeadTrackingFollowMode
            == Configuration.HeadTrackingFollowModeKind.FullFollow)
        {
            low = 0.0; high = 1.0;
        }
        else
        {
            low = 0.10; high = 0.90;
        }
        double cx = active ? Math.Clamp(headGazeX, low, high) : 0.5;
        double cy = active ? Math.Clamp(headGazeY, low, high) : 0.5;

        // Dynamic radius scales to the farthest screen corner so even at full-corner
        // center the opposite corner sits inside the falloff. v0.9 baseline (active=false)
        // keeps 0.85. ONE-CLOCK: (cx,cy) is the already-smoothed pose, so radius inherits
        // the center's exact time-constant (no separate envelope, dtMs unused).
        double radius;
        if (active)
        {
            double farX = Math.Max(cx, 1.0 - cx);
            double farY = Math.Max(cy, 1.0 - cy);
            double farthestCorner = Math.Sqrt(farX * farX + farY * farY);
            radius = Math.Max(BrushRadius, farthestCorner * BrushRadiusCornerMargin);
        }
        else
        {
            radius = BrushRadius;
        }

        if (Math.Abs(cx - _fieldCx) > FieldEpsilon
            || Math.Abs(cy - _fieldCy) > FieldEpsilon
            || Math.Abs(radius - _fieldRadius) > FieldEpsilon)
        {
            _fieldCx = cx; _fieldCy = cy; _fieldRadius = radius;
            MarkFieldDirty();
        }
    }

    // ── Field math (PURE — unit-testable without WPF) ────────────────────────

    /// <summary>Corner-normalized radial coordinate: dist(px, center) divided by the
    /// distance from the center to its FARTHEST corner. 0 at the center, 1 at the
    /// farthest corner — corner coverage WITHOUT an animated global radius (the breathing
    /// killer). px/py and cx/cy are normalized [0,1]; aspect corrects to screen pixels so
    /// the falloff is radially symmetric on screen, not in UV.</summary>
    internal static double CornerNormR(double px, double py, double cx, double cy, double aspect)
    {
        // Work in aspect-corrected space (x scaled by aspect = w/h) so a circle on screen
        // stays a circle. distanceToFarthestCorner from center within [0,1]² (aspect-scaled).
        double dx = (px - cx) * aspect;
        double dy = (py - cy);
        double dist = Math.Sqrt(dx * dx + dy * dy);

        double farX = Math.Max(cx, 1.0 - cx) * aspect;
        double farY = Math.Max(cy, 1.0 - cy);
        double farDist = Math.Sqrt(farX * farX + farY * farY);
        if (farDist <= 1e-9) return 0.0;
        return dist / farDist;
    }

    /// <summary>Continuous smoothstep falloff, 0 inside <paramref name="featherStart"/>,
    /// ramping to <paramref name="peakAlpha"/> by <paramref name="featherEnd"/> (0.95),
    /// then held. Preserves the prior 0.35→0.95 envelope SHAPE; the band START is the D-2
    /// FeatherWidth wiring. Output is the field alpha at corner-normalized coordinate r.
    /// At the default ramp (0.35→0.95) and a tier peak this reproduces the prior
    /// BuildLevelFeather output at the same r.</summary>
    internal static double ResolveFieldAlpha(double r, double peakAlpha, double featherStart, double featherEnd)
    {
        if (r <= featherStart) return 0.0;
        if (r >= featherEnd)   return peakAlpha;
        double t = (r - featherStart) / (featherEnd - featherStart);
        double s = t * t * (3.0 - (2.0 * t)); // smoothstep s(t)=t²(3-2t)
        return peakAlpha * s;
    }

    /// <summary>Resolve the faint grid LINE alpha at a pixel — the periphery-only,
    /// world-locked grid reveal (UVHS 2026-06-11 layout). Returns 0 unless the pixel is
    /// ON a world-locked grid line AND the shared peripheral window is open past
    /// <paramref name="gridThreshold"/> (so the clean centre, where <paramref name="window"/>
    /// ≈ 0, NEVER shows grid). On a periphery line pixel the alpha is the faint line ceiling
    /// (<paramref name="gridLineAlpha"/> = brightness × OverlayGridLineMaxAlpha) scaled by the
    /// SAME radial window the FOV mask uses (the inverse-feather reveal — clean centre,
    /// faint-in-periphery), gamma-shaped before encode. This reproduces the reference
    /// <c>_gridOpacityMaskBrush</c> behaviour inside the single pass. Pure + internal for
    /// unit coverage.
    ///
    /// <para><paramref name="window"/> is the shared falloff at peak 1.0 (0 in the clean
    /// centre, 1.0 in the far periphery) — identical geometry to the FOV mask, so the grid
    /// and mask windows share center/radius/feather with no seam. The grid line POSITIONS
    /// (the <paramref name="onLine"/> flag) are world-locked upstream via mod(px, cell) and
    /// are independent of the window — only the visibility follows the head.</para></summary>
    internal static double ResolveGridLineAlpha(
        double window, double gridLineAlpha, bool onLine, double gridThreshold, double gamma)
    {
        if (!onLine || gridLineAlpha <= 0.0 || window <= gridThreshold) return 0.0;
        return GammaShape(Math.Clamp(gridLineAlpha * window, 0.0, 1.0), gamma);
    }

    /// <summary>D-2: map MaskDepth [0,1] → EFFECTIVE peak alpha. Bounded multiplier on the
    /// tier ceiling, then the locked guarantees:
    /// <list type="bullet">
    ///   <item>identity at MaskDepth's default (multiplier = 1.0 ⇒ exactly the ceiling);</item>
    ///   <item>never above the tier ceiling (the VimsLevels table value / override);</item>
    ///   <item>never below the 0.20 floor when the mask is commanded on.</item>
    /// </list>
    /// When the mask is OFF the floor does NOT apply (a commanded-off mask can resolve to
    /// 0, e.g. L1's 0.00). Pure + internal for unit coverage.</summary>
    internal static double ResolveEffectivePeakAlpha(double tierCeiling, double maskDepth, bool maskActive)
    {
        double mult = 1.0 + (maskDepth - ShaderParams.DefaultMaskDepth)
                            * SenseDefaults.OverlayMaskDepthMultiplierSpan;
        double effective = tierCeiling * mult;

        // Ceiling: never above the tier table/override value.
        if (effective > tierCeiling) effective = tierCeiling;

        // Floor: never below 0.20 when commanded on — BUT only up to the ceiling itself
        // (a tier whose ceiling is below 0.20, e.g. L1 0.00 or L2 0.14, is never pushed
        // ABOVE its own table value by the floor — the floor cannot raise depth past the
        // tier the engine commanded). So the floor is min(0.20, ceiling).
        if (maskActive)
        {
            double floor = Math.Min(SenseDefaults.OverlayMaskEffectiveAlphaFloor, tierCeiling);
            if (effective < floor) effective = floor;
        }

        if (effective < 0.0) effective = 0.0;
        if (effective > 1.0) effective = 1.0;
        return effective;
    }

    /// <summary>D-2: map FeatherWidth [0,1] → smoothstep ramp START (band-width slide).
    /// The ramp END is pinned at 0.95 (corner hold). A WIDER feather pulls the start
    /// inward (longer, softer band); a NARROWER feather pushes it outward (tighter band).
    /// At FeatherWidth's default (0.50) the start resolves to exactly 0.35 ⇒ the locked
    /// 0.35→0.95 envelope. Clamped so the start never crosses 0 or the end. Pure +
    /// internal for unit coverage.</summary>
    internal static double ResolveFeatherStart(double featherWidth)
    {
        const double baseStart = 0.35;
        const double end       = 0.95;
        // featherWidth > 0.5 → wider band → start moves toward 0; < 0.5 → start moves toward end.
        double start = baseStart - (featherWidth - ShaderParams.DefaultFeatherWidth)
                                  * 2.0 * SenseDefaults.OverlayFeatherStartSpan;
        // Safe clamps: keep a non-degenerate band strictly inside (0, end).
        double lo = 0.05;
        double hi = end - 0.05;
        if (start < lo) start = lo;
        if (start > hi) start = hi;
        return start;
    }

    /// <summary>Gamma-shape the falloff parameter before 8-bit encode (banding mitigation,
    /// best-effort, no dithering). Shapes the [0,1] alpha fraction in gamma-space so the
    /// 8-bit codes concentrate where the eye is most sensitive in the near-black band.
    /// gamma=1.0 ⇒ linear (no shaping). Pure + internal for unit coverage.</summary>
    internal static double GammaShape(double a01, double gamma)
    {
        if (a01 <= 0.0) return 0.0;
        if (a01 >= 1.0) return 1.0;
        return Math.Pow(a01, gamma);
    }

    // ── WriteableBitmap field renderer ───────────────────────────────────────

    /// <summary>(Re)allocate the reduced-res field bitmap when the screen size changes.
    /// The field is computed at 1/<see cref="SenseDefaults.OverlayFieldDownscale"/> per
    /// axis and the one Image bilinear-upscales it (Stretch=Fill). Idempotent.</summary>
    private void EnsureFieldBitmap()
    {
        double sw = ActualWidth, sh = ActualHeight;
        if (sw <= 0 || sh <= 0)
        {
            // Before layout: fall back to the primary screen so a pre-layout ApplyState
            // (engine tick can beat Loaded) still produces a valid field.
            sw = SystemParameters.PrimaryScreenWidth;
            sh = SystemParameters.PrimaryScreenHeight;
            if (sw <= 0 || sh <= 0) return;
        }
        if (Math.Abs(sw - _lastScreenW) < 0.5 && Math.Abs(sh - _lastScreenH) < 0.5 && _fieldBmp is not null)
            return;

        _lastScreenW = sw; _lastScreenH = sh;
        int ds = Math.Max(1, SenseDefaults.OverlayFieldDownscale);
        _fieldW = Math.Max(1, (int)Math.Ceiling(sw / ds));
        _fieldH = Math.Max(1, (int)Math.Ceiling(sh / ds));

        _fieldBmp = new WriteableBitmap(_fieldW, _fieldH, 96, 96, PixelFormats.Pbgra32, null);
        _fieldBuf = new byte[_fieldW * _fieldH * 4];
        FieldImage.Source = _fieldBmp;
        MarkFieldDirty();
    }

    private void MarkFieldDirty() => _fieldDirty = true;

    /// <summary>Recompute + present the field ONLY when an input moved beyond epsilon (a
    /// static screen costs ~zero). Times the recompute for the live perf gate and flushes
    /// rolling avg/p99 stats to wgc_diag once a minute while the overlay is active.</summary>
    private void RenderFieldIfDirty()
    {
        if (_fieldBmp is null || _fieldBuf is null) return;

        bool inputsMoved = _fieldDirty
            || double.IsNaN(_lastCx)
            || Math.Abs(_fieldCx - _lastCx) > FieldEpsilon
            || Math.Abs(_fieldCy - _lastCy) > FieldEpsilon
            || Math.Abs(_fieldRadius - _lastRadius) > FieldEpsilon
            || _lastFieldActive != _maskActive;

        if (!inputsMoved) return;

        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        ComputeField();
        _fieldBmp.WritePixels(
            new Int32Rect(0, 0, _fieldW, _fieldH), _fieldBuf, _fieldW * 4, 0);

        long endTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double ms = (endTicks - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        RecordRecompute(ms);

        _lastCx = _fieldCx; _lastCy = _fieldCy; _lastRadius = _fieldRadius;
        _lastFieldActive = _maskActive;
        _fieldDirty = false;
    }

    /// <summary>Fill <see cref="_fieldBuf"/> with the single per-pixel field. One output
    /// pixel, ONE alpha — the single-surface property. Premultiplied BGRA (Pbgra32).
    ///
    /// <para>SPATIAL LAYOUT (UVHS 2026-06-11 correction — restores the 3762a42 presentation):
    /// the faint world-locked grid appears in the PERIPHERY, sharing the FOV mask's exact
    /// radial window; the centre is CLEAN (no grid, no dim). This is the INVERSE of the
    /// earlier (inverted) A2 read where the grid filled the clear centre. The grid's
    /// VISIBILITY follows the mask falloff (the reference's <c>_gridOpacityMaskBrush</c> was
    /// the inverse of the FOV mask, sharing identical center/radius/feather geometry) — so a
    /// periphery grid-line pixel renders the white line BLENDED OVER the mask dim at that
    /// pixel (one alpha), not the grid INSTEAD of the mask. The grid LINE POSITIONS stay
    /// world-locked: <c>mod(px, cell)</c> in screen/field space, never offset by the mask
    /// center — only the visibility window follows the head, exactly as at 3762a42 (the hole
    /// moves, the lines stay pinned to pixel origin).</para></summary>
    private void ComputeField()
    {
        int w = _fieldW, h = _fieldH;
        double aspect = (double)w / h;
        double cx = _fieldCx, cy = _fieldCy;
        double peak = _maskPeakAlpha;
        double featherStart = _featherStart;
        const double featherEnd = 0.95;
        double gamma = SenseDefaults.OverlayFieldGamma;
        var buf = _fieldBuf!;

        byte mr = _maskR, mg = _maskG, mb = _maskB;

        // World-locked grid: cell size in REDUCED-RES field pixels. The grid never moves
        // with the center (mod(px, cell) in field space). Scale GridSizePx by the downscale
        // factor so the on-screen spacing matches the legacy grid after upscale.
        int ds = Math.Max(1, SenseDefaults.OverlayFieldDownscale);
        int gridCell = Math.Max(2, (int)Math.Round(SenseDefaults.GridSizePx / (double)ds));
        double gridLineAlpha = _gridBrightness * SenseDefaults.OverlayGridLineMaxAlpha;
        // The grid-visibility window mirrors the reference _gridOpacityMaskBrush: its feather
        // is the INVERSE of the clear centre — grid invisible where the mask is clear (centre),
        // grid revealed where the mask ramps in (periphery). We reuse the SAME smoothstep
        // falloff geometry (ResolveFieldAlpha at peak 1.0) so the grid window shares the FOV
        // mask's center/radius/feather byte-for-byte (no seam, no second geometry). The grid
        // only shows where this window is non-trivially open (> threshold) — the genuinely
        // peripheral region — so the centre stays CLEAN.
        double gridThreshold = SenseDefaults.OverlayFieldGridThreshold;

        for (int y = 0; y < h; y++)
        {
            double py = (y + 0.5) / h;
            // World-locked grid line test for this row (screen-space mod, NOT center-relative).
            bool yOnGrid = (y % gridCell) == 0;
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                double px = (x + 0.5) / w;
                double r = CornerNormR(px, py, cx, cy, aspect);

                // The shared radial window (peak 1.0): 0 in the clean centre, ramping to 1.0
                // in the periphery. The FOV dim is this window scaled by the tier peak; the
                // grid visibility is this same window (the inverse-feather reveal). Both share
                // center/radius/feather so they align with no seam, exactly as at 3762a42.
                double window = ResolveFieldAlpha(r, 1.0, featherStart, featherEnd);
                double maskA  = window * peak;

                // Mask dim at this pixel (gamma-shaped before 8-bit encode; banding mitigation).
                double shapedMask = GammaShape(Math.Clamp(maskA, 0.0, 1.0), gamma);

                // World-locked grid line (positions never offset by center). Its on-screen
                // strength is the faint line alpha × the SAME peripheral window — so the grid
                // is CLEAN in the centre (window≈0) and faint in the dimmed periphery.
                bool onLine = yOnGrid || (x % gridCell) == 0;
                double gridA = ResolveGridLineAlpha(window, gridLineAlpha, onLine, gridThreshold, gamma);

                // ── Composite within the ONE pass: white grid line OVER the mask dim ──────
                // src-over of an opaque-white line (premultiplied (gridA,gridA,gridA,gridA))
                // onto the mask backdrop (premultiplied colour, alpha shapedMask). One output
                // pixel, one alpha — the single-surface property is preserved. In the clean
                // centre both terms are 0 ⇒ fully transparent (centre clean).
                double bgA = shapedMask;
                byte bgR = (byte)((mr * (int)Math.Round(bgA * 255.0) + 127) / 255);
                byte bgG = (byte)((mg * (int)Math.Round(bgA * 255.0) + 127) / 255);
                byte bgB = (byte)((mb * (int)Math.Round(bgA * 255.0) + 127) / 255);

                // Premultiplied src-over: out = src + dst·(1 − srcA). src is white at gridA
                // (premultiplied channels == gridA).
                double inv  = 1.0 - gridA;
                double outA = gridA + bgA * inv;
                double outR = gridA + (bgR / 255.0) * inv;
                double outG = gridA + (bgG / 255.0) * inv;
                double outB = gridA + (bgB / 255.0) * inv;

                int i = row + x * 4;
                buf[i + 0] = (byte)Math.Round(Math.Clamp(outB, 0.0, 1.0) * 255.0); // B
                buf[i + 1] = (byte)Math.Round(Math.Clamp(outG, 0.0, 1.0) * 255.0); // G
                buf[i + 2] = (byte)Math.Round(Math.Clamp(outR, 0.0, 1.0) * 255.0); // R
                buf[i + 3] = (byte)Math.Round(Math.Clamp(outA, 0.0, 1.0) * 255.0); // A
            }
        }
    }

    private void RecordRecompute(double ms)
    {
        _recompCount++;
        _recompSumMs += ms;
        if (ms > _recompMaxMs) _recompMaxMs = ms;

        // Flush rolling avg / p99-ish (worst-case) to wgc_diag once a minute while active.
        var now = DateTime.UtcNow;
        if (_lastPerfFlushUtc == DateTime.MinValue) _lastPerfFlushUtc = now;
        if ((now - _lastPerfFlushUtc).TotalSeconds >= SenseDefaults.OverlayFieldPerfLogIntervalSeconds
            && _recompCount > 0)
        {
            _recompAvgMs = _recompSumMs / _recompCount;
            _recompP99Ms = _recompMaxMs; // cheap high-water proxy (recompute count/min is small)

            try
            {
                SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                    $"[A2-field] recompute/min n={_recompCount} avg={_recompAvgMs:F3}ms " +
                    $"max={_recompP99Ms:F3}ms field={_fieldW}x{_fieldH} active={_maskActive}");
            }
            catch { /* perf log is best-effort; never break the render */ }

            _recompCount = 0; _recompSumMs = 0; _recompMaxMs = 0;
            _lastPerfFlushUtc = now;
        }
    }

    // ── Test seams ───────────────────────────────────────────────────────────

    /// <summary>Beta A2 test seam: the resolved field center X (after the head-pose clamp).
    /// Replaces the removed RadialGradientBrush.Center.X seam (no brush in the single-pass
    /// field). Internal + InternalsVisibleTo("SenseEase.Tests"); no public-surface widening.</summary>
    internal double FieldCenterXForTests => _fieldCx;
    internal double FieldCenterYForTests => _fieldCy;

    /// <summary>Beta A2 test seam: the resolved corner-reach field radius (the head-coupled
    /// dynamic radius). Replaces RadialGradientBrush.RadiusX/Y.</summary>
    internal double FieldRadiusForTests => _fieldRadius;

    /// <summary>Beta A2 test seam: the resolved EFFECTIVE peak mask alpha (after MaskDepth +
    /// floor/ceiling). Lets TEST-04 assert MaskDepth wiring + the 0.20-floor clamp + table
    /// identity at the default.</summary>
    internal double EffectivePeakAlphaForTests => _maskPeakAlpha;

    /// <summary>Beta A2 test seam: the resolved feather-band start (FeatherWidth wiring).</summary>
    internal double FeatherStartForTests => _featherStart;

    /// <summary>Beta A2 test seam: snapshot the rendered reduced-res BGRA buffer so TEST-04
    /// can assert two MaskDepth/FeatherWidth values produce DIFFERENT fields. Returns a copy
    /// (the live buffer is reused each frame). Null before the first render.</summary>
    internal byte[]? FieldBufferSnapshotForTests => _fieldBuf is null ? null : (byte[])_fieldBuf.Clone();

    internal int FieldWidthForTests  => _fieldW;
    internal int FieldHeightForTests => _fieldH;

    /// <summary>M8 head-tracking sprint: host-wired lookup of "does the user have a
    /// vestibular diagnosis on file?". Read-only consumer of MainViewModel
    /// .HasVestibularDiagnosis. Retained as the overlay's read-only vestibular-status seam
    /// (the escape hatch it once guarded was removed in D-10; the mask is never weakened).</summary>
    public Func<bool>? HasVestibularDiagnosis { get; set; }
}
