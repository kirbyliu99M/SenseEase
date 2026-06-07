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

    /// <summary>UM2: max entries per monthly bucket; oldest evicted FIFO when a
    /// month's bucket exceeds this. Public so the v2→v3 migration in
    /// <see cref="FileHabituationProfileStore"/> caps migrated buckets identically.</summary>
    public const int MaxObservationsPerBucket = 30;

    /// <summary>UM2: number of most-recent month buckets retained. Older buckets are
    /// pruned on every write (old observations are stale context).</summary>
    public const int MaxBucketMonths = 12;

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

    /// <summary>Append <paramref name="observation"/> to the CURRENT month's bucket
    /// in the snapshot's <c>LongitudinalObservationsV2</c> (UM2). Returns true iff the
    /// write actually happened (false for blank input, missing snapshot, or a
    /// same-month dedup hit).</summary>
    public bool Append(string? observation)
    {
        if (string.IsNullOrWhiteSpace(observation)) return false;

        string trimmed = observation.Trim();
        if (trimmed.Length > MaxObservationLength)
            trimmed = trimmed.Substring(0, MaxObservationLength);

        // H5: a chatbot OBSERVATION_ADD is model-controlled text persisted into the
        // profile and re-injected into every future prompt — a stored prompt-
        // injection vector. Strip block tokens + structural chars; drop entirely
        // if it was mostly structure (a payload, not a real observation).
        string? safe = SanitizeObservation(trimmed);
        if (safe is null) return false;
        trimmed = safe;

        // W2.0-C: append through the store's atomic Update so the Load→dedup→add→
        // Save span holds the store lock as a unit (the engine persists the SAME
        // file from another thread). Returning null from the mutator skips the Save.
        bool appended = false;
        _store.Update(snapshot =>
        {
            if (snapshot is null) return null; // no profile yet → nothing to append to

            // UM2: append into the CURRENT month's bucket; dedup is scoped to that
            // same month only (cross-month entries are different context).
            var buckets = snapshot.LongitudinalObservationsV2 ??= new();
            string month = DateTime.UtcNow.ToString("yyyy-MM");
            if (!buckets.TryGetValue(month, out var bucket))
            {
                bucket = new List<ObservationEntry>();
                buckets[month] = bucket;
            }

            if (IsDuplicateOfRecent(trimmed, bucket))
                return null; // same-month dedup hit → skip the write

            bucket.Add(new ObservationEntry { Ts = DateTime.UtcNow.ToString("o"), Text = trimmed });

            // Eviction: cap the month's bucket (drop oldest). Loop guards a malformed
            // on-disk bucket that somehow grew past the cap.
            while (bucket.Count > MaxObservationsPerBucket)
                bucket.RemoveAt(0);

            // Retention: keep only the most-recent MaxBucketMonths buckets.
            PruneOldBuckets(buckets);

            appended = true;
            return snapshot;
        });
        return appended;
    }

    /// <summary>H5: neutralise stored prompt-injection in a chatbot observation.
    /// Replaces the structural characters that let text break out of the
    /// recent_observations list ('[' ']' '&lt;' '&gt;' '|' and any newline/tab) with
    /// spaces — which also defangs the block tokens ([habituation_state], [INST],
    /// &lt;|, |&gt; …) since they're built from those chars. If more than 30% of the
    /// text was structural it's a payload, not an observation → drop it (null).
    /// Internal for unit coverage (InternalsVisibleTo SenseEase.Tests).</summary>
    internal static string? SanitizeObservation(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        s = s.Normalize(System.Text.NormalizationForm.FormKC); // fold fullwidth brackets/pipes to ASCII first (review)
        var chars = s.ToCharArray();
        int suspicious = 0;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c is '[' or ']' or '<' or '>' or '|' or '\r' or '\n' or '\t')
            {
                chars[i] = ' ';
                suspicious++;
            }
        }
        if ((double)suspicious / s.Length > 0.30) return null; // mostly structure → injection payload
        string cleaned = System.Text.RegularExpressions.Regex.Replace(new string(chars), @"\s+", " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>NE20/UM2: <paramref name="candidate"/> is the bare trimmed body.
    /// Compares against the recent entry bodies of the SAME-month bucket; StripDateStamp
    /// defends against a migrated v2 entry whose Text still carries a date prefix.</summary>
    private static bool IsDuplicateOfRecent(string candidate, IList<ObservationEntry> existing)
    {
        if (existing.Count == 0) return false;
        int from = Math.Max(0, existing.Count - DedupCompareCount);
        for (int i = existing.Count - 1; i >= from; i--)
        {
            if (JaccardSimilarity(candidate, StripDateStamp(existing[i].Text)) >= DedupSimilarityThreshold)
                return true;
        }
        return false;
    }

    /// <summary>UM2: retention — keep only the most-recent <see cref="MaxBucketMonths"/>
    /// month buckets, dropping the oldest. Keys are "YYYY-MM", so an ordinal sort is
    /// chronological. Runs on every write so the structure stays bounded.</summary>
    private static void PruneOldBuckets(Dictionary<string, List<ObservationEntry>> buckets)
    {
        if (buckets.Count <= MaxBucketMonths) return;
        var ordered = new List<string>(buckets.Keys);
        ordered.Sort(StringComparer.Ordinal); // oldest first
        int drop = buckets.Count - MaxBucketMonths;
        for (int i = 0; i < drop; i++) buckets.Remove(ordered[i]);
    }

    /// <summary>Strip the "yyyy-MM-dd: " prefix written by <see cref="Append"/>
    /// so dedup compares observation bodies, not date stamps. Returns the input
    /// unchanged if it doesn't carry the expected stamp.</summary>
    private static string StripDateStamp(string entry)
    {
        // Stamp is exactly "yyyy-MM-dd: " → 10 date chars + ": " (12 total).
        const int stampLength = 12;
        if (entry.Length >= stampLength
            && entry[4] == '-' && entry[7] == '-'
            && entry[10] == ':' && entry[11] == ' ')
        {
            return entry.Substring(stampLength);
        }
        return entry;
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
