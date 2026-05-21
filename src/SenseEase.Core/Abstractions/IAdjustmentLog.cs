using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>Aggregate of a user's historical adjustments to one parameter.</summary>
public readonly record struct PersonalWeight(string Parameter, int Count, double NetDelta, double MeanDelta);

/// <summary>
/// Append-only persistence for personalization adjustments. Records every FOV /
/// shader knob change so a per-user weight can be derived from history.
/// </summary>
public interface IAdjustmentLog
{
    void Append(AdjustmentRecord record);

    /// <summary>All records, oldest-first.</summary>
    IReadOnlyList<AdjustmentRecord> All();

    /// <summary>
    /// Personalized weight for one parameter: how strongly and in which direction
    /// this user tends to adjust it (computed from user-sourced records only).
    /// </summary>
    PersonalWeight WeightFor(string parameter);
}
