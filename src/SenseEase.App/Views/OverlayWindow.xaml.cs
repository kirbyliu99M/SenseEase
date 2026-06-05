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

    // M5 UX-iteration 3 (2026-05-29): replace static clamp + fixed expanded
    // radius with state-driven dynamic radius. User reported the [0.15, 0.85]
    // clamp blocked the sprint's actual goal — "look at screen corners with
    // the mask following." Fix B removes the clamp entirely; cx/cy travel
    // the full [0, 1]. The radius dynamically expands so the farthest screen
    // corner always sits inside the gradient regardless of mask center
    // position. v0.9 baseline (HeadTrackingActive=false) keeps BrushRadius
    // = 0.85 at center (0.5, 0.5) — zero behavioral drift.
    //
    // Formula for dynamic radius (in ApplyFovMask):
    //   farX = max(cx, 1 - cx)
    //   farY = max(cy, 1 - cy)
    //   farthestCorner = sqrt(farX² + farY²)
    //   radius = max(BrushRadius, farthestCorner * BrushRadiusCornerMargin)
    //
    // Center=(0.5, 0.5): radius = max(0.85, 0.71 × 1.10) = 0.85 → v0.9 baseline
    // Center=(0.85, 0.5): radius = max(0.85, 0.99 × 1.10) = 1.09
    // Center=(1, 1) corner: radius = max(0.85, 1.41 × 1.10) = 1.55
    //
    // The radius scales exactly to need with a 10% safety margin so the
    // farthest corner sits at smoothstep ~0.91, not at the gradient edge.
    private const double BrushRadiusCornerMargin = 1.10;

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
    /// Radial feather used as GridCanvas.OpacityMask. M5.1-A v2 hotfix
    /// (2026-05-29): converted from static-frozen to per-instance-unfrozen
    /// so Center + RadiusX/Y can be updated alongside the FOV brush when
    /// head tracking is active. Previously the grid's transparency hole
    /// stayed pinned at (0.5, 0.5) while the FOV mask moved → grid lines
    /// remained visible exactly where the user was looking. Same geometry
    /// stops (BuildLevelFeather(255, white)) so the two layers stay aligned.
    /// </summary>
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

        // NE30: attach the radial feather mask up-front so a DrawGrid triggered by
        // a SizeChanged / DPI-monitor change with no immediately-following
        // ApplyState can never paint full-strength grid lines across the whole
        // screen. The brush defaults to the centred at-rest feather; ApplyState
        // updates its centre/radius when head-tracking moves the transparency hole.
        if (!ReferenceEquals(GridCanvas.OpacityMask, _gridOpacityMaskBrush))
            GridCanvas.OpacityMask = _gridOpacityMaskBrush;

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

    /// <summary>Render the FOV mask for the supplied state. Mutates the
    /// shared <c>_fovBrush</c> (a <see cref="RadialGradientBrush"/> on
    /// <c>FovMask.Fill</c>) — so <b>must run on the UI dispatcher thread</b>.
    /// The v0.9.3 cross-thread-marshal hotfix in
    /// <c>MainWindow.OnStateChanged</c> already wraps the inbound
    /// <c>StateChanged</c> callback in <c>Dispatcher.CheckAccess + BeginInvoke</c>,
    /// so by the time <c>ApplyState → ApplyFovMask</c> runs we are always
    /// on the dispatcher. M5 (head-pose centering) relies on this invariant
    /// and does NOT introduce a new background → UI code path.</summary>
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

        // ── M5: head-pose-driven center ───────────────────────────────────
        // When state.HeadTrackingActive is true, follow the smoothed head
        // gaze; otherwise snap back to the v0.9 hardcoded (0.5, 0.5) center.
        // Q3-verified self-consistent: sidecar cx/cy use the same
        // MappingMode.RelativeToBoundingBox convention as the WPF brush,
        // so no axis or sign conversion happens at any layer. The
        // Center != center guard short-circuits no-op mutations to avoid
        // re-rendering on identical updates (and to preserve the v0.9
        // baseline byte-for-byte when HeadTrackingEnabled is false).
        // M5 UX-iter 3: head-pose application is factored into
        // ApplyHeadPoseToBrush so the high-frequency dispatcher timer
        // (Fix C, MainWindow Loaded#5) can also call it without
        // re-running the grid/mask color recomputation above. State-driven
        // path runs at engine tick; timer-driven path runs at ~30 Hz.
        ApplyHeadPoseToBrush(state.HeadGazeX, state.HeadGazeY, state.HeadTrackingActive);

        // M5.1-B: don't clobber the escape-hatch alpha animation. When
        // _escapeHatchActive is true, the Storyboard owns FovMask.Opacity
        // until it completes and clears the flag.
        if (!_escapeHatchActive)
        {
            FovMask.BeginAnimation(OpacityProperty, null);
            FovMask.Opacity = 1.0;
        }
    }

    // ── M5.1-B: escape-hatch alpha dip ───────────────────────────────────────

    private bool _escapeHatchActive;
    private Storyboard? _escapeHatchStoryboard;

    /// <summary>M8 head-tracking sprint: host-wired lookup of "does the user
    /// have a vestibular diagnosis on file?". When set and returns true,
    /// <see cref="TriggerEscapeHatch"/> is a no-op even if
    /// <see cref="Configuration.AppConfig.HeadTrackingEscapeHatchEnabled"/>
    /// is true. The FOV mask IS the VIMS protection for vestibular-sensitive
    /// users — opening a 3-second window of 30 % opacity could surface
    /// symptoms during a high-motion moment. This is the defensive layer;
    /// the Settings UI also greys out the checkbox at load time. Read-only
    /// consumer of the existing MainViewModel.HasVestibularDiagnosis API
    /// (the engine + snapshot types from SenseEase.Core are NOT modified
    /// by the head-tracking sprint — verified via empty diff at M8 close).
    /// Mirrors the same Func&lt;bool&gt; pattern used by NotificationCardWindow.HasVestibularDiagnosis.
    /// Null defaults to "() =&gt; false" so an unwired test or future caller
    /// keeps the old behaviour. Locked-file note: the lookup itself lives
    /// on MainViewModel (read-only consumer of the calibration responses
    /// on the habituation profile snapshot — engine + snapshot types are
    /// not modified by the head-tracking sprint).</summary>
    public Func<bool>? HasVestibularDiagnosis { get; set; }

    /// <summary>M5.1-B: temporarily fade the FOV mask to 30 % opacity for
    /// 3 seconds with a 250 ms fade-down + 500 ms fade-up. Triggered by
    /// double-tapping Ctrl when <see cref="Configuration.AppConfig.HeadTrackingEscapeHatchEnabled"/>
    /// is true. Lets a Balanced-mode user peek at a screen corner without
    /// committing to FullFollow permanently. Orthogonal to the follow-mode
    /// choice — works whether head-tracking is on or off, in either mode.
    /// Safe to call repeatedly: a new call mid-animation resets the cycle
    /// (the user gets a fresh 3 second window).
    ///
    /// <para>M8 escape × vestibular guard: returns early when
    /// <see cref="HasVestibularDiagnosis"/> reports true. The FOV mask is
    /// the VIMS protection for vestibular-sensitive users; we never lower
    /// it. Defensive layer — the Settings UI ALSO disables the checkbox
    /// at load time, but a user with a forced-on JSON edit (or a Ctrl tap
    /// before the prefs are hydrated) still gets the protection here.</para></summary>
    public void TriggerEscapeHatch()
    {
        if (!Configuration.AppConfig.HeadTrackingEscapeHatchEnabled) return;
        // M8: vestibular guard — read-only consumer of the host's lookup.
        // Null fallback returns false (i.e. unwired = previous behaviour).
        if (HasVestibularDiagnosis?.Invoke() == true) return;

        try { _escapeHatchStoryboard?.Stop(this); } catch { /* ignore */ }

        var sb = new Storyboard();

        var fadeDown = new DoubleAnimation
        {
            From          = 1.0,
            To            = 0.30,
            Duration      = new Duration(TimeSpan.FromMilliseconds(250)),
            BeginTime     = TimeSpan.Zero,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(fadeDown, FovMask);
        Storyboard.SetTargetProperty(fadeDown, new PropertyPath(OpacityProperty));
        sb.Children.Add(fadeDown);

        // 250 ms (end of fade-down) + 3000 ms hold → fade-up starts at 3250 ms.
        var fadeUp = new DoubleAnimation
        {
            From          = 0.30,
            To            = 1.0,
            Duration      = new Duration(TimeSpan.FromMilliseconds(500)),
            BeginTime     = TimeSpan.FromMilliseconds(3250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(fadeUp, FovMask);
        Storyboard.SetTargetProperty(fadeUp, new PropertyPath(OpacityProperty));
        sb.Children.Add(fadeUp);

        sb.Completed += (_, _) =>
        {
            _escapeHatchActive = false;
            // After the storyboard ends, hand opacity back to ApplyFovMask
            // by leaving it animated-to-1.0; ApplyFovMask's own reset will
            // take over on the next tick.
        };

        _escapeHatchStoryboard = sb;
        _escapeHatchActive = true;
        sb.Begin(this, true);
    }

    /// <summary>M5 test seam: expose the FOV mask brush so STA-bound unit
    /// tests can verify <c>Center</c> follows <c>HeadGazeX/Y</c>. Internal
    /// + <c>InternalsVisibleTo("SenseEase.Tests")</c>, no widening of the
    /// public surface.</summary>
    internal RadialGradientBrush? FovBrushForTests => _fovBrush;

    /// <summary>M5 UX-iter 3 Fix C: high-frequency entry point for the head-pose
    /// dispatcher timer (~30 Hz). Bypasses the full ApplyState grid/mask color
    /// recomputation and updates ONLY the brush's <c>Center</c> + <c>Radius</c>.
    /// Decouples mask-follows-head smoothness from the engine tick (~3.3 Hz)
    /// which was the perceived "lag / inconsistent framerate" reported during
    /// Demo B. Must run on UI thread (mutates DependencyObjects).</summary>
    public void UpdateHeadGazeCenter(double headGazeX, double headGazeY, bool active)
    {
        if (_fovBrush is null) return;
        ApplyHeadPoseToBrush(headGazeX, headGazeY, active);
    }

    /// <summary>M5 UX-iter 3: shared head-pose → brush mutation. Both the
    /// state-driven path (ApplyFovMask, engine tick) and the timer-driven
    /// path (UpdateHeadGazeCenter, 30 Hz) call this so the brush state
    /// stays consistent. Active=false snaps to v0.9 baseline (center 0.5/0.5,
    /// radius BrushRadius).</summary>
    private void ApplyHeadPoseToBrush(double headGazeX, double headGazeY, bool active)
    {
        if (_fovBrush is null) return;

        // M5.1-B: mode-based clamp. Balanced (default) restricts travel to
        // [0.10, 0.90] so the periphery is always partially dimmed regardless
        // of gaze direction — clinical default for VIMS mitigation. FullFollow
        // (opt-in) allows the full [0, 1] range; user uses the escape-hatch
        // hotkey if they want to peek at a corner without committing to
        // FullFollow permanently.
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
        var center = new Point(cx, cy);
        if (_fovBrush.Center != center)
        {
            _fovBrush.Center         = center;
            _fovBrush.GradientOrigin = center;
        }

        // Fix B: dynamic radius scales to the farthest screen corner so
        // even at full-corner center, opposite (0, 0) sits inside gradient
        // at smoothstep ~0.91. v0.9 baseline (active=false) keeps 0.85.
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
        if (_fovBrush.RadiusX != radius)
        {
            _fovBrush.RadiusX = radius;
            _fovBrush.RadiusY = radius;
        }

        // M5.1-A v2 hotfix: mirror the FOV brush's Center / Radius onto the
        // grid OpacityMask brush so the grid's transparency hole tracks the
        // FOV clear zone. Without this, the grid lines remain visible
        // exactly where the user is looking — the exact glitch reported
        // during M5.1-A v2 UVHS. Same coordinates, same radius scaling.
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

    private void ApplyGridFalloff(SenseState state)
    {
        // Grid OpacityMask uses the SAME fixed feather geometry as the FOV mask so
        // the two layers stay aligned — no visible seam at the transition. Only the
        // ramp ceiling differs (255 here because it's an OpacityMask that needs to
        // fully reveal the grid in the periphery). Per-VIMS opacity is carried by
        // GridCanvas.Opacity (driven in ApplyGridIntensity by `coverage`).
        // FIX #2: assign the pre-built frozen brush; one reference-compare guards
        // against external code clearing the mask — avoids allocation every tick.
        if (!ReferenceEquals(GridCanvas.OpacityMask, _gridOpacityMaskBrush))
            GridCanvas.OpacityMask = _gridOpacityMaskBrush;
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

        // Grid presence now tracks the SAME VIMS tier signal as the FOV mask alpha
        // (issue #4 fix): pressure-driven, not FOV-driven. The previous coverage-based
        // formula (1 − FovRadius/100) kept the grid visible after a user "I feel dizzy"
        // had narrowed FOV even when current pressure was zero — wrong, because no
        // motion → no vection risk → no anchor grid needed.
        //
        // Also: when always-on overlay is enabled, the floor applied to state.VimsTier
        // by MainWindow.OnStateChanged flows through here automatically (issue #1 fix),
        // so the Chang-Hain anchor grid appears at the user's baseline tier even on a
        // calm screen.
        int    tier        = Math.Clamp(state.VimsTier, 1, 10);
        double tierFraction = (tier - 1) / 9.0;                  // 0 at L1, 1 at L10
        double gv          = Math.Clamp(state.Shader.GridVisibility, 0.0, 1.0);
        double ceiling     = Math.Min(1.0, 0.40 + (0.80 * gv));
        double target      = Math.Clamp(tierFraction * ceiling * (0.55 + (0.45 * _smoothedIntensity)),
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
