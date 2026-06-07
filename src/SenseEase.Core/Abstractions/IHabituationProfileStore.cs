using System;
using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>Persistence interface for the habituation-profile snapshot
/// (separate from <see cref="IHabituationStore"/>, which stores offset only).
/// See docs/HABITUATION-PROFILE.md §5 for schema + write-trigger semantics.</summary>
public interface IHabituationProfileStore
{
    /// <summary>Load the most recent persisted snapshot. Returns <c>null</c>
    /// when the file is missing, empty, malformed, or has an unknown
    /// schema_version (per Q2 fall-back rule). Caller treats <c>null</c> as
    /// "use defaults" — i.e. RESILIENT + default state.</summary>
    HabituationProfileSnapshot? Load();

    /// <summary>Atomically persist the snapshot. Best-effort: never throws.
    /// Implementations should use tmp + File.Replace so a Process.Kill
    /// mid-write leaves the destination file in a coherent state (old or
    /// new, never partial).</summary>
    void Save(HabituationProfileSnapshot snapshot);

    /// <summary>W2.0-C: atomic read-modify-write. The same backing file is
    /// written by two independent threads — the engine's profile-snapshot
    /// persist and the chatbot's <c>LongitudinalObservationWriter</c> — and a
    /// bare Load…mutate…Save in each loses updates when they interleave
    /// (last-writer-wins clobbers the other's observation / strike / advisory
    /// fields). <paramref name="mutate"/> receives the current snapshot
    /// (<c>null</c> when none exists or a transient read failed) and returns the
    /// snapshot to persist, or <c>null</c> to skip the write entirely (dedup hit
    /// or "don't clobber on transient read failure").
    /// <para>This default implementation merely composes <see cref="Load"/> +
    /// <see cref="Save"/> and is therefore <b>NOT</b> atomic — adequate only for
    /// single-threaded test fakes.
    /// <see cref="T:SenseEase.Infrastructure.Storage.FileHabituationProfileStore"/>
    /// overrides it to hold its internal lock across the whole span.</para></summary>
    void Update(Func<HabituationProfileSnapshot?, HabituationProfileSnapshot?> mutate)
    {
        var next = mutate(Load());
        if (next is not null) Save(next);
    }
}
