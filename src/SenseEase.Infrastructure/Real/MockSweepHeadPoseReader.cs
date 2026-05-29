using System;
using SenseEase.Core.Abstractions;

namespace SenseEase.Infrastructure.Real;

/// <summary>M5 (Overlay Integrator) developer/QA reader that emits a
/// deterministic 20-second head-pose sweep, so the wiring from
/// <see cref="IHeadPoseReader"/> → <c>SenseState.HeadGazeX/Y</c> →
/// <c>OverlayWindow._fovBrush.Center</c> can be visually verified during
/// the G4 UVHS demo WITHOUT requiring a working webcam stream.
///
/// <para>Pattern (cx, cy in <see cref="IHeadPoseReader"/>'s
/// <c>RelativeToBoundingBox</c> convention — 0,0 = top-left, 1,1 = bottom-right):
/// <list type="bullet">
///   <item><b>0–5 s</b>: horizontal sweep. cx walks 0.5 → 0.1 → 0.9 → 0.5; cy held at 0.5.</item>
///   <item><b>5–10 s</b>: vertical sweep. cy walks 0.5 → 0.1 → 0.9 → 0.5; cx held at 0.5.</item>
///   <item><b>10–15 s</b>: circular sweep, radius 0.3 around (0.5, 0.5).</item>
///   <item><b>15–20 s</b>: hold at (0.5, 0.5).</item>
/// </list>
/// After t ≥ 20 s, <see cref="ReadLatest"/> returns null so the consumer
/// snaps back to the (0.5, 0.5) center default (matching the sidecar's
/// "no fresh sample" contract from <see cref="IHeadPoseReader"/>).</para>
///
/// <para><b>Flag independence.</b> The mock sweep is intentionally usable
/// regardless of <c>AppConfig.HeadTrackingEnabled</c>. It exists so the
/// G4 demo can prove end-to-end wiring even when (a) the user has no
/// webcam, (b) the Python sidecar is not installed, or (c) MediaPipe
/// face detection is failing for environmental reasons.</para>
///
/// <para><b>Threading.</b> The reader is stateless past construction: every
/// <see cref="ReadLatest"/> call computes the sample from the injected
/// elapsed-seconds source. Safe to call from any thread (the
/// <see cref="Func{TResult}"/> is the only mutable seam).</para></summary>
public sealed class MockSweepHeadPoseReader : IHeadPoseReader
{
    /// <summary>Total sweep length in seconds. After t ≥ this value
    /// <see cref="ReadLatest"/> returns null.</summary>
    public const double SweepDurationSeconds = 20.0;

    private const double Phase1End = 5.0;
    private const double Phase2End = 10.0;
    private const double Phase3End = 15.0;
    private const double Phase4End = SweepDurationSeconds;

    private readonly Func<double> _getElapsedSeconds;
    private readonly DateTime _wallClockStartUtc;

    /// <summary>Default constructor: drives the sweep from wall-clock time
    /// measured from construction. Used by the runtime hotkey path.</summary>
    public MockSweepHeadPoseReader()
    {
        _wallClockStartUtc = DateTime.UtcNow;
        _getElapsedSeconds = () => (DateTime.UtcNow - _wallClockStartUtc).TotalSeconds;
    }

    /// <summary>Testable constructor: caller injects an elapsed-seconds
    /// source so the pattern can be advanced deterministically without
    /// waiting on a real clock.</summary>
    public MockSweepHeadPoseReader(Func<double> getElapsedSeconds)
    {
        _getElapsedSeconds = getElapsedSeconds ?? throw new ArgumentNullException(nameof(getElapsedSeconds));
        _wallClockStartUtc = DateTime.UtcNow;
    }

