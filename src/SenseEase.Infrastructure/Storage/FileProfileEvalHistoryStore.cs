using System.IO;
using SenseEase.Core.Abstractions;

namespace SenseEase.Infrastructure.Storage;

/// <summary>B6 Stage 4b — <see cref="IProfileEvalHistoryStore"/> backed by a single crash-safe JSON file
/// (<c>%LOCALAPPDATA%\SenseEase\profile_eval_history.json</c>) via <see cref="AtomicJsonWriter"/>. Load
/// fails CLOSED (streak 0) on a missing / malformed / still-locked file — the same fail-toward-protection
/// posture as the rest of the adaptation pipeline.</summary>
public sealed class FileProfileEvalHistoryStore : IProfileEvalHistoryStore
{
    private readonly string _path;

    public FileProfileEvalHistoryStore(string path) => _path = path;

    public ProfileEvalHistory Load()
        => AtomicJsonWriter.TryRead<ProfileEvalHistory>(_path) ?? ProfileEvalHistory.Empty;

    public void Save(ProfileEvalHistory history)
        => AtomicJsonWriter.TryWrite(_path, history);

    public void Clear()
    {
        // Best-effort: a delete failure (AV lock) just leaves a stale file; the next Save overwrites it
        // and the LastQualifyUtc spacing + the disqualify-reset keep a stale streak from firing prematurely.
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort */ }
    }
}
