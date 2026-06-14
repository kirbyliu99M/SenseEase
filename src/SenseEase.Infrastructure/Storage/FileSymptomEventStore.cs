using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// Beta-1 Phase-1: append-only store for <see cref="SymptomEvent"/> records
/// (<c>symptom_log.jsonl</c>, one compact snake_case object per line) — the shared EventLog
/// foundation that carries the explicit FMS self-report (D-18/19) and the daily NL→FMS estimate
/// (D-21). Best-effort like <see cref="DailyBehaviorAggregateWriter"/> / <see cref="FileChatHistoryStore"/>:
/// telemetry logging never crashes the app.
///
/// <para>Append is a fast pure-append (the file is small — a few self-reports per session).
/// <see cref="TrimOlderThan"/> does the only read-modify-write and is meant to be called
/// occasionally (e.g. once at startup) to honour the retention window, using the same atomic
/// tmp+File.Replace pattern as the aggregate writer so a crash mid-trim cannot destroy history.</para>
/// </summary>
public sealed class FileSymptomEventStore
{
    private readonly string _path;

    // Per-PATH gate (not per-instance): the FMS widget, the daily analyzer, and a startup trim
    // can each hold their own instance over the same file — serialise them on one lock to avoid
    // a torn rewrite, mirroring DailyBehaviorAggregateWriter / FileHabituationProfileStore.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _pathGates
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public FileSymptomEventStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _gate = _pathGates.GetOrAdd(GateKey(_path), static _ => new object());
    }

    private static string GateKey(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    /// <summary>Append one event. Stamps <see cref="SymptomEvent.TsUtc"/> with the current UTC
    /// time when blank (round-trippable "o"). No-op for null. Best-effort.</summary>
    public void Append(SymptomEvent ev)
    {
        if (ev is null) return;
        if (string.IsNullOrWhiteSpace(ev.TsUtc))
            ev.TsUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_path, JsonSerializer.Serialize(ev, _opts) + Environment.NewLine);
            }
            catch { /* best-effort: telemetry logging must never crash the app */ }
        }
    }

    /// <summary>All events, oldest-first. Skips malformed lines. Never throws.</summary>
    public IReadOnlyList<SymptomEvent> ReadAll()
    {
        lock (_gate) { return ReadAllNoLock(); }
    }

    /// <summary>The most recent <paramref name="count"/> events, oldest-first.</summary>
    public IReadOnlyList<SymptomEvent> ReadRecent(int count)
    {
        if (count <= 0) return Array.Empty<SymptomEvent>();
        lock (_gate)
        {
            var all = ReadAllNoLock();
            return all.Count <= count ? all : all.GetRange(all.Count - count, count);
        }
    }

    /// <summary>Drop events older than <paramref name="retentionDays"/> (retention window;
    /// default horizon 30 days per the trend window). Atomic rewrite. Best-effort no-op on any
    /// failure (leaves the file untouched). Events with an unparseable timestamp are KEPT (never
    /// silently dropped on a parse hiccup).</summary>
    public void TrimOlderThan(int retentionDays)
    {
        if (retentionDays <= 0) return;
        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var all = ReadAllNoLock();
                var kept = all.Where(e => !TryParseTs(e.TsUtc, out DateTime t) || t >= cutoffUtc).ToList();
                if (kept.Count == all.Count) return; // nothing to trim
                RewriteNoLock(kept);
            }
            catch { /* best-effort: a failed trim must never disturb the live log */ }
        }
    }

    private List<SymptomEvent> ReadAllNoLock()
    {
        var list = new List<SymptomEvent>();
        try
        {
            if (!File.Exists(_path)) return list;
            foreach (string line in File.ReadAllLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<SymptomEvent>(line, _opts);
                    if (r is not null) list.Add(r);
                }
                catch { /* skip a malformed line, keep the rest */ }
            }
        }
        catch { /* unreadable → return whatever parsed so far; never throw into a caller */ }
        return list;
    }

    private static bool TryParseTs(string? s, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        // Parse the round-trippable "o" stamp (Z ⇒ Kind=Utc), then normalise to UTC. Combining
        // RoundtripKind with AdjustToUniversal is unreliable, so do the conversion explicitly.
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t))
        {
            utc = t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
            return true;
        }
        return false;
    }

    private void RewriteNoLock(List<SymptomEvent> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.Append(JsonSerializer.Serialize(r, _opts)).Append(Environment.NewLine);

        string tmp = _path + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                           FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
                File.Replace(tmp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
