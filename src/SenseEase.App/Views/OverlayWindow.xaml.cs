using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
/// real <c>Line</c> shapes drawn into <c>GridCanvas</c> at DPI/resolution-scaled spacing.
/// A 1.2 px world-locked hairline CANNOT survive rasterization into a quarter-res field texel
/// (each line would become a fat bilinear-blurred band and pop in 4 px-quantized brightness
/// steps) — so the grid stays full-res vector geometry, the resolution-critical layout fix
/// the UVHS round-2 verdict demanded ("grid FORM wrong", "grid JUMPS").</para>
///
/// <para><b>Grid window = the CANVAS MODEL (UVHS round-8 fix, 2026-06-11).</b> Kirby's
/// clarified spec: a STATIC full-screen grid canvas that is TRANSPARENT where the user looks
/// (hole + feather follow the head) and PRESENT everywhere else — every corner, at every
/// gaze. The grid's <c>OpacityMask</c> was a <c>RadialGradientBrush</c> that diverged from the
/// field's mask geometry in FOUR ways (ellipse-vs-circle seam, frozen-vs-damped breathing, the
/// saturated/empty zone TRANSLATING with the head, the Shape-3/4 featherStart misalignment).
/// It is REPLACED by an <c>ImageBrush</c> over a SECOND reduced-res bitmap written in the SAME
/// per-pixel pass as the field (see <see cref="ComputeFieldRows"/>): the field's monotonic
/// window value SATURATED to solid (clear hole → smootherstep rise → SOLID beyond r ≥
/// featherEnd, including the far r &gt; 1 corners). So the window shares the field's geometry
/// BYTE-FOR-BYTE — same aspect-corrected circle, same corner-growth damping, same
/// FeatherWidth-tracked featherStart (Shape-3/4 seam fixed automatically), same one-clock
/// frame. There is NO outer fade (the round-7 band-pass tail is revoked by this model).</para>
///
/// <para>The A1 One-Euro-smoothed (cx,cy) handed in by the MainWindow pump remains the
/// single pose input. ComputeField derives the field center/radius from it AND the grid-window
/// bitmap in the SAME RenderFieldIfDirty pass, so the hole rides the head while the LINES stay
/// world-locked at pixel origin — one clock, no divergent geometry, no brush to mirror.
/// MaskDepth and FeatherWidth remain REAL inputs (D-2): MaskDepth bounds the peak alpha
/// (clamped to the ≥0.20 floor / VimsLevels ceiling), FeatherWidth slides the feather-band
/// start (and thus the grid window's rise start, from the SAME LUT).</para>
/// </summary>
public partial class OverlayWindow : Window, IOverlayRenderer
{
    private const double BrushRadius = SenseDefaults.OverlayBrushRadius; // F2: single-source
    // BrushRadiusCornerMargin alias removed — the corner-coverage radius rescaling was
    // retired per Kirby's round-3 ruling (no jump/zoom; the grid window radius is frozen).
    // SenseDefaults.OverlayBrushRadiusCornerMargin stays declared for the historical record.

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

    // ── CANVAS-MODEL grid window bitmap (UVHS round-8 fix, 2026-06-11) ────────
    // Kirby's CANVAS MODEL: a STATIC full-screen grid canvas that is TRANSPARENT where
    // the user looks (hole + feather follow the head) and PRESENT everywhere else (every
    // corner, every gaze). The grid's OpacityMask is no longer a RadialGradientBrush — that
    // window diverged from the field's mask geometry in FOUR ways (ellipse-vs-circle seam,
    // frozen-vs-damped breathing, the saturated/empty zone translating with the head, and
    // the Shape-3/4 featherStart misalignment — the round-8 verdict). Instead the SAME
    // per-pixel pass that computes the field writes a SECOND reduced-res buffer carrying the
    // window value SATURATED to solid (canvas semantics: clear hole → smootherstep rise →
    // SOLID everywhere beyond r >= featherEnd, including the far r>1 corners). The grid's
    // OpacityMask is an ImageBrush over THIS bitmap (Stretch=Fill, bilinear), so the window
    // shares the field's geometry BYTE-FOR-BYTE — same aspect-corrected circle, same
    // corner-growth damping, same FeatherWidth-tracked featherStart, same one-clock frame.
    //
    // Format: Pbgra32 grayscale-alpha (premultiplied black with the window in the alpha byte).
    // WPF's managed PixelFormats does NOT expose A8 (it lives only in WIC/D2D), so the spec's
    // A8-fallback applies: write (B,G,R,A) = (0,0,0,window) — premultiplied black — and the
    // OpacityMask consumes ONLY the alpha, which IS the window value. The cheapest CORRECT
    // OpacityMask source on WPF WriteableBitmap. 4 bytes per pixel like the field buffer.
    private WriteableBitmap? _gridWindowBmp;
    private byte[]? _gridWindowBuf;              // Pbgra32 (0,0,0,window) scratch buffer (4 bytes/px)
    private ImageBrush? _gridWindowMaskBrush;    // GridCanvas.OpacityMask over _gridWindowBmp
    // Perf-DELTA seam (default true — production ALWAYS writes the window). The perf harness flips
    // it false to time a field-only pass and isolate the second-buffer-write cost. Never false at
    // runtime; hoisted to a loop-invariant local in ComputeFieldRows so it costs nothing per pixel.
    private bool _writeGridWindowForPerfDelta = true;

    // Recompute gate: only redraw when an input moved beyond epsilon (a static
    // screen must cost ~zero). Packs the inputs that change the rendered field.
    private bool   _fieldDirty = true;
    // Fix 2 (dirty-band): TRUE when a NON-pose input changed (color / peak / feather /
    // cell-strength / active / size) — those touch EVERY pixel, so the next render must
    // recompute the WHOLE buffer. A pose-only move (cx/cy/radius) leaves this false and the
    // renderer recomputes only the union band of the old+new feather annuli (the rows where
    // the window value actually changed). Set by MarkFieldDirty(); cleared after a full render.
    private bool   _fieldFullDirty = true;
    private double _lastCx = double.NaN, _lastCy = double.NaN, _lastRadius = double.NaN;
    private double _lastPeak = double.NaN, _lastFeatherStart = double.NaN;
    private int    _lastColorKey = -1;
    private bool   _lastFieldActive;
    private const double FieldEpsilon = 0.0005;
    // Kirby round-3 (item 4.1): tighter active-head-tracking motion threshold (SSOT in
    // SenseDefaults). Used in ApplyHeadPose so a slow One-Euro glide is never quantized
    // into a freeze-then-jump; the static-screen gate keeps the coarse FieldEpsilon.
    private const double FieldMotionEpsilon = SenseDefaults.OverlayFieldMotionEpsilon;

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
    // Kirby round-3 (item 4.5): monotonic count of field recomputes (== WritePixels calls)
    // since construction — never reset by the per-minute perf flush. Lets the anti-double-
    // compute test assert recomputes over N pumped frames stay <= N.
    private long _totalRecomputes;

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

    // Round-10 hysteretic re-aim gate (2026-06-11 audit): the live grid-opacity target was
    // the last un-deadbanded noise follower — .To re-aimed + BeginAnimation re-begun every
    // ~66 ms engine tick straight off OFI/pressure wander (the mask alpha and the cell tint
    // both already have quantize/snap gates). Anchor the last AIMED target and re-aim only
    // when a DISCRETE input changes (tier / gv / steady flag — deliberate, not noise) or the
    // continuous target moved ≥ GridOpacityRetargetQuantum (~2 on-screen 8-bit codes at the
    // 130/255 stroke). Mirrors the pose gate's anchored-hysteresis rationale.
    private double _lastAimedGridTarget = double.NaN;
    private int    _lastAimedGridTier   = int.MinValue;
    private double _lastAimedGridGv     = double.NaN;
    private bool?  _lastAimedGridSteady;

    // Round-10 seam fix (2026-06-11 audit, static ~12 device px offset): the grid-window
    // OpacityMask brush used RelativeToBoundingBox, which maps over the GridCanvas RENDER
    // bounds — and DrawGrid's last overshoot line (at ceil(w/step)·step ≥ w; Canvas does not
    // clip) inflates those bounds past the screen, shifting/stretching the grid hole off the
    // field hole. Record the field's true extent (DrawGrid's w/h, same pre-layout fallback as
    // EnsureFieldBitmap) and map the brush ABSOLUTELY onto it instead.
    private double _gridMaskExtentW;
    private double _gridMaskExtentH;

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

    // ── Grid OpacityMask: CANVAS-MODEL bitmap window (UVHS round-8 fix, 2026-06-11) ─
    // RETIRED: the RadialGradientBrush window (BuildGridOpacityMaskBrush / BuildLevelFeather /
    // the M5.1-A brush mirror) and its band-pass FALL tail. They diverged from the field's
    // mask geometry in four ways and translated the "empty corner" with the head — the round-8
    // empty-corner regression. The grid's OpacityMask is now an ImageBrush over the
    // _gridWindowBmp written in the SAME field pass (clear hole → rise → SOLID everywhere
    // beyond, including every corner at every gaze), so the canvas is present everywhere it
    // isn't being looked at, byte-for-byte aligned with the field's circle/damping/featherStart.

    // ── Grid: single reusable opacity animation (3762a42 FIX #14) ────────────
    /// <summary>Reused 280 ms animation object — mutate .To per tick instead of allocating a
    /// new one. Duration constant; From left unset so WPF animates from the current value.</summary>
    private readonly DoubleAnimation _gridOpacityAnim = new()
    {
        Duration = new Duration(TimeSpan.FromMilliseconds(280)),
    };

