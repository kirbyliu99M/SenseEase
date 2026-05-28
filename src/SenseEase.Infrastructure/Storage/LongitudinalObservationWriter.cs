using System;
using System.Collections.Generic;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

// ─────────────────────────────────────────────────────────────────────────────
// Phase 7g (§4.3.8): writer for the chatbot's longitudinal observations
// that live inside HabituationProfileSnapshot.LongitudinalObservations.
//
// Behaviour:
//   - Trim + length-cap each observation at 200 chars
//   - Prepend an ISO date stamp ("2026-05-28: ...") so future reads can
//     anchor the observation in time
//   - Skip blank/whitespace-only entries
//   - Cap the persisted list at 50 entries; oldest evicted FIFO
//   - Deduplicate: skip if the new observation is too similar (Jaccard
//     similarity > 0.7 on space-split tokens) to any of the last 5 entries.
//     Cheap, locale-agnostic; not perfect but plenty for spam suppression
//     of an LLM emitting near-duplicate observations across turns.
//
// Returns true iff the observation was actually appended (so callers can
// log the no-op for telemetry / debugging).
// ─────────────────────────────────────────────────────────────────────────────

public sealed class LongitudinalObservationWriter
{
    private readonly IHabituationProfileStore _store;

    /// <summary>Maximum number of observations persisted in the snapshot.
    /// Oldest evicted FIFO when this is exceeded.</summary>
    public const int MaxObservations = 50;

    /// <summary>Maximum character length per observation (after trim, before
    /// the date prefix). Longer text is truncated.</summary>
    public const int MaxObservationLength = 200;

    /// <summary>How many recent entries to compare against for deduplication.
    /// Limits the dedup cost to a small constant per append.</summary>
    public const int DedupCompareCount = 5;

    /// <summary>Jaccard similarity threshold above which a new observation
    /// is treated as a duplicate of an existing recent entry and skipped.</summary>
    public const double DedupSimilarityThreshold = 0.7;

    public LongitudinalObservationWriter(IHabituationProfileStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Append <paramref name="observation"/> to the snapshot's
    /// <c>LongitudinalObservations</c> list. Returns true iff the write
    /// actually happened (false for blank input, missing snapshot, or
    /// dedup hit).</summary>
    public bool Append(string? observation)
    {
        if (string.IsNullOrWhiteSpace(observation)) return false;

        string trimmed = observation.Trim();
        if (trimmed.Length > MaxObservationLength)
            trimmed = trimmed.Substring(0, MaxObservationLength);

        var snapshot = _store.Load();
        if (snapshot is null) return false;

        string stamped = $"{DateTime.UtcNow:yyyy-MM-dd}: {trimmed}";

        if (IsDuplicateOfRecent(stamped, snapshot.LongitudinalObservations))
            return false;

        snapshot.LongitudinalObservations.Add(stamped);

        // FIFO eviction past the cap. Loop to handle malformed snapshots
        // whose lists have somehow grown beyond MaxObservations on disk.
        while (snapshot.LongitudinalObservations.Count > MaxObservations)
            snapshot.LongitudinalObservations.RemoveAt(0);

        _store.Save(snapshot);
        return true;
    }

    private static bool IsDuplicateOfRecent(string candidate, IList<string> existing)
    {
        if (existing.Count == 0) return false;
        int from = Math.Max(0, existing.Count - DedupCompareCount);
        for (int i = existing.Count - 1; i >= from; i--)
        {
            if (JaccardSimilarity(candidate, existing[i]) >= DedupSimilarityThreshold)
                return true;
        }
        return false;
    }

    /// <summary>Token-level Jaccard similarity. Splits on whitespace +
    /// trivial punctuation, lowercases, set-compares. Returns 0.0 when
    /// either side is empty.</summary>
    internal static double JaccardSimilarity(string a, string b)
    {
        var aTokens = Tokenize(a);
        var bTokens = Tokenize(b);
        if (aTokens.Count == 0 || bTokens.Count == 0) return 0.0;

        int intersection = 0;
        foreach (var t in aTokens)
            if (bTokens.Contains(t)) intersection++;

        int union = aTokens.Count + bTokens.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Split on whitespace + a small punctuation set. Hyphens kept inside
        // tokens because compound zh tokens often use them. CJK runs through
        // as-is since the punctuation list is ASCII-only.
        char[] seps = { ' ', '\t', '\n', '\r', '.', ',', '!', '?', '。', '，', '！', '？' };
        foreach (var part in s.Split(seps, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 0) continue;
            set.Add(part);
        }
        return set;
    }
}
