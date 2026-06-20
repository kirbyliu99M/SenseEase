using System;

namespace SenseEase.Core.Abstractions;

/// <summary>B6 Stage 4b — the bidirectional elasticity classifier's durable UP-qualify streak. It is
/// RECONSTRUCTED from disk each tuning pass (never held in memory, so a crash/restart can't fabricate a
/// streak), and carries its own <see cref="LastQualifyUtc"/> so the App enforces per-eval SPACING (a
/// qualify only advances the streak when it is ≥ RelaxMinIntervalDays after the last counted one — two
/// minutes-apart passes are the SAME eval, not two).</summary>
public sealed record ProfileEvalHistory(int ConsecutiveUpQualify, DateTime LastQualifyUtc)
{
    /// <summary>The fail-closed default: no banked streak.</summary>
    public static ProfileEvalHistory Empty => new(0, DateTime.MinValue);
}

/// <summary>Durable store for <see cref="ProfileEvalHistory"/> (one small JSON file under
/// <c>%LOCALAPPDATA%\SenseEase</c>). Load fails CLOSED (streak 0) on a missing/corrupt file.</summary>
public interface IProfileEvalHistoryStore
{
    /// <summary>Load the persisted streak; <see cref="ProfileEvalHistory.Empty"/> on missing/corrupt.</summary>
    ProfileEvalHistory Load();

    /// <summary>Atomically persist the streak.</summary>
    void Save(ProfileEvalHistory history);

    /// <summary>Reset the streak to empty — called on a personalization Reset so a stale streak can never
    /// fire an UP-move right after the user resets.</summary>
    void Clear();
}
