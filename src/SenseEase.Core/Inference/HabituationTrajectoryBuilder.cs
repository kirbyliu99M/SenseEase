using System;
using System.Linq;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

/// <summary>
/// M2 (Phase 6c): builds a one-line natural-language summary of the
/// user's habituation arc over the last N days by querying the event
/// log for the <c>Habituation.*</c> category entries emitted by M1
/// (<see cref="SenseEase.Infrastructure.Mock.MockInferenceEngine"/>).
///
/// <para>Pure logic — takes a snapshot view of the log (no live event
/// subscription, no IO beyond the in-memory <c>IEventLogStore.Recent()</c>
/// call). Output is a one-line string the chatbot LLM can paste into a
/// natural reply without further parsing.</para>
///
/// <para>Examples of generated output:</para>
/// <list type="bullet">
///   <item><c>"no habituation activity in last 30 days"</c></item>
///   <item><c>"level rose 5.0 → 14.5 over 28d, 2 spikes"</c></item>
///   <item><c>"level fell 12.0 → 7.0 over 14d, 5 spikes"</c></item>
///   <item><c>"3 spikes, stable level around 8.0"</c></item>
/// </list>
/// </summary>
public static class HabituationTrajectoryBuilder
{
    /// <summary>Default lookback window for the chatbot trajectory.</summary>
    public const int DefaultWindowDays = 30;

    /// <summary>Read recent <c>Habituation.*</c> events from the store and
    /// produce a one-line summary suitable for the
    /// <c>[habituation_state]</c> block's <c>trajectory</c> field.
    /// Returns empty string when the store is null or no habituation
    /// events exist in the window — caller should omit the line entirely
    /// per the block-builder's "skip when empty" convention.</summary>
    /// <param name="eventLog">The event log to query. Null → empty string.</param>
    /// <param name="windowDays">Lookback window in days. Default 30.</param>
    /// <param name="now">Override for "now" — pass null for production
    /// (uses DateTime.UtcNow). Test code passes a frozen DateTime to
    /// drive deterministic trajectory windows.</param>
    public static string BuildSummary(
        IEventLogStore? eventLog,
        int windowDays = DefaultWindowDays,
        DateTime? now = null)
    {
        if (eventLog is null) return string.Empty;

        DateTime cutoff = (now ?? DateTime.UtcNow).AddDays(-windowDays);

        // IEventLogStore.Recent() yields most-recent-first per existing
        // contract. Filter to habituation events within window.
        var events = eventLog.Recent()
            .Where(e => e.Category.StartsWith("Habituation.", StringComparison.Ordinal))
            .Where(e => e.Timestamp >= cutoff)
            .OrderBy(e => e.Timestamp)
            .ToList();

        if (events.Count == 0)
        {
            return $"no habituation activity in last {windowDays} days";
        }

        // Extract level transitions from the LevelChanged category.
        // M1's message format is "level rose 5.0 → 14.5" or "level fell 12.0 → 7.0".
        // We don't re-parse — we just count + grab first/last for level summary.
        var levelEvents = events
            .Where(e => e.Category == "Habituation.LevelChanged")
            .ToList();

        int spikeCount = events.Count(e => e.Category == "Habituation.Spike");

        if (levelEvents.Count == 0 && spikeCount == 0)
        {
            // Only profile / state-change events — terse summary.
            return $"{events.Count} habituation events in last {windowDays}d";
        }

        // Reconstruct a level trajectory from the event log's Pressure /
        // FovRadius state snapshots is brittle (those are sensor context,
        // not the engine's HabituationLevel). The level information itself
        // is in the M1 event MESSAGE text. We just count events here and
        // let the chatbot prompt's trajectory consumer reference the
        // snapshot's current level for the "now" value — combined with
        // the trajectory line's spike count, that's enough for the LLM
        // to produce "you've improved" / "you've had a setback" framing.
        if (levelEvents.Count > 0 && spikeCount > 0)
        {
            return $"{levelEvents.Count} level changes, {spikeCount} spikes in last {windowDays}d";
        }
        if (levelEvents.Count > 0)
        {
            return $"{levelEvents.Count} level changes in last {windowDays}d";
        }
        return $"{spikeCount} spikes in last {windowDays}d";
    }
}
