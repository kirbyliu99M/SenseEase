using System;
using System.Collections.Generic;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

/// <summary>
/// UM1 (User Model sprint) — the live <b>collector</b> behind
/// <see cref="DailyBehaviorAggregate"/>. A pure, in-memory, single-day accumulator:
/// the App layer feeds it one engine tick at a time via <see cref="Sample"/> and
/// periodically materialises a numeric-only daily row via <see cref="BuildAggregate"/>
/// for <c>DailyBehaviorAggregateWriter</c> to persist. No IO, no PII — only counts and
/// percentiles, so it unit-tests in isolation and never touches disk itself.
///
/// <para><b>Memory-bounded.</b> OFI and tier samples are folded into frequency
/// histograms (≤ a few hundred small int keys), not stored per-sample — so a full day
/// at ~3 Hz costs O(distinct values), not O(ticks).</para>
///
/// <para><b>Same-day seed-merge.</b> If the app is restarted mid-day, the App layer
/// re-opens the day by passing the already-persisted row as <paramref name="seed"/> to
/// <see cref="Begin"/>. Additive signals (spikes, mask activations, stable windows,
/// session minutes, net offset delta) telescope correctly across sessions. The
/// distribution signals (OFI p50/p90, modal tier) reflect predominantly the latest
/// session — the stored row keeps only the percentiles, not the histogram, so an exact
/// cross-session merge isn't reconstructable; the live session (thousands of samples)
/// dominates any seed point. Documented limitation, acceptable for a 30-day trend.</para>
///
/// <para><b>Q4.5-safe.</b> Read-only with respect to the engine — it observes ticks and
/// the engine's habituation offset; it never writes the engine or the habituation state.</para>
/// </summary>
public sealed class DailyBehaviorAccumulator
{
    // Folded distributions (value → occurrence count). SortedDictionary so the
    // percentile walk visits keys in ascending order.
    private readonly SortedDictionary<int, int> _ofiHist = new();
    private readonly SortedDictionary<int, int> _tierHist = new();

    // Additive base carried over from an earlier same-day session (seed-merge).
    private int _baseSpikes;
    private int _baseMask;
    private int _baseStable;
    private int _baseMinutes;
    private double _baseOffsetDelta;
    private int _seedOfiP50, _seedOfiP90, _seedTierModal;
    private bool _hasSeed;

    // This session's live deltas.
    private int _spikes;
    private int _mask;
    private int _stable;
    private int _startOffsetPp;
    private int _lastOffsetPp;
    private int _prevLevel;
    private DateTime _firstSampleLocal;
    private DateTime _lastSampleLocal;
    private long _sampleCount;

    /// <summary>The calendar day ("YYYY-MM-DD") this accumulator is currently open
    /// for, or <c>null</c> before <see cref="Begin"/> / after <see cref="Reset"/>.</summary>
    public string? Date { get; private set; }

    /// <summary>Open the accumulator for <paramref name="date"/>. Pass the existing
    /// on-disk row for that date as <paramref name="seed"/> to continue a same-day
    /// session (additive signals resume from it); pass null for a brand-new day.
    /// <paramref name="startOffsetPp"/> / <paramref name="startLevel"/> anchor the
    /// day's net offset delta and stable-window edge counting.</summary>
    public void Begin(string date, DailyBehaviorAggregate? seed, int startOffsetPp, int startLevel, DateTime nowLocal)
    {
        Reset();
        Date = date;
        _startOffsetPp = startOffsetPp;
        _lastOffsetPp = startOffsetPp;
        _prevLevel = startLevel;
        _firstSampleLocal = nowLocal;
        _lastSampleLocal = nowLocal;

        if (seed is not null)
        {
            _hasSeed = true;
            _baseSpikes = seed.SpikeCount;
            _baseMask = seed.MaskActivations;
            _baseStable = seed.StableWindowsCompleted;
            _baseMinutes = seed.SessionMinutes;
            _baseOffsetDelta = seed.OffsetDeltaPp;
            _seedOfiP50 = seed.OfiP50;
            _seedOfiP90 = seed.OfiP90;
            _seedTierModal = seed.TierModal;
        }
    }

