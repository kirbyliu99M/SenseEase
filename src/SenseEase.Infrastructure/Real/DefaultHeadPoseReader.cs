using SenseEase.Core.Abstractions;

namespace SenseEase.Infrastructure.Real;

/// <summary>M3 stub used when HeadTrackingEnabled=false (the default).
/// Always returns null so the consumer falls back to center default.
/// M4 will introduce SidecarHeadPoseReader that talks to the Python
/// process; until then this stub is the only registered reader.
///
/// <para>Flag-off invariant: this class allocates nothing per call, holds
/// no state, and never spawns a thread — its existence is byte-equivalent
/// to having no reader at all when the consumer treats null as "use
/// (0.5, 0.5) center default".</para></summary>
public sealed class DefaultHeadPoseReader : IHeadPoseReader
{
    public HeadPoseSample? ReadLatest() => null;
}
