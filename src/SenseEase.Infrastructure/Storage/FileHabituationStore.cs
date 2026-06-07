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
                // H50: reject a reparse-pointed / base-escaping data path before
                // any File.* op. Derives the expected base from the same directory
                // the store already writes into (no hardcoding). A normal file in
                // a normal data dir — incl. the test suite's temp sandbox — passes.
                GuardPath();
                // Alpha-2 audit fix: read via AtomicJsonWriter.TryRead, which RETRIES a
                // transient IOException/lock (e.g. an on-access AV scanner momentarily holding
                // habituation.json at launch) before giving up. The old raw File.ReadAllText
                // returned null on the first lock, so the user's earned habituation _offset
                // (weeks of therapy) was treated as 0 for that whole session — the same
                // launch-time lock that previously reset user-preferences. Null on
                // genuinely-missing/corrupt is preserved (TryRead returns default = null).
                return AtomicJsonWriter.TryRead<HabituationState>(_path, _opts);
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
            GuardPath(); // H50: reject reparse-point / base-escape before writing
            AtomicJsonWriter.TryWrite(_path, state, _opts);
        }
    }

    /// <summary>H50: guard against a reparse-pointed or out-of-base data path.
    /// The expected base is the directory this store was constructed to live in
    /// (<see cref="Path.GetDirectoryName(string)"/> of <c>_path</c>), so a legit
    /// file inside that dir is untouched; only a symlink/junction or a base
    /// escape is rejected (<see cref="System.Security.SecurityException"/>).</summary>
    private void GuardPath()
    {
        string? baseDir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(baseDir))
            PathValidator.EnsureSafe(_path, baseDir);
    }
}
