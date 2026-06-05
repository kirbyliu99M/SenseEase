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
        // H16 / NE15: route through AtomicJsonWriter so the progress file gets the
        // crash-safe tmp -> flush-to-disk -> File.Replace path instead of the old
        // inline File.Move (which could leave a truncated file on a kill mid-write
        // and zero the user's earned offset). _opts is passed so the on-disk JSON
        // shape stays byte-identical to existing habituation.json files.
        lock (_gate)
        {
            AtomicJsonWriter.TryWrite(_path, state, _opts);
        }
    }
}
