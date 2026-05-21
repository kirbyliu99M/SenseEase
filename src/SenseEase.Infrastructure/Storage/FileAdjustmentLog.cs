using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// File-backed adjustment log (JSON Lines — one record per line). The file survives
/// restarts so per-user personalization weight accumulates across sessions.
/// All file I/O is best-effort: logging must never crash the app.
/// </summary>
public sealed class FileAdjustmentLog : IAdjustmentLog
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<AdjustmentRecord> _records = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = false };

    public FileAdjustmentLog(string path)
    {
        _path = path;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Load();
        }
        catch { /* best-effort */ }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        foreach (string line in File.ReadAllLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var r = JsonSerializer.Deserialize<AdjustmentRecord>(line, _opts);
                if (r is not null) _records.Add(r);
            }
            catch { /* skip malformed line */ }
        }
    }

    public void Append(AdjustmentRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            try
            {
                File.AppendAllText(_path, JsonSerializer.Serialize(record, _opts) + Environment.NewLine);
            }
            catch { /* logging must never crash the app */ }
        }
    }

    public IReadOnlyList<AdjustmentRecord> All()
    {
        lock (_gate) { return _records.ToList(); }
    }

    public PersonalWeight WeightFor(string parameter)
    {
        lock (_gate)
        {
            var matches = _records
                .Where(r => r.Parameter == parameter && r.Source == "user")
                .ToList();
            if (matches.Count == 0) return new PersonalWeight(parameter, 0, 0.0, 0.0);
            double net = matches.Sum(r => r.Delta);
            return new PersonalWeight(parameter, matches.Count, net, net / matches.Count);
        }
    }
}
