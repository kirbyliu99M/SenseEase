namespace SenseEase.Core.Abstractions;

/// <summary>M3 scaffolding for head-tracking sprint. M4 will provide
/// a real implementation that reads JSON lines from the Python sidecar
/// (<c>tools/head-tracker-sidecar/</c>); for M3 only the DefaultHeadPoseReader
/// (always-null) is wired so flag-off path is byte-equivalent to v0.9.
///
/// <para>The schema returned mirrors the sidecar's per-frame output —
/// see <c>tools/head-tracker-sidecar/PRIVACY.md</c> for the derived-only
/// hard constraint (NEVER raw landmarks).</para>
/// </summary>
public interface IHeadPoseReader
{
    /// <summary>Returns the latest head-pose sample, or null if no fresh
    /// sample is available (sidecar not running, face not detected, or
    /// flag off). Caller treats null as "use center default (0.5, 0.5)".</summary>
    HeadPoseSample? ReadLatest();
}

/// <summary>Per-frame head-pose sample. Privacy invariant: ONLY derived
/// fields. NEVER carries raw landmarks, frame data, or face image.</summary>
public sealed record HeadPoseSample(
    long   TimestampMs,
    double Cx,                  // 0.0-1.0, smoothed + clamped
    double Cy,                  // 0.0-1.0, smoothed + clamped
    double Luma,                // 0.0-1.0, frame mean luma
    bool   FaceDetected,
    double Confidence);