    private bool _lastMaskActive;

    // ── Win32 topmost re-assert (ABCD test fix, 2026-06-12) ──────────────────
    // One of the four alpha-test machines lost the overlay over every window
    // except SenseEase itself: a foreign app (game/launcher/consent prompt)
    // claimed the top of the topmost band and the overlay NEVER RECOVERED,
    // because WPF's Topmost=true is only asserted at Show time. This is the
    // exact bug class PersistentBarWindow already fixed (Path Z UX iter 4:
    // "WPF's Topmost=true doesn't always survive Alt-Tab round-trips against
    // other windows that briefly claim topmost") — the overlay just never got
    // the same armor. SetWindowPos(HWND_TOPMOST) is the authoritative path;
    // it re-tops the topmost band without activating, moving, or resizing.
    // NOTE: this cannot help against true EXCLUSIVE-fullscreen apps (DWM
    // composes nothing over them) or a game on a different monitor — those
    // are environment triage, not z-order.
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_FORCE_TOPMOST = 0x0001 | 0x0002 | 0x0010 | 0x0200;
        // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER

    /// <summary>Engine ticks (~66 ms) between periodic topmost re-asserts while the
    /// overlay is visible: 16 ≈ once per second. The call is one idempotent Win32
    /// round-trip (the bar fires the same call 5×/s) — negligible.</summary>
    private const int TopmostReassertTicks = 16;
    private int  _topmostTick;
    private long _topmostReasserts;

