using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SenseEase.App.Interop;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;

namespace SenseEase.App.Views;

/// <summary>
/// Transparent, topmost, click-through overlay. Beta A2 HYBRID (UVHS round 2, 2026-06-11):
/// a TWO-surface composition that splits content by spatial frequency.
///
/// <para><b>Low-frequency content → the reduced-res field bitmap.</b> The peripheral FOV
/// dimming mask AND the legacy cell-fill tint are folded into ONE per-pixel field computed
/// into a single premultiplied-BGRA <see cref="WriteableBitmap"/> at
/// 1/<see cref="SenseDefaults.OverlayFieldDownscale"/> resolution and bilinear-upscaled
/// through the <c>FieldImage</c>. A low-frequency gradient + flat tint survives the
/// quarter-res downscale with no visible loss (the field carries no high-frequency
/// content anymore).</para>
///
/// <para><b>High-frequency content → full-resolution WPF vector geometry.</b> The
/// screen-locked Chang-Hain anchor grid is restored to the PROVEN 3762a42 implementation:
/// real <c>Line</c> shapes drawn into <c>GridCanvas</c> at DPI/resolution-scaled spacing,
/// windowed by an inverse-feather <c>RadialGradientBrush</c> OpacityMask on the SAME
/// one-clock pose as the field. A 1.2 px world-locked hairline CANNOT survive rasterization
/// into a quarter-res field texel (each line would become a fat bilinear-blurred band and
/// pop in 4 px-quantized brightness steps) — so the grid stays full-res vector geometry,
/// the resolution-critical layout fix the UVHS round-2 verdict demanded ("grid FORM wrong",
/// "grid JUMPS").</para>
///
/// <para>The A1 One-Euro-smoothed (cx,cy) handed in by the MainWindow pump remains the
/// single pose input. ComputeField derives the field center/radius from it; the SAME value
/// is mirrored onto the grid OpacityMask brush in the SAME frame (the M5.1-A mirror) so the
/// hole rides the head while the LINES stay world-locked at pixel origin — one clock, no
/// divergent geometry. MaskDepth and FeatherWidth remain REAL inputs (D-2): MaskDepth bounds
/// the peak alpha (clamped to the ≥0.20 floor / VimsLevels ceiling), FeatherWidth slides the
/// feather-band start.</para>
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
    // L8 up so the mask reads as deliberate dimming on bright bg). PRESERVED byte-for-byte.
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

    // ── Cell-fill tint (folded into the field — UVHS round 2, 2026-06-11) ────
    // The reference (3762a42) painted a full-screen near-black #08080A@120 rectangle
    // (_cellFillRect) UNDER the SAME inverse-feather window the grid used, giving the
    // periphery a slightly deeper-than-mask tone. The plan named the SEPARATE rect as a
    // perceptual-scission source, so it is NOT restored as its own stratum — instead its
    // contribution is folded into the per-pixel field (composited UNDER the tier mask dim,
    // windowed by the SAME shared radial falloff). The periphery TONE matches 3762a42 with
    // no extra stratum. Premultiplied source values are precomputed once (frozen tint).
    private const byte   CellFillR = 8, CellFillG = 8, CellFillB = 10;
    private const double CellFillAlpha = 120.0 / 255.0;

    // ── A2 single field state (one moving boundary, one alpha per pixel) ─────
    // The A1 One-Euro-smoothed pose is the SINGLE input; center + radius derive from it in
    // the SAME frame on the SAME time-constant. The grid (full-res vector, separate surface)
    // is world-locked and mirrors only this center/radius onto its OpacityMask window.
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
    private double _lastPeak = double.NaN, _lastFeatherStart = double.NaN;
    private int    _lastColorKey = -1;
    private bool   _lastFieldActive;
    private const double FieldEpsilon = 0.0005;

    // Cell-fill tint strength: the SAME ApplyGridIntensity target that drives
    // GridCanvas.Opacity (tierFraction × GridCeiling × intensity). 0 at L1 resting —
    // the tint follows the 3762a42 modulation instead of painting a permanent vignette.
    private double _cellStrength;
    private double _lastCellStrength = double.NaN;

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

    // ── Grid brightness (drives GridCanvas.Opacity — the proven 3762a42 pipeline) ─
    // EMA-smoothed pressure/tier signal → per-frame GridCanvas.Opacity ceiling. NOT a
    // field input anymore (the grid is a separate full-res vector surface).
    private double _smoothedIntensity;

    // ── Grid: cached frozen white line stroke (3762a42) ──────────────────────
    /// <summary>Frozen white grid-line stroke (A=130/255). Allocated once at class load.</summary>
    private static readonly SolidColorBrush GridLineBrush = CreateGridLineBrush();
    private static SolidColorBrush CreateGridLineBrush()
    {
        var b = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255));
        b.Freeze();
        return b;
    }

    // Reused grid line objects (no clear+rebuild allocation each DrawGrid).
    private readonly List<System.Windows.Shapes.Line> _vLines = new();
    private readonly List<System.Windows.Shapes.Line> _hLines = new();
    private double _lastDrawnW    = -1;
    private double _lastDrawnH    = -1;
    private int    _lastDrawnStep = -1;

    // ── Grid: inverse-feather OpacityMask brush (the visibility window, 3762a42) ─
    /// <summary>Radial inverse-feather used as GridCanvas.OpacityMask: clear centre,
    /// revealed periphery (BuildLevelFeather(255, white) — the 0.35→0.95 window). Per-instance
    /// + UNFROZEN so its Center/RadiusX/Y can be mirrored from the one-clock pose alongside the
    /// field center each frame (M5.1-A). Same falloff geometry as the field's ResolveFieldAlpha
    /// so the grid window and the FOV mask align with no seam.</summary>
    private RadialGradientBrush _gridOpacityMaskBrush = BuildGridOpacityMaskBrush();
    private static RadialGradientBrush BuildGridOpacityMaskBrush()
    {
        return new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center         = new Point(0.5, 0.5),
            RadiusX        = BrushRadius,
            RadiusY        = BrushRadius,
            GradientStops  = BuildLevelFeather(255, Colors.White),
        };
    }

    // ── Grid: single reusable opacity animation (3762a42 FIX #14) ────────────
    /// <summary>Reused 280 ms animation object — mutate .To per tick instead of allocating a
    /// new one. Duration constant; From left unset so WPF animates from the current value.</summary>
    private readonly DoubleAnimation _gridOpacityAnim = new()
    {
        Duration = new Duration(TimeSpan.FromMilliseconds(280)),
    };

    private bool _lastMaskActive;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded            += OnLoaded;
        SizeChanged       += (_, _) =>
        {
            EnsureFieldBitmap(); MarkFieldDirty(); RenderFieldIfDirty();
            DrawGrid();
        };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        EnsureFieldBitmap(); MarkFieldDirty(); RenderFieldIfDirty();
        DrawGrid();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ClickThrough.Enable(this);

        // Fade the overlay in so it doesn't pop harshly.
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(380)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    // ── IOverlayRenderer ─────────────────────────────────────────────────────

    /// <summary>Update the overlay from the current SenseState.</summary>
    public void ApplyState(SenseState state)
    {
        EnsureFieldBitmap();

        bool maskChanged = state.MaskActive != _lastMaskActive;
        _lastMaskActive  = state.MaskActive;
        if (maskChanged || GridCanvas.Children.Count == 0)
            DrawGrid();

        ResolveMaskParams(state);
        ApplyGridIntensity(state);     // drives GridCanvas.Opacity (full-res vector grid)
        EnsureGridOpacityMask();
        // Head pose comes from the SAME state on the engine-tick path; the ~165 Hz pump
        // path uses UpdateHeadGazeCenter. Both funnel into ApplyHeadPose.
        ApplyHeadPose(state.HeadGazeX, state.HeadGazeY, state.HeadTrackingActive);
        RenderFieldIfDirty();
    }

    /// <summary>High-frequency entry point for the head-pose pump (~165 Hz
    /// <c>CompositionTarget.Rendering</c>, MainWindow). Bypasses the full mask/grid
    /// parameter recomputation and updates ONLY the pose-derived center + radius (the field
    /// AND the grid OpacityMask window, in the same frame), then re-renders the field if
    /// anything moved beyond epsilon.
    ///
    /// <para>Beta A1 (one-clock): the pose handed in is ALREADY the single One-Euro-
    /// smoothed value (the pump owns the one filter); this method does NOT re-smooth.
    /// <paramref name="dtMs"/> is retained for signature compatibility but no longer
    /// drives a separate radius envelope — radius derives directly from the smoothed
    /// center. Must run on the UI thread (mutates a WriteableBitmap + DependencyObjects).</para></summary>
    public void UpdateHeadGazeCenter(double headGazeX, double headGazeY, bool active, double dtMs = 0.0)
    {
        if (_fieldBmp is null) return;
        ApplyHeadPose(headGazeX, headGazeY, active);
        RenderFieldIfDirty();
    }

    // ── Mask parameter resolution (engine-tick cadence) ──────────────────────

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

    // ── Grid brightness → GridCanvas.Opacity (the proven 3762a42 pipeline) ───

    /// <summary>Drive the full-res vector grid's <c>GridCanvas.Opacity</c> from the SAME
    /// tier/pressure signal as the legacy renderer (issue #4 fix: pressure-driven, not
    /// FOV-driven). EMA(α=0.85)-smoothed intensity × GridCeiling(gv) ceiling × tier fraction,
    /// eased over 280 ms via the single reusable DoubleAnimation. <c>GridCeiling(gv=0)=0</c>
    /// keeps "/grid off" truly off. PRESERVED byte-for-byte from 3762a42 (GridCeilingTests).</summary>
    private void ApplyGridIntensity(SenseState state)
    {
        // EMA smoothing (α=0.85, τ≈1.7 s at 300 ms tick) — eliminates per-tick jumps from
        // WGC pixel-diff noise without losing responsiveness to sustained pressure.
        double intensity = ComputeAdaptiveIntensity(state);
        _smoothedIntensity = 0.85 * _smoothedIntensity + 0.15 * intensity;

        int    tier         = Math.Clamp(state.VimsTier, 1, 12);
        double tierFraction = (tier - 1) / 11.0;                 // 0 at L1, 1 at L12
        double gv           = Math.Clamp(state.Shader.GridVisibility, 0.0, 1.0);
        double ceiling      = GridCeiling(gv);
        double target       = Math.Clamp(tierFraction * ceiling * (0.55 + (0.45 * _smoothedIntensity)),
                                         0.0, ceiling);

        // Reuse the single pre-allocated DoubleAnimation; only mutate .To. From is left
        // unset → WPF animates from the current animated value (zero per-tick allocation).
        _gridOpacityAnim.To = target;
        GridCanvas.BeginAnimation(OpacityProperty, _gridOpacityAnim, HandoffBehavior.SnapshotAndReplace);

        // Mirror the same target into the field's cell-fill tint strength (the 3762a42
        // modulation: the tint rode GridCanvas.Opacity). Field re-renders on real change.
        if (double.IsNaN(_lastCellStrength) || Math.Abs(target - _lastCellStrength) > FieldEpsilon)
        {
            _cellStrength = target;
            _lastCellStrength = target;
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

    // ── Grid geometry (full-res vector lines — restored verbatim from 3762a42) ─

    /// <summary>
    /// Draw / refresh the CH stationary anchor grid: 40 px (DPI/resolution-scaled) spacing,
    /// faint white lines. First call builds all child <c>Line</c> objects; subsequent calls
    /// only mutate coordinates when the canvas size or step changes — no clear+rebuild
    /// allocation. A 1.2 px world-locked hairline lives here as full-res vector geometry
    /// (it cannot survive rasterization into the quarter-res field — UVHS round 2).
    /// </summary>
    private void DrawGrid()
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            // Before layout (an engine-tick ApplyState can beat Loaded; an unshown STA test
            // window never lays out): fall back to the primary screen so the grid still builds
            // at the real spacing. Mirrors EnsureFieldBitmap's pre-layout fallback so the field
            // and the grid cover the same extent.
            w = SystemParameters.PrimaryScreenWidth;
            h = SystemParameters.PrimaryScreenHeight;
            if (w <= 0 || h <= 0) return;
        }

        // NE30: attach the radial inverse-feather mask up-front so a DrawGrid triggered by a
        // SizeChanged / DPI-monitor change with no immediately-following ApplyState can never
        // paint full-strength grid lines across the whole screen. The brush defaults to the
        // centred at-rest feather; ApplyHeadPose updates its centre/radius when the head moves.
        EnsureGridOpacityMask();

        // Scale grid spacing and line thickness proportionally from the 1920×1080 baseline so
        // the on-screen spacing matches the approved grid on any resolution/DPI (a wide screen
        // gets a sparser, scaled grid; this is the spacing the UVHS approved).
        double dpiX = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
        double physicalWidth = SystemParameters.PrimaryScreenWidth * dpiX;
        double scale = physicalWidth / 1920.0;
        int step = Math.Max(20, (int)Math.Round(SenseDefaults.GridSizePx * scale));
        double thickness = Math.Max(0.5, 1.2 * scale);

        // Early-out when nothing has changed (no size change, no step change).
        if (w == _lastDrawnW && h == _lastDrawnH && step == _lastDrawnStep)
            return;

        // ── Vertical lines ────────────────────────────────────────────────────
        int vCount = (int)Math.Ceiling(w / step) + 1;

        while (_vLines.Count < vCount)
        {
            var line = new System.Windows.Shapes.Line { Stroke = GridLineBrush };
            _vLines.Add(line);
            GridCanvas.Children.Add(line);
        }
        while (_vLines.Count > vCount)
        {
            int last = _vLines.Count - 1;
            GridCanvas.Children.Remove(_vLines[last]);
            _vLines.RemoveAt(last);
        }
        for (int i = 0; i < vCount; i++)
        {
            double x = i * step;
            var line = _vLines[i];
            line.X1 = x;  line.X2 = x;
            line.Y1 = 0;  line.Y2 = h;
            line.StrokeThickness = thickness;
        }

        // ── Horizontal lines ──────────────────────────────────────────────────
        int hCount = (int)Math.Ceiling(h / step) + 1;

        while (_hLines.Count < hCount)
        {
            var line = new System.Windows.Shapes.Line { Stroke = GridLineBrush };
            _hLines.Add(line);
            GridCanvas.Children.Add(line);
        }
        while (_hLines.Count > hCount)
        {
            int last = _hLines.Count - 1;
            GridCanvas.Children.Remove(_hLines[last]);
            _hLines.RemoveAt(last);
        }
        for (int i = 0; i < hCount; i++)
        {
            double y = i * step;
            var line = _hLines[i];
            line.X1 = 0;  line.X2 = w;
            line.Y1 = y;  line.Y2 = y;
            line.StrokeThickness = thickness;
        }

        _lastDrawnW    = w;
        _lastDrawnH    = h;
        _lastDrawnStep = step;
    }

    /// <summary>Attach the inverse-feather OpacityMask to GridCanvas if external code (or a
    /// fresh window) has not yet. One reference-compare guards against re-allocation. The
    /// grid lines are only revealed where this window is open (the periphery); the centre
    /// hole is clear so the grid never shows over the clean centre.</summary>
    private void EnsureGridOpacityMask()
    {
        if (!ReferenceEquals(GridCanvas.OpacityMask, _gridOpacityMaskBrush))
            GridCanvas.OpacityMask = _gridOpacityMaskBrush;
    }

    /// <summary>
    /// Builds the FIXED feather gradient mandated by the VIMS comfort spec:
    ///   r ∈ [0.00, 0.35]  fully transparent (clear centre)
    ///   r ∈ [0.35, 0.95]  smoothstep ramp 0 → peakAlpha
    ///   r ∈ [0.95, 1.00]  hold at peakAlpha
    /// Smoothstep s(t)=t²(3−2t). Used for the grid OpacityMask (peakAlpha 255, white) — the
    /// inverse-feather window (clear centre, revealed periphery) — sharing the SAME falloff
    /// geometry as the field's ResolveFieldAlpha so the grid and FOV mask align with no seam.
    /// </summary>
    private static GradientStopCollection BuildLevelFeather(byte peakAlpha, Color color)
    {
        var stops  = new GradientStopCollection();
        var clear  = Color.FromArgb(0, color.R, color.G, color.B);
        var solid  = Color.FromArgb(peakAlpha, color.R, color.G, color.B);

        const double rampStart = 0.35;
        const double rampEnd   = 0.95;
        const double rampWidth = rampEnd - rampStart;
        const int    n         = 8;

        stops.Add(new GradientStop(clear, 0.0));
        stops.Add(new GradientStop(clear, rampStart));
        for (int k = 1; k < n; k++)
        {
            double t   = (double)k / n;
            double s   = t * t * (3.0 - (2.0 * t)); // smoothstep
            double pos = rampStart + (rampWidth * t);
            byte a     = (byte)Math.Round(peakAlpha * s);
            stops.Add(new GradientStop(Color.FromArgb(a, color.R, color.G, color.B), pos));
        }
        stops.Add(new GradientStop(solid, rampEnd));
        stops.Add(new GradientStop(solid, 1.0));
        stops.Freeze();
        return stops;
    }

    // ── Head pose → field center + radius + grid window (one clock) ──────────

    /// <summary>Shared head-pose → field-center mutation. Both the state-driven path
    /// (ApplyState, engine tick) and the pump path (UpdateHeadGazeCenter, ~165 Hz) call
    /// this. Active=false snaps to the v0.9 baseline (center 0.5/0.5, radius BrushRadius).
    ///
    /// <para>Beta A1 (one-clock): the field center, the field radius AND the grid OpacityMask
    /// window (Center/GradientOrigin/RadiusX/Y) all derive from the SAME (cx,cy) in the SAME
    /// frame — the pose was smoothed ONCE upstream by the pump's One-Euro filter, so radius is
    /// a pure function of the smoothed center (no separate asymmetric radius clock). The grid
    /// HOLE rides the head (its window mirrors center/radius) while the grid LINES stay
    /// world-locked at pixel origin — the M5.1-A mirror, restored on the one clock.</para></summary>
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

        // M5.1-A mirror (one clock): mirror the SAME center/radius onto the grid OpacityMask
        // brush so the grid's transparency hole tracks the field's clear zone in the SAME
        // frame. Same pose value, same call — the A1 one-clock property holds (no divergent
        // clocks). The grid LINES stay world-locked at pixel origin; only the WINDOW follows.
        var center = new Point(cx, cy);
        if (_gridOpacityMaskBrush.Center != center)
        {
            _gridOpacityMaskBrush.Center         = center;
            _gridOpacityMaskBrush.GradientOrigin = center;
        }
        if (_gridOpacityMaskBrush.RadiusX != radius)
        {
            _gridOpacityMaskBrush.RadiusX = radius;
            _gridOpacityMaskBrush.RadiusY = radius;
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
    /// BuildLevelFeather output at the same r — so the field FOV dim and the grid's
    /// inverse-feather OpacityMask share identical falloff geometry (no seam).</summary>
    internal static double ResolveFieldAlpha(double r, double peakAlpha, double featherStart, double featherEnd)
    {
        if (r <= featherStart) return 0.0;
        if (r >= featherEnd)   return peakAlpha;
        double t = (r - featherStart) / (featherEnd - featherStart);
        double s = t * t * (3.0 - (2.0 * t)); // smoothstep s(t)=t²(3-2t)
        return peakAlpha * s;
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

    /// <summary>Fold the legacy cell-fill tint UNDER the tier mask dim, both windowed by the
    /// SAME shared radial falloff, and return the composited premultiplied output for ONE
    /// field pixel. <paramref name="window"/> is the shared smoothstep falloff at peak 1.0
    /// (0 in the clean centre, 1.0 in the far periphery — identical geometry to the grid's
    /// inverse-feather OpacityMask). The cell-fill backdrop is src-over composited UNDER the
    /// mask dim (reference order: _cellFillRect below, mask above), so the periphery TONE
    /// matches 3762a42 without a separate cell-fill stratum. Returns premultiplied
    /// (B,G,R,A) bytes. Pure + internal for unit coverage.</summary>
    internal static (byte B, byte G, byte R, byte A) ComposeFieldPixel(
        double window, byte maskR, byte maskG, byte maskB, double maskPeak, double gamma,
        double cellStrength)
    {
        // Cell-fill (low layer): near-black tint, windowed by the SAME shared falloff so it
        // shares the FOV mask geometry (clear centre, full periphery). Gamma-shaped before
        // encode (banding mitigation) exactly like the mask term.
        //
        // cellStrength is the SAME pressure/tier-driven signal that drives GridCanvas.Opacity
        // (the ApplyGridIntensity target). At 3762a42 the _cellFillRect lived ON GridCanvas and
        // was therefore modulated by that opacity — at L1 resting (tierFraction = 0) the tint
        // was INVISIBLE. An unmodulated fold would paint a permanent peripheral vignette at
        // rest, violating the A2 hard-stop "resting 0.00 at L1" (clinical lock outranks the
        // plan's single-stratum note). cellStrength = 0 ⇒ tint contributes nothing.
        double cellA = GammaShape(Math.Clamp(CellFillAlpha * cellStrength * window, 0.0, 1.0), gamma);

        // Mask dim (high layer): tier colour at window×peak, gamma-shaped.
        double maskA = GammaShape(Math.Clamp(window * maskPeak, 0.0, 1.0), gamma);

        // src-over: mask OVER cell-fill (reference compositing order — _cellFillRect was the
        // lower stratum). Work in straight-alpha then premultiply on encode.
        //   outA = maskA + cellA·(1−maskA)
        //   outC = (maskC·maskA + cellC·cellA·(1−maskA)) / outA   (straight)
        double inv  = 1.0 - maskA;
        double outA = maskA + cellA * inv;

        double mR = maskR / 255.0, mG = maskG / 255.0, mB = maskB / 255.0;
        double cR = CellFillR / 255.0, cG = CellFillG / 255.0, cB = CellFillB / 255.0;

        // Premultiplied output channels (skip the divide-then-multiply round-trip):
        //   outC·outA = maskC·maskA + cellC·cellA·(1−maskA)
        double pmR = mR * maskA + cR * cellA * inv;
        double pmG = mG * maskA + cG * cellA * inv;
        double pmB = mB * maskA + cB * cellA * inv;

        byte B = (byte)Math.Round(Math.Clamp(pmB, 0.0, 1.0) * 255.0);
        byte G = (byte)Math.Round(Math.Clamp(pmG, 0.0, 1.0) * 255.0);
        byte R = (byte)Math.Round(Math.Clamp(pmR, 0.0, 1.0) * 255.0);
        byte A = (byte)Math.Round(Math.Clamp(outA, 0.0, 1.0) * 255.0);
        return (B, G, R, A);
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

    /// <summary>Fill <see cref="_fieldBuf"/> with the single per-pixel LOW-FREQUENCY field:
    /// the FOV mask dim with the legacy cell-fill tint folded in (UVHS round 2, 2026-06-11).
    /// One output pixel, ONE alpha — premultiplied BGRA (Pbgra32).
    ///
    /// <para>The grid is NO LONGER rasterized here — a 1.2 px world-locked hairline cannot
    /// survive the quarter-res downscale (it would become a fat bilinear-blurred band and pop
    /// in 4 px-quantized brightness steps: the "grid FORM wrong / grid JUMPS" verdict). The
    /// grid is now full-res vector geometry on GridCanvas (see DrawGrid), windowed by the
    /// inverse-feather OpacityMask on the SAME one-clock pose. This field carries only
    /// low-frequency content (mask gradient + flat cell tint), which the reduced-res downscale
    /// reproduces with no visible loss.</para>
    ///
    /// <para>SPATIAL LAYOUT: the dim + tint appear in the PERIPHERY (sharing the inverse-
    /// feather window); the centre is CLEAN (window ≈ 0 ⇒ fully transparent). The window
    /// geometry is identical to the grid's OpacityMask so the field dim and the grid reveal
    /// align with no seam.</para></summary>
    private void ComputeField()
    {
        int w = _fieldW, h = _fieldH;
        double aspect = (double)w / h;
        double cx = _fieldCx, cy = _fieldCy;
        double peak = _maskPeakAlpha;
        double featherStart = _featherStart;
        const double featherEnd = 0.95;
        double gamma = SenseDefaults.OverlayFieldGamma;
        double cellStrength = _cellStrength;
        var buf = _fieldBuf!;

        byte mr = _maskR, mg = _maskG, mb = _maskB;

        for (int y = 0; y < h; y++)
        {
            double py = (y + 0.5) / h;
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                double px = (x + 0.5) / w;
                double r = CornerNormR(px, py, cx, cy, aspect);

                // The shared radial window (peak 1.0): 0 in the clean centre, ramping to 1.0
                // in the periphery. The FOV dim is this window scaled by the tier peak; the
                // cell-fill tint is this same window scaled by its own alpha. Both share
                // center/radius/feather with the grid's OpacityMask (no seam).
                double window = ResolveFieldAlpha(r, 1.0, featherStart, featherEnd);

                // Fold the cell-fill tint UNDER the mask dim (reference compositing order),
                // both windowed by the shared falloff. One output pixel, one alpha. In the
                // clean centre window≈0 ⇒ both terms 0 ⇒ fully transparent (centre clean).
                var (oB, oG, oR, oA) = ComposeFieldPixel(window, mr, mg, mb, peak, gamma, cellStrength);

                int i = row + x * 4;
                buf[i + 0] = oB; // B
                buf[i + 1] = oG; // G
                buf[i + 2] = oR; // R
                buf[i + 3] = oA; // A
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
    /// Internal + InternalsVisibleTo("SenseEase.Tests"); no public-surface widening.</summary>
    internal double FieldCenterXForTests => _fieldCx;
    internal double FieldCenterYForTests => _fieldCy;

    /// <summary>Beta A2 test seam: the resolved corner-reach field radius (the head-coupled
    /// dynamic radius).</summary>
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

    /// <summary>UVHS round 2 (2026-06-11) test seam: the grid OpacityMask brush (the grid's
    /// transparency-hole window — the full-res vector grid's visibility geometry) so the
    /// one-clock tests can assert it mirrors the field center/radius in the SAME frame.
    /// Internal, no public-surface widening.</summary>
    internal RadialGradientBrush GridMaskBrushForTests => _gridOpacityMaskBrush;

    /// <summary>UVHS round 2 (2026-06-11) test seam: the count of grid Line shapes currently
    /// on GridCanvas (vertical + horizontal). Lets the STA grid tests assert DrawGrid produced
    /// real full-res vector lines at DPI-scaled spacing.</summary>
    internal int GridLineCountForTests => _vLines.Count + _hLines.Count;

    /// <summary>UVHS round 2 test seam: the resolved grid step (DPI/resolution-scaled spacing
    /// in device-independent pixels) from the last DrawGrid.</summary>
    internal int GridStepForTests => _lastDrawnStep;

    /// <summary>UVHS round 2 test seam: the last ApplyGridIntensity opacity TARGET (the
    /// GridCeiling × tier-fraction × intensity value the 280 ms animation eases GridCanvas.Opacity
    /// toward). The seam reads the animation's <c>.To</c> rather than the live
    /// <c>GridCanvas.Opacity</c> because a bare STA test thread has no render clock advancing the
    /// animation, so the live property would report the base value, not the pipeline output.</summary>
    internal double GridOpacityTargetForTests => _gridOpacityAnim.To ?? 0.0;

    /// <summary>M8 head-tracking sprint: host-wired lookup of "does the user have a
    /// vestibular diagnosis on file?". Read-only consumer of MainViewModel
    /// .HasVestibularDiagnosis. Retained as the overlay's read-only vestibular-status seam
    /// (the escape hatch it once guarded was removed in D-10; the mask is never weakened).</summary>
    public Func<bool>? HasVestibularDiagnosis { get; set; }
}
