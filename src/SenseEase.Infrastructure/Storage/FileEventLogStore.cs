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
/// Chatbot. / Calibration. / Safety. / Storage.) are written to disk — the
/// in-memory window keeps everything for the dashboard. All file I/O is
/// best-effort: logging must never crash the app.</para>
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
        { "Habituation.", "Chatbot.", "Calibration.", "Safety.", "Storage.", "State." };

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
