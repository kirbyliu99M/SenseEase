using System.Diagnostics;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>File-backed habituation-profile snapshot store. Atomic write via
/// <see cref="AtomicJsonWriter"/> (tmp + File.Replace). Unknown
/// schema_version → null + Debug.WriteLine warning per Q2 fall-back.
/// See docs/HABITUATION-PROFILE.md §5 for the full schema and write triggers.</summary>
public sealed class FileHabituationProfileStore : IHabituationProfileStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private const int SupportedSchemaVersion = 1;

    public FileHabituationProfileStore(string path)
    {
        _path = path;
    }

    public HabituationProfileSnapshot? Load()
    {
        lock (_gate)
        {
            var snap = AtomicJsonWriter.TryRead<HabituationProfileSnapshot>(_path);
            if (snap is null) return null;

            // Q2 schema-version fall-back. Unknown version → null + diagnostic.
            // We deliberately do NOT open a separate engine.log here; per Opus
            // Option 1, Debug.WriteLine is visible to an attached debugger and
            // silent in prod, which is the right cost/benefit for this
            // expected-rare path.
            if (snap.SchemaVersion != SupportedSchemaVersion)
            {
                Debug.WriteLine(
                    $"[HabituationProfile] Unknown schema_version {snap.SchemaVersion} " +
                    $"in {_path}; falling back to RESILIENT defaults.");
                return null;
            }

            return snap;
        }
    }

    public void Save(HabituationProfileSnapshot snapshot)
    {
        lock (_gate)
        {
            // AtomicJsonWriter handles tmp + File.Replace + first-write
            // fallback. Returns false on failure but we never surface that
            // to the caller — persistence must not crash the app, and the
            // next write attempt will retry.
            AtomicJsonWriter.TryWrite(_path, snapshot);
        }
    }
}
