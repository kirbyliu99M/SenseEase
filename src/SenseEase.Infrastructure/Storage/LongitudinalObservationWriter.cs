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

        // H5: a chatbot OBSERVATION_ADD is model-controlled text persisted into the
        // profile and re-injected into every future prompt — a stored prompt-
        // injection vector. Strip block tokens + structural chars; drop entirely
        // if it was mostly structure (a payload, not a real observation).
        string? safe = SanitizeObservation(trimmed);
        if (safe is null) return false;
        trimmed = safe;

        var snapshot = _store.Load();
        if (snapshot is null) return false;

        // NE20: dedup on the trimmed body, not the stamped string. The date
        // prefix is constant within a day and would otherwise inflate Jaccard
        // overlap (and mask same-day repeats); strip the stamp off existing
        // entries before comparing so the body is matched against the body.
        if (IsDuplicateOfRecent(trimmed, snapshot.LongitudinalObservations))
            return false;

        string stamped = $"{DateTime.UtcNow:yyyy-MM-dd}: {trimmed}";
        snapshot.LongitudinalObservations.Add(stamped);

        // FIFO eviction past the cap. Loop to handle malformed snapshots
        // whose lists have somehow grown beyond MaxObservations on disk.
        while (snapshot.LongitudinalObservations.Count > MaxObservations)
            snapshot.LongitudinalObservations.RemoveAt(0);

        _store.Save(snapshot);
        return true;
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

    /// <summary>NE20: <paramref name="candidate"/> is the bare trimmed body (no
    /// date stamp). Existing entries are stamped, so strip their date prefix
    /// before comparing body-to-body.</summary>
    private static bool IsDuplicateOfRecent(string candidate, IList<string> existing)
    {
        if (existing.Count == 0) return false;
        int from = Math.Max(0, existing.Count - DedupCompareCount);
        for (int i = existing.Count - 1; i >= from; i--)
        {
            if (JaccardSimilarity(candidate, StripDateStamp(existing[i])) >= DedupSimilarityThreshold)
                return true;
        }
        return false;
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