    /// <summary>Fold one engine tick into the day. <paramref name="maskRising"/> and
    /// <paramref name="spike"/> are rising-edge booleans (count, not level), supplied
    /// by the caller which already derives them. <paramref name="offsetPp"/> is the
    /// engine's live habituation offset; <paramref name="level"/> its habituation
    /// level (level-ups are counted as completed stable windows).</summary>
    public void Sample(int ofi, int tier, bool maskRising, bool spike, int offsetPp, int level, DateTime nowLocal)
    {
        if (Date is null) return; // not opened — caller contract violation, fail soft
        Bump(_ofiHist, ofi < 0 ? 0 : ofi);
        Bump(_tierHist, tier);
        if (spike) _spikes++;
        if (maskRising) _mask++;
        if (level > _prevLevel) _stable += level - _prevLevel;
        _prevLevel = level;
        _lastOffsetPp = offsetPp;
        _lastSampleLocal = nowLocal;
        _sampleCount++;
    }

    /// <summary>Materialise the current day as a persistable row, or <c>null</c> when
    /// nothing has been sampled and there is no seed to re-emit. The numeric-only /
    /// PII annotations (<see cref="DailyBehaviorAggregate.Exportable"/>=false,
    /// <see cref="DailyBehaviorAggregate.DeletableOnClear"/>=true) come from the DTO
    /// defaults.</summary>
    public DailyBehaviorAggregate? BuildAggregate()
    {
        if (Date is null) return null;
        if (_sampleCount == 0 && !_hasSeed) return null;

        int elapsedMin = (int)Math.Round((_lastSampleLocal - _firstSampleLocal).TotalMinutes);
        if (elapsedMin < 0) elapsedMin = 0;

        bool haveOfi = _ofiHist.Count > 0;
        bool haveTier = _tierHist.Count > 0;

        return new DailyBehaviorAggregate
        {
            Date = Date,
            SessionMinutes = Clamp1440(_baseMinutes + elapsedMin),
            OfiP50 = haveOfi ? Percentile(_ofiHist, 0.50) : _seedOfiP50,
            OfiP90 = haveOfi ? Percentile(_ofiHist, 0.90) : _seedOfiP90,
            TierModal = haveTier ? Modal(_tierHist) : _seedTierModal,
            SpikeCount = _baseSpikes + _spikes,
            StableWindowsCompleted = _baseStable + _stable,
            OffsetDeltaPp = _baseOffsetDelta + (_lastOffsetPp - _startOffsetPp),
            MaskActivations = _baseMask + _mask,
        };
    }

    /// <summary>Clear all state back to the unopened condition.</summary>
    public void Reset()
    {
        _ofiHist.Clear();
        _tierHist.Clear();
        _baseSpikes = _baseMask = _baseStable = _baseMinutes = 0;
        _baseOffsetDelta = 0;
        _seedOfiP50 = _seedOfiP90 = _seedTierModal = 0;
        _hasSeed = false;
        _spikes = _mask = _stable = 0;
        _startOffsetPp = _lastOffsetPp = _prevLevel = 0;
        _sampleCount = 0;
        Date = null;
    }

    private static void Bump(SortedDictionary<int, int> hist, int key)
        => hist[key] = hist.TryGetValue(key, out int c) ? c + 1 : 1;

    private static int Clamp1440(int minutes) => minutes < 0 ? 0 : minutes > 1440 ? 1440 : minutes;

    /// <summary>Nearest-rank percentile over a frequency histogram. <paramref name="p"/>
    /// is a fraction in [0,1]; rank = ceil(p · N), 1-based.</summary>
    private static int Percentile(SortedDictionary<int, int> hist, double p)
    {
        long total = 0;
        foreach (int c in hist.Values) total += c;
        if (total == 0) return 0;

        long rank = (long)Math.Ceiling(p * total);
        if (rank < 1) rank = 1;

        long cum = 0;
        foreach (var kv in hist)
        {
            cum += kv.Value;
            if (cum >= rank) return kv.Key;
        }
        // Unreachable for a non-empty histogram; return the largest key defensively.
        int last = 0;
        foreach (var k in hist.Keys) last = k;
        return last;
    }

    /// <summary>Distribution mode — the most-frequent key. Ties resolve to the LOWER
    /// key (deterministic; SortedDictionary ascending + strict ">" keeps the first).</summary>
    private static int Modal(SortedDictionary<int, int> hist)
    {
        int bestKey = 0, bestCount = -1;
        foreach (var kv in hist)
        {
            if (kv.Value > bestCount) { bestCount = kv.Value; bestKey = kv.Key; }
        }
        return bestKey;
    }
}
