using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// H17: durable, append-only JSONL event log. Drop-in replacement for
/// <see cref="InMemoryEventLogStore"/> — the in-memory recent window + the
/// <see cref="Appended"/> event behave identically (so the dashboard mirror and
/// Recent() are unchanged), but forensic-category entries are ALSO persisted to a
/// per-UTC-day file (event-log-YYYY-MM-DD.jsonl) so a tester's "my profile flipped
/// overnight" report has durable evidence after a restart.
///
/// <para>One file per UTC day; files older than <see cref="RetentionDays"/> are
/// pruned at construction; each day's file is capped at <see cref="MaxDailyFileBytes"/>.
/// Only entries whose Category starts with a forensic prefix (Habituation. /
/// Chatbot. / Calibration. / Safety. / Storage. / State. / AdaptiveFit.) are written
/// to disk — the in-memory window keeps everything for the dashboard. All file I/O is
/// best-effort: logging must never crash the app.</para>
///
/// <para><see cref="ReadDurable"/> reads those daily files back (filtered by a `since`
/// instant) for the Adaptive Fit per-user stats pipeline — the only read path that
/// crosses a process restart.</para>
/// </summary>
public sealed class FileEventLogStore : IEventLogStore
{
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly LinkedList<EventLogEntry> _recent = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = false };

    public const long MaxDailyFileBytes = 1024 * 1024; // 1 MB
    public const int RetentionDays = 7;

    private static readonly string[] DurableCategoryPrefixes =
        { "Habituation.", "Chatbot.", "Calibration.", "Safety.", "Storage.", "State.", "AdaptiveFit." };

    // FEAT-09/LOGIC-07/SEC-04: forensic categories that MUST land even after the daily
    // cap is hit — losing them silently is the actual harm (a Safety.* intercept or a
    // Chatbot.Declined that drives the rule-5 cooldown vanishing across a restart). The
    // "Safety." prefix is matched by StartsWith; "Chatbot.Declined" is matched exactly.
    internal const string SafetyCategoryPrefix = "Safety.";
    internal const string ChatbotDeclinedCategory = "Chatbot.Declined";

    public event EventHandler<EventLogEntry>? Appended;

    public FileEventLogStore(string directory)
    {
        _dir = directory;
        try
        {
            Directory.CreateDirectory(_dir);
            AuditLogFiles.Prune(_dir, "event-log-*.jsonl", RetentionDays);
        }
        catch { /* best-effort */ }
    }

    public void Append(EventLogEntry entry)
    {
        lock (_gate)
        {
            _recent.AddFirst(entry);
            while (_recent.Count > SenseDefaults.EventLogMaxCount)
                _recent.RemoveLast();

            if (IsDurableCategory(entry.Category))
            {
                string line = JsonSerializer.Serialize(entry, _opts);
                AuditLogFiles.Append(_dir, "event-log", line, MaxDailyFileBytes,
                                     capExempt: IsCapExemptCategory(entry.Category));
            }
        }
        // Outside the lock — UI subscribers marshal to the dispatcher (M1 mirror).
        Appended?.Invoke(this, entry);
    }

    public IReadOnlyList<EventLogEntry> Recent()
    {
        lock (_gate) { return _recent.ToList(); }
    }

    /// <summary>
    /// Durable history at or after <paramref name="since"/>, read back from the 7-day
    /// rolling JSONL this store already writes (one file per UTC day). Backs the Adaptive
    /// Fit per-user stats pipeline. Best-effort: any malformed line or I/O error is
    /// skipped/swallowed, never thrown. Does not touch the in-memory window or Append path.
    /// </summary>
    public IReadOnlyList<EventLogEntry> ReadDurable(DateTime since)
    {
        var result = new List<EventLogEntry>();
        try
        {
            if (!Directory.Exists(_dir)) return result;
            foreach (string file in Directory.GetFiles(_dir, "event-log-*.jsonl"))
            {
                IEnumerable<string> lines;
                try { lines = File.ReadLines(file); }
                catch { continue; } // file vanished / locked — skip it
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    EventLogEntry? entry;
                    try { entry = JsonSerializer.Deserialize<EventLogEntry>(line, _opts); }
                    catch { continue; } // tolerate a torn/partial trailing line
                    if (entry is not null && entry.Timestamp >= since)
                        result.Add(entry);
                }
            }
        }
        catch { /* best-effort: never crash a read-back */ }
        return result;
    }

    private static bool IsDurableCategory(string? category)
    {
        if (string.IsNullOrEmpty(category)) return false;
        foreach (string p in DurableCategoryPrefixes)
            if (category.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>FEAT-09: a forensic entry that must always be persisted, bypassing the
    /// daily byte cap — any <c>Safety.*</c> category or the exact <c>Chatbot.Declined</c>
    /// category (the rule-5 decline cooldown rehydrates from these across restarts).</summary>
    private static bool IsCapExemptCategory(string? category)
    {
        if (string.IsNullOrEmpty(category)) return false;
        return category.StartsWith(SafetyCategoryPrefix, StringComparison.Ordinal)
            || string.Equals(category, ChatbotDeclinedCategory, StringComparison.Ordinal);
    }
}

/// <summary>Shared helpers for the H17 file-backed audit logs: daily-rolled
/// JSONL append with a per-day size cap, and retention pruning. Best-effort.</summary>
internal static class AuditLogFiles
{
    /// <summary>FEAT-09: the single sentinel line appended once per day the first time a
    /// non-exempt entry is dropped at the cap, so a forensic reader sees the truncation
    /// explicitly instead of an unexplained gap.</summary>
    internal const string TruncationSentinel = "[truncated: daily cap reached]";

    // FEAT-09: tracks, per day file path, that the truncation sentinel has already been
    // written this process so we emit it exactly once even under a burst of dropped
    // entries. Cross-instance / cross-restart dedup is handled by the on-disk content
    // check before the write (a cap-hit day file is small enough to scan cheaply).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _sentinelWritten
        = new(StringComparer.OrdinalIgnoreCase);

    public static string DayFilePath(string dir, string prefix)
        => Path.Combine(dir, $"{prefix}-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

    /// <summary>Append one JSONL line to the day file, honouring the per-day byte cap.
    /// <paramref name="capExempt"/> entries (FEAT-09 forensic Safety.* / Chatbot.Declined)
    /// always land regardless of the cap; ordinary entries past the cap are dropped after
    /// one truncation sentinel is recorded for the day.</summary>
    public static void Append(string dir, string prefix, string jsonLine, long maxBytes,
                              bool capExempt = false)
    {
        try
        {
            string path = DayFilePath(dir, prefix);

            bool overCap = File.Exists(path) && new FileInfo(path).Length >= maxBytes;
            if (overCap && !capExempt)
            {
                // First non-exempt drop of the day: record one sentinel so the gap is
                // explicit. Further non-exempt entries drop silently.
                WriteTruncationSentinelOnce(path);
                return;
            }

            // Exempt entries always append (even past the cap); ordinary entries append
            // while under the cap.
            File.AppendAllText(path, jsonLine + Environment.NewLine);
        }
        catch { /* logging must never crash the app */ }
    }

    private static void WriteTruncationSentinelOnce(string path)
    {
        try
        {
            // In-process guard: emit at most once per day file per run.
            if (!_sentinelWritten.TryAdd(path, 0)) return;

            // Cross-instance / cross-restart guard: don't double-stamp if a prior process
            // already wrote the sentinel into today's file.
            try
            {
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadLines(path))
                        if (line.Contains(TruncationSentinel, StringComparison.Ordinal))
                            return;
                }
            }
            catch { /* unreadable → fall through and append; a duplicate sentinel is harmless */ }

            File.AppendAllText(path, TruncationSentinel + Environment.NewLine);
        }
        catch { /* best-effort */ }
    }

    public static void Prune(string dir, string globPattern, int retentionDays)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            DateTime cutoff = DateTime.UtcNow.Date.AddDays(-retentionDays);
            foreach (string file in Directory.GetFiles(dir, globPattern))
            {
                string name = Path.GetFileNameWithoutExtension(file); // {prefix}-YYYY-MM-DD
                if (name.Length < 10) continue;
                string datePart = name[^10..];
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fileDate)
                    && fileDate < cutoff)
                {
                    try { File.Delete(file); } catch { /* best-effort */ }
                }
            }
        }
        catch { /* best-effort */ }
    }
}
