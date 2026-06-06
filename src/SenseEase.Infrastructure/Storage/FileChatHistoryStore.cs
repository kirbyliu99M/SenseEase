using System.Text.Json;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// File-backed chat transcript store (JSON Lines — one ChatMessage per line). The
/// transcript is non-essential UX state, so every operation is best-effort: I/O
/// failures and malformed lines are swallowed silently and never propagate to the
/// chat path.
/// </summary>
public sealed class FileChatHistoryStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = false };

    // Huge-line DoS guard: a single line longer than this is skipped rather than
    // buffered/parsed. A well-formed ChatMessage is far smaller; an oversized line
    // is corruption or an attempt to exhaust memory.
    private const int MaxLineChars = 64 * 1024;

    public FileChatHistoryStore(string path)
    {
        _path = path;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Reads existing transcript from disk. Returns empty list if file
    /// missing, unreadable, or corrupt (best-effort — history is non-essential).
    /// Lines that fail to parse are skipped silently.</summary>
    public IReadOnlyList<ChatMessage> Load()
    {
        lock (_gate)
        {
            return ReadAllLocked();
        }
    }

    /// <summary>Streams the transcript off disk and parses each line. Caller must
    /// hold <see cref="_gate"/>. Uses File.ReadLines (lazy, one line at a time) so
    /// the whole file is never buffered into an array, and skips any single line
    /// longer than <see cref="MaxLineChars"/> as a huge-line DoS guard.</summary>
    private List<ChatMessage> ReadAllLocked()
    {
        var result = new List<ChatMessage>();
        try
        {
            // H50: reject a reparse-pointed / base-escaping path before reading.
            // Wrapped in the existing best-effort try so the chat path never
            // crashes — a normal missing file (or normal existing file) passes
            // the guard; only a symlink/junction or base escape is rejected,
            // and even then we degrade to an empty transcript rather than throw.
            GuardPath();
            if (!File.Exists(_path)) return result;
            foreach (string line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > MaxLineChars) continue; // skip oversized line
                try
                {
                    var m = JsonSerializer.Deserialize<ChatMessage>(line, _opts);
                    if (m is not null) result.Add(m);
                }
                catch { /* skip malformed line */ }
            }
        }
        catch { /* best-effort */ }
        return result;
    }

    /// <summary>Append one message to the transcript file. Best-effort: silently
    /// swallows I/O errors so a write failure can never crash the chat path.</summary>
    public void Append(ChatMessage message)
    {
        if (message is null) return;
        lock (_gate)
        {
            try
            {
                GuardPath(); // H50: reject reparse-point / base-escape before writing
                File.AppendAllText(_path, JsonSerializer.Serialize(message, _opts) + Environment.NewLine);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Rewrites the file with at most maxKeep most-recent messages. Call
    /// after Load() returns more than maxKeep so the file doesn't grow forever.
    /// Atomic via temp-file-then-move pattern.</summary>
    public void Trim(int maxKeep)
    {
        if (maxKeep < 0) maxKeep = 0;
        lock (_gate)
        {
            try
            {
                GuardPath(); // H50: reject reparse-point / base-escape before rewrite
                if (!File.Exists(_path)) return;
                var all = ReadAllLocked();
                if (all.Count <= maxKeep) return;

                var keep = all.Skip(all.Count - maxKeep).ToList();
                string tmp = _path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs))
                {
                    foreach (var m in keep)
                        sw.WriteLine(JsonSerializer.Serialize(m, _opts));
                }
                // File.Move with overwrite is atomic on the same volume on Windows / .NET 8.
                File.Move(tmp, _path, overwrite: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Delete the underlying file. Best-effort.</summary>
    public void DeleteFile()
    {
        lock (_gate)
        {
            try
            {
                GuardPath(); // H50: reject reparse-point / base-escape before delete
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>H50: guard against a reparse-pointed or out-of-base transcript
    /// path. Expected base is the directory this store lives in
    /// (<see cref="Path.GetDirectoryName(string)"/> of <c>_path</c>) — no
    /// hardcoding. A legit file inside that dir is untouched; only a
    /// symlink/junction or base escape throws
    /// <see cref="System.Security.SecurityException"/>.
    /// All callers already wrap this in a best-effort try, preserving the
    /// chat-history "never crash the chat path" contract.</summary>
    private void GuardPath()
    {
        string? baseDir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(baseDir))
            PathValidator.EnsureSafe(_path, baseDir);
    }
}
