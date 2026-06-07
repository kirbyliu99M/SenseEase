using System;
using System.Collections.Generic;
using System.IO;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// User Model sprint: file-backed <see cref="IUserModelStore"/>. The index
/// (<c>user_model_index.json</c>, UM3) is written atomically via
/// <see cref="AtomicJsonWriter"/>. The H24 clear path also deletes
/// <c>behavior_log.jsonl</c> and truncates the habituation profile's observation
/// memory — wired in via the optional behaviour-log path + profile store so this
/// one type owns the whole "user-model files" surface.
/// </summary>
public sealed class UserModelIndexStore : IUserModelStore
{
    private readonly string _path;
    private readonly string? _behaviorLogPath;
    private readonly IHabituationProfileStore? _profileStore;
    private readonly object _gate = new();

    /// <param name="path">user_model_index.json path (required).</param>
    /// <param name="behaviorLogPath">behavior_log.jsonl path; null disables its
    /// deletion in <see cref="ClearUserModel"/> (e.g. index-only unit tests).</param>
    /// <param name="profileStore">habituation profile store, for observation-bucket
    /// access + truncation; null disables those (index-only contexts).</param>
    public UserModelIndexStore(
        string path,
        string? behaviorLogPath = null,
        IHabituationProfileStore? profileStore = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _behaviorLogPath = behaviorLogPath;
        _profileStore = profileStore;
    }

    public UserModelIndex LoadIndex()
    {
        lock (_gate)
        {
            return AtomicJsonWriter.TryRead<UserModelIndex>(_path) ?? new UserModelIndex();
        }
    }

    public void SaveIndex(UserModelIndex index)
    {
        if (index is null) return;
        lock (_gate)
        {
            AtomicJsonWriter.TryWrite(_path, index);
        }
    }

    public IReadOnlyDictionary<string, List<ObservationEntry>> GetObservationBuckets()
    {
        var snap = _profileStore?.Load();
        return snap?.LongitudinalObservationsV2
               ?? new Dictionary<string, List<ObservationEntry>>();
    }

    public void TruncateObservations()
    {
        // Q4.5: observation memory ONLY. The atomic Update preserves every other
        // field (profile, strikes, engine_state, calibration_count,
        // personal_pressure_scale) and never touches the offset or live engine.
        _profileStore?.Update(snap =>
        {
            if (snap is null) return null;
            snap.LongitudinalObservations.Clear();
            snap.LongitudinalObservationsV2.Clear();
            return snap;
        });
    }

    public void ClearUserModel()
    {
        // 1. Behaviour log → gone.
        try
        {
            if (_behaviorLogPath is not null && File.Exists(_behaviorLogPath))
                File.Delete(_behaviorLogPath);
        }
        catch { /* best-effort PII cleanup */ }

        // 2. Index → zero state.
        SaveIndex(new UserModelIndex());

        // 3. Observation memory → truncated (structured deltas preserved; Q4.5-safe).
        TruncateObservations();
    }
}
