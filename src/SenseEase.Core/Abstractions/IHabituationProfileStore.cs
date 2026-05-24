using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>Persistence interface for the habituation-profile snapshot
/// (separate from <see cref="IHabituationStore"/>, which stores offset only).
/// See docs/HABITUATION-PROFILE.md §5 for schema + write-trigger semantics.</summary>
public interface IHabituationProfileStore
{
    /// <summary>Load the most recent persisted snapshot. Returns <c>null</c>
    /// when the file is missing, empty, malformed, or has an unknown
    /// schema_version (per Q2 fall-back rule). Caller treats <c>null</c> as
    /// "use defaults" — i.e. RESILIENT + default state.</summary>
    HabituationProfileSnapshot? Load();

    /// <summary>Atomically persist the snapshot. Best-effort: never throws.
    /// Implementations should use tmp + File.Replace so a Process.Kill
    /// mid-write leaves the destination file in a coherent state (old or
    /// new, never partial).</summary>
    void Save(HabituationProfileSnapshot snapshot);
}
