using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// File-backed habituation-progress store (a single JSON object). Best-effort I/O —
/// never throws to the caller; a missing or corrupt file simply yields no saved state.
/// </summary>
public sealed class FileHabituationStore : IHabituationStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public FileHabituationStore(string path)
    {
        _path = path;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { /* best-effort */ }
    }

    public HabituationState? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return null;
                string json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<HabituationState>(json, _opts);
            }
            catch { return null; }
        }
    }

    public void Save(HabituationState state)
    {
        lock (_gate)
        {
            try
            {
                string json = JsonSerializer.Serialize(state, _opts);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch { /* persistence must never crash the app */ }
        }
    }
}
