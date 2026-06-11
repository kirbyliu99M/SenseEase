namespace SenseEase.Core.Models;

/// <summary>
/// Persistable habituation progress. Only the earned FOV-relaxation offset is stored;
/// stability counters restart on load so calm must be re-confirmed before relaxing further.
/// </summary>
public sealed class HabituationState
{
    /// <summary>Earned FOV-relaxation offset in percent points, capped by the active
    /// profile's <see cref="Inference.HabituationProfileParams"/> max-offset (the old
    /// SenseDefaults.HabituationMaxOffset was retired with the DEAD-09 sweep).</summary>
    public double Offset { get; set; }

    /// <summary>When this snapshot was taken (UTC).</summary>
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
}
