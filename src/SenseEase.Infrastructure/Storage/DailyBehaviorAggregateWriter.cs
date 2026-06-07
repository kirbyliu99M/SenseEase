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
    private readonly object _gate = new();

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

    private void RewriteNoLock(List<DailyBehaviorAggregate> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.Append(JsonSerializer.Serialize(r, _opts)).Append(Environment.NewLine);
        File.WriteAllText(_path, sb.ToString());
    }
}
