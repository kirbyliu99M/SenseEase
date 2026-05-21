using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>
/// Persistence for habituation progress across app restarts. Re-habituation is a
/// multi-session therapeutic process, so progress must survive a restart.
/// </summary>
public interface IHabituationStore
{
    /// <summary>Load the last saved progress, or null if none exists.</summary>
    HabituationState? Load();

    /// <summary>Persist current progress. Best-effort — must never throw to the caller.</summary>
    void Save(HabituationState state);
}
