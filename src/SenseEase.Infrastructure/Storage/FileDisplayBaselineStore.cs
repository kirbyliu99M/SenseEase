using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// File-backed display-baseline store (single JSON object, atomic temp+move write).
/// Best-effort — never throws. Lets a force-killed session be recovered next launch.
/// </summary>
public sealed class FileDisplayBaselineStore : IDisplayBaselineStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public FileDisplayBaselineStore(string path)
    {
        _path = path;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { /* best-effort */ }
    }

    public DisplayBaselineRecord? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return null;
                string json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<DisplayBaselineRecord>(json, _opts);
            }
            catch { return null; }
        }
    }

    public void Save(DisplayBaselineRecord record)
    {
        lock (_gate)
        {
            try
            {
                string json = JsonSerializer.Serialize(record, _opts);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch { /* persistence must never crash the app */ }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch { /* best-effort */ }
        }
    }
}
