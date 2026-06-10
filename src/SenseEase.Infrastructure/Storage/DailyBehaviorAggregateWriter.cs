using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// UM1 (User Model sprint): append-only writer for the daily behaviour aggregate
/// (<c>behavior_log.jsonl</c>, one numeric-only row per day). Best-effort like
/// <see cref="FileChatHistoryStore"/> / <see cref="FileAdjustmentLog"/> — logging
/// never crashes the app.
///
/// <para><b>Idempotent by date:</b> if a row for the aggregate's date already
/// exists on disk, it is updated in place (full-file rewrite) rather than appended
/// as a duplicate — so the app-exit and midnight triggers, or repeated exits in one
/// day, converge on a single row per day.</para>
/// </summary>
public sealed class DailyBehaviorAggregateWriter
{
    private readonly string _path;

    // LOGIC-06: the read-modify-write gate is keyed by FILE PATH (not per-instance),
    // mirroring FileHabituationProfileStore — a second writer instance over the same
    // behavior_log.jsonl (the app-exit + midnight triggers can each hold their own
    // instance) serialises against the same lock instead of racing a non-atomic rewrite
    // (lost update / torn read of all UM1/UM5 history).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _pathGates
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;

    // snake_case + compact (one object per line) so the JSONL matches the UM1
    // schema field names (session_minutes, ofi_p50, stable_windows_completed, ...).
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public DailyBehaviorAggregateWriter(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _gate = _pathGates.GetOrAdd(GateKey(_path), static _ => new object());
    }

    /// <summary>Canonical key for the per-path lock: full path, case-insensitive
    /// (Windows). Falls back to the raw path if it cannot be canonicalised, so two
    /// instances passing the identical AppConfig path always share one lock.</summary>
    private static string GateKey(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    /// <summary>Persist <paramref name="aggregate"/>, replacing any existing row
    /// for the same <see cref="DailyBehaviorAggregate.Date"/>. No-op for a null
    /// aggregate or a blank date.</summary>
    public void Write(DailyBehaviorAggregate aggregate)
    {
        if (aggregate is null || string.IsNullOrWhiteSpace(aggregate.Date)) return;
        lock (_gate)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var rows = ReadAllNoLock();
                int idx = rows.FindIndex(r => r.Date == aggregate.Date);
                if (idx >= 0)
                {
                    // Idempotent: same-day re-write replaces the prior row.
                    rows[idx] = aggregate;
                    RewriteNoLock(rows);
                }
                else
                {
                    File.AppendAllText(_path,
                        JsonSerializer.Serialize(aggregate, _opts) + Environment.NewLine);
                }
            }
            catch { /* best-effort: telemetry logging must never crash the app */ }
        }
    }

    /// <summary>All rows, oldest-first. Used by UM5's LongitudinalSummaryBuilder
    /// (reads the recent tail) and by tests.</summary>
    public IReadOnlyList<DailyBehaviorAggregate> ReadAll()
    {
        lock (_gate) { return ReadAllNoLock(); }
    }

    private List<DailyBehaviorAggregate> ReadAllNoLock()
    {
        var list = new List<DailyBehaviorAggregate>();
        try
        {
            if (!File.Exists(_path)) return list;
            foreach (string line in File.ReadAllLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<DailyBehaviorAggregate>(line, _opts);
                    if (r is not null) list.Add(r);
                }
                catch { /* skip a malformed line, keep the rest */ }
            }
        }
        catch
        {
            // File locked / permission denied / unreadable → fail CLOSED with whatever
            // parsed so far. This read feeds the chatbot's [behavior_trend] block; it
            // must never throw into the chat turn (would surface as a raw "[Error]").
        }
        return list;
    }

    /// <summary>LOGIC-06/H16: atomic full-file rewrite. The old File.WriteAllText
    /// truncated the file first, so a crash mid-write destroyed ALL prior UM1/UM5
    /// history. We follow the <see cref="AtomicJsonWriter"/> pattern — write the whole
    /// JSONL body to a tmp file, flush it to disk, then File.Replace (or File.Move on a
    /// first-ever write) so a reader always sees either the complete old file or the
    /// complete new one, never a torn state. We can't reuse AtomicJsonWriter.TryWrite&lt;T&gt;
    /// directly: it serialises ONE value with indented options, whereas this file is
    /// compact snake_case JSONL with one object per line.</summary>
    private void RewriteNoLock(List<DailyBehaviorAggregate> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.Append(JsonSerializer.Serialize(r, _opts)).Append(Environment.NewLine);

        string tmp = _path + ".tmp";
        try
        {
            // WriteThrough + Flush(flushToDisk:true) forces the bytes down before the
            // rename, so a crash just after File.Replace can't leave a renamed-but-
            // unpersisted file.
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
                // overwrite:true guards the Exists→Move TOCTOU window (NE23/H46 pattern,
                // same as AtomicJsonWriter).
                File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Leave the previous file untouched (the whole point of the atomic rewrite).
            // Clean up the tmp so repeated failures don't leak a growing pile of *.tmp,
            // then let the outer best-effort catch swallow it.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
