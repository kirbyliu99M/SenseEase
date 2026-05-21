using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>
/// Diagnostics layer contract: an append-only, bounded event log.
/// </summary>
public interface IEventLogStore
{
    /// <summary>Append an entry; the store keeps at most SenseDefaults.EventLogMaxCount.</summary>
    void Append(EventLogEntry entry);

    /// <summary>Most-recent-first snapshot of stored entries.</summary>
    IReadOnlyList<EventLogEntry> Recent();
}
