using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SenseEase.App.Interop;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;

namespace SenseEase.App.Views;

/// <summary>
/// Transparent, topmost, click-through overlay. Renders a styled anchor grid and
/// an animated radial FOV mask. Phase-1 prototype — not a DirectX renderer.
/// </summary>
public partial class OverlayWindow : Window, IOverlayRenderer
{
    private const double BrushRadius = 0.85;

    /// <summary>
    /// VIMS 10-level table (方案 A). Each row is (R, G, B, peak-alpha fraction 0–1).
    /// L1-L6 are pure gray (#666 → #444 → #222). L7-L10 carry a subtle cool tint
    /// (Blue channel up to +20) — pure black mask reads as "uniform overlay" on bright
    /// backgrounds, but a cool-shifted near-black reads as a deliberate "out-of-focus
    /// dimming" effect, drawing the eye to the clear center even on white content.
    /// Engine hysteresis (state.VimsTier) controls discrete tier identity;
    /// OverlayWindow's per-tick smoothing cross-fades color+alpha between tiers.
    /// </summary>
    private static readonly (byte R, byte G, byte B, double Alpha)[] VimsLevels =
    {
        (102, 102, 102, 0.00), // L1  #666666  0%   neutral gray
        (102, 102, 102, 0.17), // L2  #666666  17%
        ( 68,  68,  68, 0.34), // L3  #444444  34%
        ( 68,  68,  68, 0.43), // L4  #444444  43%
        ( 34,  34,  34, 0.51), // L5  #222222  51%
        ( 34,  34,  34, 0.60), // L6  #222222  60%
        (  4,   4,  10, 0.68), // L7  #04040A  68%  cool-shifted near-black (+blue tint)
        (  2,   2,   8, 0.77), // L8  #020208  77%
        (  0,   0,   6, 0.85), // L9  #000006  85%
        (  0,   0,   4, 0.92), // L10 #000004  92%  peak — slight blue read on bright bg
    };

    // Smoothed currently-displayed mask color + alpha. Tracks the engine's discrete tier
    // target via per-tick exponential approach so tier transitions cross-fade visually
    // over ~250 ms (smoothing factor 0.25 at ~15 Hz tick rate). This gives back the
    // continuous-looking transitions the user preferred earlier WITHOUT giving up the
    // tier hysteresis — the engine still snaps `state.VimsTier` discretely; the visual
    // just eases between tier values.
    private double _displayedR, _displayedG, _displayedB;
    private double _displayedAlpha;
    private bool   _displayedSeeded;
    private const double MaskSmoothingFactor = 0.25;

    // Peripheral cell-fill: deep near-black at moderate alpha. Final on-screen strength
    // is further modulated by GridCanvas.Opacity (pressure) and its radial OpacityMask.
    private static readonly SolidColorBrush CellFillBrush = CreateCellFillBrush();

    private static SolidColorBrush CreateCellFillBrush()
    {
        var b = new SolidColorBrush(Color.FromArgb(120, 8, 8, 10));
        b.Freeze();
        return b;
    }

    private bool _lastMaskActive;
    private double _smoothedIntensity;

    // ── FIX #1: pre-computed FOV stop offsets and smoothstep weights ─────────

    /// <summary>
    /// Constant radial offsets for all 11 gradient stops (0.0, 0.35, ramp×8, 0.95, 1.0).
    /// Never changes — computed once at class load.
    /// </summary>
    private static readonly double[] FovStopOffsets = BuildFovStopOffsets();
    private static double[] BuildFovStopOffsets()
    {
        const double rampStart = 0.35;
        const double rampEnd   = 0.95;
        const double rampWidth = rampEnd - rampStart;
        const int    n         = 8;
        // 11 stops: [0.0, 0.35, 0.35+w*1/8 … 0.35+w*8/8=0.95, 1.0]
        var arr = new double[11];
        arr[0] = 0.0;
        arr[1] = rampStart;
        for (int k = 1; k <= n; k++) arr[1 + k] = rampStart + (rampWidth * k / n);
        arr[10] = 1.0;
        return arr;
    }

