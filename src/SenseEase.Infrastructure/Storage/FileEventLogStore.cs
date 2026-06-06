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
                AuditLogFiles.Append(_dir, "event-log", line, MaxDailyFileBytes);
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
}

/// <summary>Shared helpers for the H17 file-backed audit logs: daily-rolled
/// JSONL append with a per-day size cap, and retention pruning. Best-effort.</summary>
internal static class AuditLogFiles
{
    public static string DayFilePath(string dir, string prefix)
        => Path.Combine(dir, $"{prefix}-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

    public static void Append(string dir, string prefix, string jsonLine, long maxBytes)
    {
        try
        {
            string path = DayFilePath(dir, prefix);
            // Per-day size cap: once exceeded, stop appending for the rest of the
            // day (best-effort forensic log; callers keep their in-memory state).
            if (File.Exists(path) && new FileInfo(path).Length >= maxBytes) return;
            File.AppendAllText(path, jsonLine + Environment.NewLine);
        }
        catch { /* logging must never crash the app */ }
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
