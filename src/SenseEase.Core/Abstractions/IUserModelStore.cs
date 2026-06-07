using System.Collections.Generic;
using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>
/// User Model sprint: persistence facade for the passive user-model state — the
/// analysis index + bucket manifest (<c>user_model_index.json</c>, UM3), the
/// monthly observation buckets (UM2, owned by the habituation profile), and the
/// H24 "clear all my data" path.
/// </summary>
public interface IUserModelStore
{
    /// <summary>Load the index, or a fresh zero-state instance when the file is
    /// missing / unreadable. Never returns null.</summary>
    UserModelIndex LoadIndex();

    /// <summary>Atomically persist the index (tmp + File.Replace). Best-effort —
    /// never throws.</summary>
    void SaveIndex(UserModelIndex index);

    /// <summary>UM2/H24: read-only view of the habituation profile's monthly
    /// observation buckets (keyed "YYYY-MM"). Empty when none / no profile store.</summary>
    IReadOnlyDictionary<string, List<ObservationEntry>> GetObservationBuckets();

    /// <summary>UM2/H24: clear ALL longitudinal observation memory (flat v2 list +
    /// monthly buckets) from the habituation profile, preserving the structured
    /// deltas — profile, strikes, engine state, calibration_count,
    /// personal_pressure_scale. <b>Q4.5: never resets the habituation offset or the
    /// live engine</b> — this only edits the persisted observation memory.</summary>
    void TruncateObservations();

    /// <summary>H24 "clear all my data": delete <c>behavior_log.jsonl</c>, reset the
    /// index to zero-state, and <see cref="TruncateObservations"/> (structured deltas
    /// preserved). <b>Q4.5: never calls the engine's Reset / zeroes <c>_offset</c></b> —
    /// it wipes user-model files only. Best-effort; never throws.</summary>
    void ClearUserModel();
}