    /// <summary>Compute the sweep sample at the given elapsed seconds.
    /// Pure function — exposed internal so the unit tests can drive
    /// specific timestamps without instantiating the reader.</summary>
    internal static (double Cx, double Cy)? SampleAt(double elapsedSeconds)
    {
        if (elapsedSeconds < 0) return (0.5, 0.5);
        if (elapsedSeconds >= Phase4End) return null;

        if (elapsedSeconds < Phase1End)
        {
            // Horizontal sweep: 0..5 s drives cx through 0.5 → 0.1 → 0.9 → 0.5.
            // Quarter waypoints land at 0, 1.25, 2.5, 3.75, 5.
            double t = elapsedSeconds / Phase1End;            // 0..1
            double cx = HorizontalWaypointInterp(t);
            return (cx, 0.5);
        }
        if (elapsedSeconds < Phase2End)
        {
            // Vertical sweep: 5..10 s drives cy through 0.5 → 0.1 → 0.9 → 0.5.
            double t = (elapsedSeconds - Phase1End) / (Phase2End - Phase1End);
            double cy = HorizontalWaypointInterp(t);          // same shape
            return (0.5, cy);
        }
        if (elapsedSeconds < Phase3End)
        {
            // Circular sweep: 10..15 s traces one full revolution at
            // radius 0.3 around (0.5, 0.5). Angle goes 0 → 2π.
            double t = (elapsedSeconds - Phase2End) / (Phase3End - Phase2End);
            double angle = t * 2.0 * Math.PI;
            double cx = 0.5 + 0.3 * Math.Cos(angle);
            double cy = 0.5 + 0.3 * Math.Sin(angle);
            return (cx, cy);
        }
        // Phase 4 (15..20 s): hold at center so the user sees the mask
        // settle visibly before the sweep ends.
        return (0.5, 0.5);
    }

    /// <summary>Piecewise-linear interpolation of the waypoint sequence
    /// (0.5, 0.1, 0.9, 0.5) with four equal segments over t ∈ [0, 1].
    /// At t=0.0 → 0.5; t=0.25 → 0.3 (midway down); t=0.5 → 0.9; t=0.75 → 0.7; t=1 → 0.5.
    /// Wait — re-derive with exact waypoints landing on quarters:
    /// segments are (0.5→0.1), (0.1→0.9), (0.9→0.5) over t in [0, 1/3], [1/3, 2/3], [2/3, 1].
    /// Sticking with the simpler four-quarter form because the spec only
    /// pins the endpoints + center hold, and the test checks t=2.5 (=t_local=0.5)
    /// for cx ≈ 0.1 → that fits the (0.5 → 0.1 → 0.9 → 0.5) shape with
    /// three segments at t_local ∈ {0, 0.5, 1.0} only if we use three
    /// segments. Implementing as three equal thirds.</summary>
    private static double HorizontalWaypointInterp(double t)
    {
        // Three-segment waypoint walk: 0.5 → 0.1 → 0.9 → 0.5
        // Segment 0: t in [0, 1/2]  → 0.5 → 0.1     (steeper move out to one extreme)
        // Segment 1: t in [1/2, 3/4] would split asymmetrically — use the
        //   spec literally: split into two halves so the midpoint (t=0.5
        //   inside the phase) lands on the first extreme. Spec test:
        //     t=2.5 s in the 0..5 s horizontal phase → t_local = 0.5 → cx ≈ 0.1
        //   That fixes the first half as (0.5 → 0.1) over t_local ∈ [0, 0.5].
        //   Second half then traverses 0.1 → 0.9 → 0.5 over t_local ∈ [0.5, 1.0].
        //   Two sub-segments inside it of equal width [0.5, 0.75], [0.75, 1.0]:
        //     t_local=0.75 → cx = 0.9 (the second extreme).
        //     t_local=1.0  → cx = 0.5 (back to center).
        if (t <= 0) return 0.5;
        if (t >= 1) return 0.5;
        if (t <= 0.5)
        {
            double u = t / 0.5;                  // 0..1
            return 0.5 + (0.1 - 0.5) * u;        // 0.5 → 0.1
        }
        if (t <= 0.75)
        {
            double u = (t - 0.5) / 0.25;         // 0..1
            return 0.1 + (0.9 - 0.1) * u;        // 0.1 → 0.9
        }
        {
            double u = (t - 0.75) / 0.25;        // 0..1
            return 0.9 + (0.5 - 0.9) * u;        // 0.9 → 0.5
        }
    }

    public HeadPoseSample? ReadLatest()
    {
        double t = _getElapsedSeconds();
        var pt = SampleAt(t);
        if (pt is null) return null;
        long tMs = (long)(t * 1000.0);
        return new HeadPoseSample(
            TimestampMs:  tMs,
            Cx:           pt.Value.Cx,
            Cy:           pt.Value.Cy,
            Luma:         0.5,
            FaceDetected: true,
            FacePresent:  1.0);
    }
}
