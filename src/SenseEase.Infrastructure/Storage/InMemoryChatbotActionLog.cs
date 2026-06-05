using System;
using System.Collections.Generic;
using System.Linq;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// Phase 7g (Q4.7 = B): in-memory implementation of <see cref="IChatbotActionLog"/>.
/// Lifetime = the running process. Per Q4.6 (stateless chatbot decision)
/// it's acceptable for the action history to reset on app restart —
/// chatbot context only grounds on persisted state (snapshot + event log),
/// not on the action log. A file-backed JSONL variant can come later if
/// the chatbot ever needs to "remember it proposed X yesterday" across
/// restarts.
///
/// Thread-safe via a single gate lock. NE19: a FIFO soft cap
/// (<see cref="SoftCap"/> entries) evicts the oldest on insert so a long
/// session can't grow the log unbounded.
/// </summary>
public sealed class InMemoryChatbotActionLog : IChatbotActionLog
{
    /// <summary>NE19: max retained entries before the oldest is evicted (FIFO).</summary>
    private const int SoftCap = 500;

    private readonly object _gate = new();
    private readonly List<ChatbotActionEntry> _entries = new();

    public void Record(ChatbotActionKind kind, ProfileOverrideProposal proposal, string? reason = null)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        var entry = new ChatbotActionEntry(
            TimestampUtc: DateTime.UtcNow,
            Kind: kind,
            Intent: proposal.Intent,
            ProposedValue: proposal.ProposedValue,
            Rationale: proposal.Rationale,
            Reason: reason);
        lock (_gate)
        {
            _entries.Add(entry);
            // NE19: evict oldest-first once over the soft cap (typically 0-1 removals per insert).
            if (_entries.Count > SoftCap)
                _entries.RemoveRange(0, _entries.Count - SoftCap);
        }
    }

    public DateTime? MostRecentDeclineFor(OverrideIntent intent)
    {
        lock (_gate)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.Kind == ChatbotActionKind.Declined && e.Intent == intent)
                    return e.TimestampUtc;
            }
            return null;
        }
    }

    public IReadOnlyList<ChatbotActionEntry> Recent(int count = 20)
    {
        if (count <= 0) return Array.Empty<ChatbotActionEntry>();
        lock (_gate)
        {
            int take = Math.Min(count, _entries.Count);
            // Reverse chronological — newest first.
            var slice = new ChatbotActionEntry[take];
            for (int i = 0; i < take; i++)
                slice[i] = _entries[_entries.Count - 1 - i];
            return slice;
        }
    }
}
