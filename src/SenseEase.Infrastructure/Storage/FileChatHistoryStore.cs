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
        var result = new List<ChatMessage>();
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return result;
                foreach (string line in File.ReadAllLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var m = JsonSerializer.Deserialize<ChatMessage>(line, _opts);
                        if (m is not null) result.Add(m);
                    }
                    catch { /* skip malformed line */ }
                }
            }
            catch { /* best-effort */ }
        }
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
                if (!File.Exists(_path)) return;
                var all = Load();
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
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch { /* best-effort */ }
        }
    }
}