    /// <summary>
    /// Per-stop smoothstep multiplier s(t)=t²(3-2t) in [0,1]. Index matches FovStopOffsets.
    /// Stops 0 and 1 are fully transparent (value 0.0). Stop 10 is fully opaque (1.0).
    /// </summary>
    private static readonly double[] FovStopSmoothstep = BuildFovStopSmoothstep();
    private static double[] BuildFovStopSmoothstep()
    {
        const int n = 8;
        var arr = new double[11];
        arr[0] = 0.0;
        arr[1] = 0.0; // both clear stops → alpha 0
        for (int k = 1; k <= n; k++)
        {
            double t = (double)k / n;
            arr[1 + k] = t * t * (3 - 2 * t);
        }
        arr[10] = 1.0;
        return arr;
    }

    // Mutable (unfrozen) FOV brush reused across ticks; allocated once on first call.
    private RadialGradientBrush?      _fovBrush;
    private GradientStopCollection?   _fovStops;
    /// <summary>Packed (r&lt;&lt;24|g&lt;&lt;16|b&lt;&lt;8|peakAlpha) — skip mutation when unchanged.</summary>
    private int _lastFovBrushKey = -1;

    // ── FIX #2: cached, frozen grid opacity-mask brush ───────────────────────

    /// <summary>
    /// Invariant radial feather used as GridCanvas.OpacityMask. Built once, frozen.
    /// Always uses BuildLevelFeather(255, Colors.White) — the only call site.
    /// </summary>
    private static readonly RadialGradientBrush GridOpacityMaskBrush = BuildGridOpacityMaskBrush();
    private static RadialGradientBrush BuildGridOpacityMaskBrush()
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center         = new Point(0.5, 0.5),
            RadiusX        = BrushRadius,
            RadiusY        = BrushRadius,
            GradientStops  = BuildLevelFeather(255, Colors.White),
        };
        brush.Freeze();
        return brush;
    }

    // ── FIX #13: cached grid line objects ────────────────────────────────────

    /// <summary>Frozen white grid-line stroke. Allocated once at class load.</summary>
    private static readonly SolidColorBrush GridLineBrush = CreateGridLineBrush();
    private static SolidColorBrush CreateGridLineBrush()
    {
        var b = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255));
        b.Freeze();
        return b;
    }

    private System.Windows.Shapes.Rectangle? _cellFillRect;
    private readonly List<System.Windows.Shapes.Line> _vLines = new();
    private readonly List<System.Windows.Shapes.Line> _hLines = new();
    private double _lastDrawnW    = -1;
    private double _lastDrawnH    = -1;
    private int    _lastDrawnStep = -1;

    // ── FIX #14: single reusable DoubleAnimation for grid opacity ─────────────

    /// <summary>
    /// Reused animation object — mutate .To per tick instead of allocating a new one.
    /// Duration is constant; From is left unset so WPF animates from the current value.
    /// </summary>
    private readonly DoubleAnimation _gridOpacityAnim = new()
    {
        Duration = new Duration(TimeSpan.FromMilliseconds(280)),
    };

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded            += OnLoaded;
        SizeChanged       += (_, _) => DrawGrid();
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

    private void OnLoaded(object? sender, RoutedEventArgs e) => DrawGrid();

    // ── Grid ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Draw / refresh CH stationary anchor grid: fixed 40 px spacing, white lines.
    /// First call builds all child objects; subsequent calls only mutate coordinates
    /// when the canvas size or step changes — no clear+rebuild allocation.
    /// </summary>
    private void DrawGrid()
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Scale grid spacing and line thickness proportionally from the 1920×1080 baseline.
        double dpiX = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
        double physicalWidth = SystemParameters.PrimaryScreenWidth * dpiX;
        double scale = physicalWidth / 1920.0;
        int step = Math.Max(20, (int)Math.Round(SenseDefaults.GridSizePx * scale));
        double thickness = Math.Max(0.5, 1.2 * scale);

        // Early-out when nothing has changed (no size change, no step change).
        if (w == _lastDrawnW && h == _lastDrawnH && step == _lastDrawnStep)
            return;

        // ── Cell-fill rectangle ───────────────────────────────────────────────
        if (_cellFillRect is null)
        {
            _cellFillRect = new System.Windows.Shapes.Rectangle
            {
                Fill = CellFillBrush,
            };
            System.Windows.Controls.Canvas.SetLeft(_cellFillRect, 0);
            System.Windows.Controls.Canvas.SetTop(_cellFillRect, 0);
            GridCanvas.Children.Add(_cellFillRect);
        }
        _cellFillRect.Width  = w;
        _cellFillRect.Height = h;

        // ── Vertical lines ────────────────────────────────────────────────────
        int vCount = (int)Math.Ceiling(w / step) + 1;

        // Grow list if needed.
        while (_vLines.Count < vCount)
        {
            var line = new System.Windows.Shapes.Line
            {
                Stroke = GridLineBrush,
            };
            _vLines.Add(line);
            GridCanvas.Children.Add(line);
        }

        // Shrink list if needed.
        while (_vLines.Count > vCount)
        {
            int last = _vLines.Count - 1;
            GridCanvas.Children.Remove(_vLines[last]);
            _vLines.RemoveAt(last);
        }

        // Mutate endpoints + thickness.
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
            var line = new System.Windows.Shapes.Line
            {
                Stroke = GridLineBrush,
            };
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

    // ── IOverlayRenderer ─────────────────────────────────────────────────────

    /// <summary>Update the overlay from the current SenseState.</summary>
    public void ApplyState(SenseState state)
    {
        bool maskChanged = state.MaskActive != _lastMaskActive;
        _lastMaskActive  = state.MaskActive;

        if (maskChanged || GridCanvas.Children.Count == 0)
        {
            DrawGrid();
            // Grid opacity is fully driven by ApplyGridIntensity via BeginAnimation.
            // Do NOT call GridCanvas.BeginAnimation(OpacityProperty) here — any HoldEnd
            // animation would permanently block the pressure-driven opacity updates.
        }

        ApplyGridIntensity(state);
        ApplyFovMask(state);
        ApplyGridFalloff(state);
    }

    private void ApplyFovMask(SenseState state)
    {
        // Engine snaps `state.VimsTier` discretely (hysteresis + debounce → no flicker on
        // sub-tier noise). Visual layer eases between tier values via per-tick exponential
        // smoothing (~250 ms convergence) so transitions feel continuous, matching the
        // user's earlier-praised smooth look without re-introducing per-tick flicker.
        // L7+ carries a subtle cool tint so the mask reads as deliberate dimming even on
        // bright backgrounds (pure-black mask on white content reads as "uniform gray
        // wash" — the slight blue shift restores the "out-of-focus" perceptual cue).
        int tier = Math.Clamp(state.VimsTier, 1, 10);
        var (tR, tG, tB, tAlpha) = VimsLevels[tier - 1];

        // Seed on first tick so we don't ease in from black/zero at startup.
        if (!_displayedSeeded)
        {
            _displayedR = tR; _displayedG = tG; _displayedB = tB; _displayedAlpha = tAlpha;
            _displayedSeeded = true;
        }
        else
        {
            _displayedR     += (tR     - _displayedR)     * MaskSmoothingFactor;
            _displayedG     += (tG     - _displayedG)     * MaskSmoothingFactor;
            _displayedB     += (tB     - _displayedB)     * MaskSmoothingFactor;
            _displayedAlpha += (tAlpha - _displayedAlpha) * MaskSmoothingFactor;
        }

        byte r         = (byte)Math.Round(Math.Clamp(_displayedR,     0, 255));
        byte g         = (byte)Math.Round(Math.Clamp(_displayedG,     0, 255));
        byte b         = (byte)Math.Round(Math.Clamp(_displayedB,     0, 255));
        byte peakAlpha = (byte)Math.Round(Math.Clamp(_displayedAlpha, 0, 1) * 255.0);

        // ── Build brush once; thereafter mutate stops in-place (FIX #1) ───────
        if (_fovBrush is null)
        {
            _fovStops = new GradientStopCollection();
            for (int i = 0; i < FovStopOffsets.Length; i++)
                _fovStops.Add(new GradientStop(Colors.Transparent, FovStopOffsets[i]));

            _fovBrush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center         = new Point(0.5, 0.5),
                RadiusX        = BrushRadius,
                RadiusY        = BrushRadius,
                GradientStops  = _fovStops,
            };
            FovMask.Fill = _fovBrush;
        }

        // Pack quantized key; skip mutation when nothing changed.
        int key = (r << 24) | (g << 16) | (b << 8) | peakAlpha;
        if (key != _lastFovBrushKey)
        {
            _lastFovBrushKey = key;

            var clear = Color.FromArgb(0, r, g, b);
            _fovStops![0].Color = clear;
            _fovStops[1].Color  = clear;
            for (int i = 2; i < _fovStops.Count; i++)
            {
                byte a = (byte)Math.Round(peakAlpha * FovStopSmoothstep[i]);
                _fovStops[i].Color = Color.FromArgb(a, r, g, b);
            }
        }

        FovMask.BeginAnimation(OpacityProperty, null);
        FovMask.Opacity = 1.0;
    }

    private void ApplyGridFalloff(SenseState state)
    {
        // Grid OpacityMask uses the SAME fixed feather geometry as the FOV mask so
        // the two layers stay aligned — no visible seam at the transition. Only the
        // ramp ceiling differs (255 here because it's an OpacityMask that needs to
        // fully reveal the grid in the periphery). Per-VIMS opacity is carried by
        // GridCanvas.Opacity (driven in ApplyGridIntensity by `coverage`).
        // FIX #2: assign the pre-built frozen brush; one reference-compare guards
        // against external code clearing the mask — avoids allocation every tick.
        if (!ReferenceEquals(GridCanvas.OpacityMask, GridOpacityMaskBrush))
            GridCanvas.OpacityMask = GridOpacityMaskBrush;
    }

    // Builds the FIXED gradient structure mandated by the VIMS comfort spec:
    //   r ∈ [0.00, 0.35]  fully transparent
    //   r ∈ [0.35, 0.95]  smoothstep ramp 0 → peakAlpha
    //   r ∈ [0.95, 1.00]  hold at peakAlpha
    // Smoothstep: s(t) = t² × (3 - 2t). Only `peakAlpha` (and color, for the FOV vs
    // grid layers) varies — the structure is identical between layers so they align.
    private static System.Windows.Media.GradientStopCollection BuildLevelFeather(byte peakAlpha, Color color)
    {
        var stops  = new System.Windows.Media.GradientStopCollection();
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

    private void ApplyGridIntensity(SenseState state)
    {
        // EMA smoothing (α=0.85, τ≈1.7 s at 300 ms tick) — eliminates per-tick jumps
        // from WGC pixel-diff noise without losing responsiveness to sustained pressure.
        double intensity = ComputeAdaptiveIntensity(state);
        _smoothedIntensity = 0.85 * _smoothedIntensity + 0.15 * intensity;

        // Grid presence tracks clear-zone shrinkage: it is FULLY GONE at FovRadius 100%
        // (coverage 0 = no protection needed) and rises toward `ceiling` as the zone
        // tightens under pressure. No opacity floor — a 100% clear zone shows no grid.
        // The pressure-EMA modulates intensity within that coverage-gated envelope.
        double coverage = Math.Clamp(1.0 - (state.FovRadiusPercent / 100.0), 0.0, 1.0);
        double gv       = Math.Clamp(state.Shader.GridVisibility, 0.0, 1.0);
        double ceiling  = Math.Min(1.0, 0.40 + (0.80 * gv));
        double target   = Math.Clamp(coverage * ceiling * (0.55 + (0.45 * _smoothedIntensity)),
                                     0.0, ceiling);

        // FIX #14: reuse the single pre-allocated DoubleAnimation; only mutate .To.
        // From is left unset → WPF animates from the current animated value (identical
        // visual behavior, zero per-tick allocation).
        _gridOpacityAnim.To = target;
        GridCanvas.BeginAnimation(OpacityProperty, _gridOpacityAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private static double ComputeAdaptiveIntensity(SenseState state)
    {
        double flowTerm = Math.Clamp(state.OpticalFlowIndex / 120.0, 0.0, 1.0);
        double pressureTerm = Math.Clamp(state.Pressure / SenseDefaults.TriggerPressure, 0.0, 1.0);
        return Math.Clamp((flowTerm * 0.35) + (pressureTerm * 0.85), 0.0, 1.0);
    }
}