    /// <summary>Force the overlay back to the top of the topmost band. Safe no-op
    /// before the HWND exists or while hidden. Mirrors PersistentBarWindow.ReassertTopmost.</summary>
    public void ReassertTopmost()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _topmostReasserts++;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_FORCE_TOPMOST);
    }

    /// <summary>Test seam: count of Win32 topmost re-asserts actually issued.</summary>
    internal long TopmostReassertsForTests => _topmostReasserts;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded            += OnLoaded;
        Closed            += (_, _) => SenseEase.App.Services.SystemSurfaceZGuard.Stop();
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
        // Beta-1 theme-3: system-UI overlay — click-through AND absent from Alt-Tab + taskbar +
        // never foreground-activating (fixes the blank "SenseEase Overlay" Alt-Tab entry + the
        // Show()-steals-focus jolt). The no-activate mechanism is WS_EX_NOACTIVATE set HERE
        // (SourceInitialized fires within Show(), before the window is presented, so the show
        // itself does not activate). NOTE: WPF forbids ShowActivated="False" on a Maximized
        // window, so WS_EX_NOACTIVATE — the Win32-authoritative path — is what we rely on.
        ClickThrough.EnableSystemUiClickThrough(this);

        // Keep native menus / the IME 中 UI / the taskbar above the overlay (they'd otherwise be buried by the
        // ~1/s topmost re-assert — see the lift call in ApplyState). Started here so the WinEvent callbacks land
        // on this UI thread. Best-effort; a hook failure never affects the overlay.
        SenseEase.App.Services.SystemSurfaceZGuard.Start();

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

        // ABCD test fix: periodic topmost re-assert (~1/s at the 66 ms engine tick).
        // Recovers the overlay after a foreign app claims the top of the topmost band
        // (the 1-of-4-machines "overlay only shows over SenseEase" report) — the same
        // self-healing the persistent bar has had since Path Z iter 4.
        if (IsVisible && _topmostTick++ % TopmostReassertTicks == 0)
        {
            ReassertTopmost();
            // The overlay just re-topped — which lands it ABOVE SenseEase's own UI windows (the
            // "owned-above-owner" rule only fires on the make-topmost transition, NOT on an already-topmost
            // re-assert; verified empirically via owner_diag). Re-lift our windows + any open tooltip in this
            // SAME synchronous tick so no frame ever paints with the overlay over our UI. No separate clock ⇒
            // no race ⇒ no flicker (the one thing the old ~60 Hz keeper could not do). Touches only our own
            // windows, never the overlay's Z — so the overlay can't be pushed to the back.
            //
            // Same tick, same reason, for FOREIGN system surfaces we must not bury: native menus (incl. the
            // right-click menu off the IME 中 tray icon), the IME candidate UI, and the taskbar/tray. Lifted
            // FIRST so SenseEase's own windows (re-lifted just below) still end up on top of them.
            SenseEase.App.Services.SystemSurfaceZGuard.LiftAboveOverlay();
            SenseEase.App.Services.OverlayZOrder.ReassertAboveOverlay();
        }

        bool maskChanged = state.MaskActive != _lastMaskActive;
        _lastMaskActive  = state.MaskActive;
        if (maskChanged || GridCanvas.Children.Count == 0)
            DrawGrid();

        ResolveMaskParams(state);
        ApplyGridIntensity(state);     // drives GridCanvas.Opacity (full-res vector grid)
        EnsureGridOpacityMask();
        // Round-10 one-clock fix (2026-06-11 audit, smoking gun): state.HeadGazeX/Y on this
        // engine-tick path carries the RAW un-One-Euro'd sidecar sample (MainViewModel
        // .UpdateFromState writes poseReader.ReadLatest() straight in — no filter, no
        // FaceDetected grace). While the ~165 Hz pump owns the pose (UpdateHeadGazeCenter
        // feeding the single One-Euro output), applying the raw pose here made TWO writers
        // fight over _fieldCx at ~15 Hz — the presented hole hopped raw↔filtered by ~1-7
        // device px, bypassing the filter entirely (the post-freeze 抖動). The pump's clock
        // is authoritative: skip the pose write whenever it is subscribed. With the pump
        // parked (head tracking off / power save) this path remains the pose fallback.
        if (!PoseOwnedByPump)
            ApplyHeadPose(state.HeadGazeX, state.HeadGazeY, state.HeadTrackingActive);
        RenderFieldIfDirty();
    }

    /// <summary>Round-10 one-clock fix: true while the MainWindow head-pose pump
    /// (CompositionTarget.Rendering → UpdateHeadGazeCenter, the single One-Euro clock) is
    /// subscribed. MainWindow refreshes it on every OnStateChanged, so ApplyState never
    /// injects the raw engine-tick pose while the pump runs (dual-writer tremble), and the
    /// engine path automatically becomes the fallback when the pump parks.</summary>
    internal bool PoseOwnedByPump { get; set; }

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

        // Item 3 (persistent-overlay sensing isolation): when rendering the baseline tier
        // steadily, snap the displayed mask color+alpha straight to the baseline tier target
        // — no cross-fade chase, no OFI flicker. The baseline tier is already debounced by the
        // engine's VimsTier hysteresis (live tier <= baseline ⇒ ResolveDisplayedTier floors to
        // the constant baseline), so the steady render holds rock-still. The first tick of a
        // spike (steady→live) just resumes the EMA cross-fade from the held value.
        if (!_displayedSeeded || state.PersistentBaselineSteady)
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

            // Parked-storm fix (2026-06-11): SNAP the cross-fade EMAs to their (steady) tier
            // targets once within half an 8-bit quantum. A geometric EMA never exactly reaches its
            // target, so for a STEADY tier the displayed values keep changing by ever-smaller sub-
            // code amounts forever — a residual re-dirt tail. The colour channels already round to
            // bytes (the colorKey gate stops them), but snapping kills the tail at the source so the
            // steady non-persistent render also reaches an EXACT fixed point. The snap distance is
            // below one 8-bit code on each channel (R/G/B in 0..255 units; alpha in 0..1 units), so
            // the rendered byte is identical to the un-snapped value — invisible by definition. A
            // genuine tier change moves the target far past the quantum ⇒ the EMA cross-fade resumes
            // (no visible step). PersistentBaselineSteady takes the snap-to-target branch above.
            const double colorQuantum = 0.5; // half an 8-bit code in 0..255 channel units
            double alphaQuantum = SenseDefaults.OverlayEightBitQuantum * 0.5;
            if (Math.Abs(tR        - _displayedR)     < colorQuantum) _displayedR     = tR;
            if (Math.Abs(tG        - _displayedG)     < colorQuantum) _displayedG     = tG;
            if (Math.Abs(tB        - _displayedB)     < colorQuantum) _displayedB     = tB;
            if (Math.Abs(tierAlpha - _displayedAlpha) < alphaQuantum) _displayedAlpha = tierAlpha;
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
    /// keeps "/grid off" truly off (curve PRESERVED byte-for-byte from 3762a42 — GridCeilingTests).
    ///
    /// <para>Kirby round-3 (items 1, 2, 2026-06-11): the grid now has a tier ONSET
    /// (<see cref="SenseDefaults.GridOnsetTier"/>) — it contributes NOTHING below it and ramps
    /// from there to full strength at L12 (it appeared too EARLY/STRONG as crisp vector lines),
    /// and the target is capped at <see cref="SenseDefaults.GridOpacityCap"/> so the on-screen
    /// line alpha stays clearly half-transparent. Both WRAP the GridCeiling curve; the curve is
    /// unchanged. Item 3: while <see cref="SenseState.PersistentBaselineSteady"/> is set the
    /// intensity term is the CONSTANT 0.55 floor (no smoothedIntensity) so the steady baseline
    /// never pulses on live OFI/pressure noise.</para></summary>
    private void ApplyGridIntensity(SenseState state)
    {
        // Round-10 (2026-06-11 audit findings 2-4): the EMA stays ALIVE on BOTH branches.
        // The steady branch used to skip it entirely, freezing _smoothedIntensity at whatever
        // the last live episode left (possibly saturated ~1.0 for minutes) — so every
        // steady→live edge re-entered the live path with a STALE intensity, stepping the grid
        // target by up to ~22 on-screen codes in one tick (the largest single pulse in the
        // pipeline). Now the EMA keeps tracking the live signals silently while steady renders
        // the constant floor, so the handoff is continuous — mirroring how ResolveMaskParams'
        // steady branch keeps seeding _displayedR/G/B/Alpha for the same reason.
        //
        // The retain is sized for the REAL StateChanged cadence (~66 ms, WgcCaptureIntervalMs)
        // restoring the designed τ≈1.85 s: the old literal 0.85 was written for a 300 ms tick
        // that never existed in this repo — the EMA ran at ~4.5× its design noise bandwidth.
        double retain = SenseDefaults.OverlayGridIntensityEmaRetain;
        _smoothedIntensity = retain * _smoothedIntensity
                           + (1.0 - retain) * ComputeAdaptiveIntensity(state);

        // Item 3 (round-3): while rendering the persistent baseline steadily, the RENDERED
        // intensity is the CONSTANT 0.55 floor — never the live EMA — so the grid + cell tint
        // hold rock-steady instead of pulsing on live sensing noise.
        double intensityTerm = state.PersistentBaselineSteady
            ? SenseDefaults.OverlaySteadyBaselineIntensity
            : 0.55 + (0.45 * _smoothedIntensity);

        int    tier         = Math.Clamp(state.VimsTier, 1, 12);
        // Item 1: grid tier-ONSET. Below GridOnsetTier the grid is fully invisible; from the
        // onset it ramps 0→1 reaching full strength at L12 (replaces the old (tier-1)/11 ramp
        // that faded in from L2 ≈ 5%). At onset 4: L1..L3 ⇒ 0, L12 ⇒ 1.
        double gridTierFraction = GridTierFraction(tier);
        double gv           = Math.Clamp(state.Shader.GridVisibility, 0.0, 1.0);
        double ceiling      = GridCeiling(gv);
        double computed     = Math.Clamp(gridTierFraction * ceiling * intensityTerm, 0.0, ceiling);
        // Item 2: opacity CAP — the on-screen line alpha (cap × stroke 130/255) stays clearly
        // half-transparent. min() with the ceiling-clamped computed target; "/grid off"
        // (ceiling 0 ⇒ computed 0) is preserved (min(0, cap) = 0).
        double target       = Math.Min(computed, SenseDefaults.GridOpacityCap);

        // 錨點格線 (anchor grid) user toggle (UI-RESHAPE quick-settings): force the grid LINES hidden
        // when the user turned the anchor grid off. The EMA + cell-fill above stay alive, so only the
        // rendered line opacity is gated — re-enabling resumes seamlessly on the next tick.
        if (!Configuration.UserPreferences.Current.AnchorGridEnabled) target = 0.0;

        // Round-10 hysteretic re-aim gate (audit finding: the only un-deadbanded noise
        // follower left). Re-aim the 280 ms animation ONLY when a discrete input changed
        // (tier / gv / steady — a deliberate or protection-relevant step, never noise) or the
        // continuous target drifted ≥ ~2 on-screen 8-bit codes from the last AIMED value.
        // Below that the previous aim stands — the difference is invisible by the project's
        // own 8-bit bar, and the lines stop breathing on OFI/pressure wander. The anchor
        // advances only on re-aim (a fixed-grid quantize is NOT enough: the target drifts
        // >1/256 over seconds and would still re-aim every crossing).
        bool discreteChanged = tier != _lastAimedGridTier
                            || gv   != _lastAimedGridGv
                            || state.PersistentBaselineSteady != _lastAimedGridSteady;
        if (double.IsNaN(_lastAimedGridTarget) || discreteChanged
            || Math.Abs(target - _lastAimedGridTarget) >= SenseDefaults.GridOpacityRetargetQuantum)
        {
            // Reuse the single pre-allocated DoubleAnimation; only mutate .To. From is left
            // unset → WPF animates from the current animated value (zero per-tick allocation).
            _gridOpacityAnim.To = target;
            GridCanvas.BeginAnimation(OpacityProperty, _gridOpacityAnim, HandoffBehavior.SnapshotAndReplace);
            _lastAimedGridTarget = target;
            _lastAimedGridTier   = tier;
            _lastAimedGridGv     = gv;
            _lastAimedGridSteady = state.PersistentBaselineSteady;
        }

        // Mirror the same target into the field's cell-fill tint strength (the 3762a42
        // modulation: the tint rode GridCanvas.Opacity). Field re-renders on real change.
        //
        // Parked-storm fix (2026-06-11): the GridCanvas.Opacity ANIMATION above still rides the
        // EXACT continuous target (the grid opacity is a WPF property, not an 8-bit buffer — never
        // quantized). But the field cell-tint strength feeds an 8-bit premultiplied buffer scaled
        // by CellFillAlpha (≈0.47) × window × gamma, so a sub-1/256 change in cellStrength cannot
        // flip any output byte. The non-persistent live path EMA-chases OFI/pressure noise, so the
        // raw target wobbles every engine tick; QUANTIZING cellStrength to the 1/256 grid drops that
        // sub-8-bit wobble — the gate only re-dirts the field when the quantized strength changes
        // (the parked non-persistent storm source). PersistentBaselineSteady already feeds a CONSTANT
        // intensity ⇒ a constant target ⇒ this quantizes to a fixed bucket and never re-dirts (it
        // already snapped). Invisible by definition (the buffer is 8-bit).
        //
        // Round-10: mirror the AIMED target (not the raw per-tick one) so the cell tint and the
        // vector lines move on the same re-aim events — strictly quieter, divergence < 2 codes.
        double qTarget = Quantize8Bit(_lastAimedGridTarget);
        if (double.IsNaN(_lastCellStrength) || qTarget != _lastCellStrength)
        {
            _cellStrength = qTarget;
            _lastCellStrength = qTarget;
            MarkFieldDirty();
        }
    }

    /// <summary>Snap a [0,1] contribution to the nearest 1/256 grid (one 8-bit code). A change
    /// below this cannot flip any byte in the premultiplied 8-bit field buffer, so quantizing an
    /// EMA output here before the re-dirt gate drops sub-8-bit wobble (live OFI/pressure noise)
    /// WITHOUT changing the rendered byte — the quantized value reaches the buffer identically to
    /// the un-quantized one (both round to the same code). Pure + internal for unit coverage.</summary>
    internal static double Quantize8Bit(double v)
        => Math.Round(v / SenseDefaults.OverlayEightBitQuantum) * SenseDefaults.OverlayEightBitQuantum;

    /// <summary>Kirby round-3 (item 1): grid tier-ONSET ramp. The grid contributes NOTHING
    /// below <see cref="SenseDefaults.GridOnsetTier"/>; from the onset it ramps
    /// <c>clamp((tier − onset) / (12 − onset), 0, 1)</c>, reaching full strength (1.0) at L12.
    /// Pure + internal so GridOnsetTests can pin tier 1..3 ⇒ 0, the monotonic ramp, and L12 ⇒ 1.</summary>
    internal static double GridTierFraction(int tier)
    {
        int onset = SenseDefaults.GridOnsetTier;
        if (tier < onset) return 0.0;
        double span = 12.0 - onset;
        if (span <= 0.0) return 1.0;               // degenerate guard (onset >= 12)
        return Math.Clamp((tier - onset) / span, 0.0, 1.0);
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
        //
        // Round-10 seam fix: this w/h IS the field's extent (same pre-layout fallback as
        // EnsureFieldBitmap) — record it so the window brush maps ABSOLUTELY onto it instead
        // of onto the overshoot-inflated canvas render bounds (~12 device px hole offset).
        _gridMaskExtentW = w;
        _gridMaskExtentH = h;
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

    /// <summary>Attach the CANVAS-MODEL grid OpacityMask (UVHS round-8 fix, 2026-06-11): an
    /// <see cref="ImageBrush"/> over the <see cref="_gridWindowBmp"/> reduced-res window bitmap
    /// (Stretch=Fill, bilinear). The bitmap is written in the SAME per-pixel pass as the field
    /// (see <see cref="ComputeFieldRows"/>) and carries the field's monotonic window value
    /// SATURATED to solid — clear hole where the user looks (alpha 0), smootherstep rise, then
    /// SOLID (alpha 255) everywhere beyond the feather end, including every corner at every gaze.
    /// So the grid lines are revealed wherever the canvas is present (everywhere outside the
    /// gaze hole) and hidden inside the hole — and the hole shares the field's geometry
    /// byte-for-byte. One reference-compare guards re-attachment. No-op until the bitmap exists
    /// (created by <see cref="EnsureFieldBitmap"/>).</summary>
    private void EnsureGridOpacityMask()
    {
        if (_gridWindowBmp is null) return;
        if (_gridWindowMaskBrush is null
            || !ReferenceEquals(_gridWindowMaskBrush.ImageSource, _gridWindowBmp))
        {
            _gridWindowMaskBrush = new ImageBrush(_gridWindowBmp)
            {
                Stretch       = Stretch.Fill,
                // Match the FieldImage upscale: bilinear (LowQuality) on a low-frequency window.
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            };
            RenderOptions.SetBitmapScalingMode(_gridWindowMaskBrush, BitmapScalingMode.LowQuality);
        }
        // Round-10 seam fix (2026-06-11 audit): map the window ABSOLUTELY onto the field's
        // extent. RelativeToBoundingBox mapped over the GridCanvas RENDER bounds, which
        // DrawGrid's overshoot lines (last line at ceil(w/step)·step ≥ w; Canvas never clips)
        // inflate past the screen — stretching the window so the grid hole sat ~12 device px
        // off the field hole (a static concentricity error on the seam Kirby checks). With an
        // absolute viewport the window spans exactly the field rect; the overshoot strip falls
        // outside the viewport (mask 0) and is off-screen anyway.
        if (_gridMaskExtentW > 0 && _gridMaskExtentH > 0)
        {
            _gridWindowMaskBrush.ViewportUnits = BrushMappingMode.Absolute;
            var extent = new Rect(0, 0, _gridMaskExtentW, _gridMaskExtentH);
            if (_gridWindowMaskBrush.Viewport != extent)
                _gridWindowMaskBrush.Viewport = extent;
        }
        if (!ReferenceEquals(GridCanvas.OpacityMask, _gridWindowMaskBrush))
            GridCanvas.OpacityMask = _gridWindowMaskBrush;
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
        // Kirby round-3 live ruling (2026-06-11, option A): the grid window radius is
        // FROZEN at BrushRadius — no corner-coverage rescaling. The old formula
        // (max(BrushRadius, farthestCorner × 1.10)) grew the window as the center
        // approached an edge/corner, which read as the grid "snapping/zooming" (吸附放大).
        // Design principle: NOTHING may jump or breathe — following the user's head is
        // the maximum allowed motion (the hole's CENTER still tracks the One-Euro pose).
        // Accepted trade-off: with the center at an extreme, the grid REVEAL may not
        // reach the farthest corner — the user is looking away from it, and the mask
        // dim (per-pixel corner-normalized in the field) still covers it fully.
        const double radius = BrushRadius;

        // Kirby round-3 (item 4.1): the dirty-mark / mutation threshold. The legacy
        // FieldEpsilon (0.0005) was sized for the STATIC-screen "cost ~zero" gate, but while
        // head-tracking is ACTIVE the One-Euro filter emits a continuous slow-settling stream
        // whose per-frame delta can dip just below 0.0005 — freezing the field mid-glide while
        // the grid brush (mirrored below at WPF's own tighter Point inequality) keeps moving,
        // a divergence the eye reads as micro-stutter / 彈跳. While active we recompute on ANY
        // real motion (a sub-texel threshold, ~1/40 px on the quarter-res field) so the field
        // follows the head faithfully; while inactive the coarser FieldEpsilon keeps a parked
        // overlay free. The field center/radius are kept in exact lock-step with the brush
        // mirror (one clock) — no quantized divergence.
        double motionEps = active ? FieldMotionEpsilon : FieldEpsilon;
        if (Math.Abs(cx - _fieldCx) > motionEps
            || Math.Abs(cy - _fieldCy) > motionEps
            || Math.Abs(radius - _fieldRadius) > motionEps)
        {
            _fieldCx = cx; _fieldCy = cy; _fieldRadius = radius;
            MarkPoseDirty();   // Fix 2: pose-only move ⇒ dirty-band recompute (not full buffer)
        }

        // UVHS round-8 fix (2026-06-11): the M5.1-A brush mirror is GONE. There is no
        // RadialGradientBrush to mirror — the grid's transparency hole is now the
        // _gridWindowBmp written in the SAME RenderFieldIfDirty pass from the SAME (cx,cy).
        // The hole rides the head via the bitmap (one clock, no divergent geometry); the grid
        // LINES stay world-locked at pixel origin. The window buffer is updated in the field
        // recompute — see ComputeFieldRows / RenderFieldIfDirty.
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

        double farDist = LiveCornerDenom(cx, cy, aspect);
        if (farDist <= 1e-9) return 0.0;
        return dist / farDist;
    }

    /// <summary>The raw CornerNormR denominator from the LIVE center: the aspect-corrected
    /// distance from <paramref name="cx"/>/<paramref name="cy"/> to its farthest corner of the
    /// [0,1]² screen. This is the value that GROWS as the center approaches an edge/corner
    /// (≈2× at a full corner vs. rest), enlarging the field's clear hole + feather band — the
    /// "corner-approach zoom" Kirby's round-9 ruling freezes out entirely (round-4 had halved
    /// it; the canvas-model unification put the grid window on this same geometry and the zoom
    /// percept returned on the grid lines). Depends ONLY on (cx,cy,aspect),
    /// NOT on the pixel, so the field loop hoists it out (today's per-pixel CornerNormR
    /// recomputed it 324k×/frame — a hidden per-pixel sqrt). Pure + internal for unit cover.</summary>
    internal static double LiveCornerDenom(double cx, double cy, double aspect)
    {
        double farX = Math.Max(cx, 1.0 - cx) * aspect;
        double farY = Math.Max(cy, 1.0 - cy);
        return Math.Sqrt(farX * farX + farY * farY);
    }

    /// <summary>The CornerNormR denominator from the REST center (0.5, 0.5) under the SAME
    /// aspect correction — the field size at rest. Pure function of aspect; the renderer
    /// computes it once per field-size change (NOT per pixel). At a 16:9 screen this is the
    /// half-diagonal of the aspect-corrected unit square.</summary>
    internal static double RestCornerDenom(double aspect)
        => LiveCornerDenom(0.5, 0.5, aspect);

    /// <summary>Kirby round-4 ruling (2026-06-11): the corner-growth-DAMPED CornerNormR
    /// denominator. Blends the live denominator toward the rest denominator so the field's
    /// clear hole + feather band grow by only <paramref name="damping"/>× the old amount as the
    /// center approaches an edge/corner:
    /// <c>effectiveDenom = restDenom + (liveDenom − restDenom) · damping</c>.
    /// <list type="bullet">
    ///   <item>damping = 1.0 ⇒ effectiveDenom == liveDenom (today's full growth, unchanged);</item>
    ///   <item>damping = 0.5 ⇒ HALF the growth (the round-4 default);</item>
    ///   <item>damping = 0.0 ⇒ ZERO growth (the round-9 default): effectiveDenom == restDenom
    ///         always — the hole/feather SIZE is frozen, only the CENTER head-follows;</item>
    ///   <item>at the rest center liveDenom == restDenom ⇒ effectiveDenom == restDenom for
    ///         ANY damping ⇒ BYTE-IDENTICAL to today (the damping is inert when not moving).</item>
    /// </list>
    /// A SMALLER effective denom ⇒ a LARGER normalized r for every pixel ⇒ the farthest screen
    /// corner saturates the feather to full dim even harder, so protection is never weakened.
    /// Pure + internal for unit cover.</summary>
    internal static double EffectiveCornerDenom(double cx, double cy, double aspect, double damping)
    {
        double restDenom = RestCornerDenom(aspect);
        double liveDenom = LiveCornerDenom(cx, cy, aspect);
        return restDenom + (liveDenom - restDenom) * damping;
    }

    /// <summary>Continuous SMOOTHERSTEP falloff, 0 inside <paramref name="featherStart"/>,
    /// ramping to <paramref name="peakAlpha"/> by <paramref name="featherEnd"/> (0.95),
    /// then held. Preserves the prior 0.35→0.95 envelope endpoints + peak output exactly;
    /// the band START is the D-2 FeatherWidth wiring. Output is the field alpha at
    /// corner-normalized coordinate r.
    ///
    /// <para><b>Banding round-5 (2026-06-11, plan-sanctioned):</b> the falloff was upgraded from
    /// Hermite smoothstep s(t)=t²(3−2t) to Perlin SMOOTHERSTEP s(t)=6t⁵−15t⁴+10t³. Smootherstep
    /// has a continuous SECOND derivative at both endpoints (smoothstep's 2nd derivative jumps),
    /// which removes the Mach-band edge salience — the faint crawling contour lines Kirby reported
    /// at shallow tiers. SAME endpoints (0 at featherStart, peak at featherEnd) and SAME peak
    /// output, so the protection envelope is unchanged; only the interior shaping is gentler. The
    /// plan (§A2) names smootherstep as the sanctioned optional banding mitigation. The grid's
    /// CANVAS-MODEL OpacityMask window (the <see cref="_gridWindowBmp"/> written from the SAME
    /// <see cref="WindowLutLookup"/> in <see cref="ComputeFieldRows"/>) uses the SAME curve so the
    /// field FOV dim and the grid window stay seam-aligned BYTE-FOR-BYTE.</para></summary>
    internal static double ResolveFieldAlpha(double r, double peakAlpha, double featherStart, double featherEnd)
    {
        if (r <= featherStart) return 0.0;
        if (r >= featherEnd)   return peakAlpha;
        double t = (r - featherStart) / (featherEnd - featherStart);
        double s = Smootherstep(t); // smootherstep s(t)=6t⁵−15t⁴+10t³ (continuous 2nd derivative)
        return peakAlpha * s;
    }

    /// <summary>Perlin SMOOTHERSTEP s(t)=6t⁵−15t⁴+10t³ on [0,1] (continuous 1st AND 2nd
    /// derivative at both endpoints — the Mach-band killer). Endpoints identical to smoothstep
    /// (s(0)=0, s(1)=1, s(0.5)=0.5); the interior is flatter near the ends and steeper at the
    /// middle. Pure + internal so the falloff tests can pin the curve, monotonicity and
    /// endpoint identity, and the grid-window bitmap shares the exact same shaping.</summary>
    internal static double Smootherstep(double t)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
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
    /// <summary>HOTFIX 2026-06-20 (Kirby): the feather-band OUTER end, pulled in from the old 0.95 to NARROW
    /// the smoothstep band → a SHARP clean→dim edge. The wide 0.35→0.95 band quantized into concentric
    /// contour "lines" that swept with the head-tracked hole and smeared into 殘影 (ghosting) — the artifact
    /// testers reported. Beyond this radius the field + the grid-window saturate to full dim (so the
    /// periphery dims a touch more firmly — the deliberate trade for the sharp edge). UVHS-tunable:
    /// LOWER = sharper edge + firmer periphery, HIGHER (toward 0.95) = softer/wider.</summary>
    internal const double FeatherEnd = 0.55;

    internal static double ResolveFeatherStart(double featherWidth)
    {
        const double baseStart = 0.35;
        const double end       = FeatherEnd;
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

    // ── Fix 2: per-pixel transcendental elimination (Kirby round-4: kill the lag) ─
    //
    // ComputeField ran ~324k px/frame at up to 165 Hz single-threaded, calling Math.Pow
    // TWICE per pixel (GammaShape on the mask term AND the cell-tint term) and Math.Sqrt
    // once per pixel (inside CornerNormR). At 720×450 that profiled as the dominant cost.
    // Replace the per-pixel transcendentals with table lookups built ONCE (or rebuilt only
    // when their parameter changes) + linear interpolation, validated to < 1/512 (half an
    // 8-bit step) max abs error against the exact math so the rendered field cannot drift
    // visibly — pinned by GammaLut_MatchesExactMath_WithinHalfAStep / Window LUT tests.

    /// <summary>Gamma LUT resolution. 2048 entries + linear interp over [0,1] keeps the worst
    /// interpolation error of x^2.2 well under 1/512 across the whole domain (the curvature is
    /// gentle for gamma=2.2; the near-zero region — where the eye is most sensitive — is the
    /// steepest and still resolves to &lt; 1/512 at this density).</summary>
    private const int GammaLutSize = 2048;
    private static readonly double _gammaLutGamma = SenseDefaults.OverlayFieldGamma;
    private static readonly float[] _gammaLut = BuildGammaLut(GammaLutSize, _gammaLutGamma);

    /// <summary>Build a <paramref name="size"/>+1-entry gamma LUT: <c>lut[i] = (i/size)^gamma</c>
    /// over [0,1]. The extra last entry (index == size) lets <see cref="GammaLutLookup"/> index
    /// the upper bracket without a bounds branch. Built once at class load (gamma is fixed).</summary>
    internal static float[] BuildGammaLut(int size, double gamma)
    {
        var lut = new float[size + 1];
        for (int i = 0; i <= size; i++)
            lut[i] = (float)Math.Pow((double)i / size, gamma);
        return lut;
    }

    /// <summary>Linear-interpolated gamma LUT lookup, the per-pixel replacement for
    /// <see cref="GammaShape"/> (Math.Pow). <paramref name="a01"/> is clamped to [0,1]; the
    /// fractional table position is split into an index + lerp weight. Pure + internal so the
    /// accuracy test can sweep it against the exact Math.Pow curve.</summary>
    internal static double GammaLutLookup(double a01)
    {
        if (a01 <= 0.0) return 0.0;
        if (a01 >= 1.0) return 1.0;
        double pos = a01 * GammaLutSize;
        int idx = (int)pos;
        double frac = pos - idx;
        float lo = _gammaLut[idx];
        float hi = _gammaLut[idx + 1];
        return lo + (hi - lo) * frac;
    }

    /// <summary>Window LUT resolution (indexed by r-SQUARED). The window only changes shape
    /// across the feather band [featherStart, 0.95]; outside it the value is flat (0 below,
    /// 1.0 at/above 0.95). Indexing by r² removes the per-pixel sqrt entirely:
    /// <c>r² = d2 · invDenom²</c>. 2048 entries over r² ∈ [0, 1] resolves the smoothstep ramp
    /// to &lt; 1/512; r² beyond 1 clamps to the saturated last entry (window == 1.0).</summary>
    private const int WindowLutSize = 2048;
    private float[] _windowLut = new float[WindowLutSize + 1];
    private double _windowLutFeatherStart = double.NaN;   // the featherStart the LUT was built for

    /// <summary>(Re)build the window LUT for the current <paramref name="featherStart"/> (the
    /// ramp END is pinned at 0.95). Indexed by r² ∈ [0,1]: <c>lut[i] = ResolveFieldAlpha(
    /// sqrt(i/size), 1.0, featherStart, 0.95)</c> — the shared inverse-feather window at peak 1.0
    /// (0 in the clean centre, smoothstep up to 1.0 by r=0.95, held at 1.0 to the corner). Only
    /// rebuilt when featherStart changes (FeatherWidth knob) — NOT per pose move, NOT per pixel.
    /// Internal so the accuracy test can rebuild + sweep it.</summary>
    internal static void BuildWindowLut(float[] lut, int size, double featherStart, double featherEnd)
    {
        for (int i = 0; i <= size; i++)
        {
            double r2 = (double)i / size;
            double r  = Math.Sqrt(r2);
            lut[i] = (float)ResolveFieldAlpha(r, 1.0, featherStart, featherEnd);
        }
    }

    /// <summary>Linear-interpolated window LUT lookup by r-SQUARED (no per-pixel sqrt).
    /// r² ≥ 1 clamps to the saturated last entry (the window holds at 1.0 past r=0.95, so the
    /// far corner — including the post-damping r &gt; 1 corner — reads full window). Pure +
    /// internal for the accuracy sweep.</summary>
    internal static double WindowLutLookup(float[] lut, int size, double r2)
    {
        if (r2 <= 0.0) return lut[0];
        if (r2 >= 1.0) return lut[size];
        double pos = r2 * size;
        int idx = (int)pos;
        double frac = pos - idx;
        float lo = lut[idx];
        float hi = lut[idx + 1];
        return lo + (hi - lo) * frac;
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

    /// <summary>Fix 2: the LUT-backed twin of <see cref="ComposeFieldPixel"/> — identical math
    /// with both <see cref="GammaShape"/> (Math.Pow) calls replaced by the linear-interpolated
    /// <see cref="GammaLutLookup"/>. This is exactly what the per-pixel hot loop computes (the
    /// loop inlines these terms). The accuracy test sweeps it against ComposeFieldPixel and pins
    /// max abs byte error ≤ 1 (the &lt; 1/512 LUT error cannot flip a rounded byte by more than
    /// one code), so the LUT can never drift the rendered field visibly.</summary>
    internal static (byte B, byte G, byte R, byte A) ComposeFieldPixelLut(
        double window, byte maskR, byte maskG, byte maskB, double maskPeak, double cellStrength)
    {
        double cellA = GammaLutLookup(Math.Clamp(CellFillAlpha * cellStrength * window, 0.0, 1.0));
        double maskA = GammaLutLookup(Math.Clamp(window * maskPeak, 0.0, 1.0));

        double inv  = 1.0 - maskA;
        double outA = maskA + cellA * inv;

        double mR = maskR / 255.0, mG = maskG / 255.0, mB = maskB / 255.0;
        double cR = CellFillR / 255.0, cG = CellFillG / 255.0, cB = CellFillB / 255.0;

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
    /// axis and the one Image bilinear-upscales it (Stretch=Fill). Idempotent.
    ///
    /// <para><b>Banding round-5 (2026-06-11): DEVICE-PIXEL field resolution.</b> The field is
    /// now sized from PHYSICAL device pixels (ActualWidth/Height × CompositionTarget
    /// .TransformToDevice.M11/M22 — the SAME DPI dance DrawGrid does), NOT from WPF DIPs.
    /// At Kirby's 200% scaling DIP sizing gave 360×225 (1/8 PHYSICAL resolution); device-pixel
    /// sizing gives 720×450 — 4× the pixels, so every banding contour's on-screen width HALVES
    /// and the gradient sample density DOUBLES. On a 100%-scaling machine M11==M22==1.0 so this
    /// is BYTE-IDENTICAL to the prior DIP sizing (unchanged). The WriteableBitmap is still
    /// authored at 96 DPI and the Image rect stays in DIPs (Stretch=Fill); WPF's bilinear maps
    /// the device-pixel bitmap onto the DIP rect 1:1-ish — the bitmap now resolves to ~1 device
    /// texel per OverlayFieldDownscale device pixels (no double-scaling blur: the field's own
    /// upscale is exactly OverlayFieldDownscale, the prior extra ×DPI stretch is gone).</para></summary>
    private void EnsureFieldBitmap()
    {
        double sw = ActualWidth, sh = ActualHeight;
        // DPI scale (device pixels per DIP). Mirrors DrawGrid's TransformToDevice dance so the
        // field and the grid share one notion of physical resolution. Before a PresentationSource
        // exists (pre-layout / unshown STA test window) M11/M22 fall back to 1.0.
        var ct = PresentationSource.FromVisual(this)?.CompositionTarget;
        double dpiX = ct?.TransformToDevice.M11 ?? 1.0;
        double dpiY = ct?.TransformToDevice.M22 ?? 1.0;
        if (dpiX <= 0) dpiX = 1.0;
        if (dpiY <= 0) dpiY = 1.0;

        if (sw <= 0 || sh <= 0)
        {
            // Before layout: fall back to the primary screen so a pre-layout ApplyState
            // (engine tick can beat Loaded) still produces a valid field. PrimaryScreenWidth is
            // in DIPs; the same DPI multiply below maps it to physical pixels (mirrors DrawGrid's
            // physicalWidth = PrimaryScreenWidth × dpiX pre-layout fallback).
            sw = SystemParameters.PrimaryScreenWidth;
            sh = SystemParameters.PrimaryScreenHeight;
            if (sw <= 0 || sh <= 0) return;
        }

        // PHYSICAL device pixels: DIP extent × DPI scale.
        double physW = sw * dpiX;
        double physH = sh * dpiY;

        // The size-change gate keys on the PHYSICAL extent (so a DPI change alone, with ActualWidth
        // unchanged in DIPs, still re-allocates at the new device resolution).
        if (Math.Abs(physW - _lastScreenW) < 0.5 && Math.Abs(physH - _lastScreenH) < 0.5 && _fieldBmp is not null)
            return;

        _lastScreenW = physW; _lastScreenH = physH;
        int ds = Math.Max(1, ResolveFieldDownscale(physW, physH));
        _fieldW = Math.Max(1, (int)Math.Ceiling(physW / ds));
        _fieldH = Math.Max(1, (int)Math.Ceiling(physH / ds));

        _fieldBmp = new WriteableBitmap(_fieldW, _fieldH, 96, 96, PixelFormats.Pbgra32, null);
        _fieldBuf = new byte[_fieldW * _fieldH * 4];
        FieldImage.Source = _fieldBmp;

        // CANVAS-MODEL grid window bitmap (UVHS round-8 fix): same reduced-res dimensions,
        // Pbgra32 grayscale-alpha. WPF's managed PixelFormats has no A8 (the spec's fallback),
        // so each pixel is premultiplied black with the window in the ALPHA byte: (B,G,R,A) =
        // (0,0,0,window). An OpacityMask consumes ONLY the alpha, so that byte IS the window
        // value: 0 = the gaze hole (grid hidden), 255 = the canvas (grid present) — solid
        // everywhere outside the hole, including every corner at every gaze. Written in the SAME
        // ComputeFieldRows pass (the window value is already computed for the field). The mask
        // brush is (re)bound to this fresh bitmap on the next EnsureGridOpacityMask.
        _gridWindowBmp = new WriteableBitmap(_fieldW, _fieldH, 96, 96, PixelFormats.Pbgra32, null);
        _gridWindowBuf = new byte[_fieldW * _fieldH * 4];
        EnsureGridOpacityMask();   // (re)bind the ImageBrush mask to the new window bitmap

        MarkFieldDirty();
    }

    /// <summary>Banding round-5 (2026-06-11): resolve the field downscale for a PHYSICAL screen
    /// extent. The base factor is <see cref="SenseDefaults.OverlayFieldDownscale"/> (4 ⇒ 720×450
    /// at 2880×1800 physical — 4× the pre-fix DIP-sized 360×225). For VERY LARGE physical screens
    /// the band-path recompute would grow past the live-perf budget, so the factor steps UP once
    /// the field would otherwise exceed <see cref="SenseDefaults.OverlayFieldMaxFieldPixels"/>
    /// device pixels — keeping the recompute bounded while every common 1080p–4K panel keeps the
    /// full base downscale (finer gradient, narrower bands). Pure given the extent + the two
    /// SenseDefaults knobs; internal so a unit test can pin the adaptive step.</summary>
    internal static int ResolveFieldDownscale(double physW, double physH)
    {
        int ds = Math.Max(1, SenseDefaults.OverlayFieldDownscale);
        double cap = SenseDefaults.OverlayFieldMaxFieldPixels;
        // Step the downscale up until the field fits under the pixel cap (never below the base).
        while (((physW / ds) * (physH / ds)) > cap)
            ds++;
        return ds;
    }

    /// <summary>Mark the WHOLE field for recompute (a non-pose input changed: color / peak /
    /// feather / cell-strength / active / size — these touch every pixel). The next render
    /// recomputes the entire buffer.</summary>
    private void MarkFieldDirty() { _fieldDirty = true; _fieldFullDirty = true; }

    /// <summary>Fix 2 (dirty-band): mark ONLY the pose as moved (cx/cy/radius). The next render
    /// may recompute just the union band of the old+new feather annuli instead of the whole buffer.
    ///
    /// <para>Parked-storm fix (2026-06-11): a pose move sets NO dirty FLAG — the render decision is
    /// made directly from the texel-quantized pose bucket in <see cref="RenderFieldIfDirty"/>. The
    /// field center + grid-window mirror still track the head on the locked sub-texel epsilon (set
    /// in ApplyHeadPose), but a sub-texel pose creep produces a byte-identical field, so it must NOT
    /// force a recompute (the ~165 Hz pump's creep was the dominant ~75/s storm). Kept as a named
    /// no-op so the two call sites still read as an explicit "pose changed" intent.</para></summary>
    private void MarkPoseDirty() { /* pose render is gated on the texel bucket, not a flag */ }

    /// <summary>Parked-storm render gate threshold: the smallest normalized pose move that can
    /// change the rendered field — 1/<see cref="SenseDefaults.OverlayFieldRenderTexelFraction"/> of
    /// ONE field texel, measured on the LARGER field axis (conservative on both). A move below this
    /// upscales to a byte-identical low-frequency field, so the render skips it. Measured as an
    /// ABSOLUTE delta from the last RENDERED pose (not a fixed grid), so it is HYSTERETIC: the
    /// threshold only advances when a render actually happens, immune to dither straddling a fixed
    /// bucket boundary. Pure given _fieldW/H. At a 1×1 field (never ships) the texel is the whole
    /// axis ⇒ the threshold is huge ⇒ the pose gate never forces a recompute (harmless: flat pixel).</summary>
    private double PoseRenderQuantum()
    {
        int maxDim = Math.Max(1, Math.Max(_fieldW, _fieldH));
        return 1.0 / (maxDim * SenseDefaults.OverlayFieldRenderTexelFraction);
    }

    /// <summary>Recompute + present the field ONLY when an input moved beyond epsilon (a
    /// static screen costs ~zero). Times the recompute for the live perf gate and flushes
    /// rolling avg/p99 stats to wgc_diag once a minute while the overlay is active.
    ///
    /// <para>Fix 2 (Kirby round-4, kill the lag): when ONLY the pose moved (no color / peak /
    /// feather / cell-strength / active / size change), only the union band of rows touched by
    /// the old AND new feather discs is recomputed (the rest of the buffer is identical — inside
    /// the clean centre window=0 and deep in the saturated periphery window=1.0, neither of which
    /// depends on the exact pose). Any non-pose change forces a full recompute (_fieldFullDirty).</para></summary>
    private void RenderFieldIfDirty()
    {
        if (_fieldBmp is null || _fieldBuf is null) return;

        // Parked-storm fix (2026-06-11): split "a non-pose input changed" (color / peak /
        // feather / cell-strength / active / size — touches every pixel) from "the pose moved".
        // A non-pose change ALWAYS recomputes. A pose-only move recomputes ONLY when it crossed a
        // RENDER-VISIBLE texel quantum since the last RENDERED pose — the field center + grid-window
        // mirror still TRACK the head on the locked sub-texel OverlayFieldMotionEpsilon (set in
        // ApplyHeadPose), but the ~165 Hz pump's sub-texel One-Euro creep produces a byte-identical
        // field, so re-rendering it is pure waste (the ~75/s wgc_diag storm). The quantum keeps the
        // rendered field within 1 LSB end-to-end (pinned by OverlayHeadPoseTests
        // .ParkedPump_SubTexelPose_RendersWithinOneByte + the parked-zero regressions in
        // OverlaySmoothnessTests), so this is invisible — NOT a visible step (Kirby's motion
        // principle holds: the grid hole keeps gliding; only the redundant field re-upload is skipped).
        bool nonPoseDirty = _fieldDirty || _fieldFullDirty;   // both set by MarkFieldDirty
        bool activeFlip   = _lastFieldActive != _maskActive;
        double q = PoseRenderQuantum();
        bool poseRenderMoved = double.IsNaN(_lastCx)
            || Math.Abs(_fieldCx - _lastCx) >= q
            || Math.Abs(_fieldCy - _lastCy) >= q
            || Math.Abs(_fieldRadius - _lastRadius) >= q;
        bool inputsMoved = nonPoseDirty || activeFlip || poseRenderMoved;

        if (!inputsMoved)
        {
            // Nothing the rendered buffer depends on changed — drop the (possibly pose-set) dirty
            // flag so a parked overlay costs ~zero. _lastCx/_lastCy are left at the last RENDERED
            // pose (NOT advanced to the sub-texel creep) so the bucket delta accumulates until it
            // genuinely crosses a texel — the field can never freeze permanently mid-glide.
            _fieldDirty = false;
            return;
        }

        EnsureWindowLut();   // rebuild the r²-indexed window LUT iff featherStart changed

        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        // Full recompute when a non-pose input changed (touches every pixel), the mask
        // active-state flipped, or on the very first render; otherwise a pose-only move
        // recomputes just the dirty band. (The active flip already implies _fieldFullDirty via
        // ResolveMaskParams; included here explicitly so the band path can NEVER miss it.)
        bool full = _fieldFullDirty || double.IsNaN(_lastCx) || _lastFieldActive != _maskActive;
        int y0, y1;
        if (full)
        {
            y0 = 0; y1 = _fieldH;
            ComputeFieldRows(0, _fieldH);
        }
        else
        {
            (y0, y1) = DirtyBandRows(_lastCx, _lastCy, _fieldCx, _fieldCy);
            // Fix 3: ComputeFieldRows itself chooses single-thread (tiny band) vs capped-parallel
            // (large band) by pixel count — a small pose nudge runs inline (starvation-immune),
            // a near-full-height band uses the capped Parallel.For (never starves the whole pool).
            if (y1 > y0) ComputeFieldRows(y0, y1);
        }

        // Upload only the rows that changed. WritePixels takes the source buffer + a source
        // rect; pointing it at the band sub-rect skips re-uploading the static rows.
        if (y1 > y0)
        {
            int rowBytes = _fieldW * 4;
            _fieldBmp.WritePixels(
                new Int32Rect(0, y0, _fieldW, y1 - y0),
                _fieldBuf, rowBytes, /*offset*/ y0 * rowBytes);

            // CANVAS-MODEL grid window (UVHS round-8 fix): upload the SAME band rows of the
            // Pbgra32 window buffer in lock-step (one clock — the band rows ComputeFieldRows wrote
            // both buffers for). Same 4-byte stride/offset as the field. The dirty-band path
            // updates exactly the window rows the field band updated, so the window buffer stays
            // byte-equal to a full window recompute too (pinned by the band-equality test).
            if (_writeGridWindowForPerfDelta && _gridWindowBmp is not null && _gridWindowBuf is not null)
            {
                _gridWindowBmp.WritePixels(
                    new Int32Rect(0, y0, _fieldW, y1 - y0),
                    _gridWindowBuf, rowBytes, /*offset*/ y0 * rowBytes);
            }
        }
        _totalRecomputes++;   // Kirby round-3 (item 4.5): monotonic recompute/WritePixels count

        long endTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double ms = (endTicks - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        RecordRecompute(ms);

        _lastCx = _fieldCx; _lastCy = _fieldCy; _lastRadius = _fieldRadius;
        _lastFieldActive = _maskActive;
        _fieldDirty = false;
        _fieldFullDirty = false;
    }

    /// <summary>(Re)build the r²-indexed window LUT iff <see cref="_featherStart"/> changed since
    /// the last build (the window shape depends ONLY on featherStart + the pinned 0.95 end — not
    /// on pose, color, peak or cell-strength). So this is a no-op on every pose-driven frame.</summary>
    private void EnsureWindowLut()
    {
        if (_windowLutFeatherStart == _featherStart) return;
        BuildWindowLut(_windowLut, WindowLutSize, _featherStart, FeatherEnd);
        _windowLutFeatherStart = _featherStart;
    }

    /// <summary>Fix 2 (dirty-band): the union row range [y0, y1) of the feather discs at the OLD
    /// and NEW pose — a conservative superset of every row whose pixel could have changed. Rows
    /// outside it are entirely inside the clean centre (window 0) or entirely in the saturated
    /// periphery (window 1.0) for BOTH poses, so their pixels are pose-invariant. The vertical
    /// half-extent of the <c>r ≤ featherEnd</c> disc is <c>featherEnd · effectiveDenom</c> in
    /// normalized-py units (py / dy are NOT aspect-scaled), padded by one row for the half-texel
    /// sample offset + interpolation slack.</summary>
    private (int y0, int y1) DirtyBandRows(double oldCx, double oldCy, double newCx, double newCy)
    {
        int h = _fieldH;
        double aspect = (double)_fieldW / h;
        double damping = SenseDefaults.OverlayFieldCornerGrowthDamping;
        const double featherEnd = 0.95;

        double oldDenom = EffectiveCornerDenom(oldCx, oldCy, aspect, damping);
        double newDenom = EffectiveCornerDenom(newCx, newCy, aspect, damping);
        double oldReach = featherEnd * oldDenom;   // vertical half-extent at old pose (py units)
        double newReach = featherEnd * newDenom;

        double topN    = Math.Min(oldCy - oldReach, newCy - newReach);
        double bottomN = Math.Max(oldCy + oldReach, newCy + newReach);

        // Normalized [0,1] → pixel rows; pad 1 row each side for the half-texel sample centre.
        int y0 = (int)Math.Floor(topN * h) - 1;
        int y1 = (int)Math.Ceiling(bottomN * h) + 1;
        if (y0 < 0) y0 = 0;
        if (y1 > h) y1 = h;
        if (y1 < y0) y1 = y0;
        return (y0, y1);
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
    /// <summary>Fix 2 (Kirby round-4): fill rows [<paramref name="yStart"/>, <paramref name="yEnd"/>)
    /// of <see cref="_fieldBuf"/>. The full render passes [0, h); the dirty-band path passes only
    /// the changed rows. Every per-pixel transcendental is gone:
    /// <list type="bullet">
    ///   <item>the per-pixel <c>Math.Sqrt</c> inside CornerNormR is removed — the damped
    ///         denominator is computed ONCE for the frame and the window is looked up by
    ///         <c>r² = d2 · invDenom²</c> (the r²-indexed window LUT);</item>
    ///   <item>both per-pixel <c>Math.Pow</c> gamma calls are replaced by the gamma LUT
    ///         (linear-interpolated, &lt; 1/512 error — see ComposeFieldPixelLut);</item>
    ///   <item>the row loop is a <c>Parallel.For</c> — rows write disjoint buffer slices, no
    ///         synchronization. Loop-invariant locals are captured once outside.</item>
    /// </list>
    /// Fix 1 (corner-growth damping) is applied via <see cref="EffectiveCornerDenom"/>: the
    /// denominator is blended toward the rest-center denominator so the hole/feather grows by
    /// only OverlayFieldCornerGrowthDamping× the old amount. At rest the live and rest denoms are
    /// equal ⇒ byte-identical to today.</summary>
    /// <summary>Recompute rows [<paramref name="yStart"/>, <paramref name="yEnd"/>) of the field
    /// buffer. Fix 3 (parked-storm, 2026-06-11): the dispatch is chosen by the band PIXEL COUNT.
    /// <list type="bullet">
    ///   <item>A SMALL band (&lt; <see cref="SenseDefaults.OverlayFieldSingleThreadPixelThreshold"/>
    ///         px — a tiny pose nudge whose feather annulus does not span the field) runs
    ///         single-threaded on the UI thread: cheaper than fan-out AND immune to thread-pool
    ///         starvation when a fullscreen game saturates every core.</item>
    ///   <item>A LARGE band or a full recompute uses <c>Parallel.For</c> with
    ///         <c>MaxDegreeOfParallelism = </c><see cref="SenseDefaults.OverlayFieldMaxParallelism"/>
    ///         (= ProcessorCount − 2) so the game can never starve the WHOLE pool from under the
    ///         render — the cap, not the thread count, is the starvation fix.</item>
    /// </list>
    /// For this field geometry the feather disc reaches ±~0.66 normalized, so most pose moves dirty
    /// a near-full-height band and correctly take the capped-parallel path; only genuinely tiny
    /// bands go single-threaded. Output is byte-identical either way
    /// (Fix2_DirtyBand_MatchesFullRecompute_ByteForByte).</summary>
    private void ComputeFieldRows(int yStart, int yEnd)
    {
        int w = _fieldW, h = _fieldH;
        double aspect = (double)w / h;
        double cx = _fieldCx, cy = _fieldCy;
        double peak = _maskPeakAlpha;
        double cellStrength = _cellStrength;
        var buf = _fieldBuf!;
        var winBuf = _gridWindowBuf!;   // Pbgra32 grid-window (4 bytes/px, window in the ALPHA byte;
                                        // WPF WriteableBitmap has no managed A8) — written in the same pass
        var winLut = _windowLut;
        const int winSize = WindowLutSize;

        byte mr = _maskR, mg = _maskG, mb = _maskB;

        // Fix 1: the corner-growth-damped denominator — computed ONCE per frame (depends only on
        // center + aspect, never on the pixel). 1/denom² lets the inner loop turn d2 into r²
        // with a single multiply (no per-pixel sqrt). A degenerate denom (≈0 only at a 1×1
        // field) falls back to a large FINITE multiplier ⇒ window saturates everywhere without a
        // 0×∞ NaN (safe: full dim in the periphery, never weaker — and a 1×1 field never ships).
        double denom = EffectiveCornerDenom(cx, cy, aspect, SenseDefaults.OverlayFieldCornerGrowthDamping);
        double invDenomSq = denom > 1e-9 ? 1.0 / (denom * denom) : 1e18;

        // Perf-DELTA seam ONLY (default true in production): when false, skip the grid-window
        // byte write so the perf harness can isolate the second-buffer-write cost. Captured ONCE
        // as a local here (hoisted out of the inner loop), so in production the JIT sees a loop-
        // invariant `true` and the branch costs nothing per pixel — the window write is always on.
        bool writeWindow = _writeGridWindowForPerfDelta;

        // The per-row work is identical regardless of dispatch; capture it once so the single-
        // threaded band path and the capped-parallel full path share one body (byte-identical
        // output — pinned by Fix2_DirtyBand_MatchesFullRecompute_ByteForByte).
        void Row(int y)
        {
            double py = (y + 0.5) / h;
            double dy = py - cy;
            double dy2 = dy * dy;
            int row = y * w * 4;
            int winRow = y * w * 4;              // Pbgra32 grid-window: 4 bytes/px (alpha = window)
            for (int x = 0; x < w; x++)
            {
                double px = (x + 0.5) / w;
                double dx = (px - cx) * aspect;       // aspect-corrected so the falloff is round
                double d2 = dx * dx + dy2;            // squared aspect-corrected distance
                double r2 = d2 * invDenomSq;          // == (dist/denom)² — no per-pixel sqrt

                // The shared radial window (peak 1.0) by r² lookup: 0 in the clean centre,
                // smoothstep to 1.0 by r=0.95, held at 1.0 to the corner. r² ≥ 1 saturates.
                double window = WindowLutLookup(winLut, winSize, r2);

                // CANVAS-MODEL grid-window pixel (UVHS round-8 fix): the SAME monotonic window,
                // already saturated to 1.0 beyond the feather end by the LUT (r² ≥ 1 ⇒ last
                // entry), encoded into a Pbgra32 premultiplied-black pixel whose ALPHA is the
                // window. 0 = the gaze hole (grid hidden), 255 = the canvas (grid present) — solid
                // everywhere outside the hole, including every corner at every gaze. The
                // OpacityMask consumes only the alpha; B/G/R are 0 (premultiplied black, so the
                // value is valid premultiplied data at every alpha). ResolveFieldAlpha/
                // ComposeFieldPixel are NOT touched: the grid window is a SEPARATE consumer of the
                // exact same per-pixel window value, so it shares the field's
                // circle/damping/featherStart byte-for-byte.
                if (writeWindow)
                {
                    byte winA = (byte)(window * 255.0 + 0.5); // round; window ∈ [0,1]
                    int wi = winRow + x * 4;
                    winBuf[wi + 0] = 0;    // B (premultiplied black)
                    winBuf[wi + 1] = 0;    // G
                    winBuf[wi + 2] = 0;    // R
                    winBuf[wi + 3] = winA; // A — the window value (what the OpacityMask uses)
                }

                // Fold the cell-fill tint UNDER the mask dim (reference compositing order), both
                // windowed by the shared falloff, gamma via LUT. Clean centre ⇒ window 0 ⇒ fully
                // transparent. This is exactly ComposeFieldPixelLut, inlined for the hot loop.
                var (oB, oG, oR, oA) = ComposeFieldPixelLut(window, mr, mg, mb, peak, cellStrength);

                int i = row + x * 4;
                buf[i + 0] = oB; // B
                buf[i + 1] = oG; // G
                buf[i + 2] = oR; // R
                buf[i + 3] = oA; // A
            }
        }

        long bandPixels = (long)(yEnd - yStart) * w;
        if (bandPixels < SenseDefaults.OverlayFieldSingleThreadPixelThreshold)
        {
            // Small band — inline on the UI thread (no Parallel.For dispatch; starvation-immune).
            for (int y = yStart; y < yEnd; y++) Row(y);
        }
        else
        {
            // Large band / full recompute — capped Parallel.For so a saturated game can never
            // starve the whole pool (leaves ProcessorCount − 2 cores for the UI/compositor).
            var opts = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = SenseDefaults.OverlayFieldMaxParallelism,
            };
            System.Threading.Tasks.Parallel.For(yStart, yEnd, opts, Row);
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

    /// <summary>Fix 2 perf-harness test seam: allocate the field buffer + bitmap at an EXACT
    /// size (bypassing the screen-derived EnsureFieldBitmap) and seed a renderable mask, so the
    /// perf harness can time ComputeField at exactly 720×450 regardless of the test machine's
    /// screen. Also primes the window LUT. Must run on an STA thread (creates a WriteableBitmap).</summary>
    internal void ConfigureFieldForTests(int w, int h, int tier = 8, double featherWidth = 0.5)
    {
        _fieldW = Math.Max(1, w);
        _fieldH = Math.Max(1, h);
        _fieldBmp = new WriteableBitmap(_fieldW, _fieldH, 96, 96, PixelFormats.Pbgra32, null);
        _fieldBuf = new byte[_fieldW * _fieldH * 4];
        FieldImage.Source = _fieldBmp;
        // CANVAS-MODEL grid window bitmap/buffer (UVHS round-8 fix) at the same exact size, so
        // ComputeFieldRows can write the window byte and the band-vs-full seam can compare it.
        _gridWindowBmp = new WriteableBitmap(_fieldW, _fieldH, 96, 96, PixelFormats.Pbgra32, null);
        _gridWindowBuf = new byte[_fieldW * _fieldH * 4];
        EnsureGridOpacityMask();
        _lastScreenW = _fieldW; _lastScreenH = _fieldH;

        var (tr, tg, tb, alpha) = VimsLevels[Math.Clamp(tier, 1, 12) - 1];
        _maskR = tr; _maskG = tg; _maskB = tb;
        _maskPeakAlpha = alpha;
        _featherStart = ResolveFeatherStart(featherWidth);
        _maskActive = true;
        _cellStrength = 0.5;
        EnsureWindowLut();
    }

    /// <summary>Fix 2 perf-harness test seam: drive ONE render at the given center via the real
    /// RenderFieldIfDirty path (full or dirty-band, exactly as production), returning the elapsed
    /// milliseconds for the recompute+upload. <paramref name="forceFull"/> marks the whole field
    /// dirty (a non-pose change); otherwise a pose-only move uses the dirty band.</summary>
    internal double RenderOnceForTests(double cx, double cy, bool forceFull)
    {
        _fieldCx = cx; _fieldCy = cy; _fieldRadius = BrushRadius;
        if (forceFull) MarkFieldDirty(); else MarkPoseDirty();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderFieldIfDirty();
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        return (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>Fix 2 dirty-band correctness seam: compute the WHOLE field at the given center
    /// into a fresh buffer (the full-recompute reference), independent of the dirty-band path.
    /// Lets the band-vs-full correctness test prove a pose-only band render produces a buffer
    /// byte-identical to a full render at the same pose. Also recomputes the grid-window buffer
    /// in the same pass (ComputeFieldRows writes both), so a follow-up
    /// <see cref="GridWindowBufferSnapshotForTests"/> reads the full-window reference too.</summary>
    internal byte[] ComputeFullFieldReferenceForTests(double cx, double cy)
    {
        double sCx = _fieldCx, sCy = _fieldCy;
        _fieldCx = cx; _fieldCy = cy;
        EnsureWindowLut();
        ComputeFieldRows(0, _fieldH);
        var copy = (byte[])_fieldBuf!.Clone();
        _fieldCx = sCx; _fieldCy = sCy;
        return copy;
    }

    /// <summary>UVHS round-8 (2026-06-11) test seam: compute the WHOLE grid-window A8 buffer at
    /// the given center (the full-window-recompute reference), independent of the dirty-band path.
    /// Lets the dirty-band window byte-equality test (test (e)) prove a pose-only band render
    /// produces a window buffer byte-identical to a full window recompute at the same pose.</summary>
    internal byte[] ComputeFullGridWindowReferenceForTests(double cx, double cy)
    {
        double sCx = _fieldCx, sCy = _fieldCy;
        _fieldCx = cx; _fieldCy = cy;
        EnsureWindowLut();
        ComputeFieldRows(0, _fieldH);
        var copy = (byte[])_gridWindowBuf!.Clone();
        _fieldCx = sCx; _fieldCy = sCy;
        return copy;
    }

    /// <summary>Kirby round-3 (item 4.5) test seam: monotonic count of field recomputes
    /// (== WriteableBitmap.WritePixels calls) since construction, never reset by the
    /// per-minute perf flush. Lets the anti-double-compute smoothness test assert that the
    /// recompute count over N pumped frames stays &lt;= N (one WritePixels per dirty frame).</summary>
    internal long TotalRecomputesForTests => _totalRecomputes;

    /// <summary>UVHS round-8 (2026-06-11) perf-DELTA test seam: toggle the grid-window second
    /// buffer write (the per-pixel encode + its WritePixels). Default true (production always
    /// writes the window); the perf harness flips it false for a field-only timing leg to isolate
    /// the added cost. NEVER set false at runtime.</summary>
    internal bool WriteGridWindowForPerfTests
    {
        get => _writeGridWindowForPerfDelta;
        set => _writeGridWindowForPerfDelta = value;
    }

    /// <summary>UVHS round-8 (2026-06-11) test seam: snapshot the rendered reduced-res A8
    /// grid-window buffer (1 byte/px — the canvas-model OpacityMask alpha). 0 = the gaze hole
    /// (grid hidden), 255 = the canvas (grid present). Lets the seam-equality / canvas-full /
    /// hole / dirty-band-equality tests read exactly what WPF rasterizes as the OpacityMask.
    /// Returns a copy (the live buffer is reused each frame). Null before the first render.</summary>
    internal byte[]? GridWindowBufferSnapshotForTests
        => _gridWindowBuf is null ? null : (byte[])_gridWindowBuf.Clone();

    /// <summary>UVHS round-8 (2026-06-11) test seam: the GridCanvas.OpacityMask is the
    /// canvas-model ImageBrush over the window bitmap (no RadialGradientBrush anymore). Lets a
    /// test assert the mask is wired to the bitmap window, not a radial gradient.</summary>
    internal ImageBrush? GridWindowMaskBrushForTests => _gridWindowMaskBrush;

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
