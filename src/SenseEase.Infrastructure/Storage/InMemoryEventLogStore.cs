using System;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// In-memory, bounded event log (most-recent-first). Keeps at most
/// <see cref="SenseDefaults.EventLogMaxCount"/> entries.
/// </summary>
public sealed class InMemoryEventLogStore : IEventLogStore
{
    private readonly LinkedList<EventLogEntry> _entries = new();
    private readonly object _gate = new();

    public event EventHandler<EventLogEntry>? Appended;

    public void Append(EventLogEntry entry)
    {
        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > SenseDefaults.EventLogMaxCount)
            {
                _entries.RemoveLast();
            }
        }
        // Outside the lock — subscribers (notably MainViewModel's dispatcher
        // marshal) may take a while; we don't want to hold _gate during that.
        Appended?.Invoke(this, entry);
    }

    public IReadOnlyList<EventLogEntry> Recent()
    {
        lock (_gate)
        {
            return _entries.ToList();
        }
    }
}
