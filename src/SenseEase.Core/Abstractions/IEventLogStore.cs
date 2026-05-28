using System;
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

    /// <summary>Raised AFTER each successful <see cref="Append"/>. Lets the
    /// dashboard's bound <c>EventLog</c> ObservableCollection mirror entries
    /// that originate outside MainViewModel (M1 habituation events from
    /// MockInferenceEngine, Phase 7d/7f Applicator + intercept events).
    /// May fire from any thread — UI subscribers must marshal to the
    /// dispatcher.</summary>
    event EventHandler<EventLogEntry>? Appended;
}
