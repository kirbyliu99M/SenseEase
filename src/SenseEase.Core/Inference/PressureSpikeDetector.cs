using System;
using System.Collections.Generic;

namespace SenseEase.Core.Inference;

/// <summary>
/// Detects sharp relative rises in VIMS pressure within a sliding time window,
/// with a cooldown so a sustained elevated reading does not refire continuously.
/// Time is injected via the <c>nowUtc</c> argument on <see cref="Push"/> so the
/// detector is deterministic and trivially unit-testable.
/// </summary>
public sealed class PressureSpikeDetector
{
    private readonly double _windowSeconds;
    private readonly double _relativeThreshold;
    private readonly double _cooldownSeconds;
    private readonly double _minBaseline;

    private readonly Queue<(DateTime t, double p)> _samples = new();
    private DateTime? _lastFiredUtc;

    public PressureSpikeDetector(
        double windowSeconds = 3.0,
        double relativeThreshold = 0.30,
        double cooldownSeconds = 30.0,
        double minBaseline = 5.0)
    {
        _windowSeconds = windowSeconds;
        _relativeThreshold = relativeThreshold;
        _cooldownSeconds = cooldownSeconds;
        _minBaseline = minBaseline;
    }

    public double LastNormalizedSpike { get; private set; }

    public bool Push(double pressure, DateTime nowUtc)
    {
        _samples.Enqueue((nowUtc, pressure));

        // Evict samples older than the window, but keep the most recent
        // out-of-window sample as the baseline reference point. We do this by
        // peeking and dropping until either the queue is empty or the head is
        // within (nowUtc - window).
        while (_samples.Count > 1 && (nowUtc - _samples.Peek().t).TotalSeconds > _windowSeconds)
        {
            _samples.Dequeue();
        }

        // Cooldown: even if the condition still holds, suppress duplicate fires.
        // A sustained elevated reading after a spike should not nag the user
        // repeatedly — they have already been told.
        if (_lastFiredUtc.HasValue &&
            (nowUtc - _lastFiredUtc.Value).TotalSeconds < _cooldownSeconds)
        {
            return false;
        }

        // Need at least one prior sample to compute a rise.
        if (_samples.Count < 2) return false;

        double baseline = _samples.Peek().p;

        // Suppress firing when baseline is near zero — a rise from 0.1 to 1.0
        // is technically a "1000%" jump but is not interesting for a VIMS
        // pressure scale that runs to ~200.
        if (baseline < _minBaseline) return false;

        if (pressure >= baseline * (1.0 + _relativeThreshold))
        {
            LastNormalizedSpike = Math.Clamp((pressure - baseline) / Math.Max(baseline, 1.0), 0.0, 1.0);
            _lastFiredUtc = nowUtc;
            return true;
        }

        return false;
    }

    public void Reset()
    {
        _samples.Clear();
        _lastFiredUtc = null;
        LastNormalizedSpike = 0.0;
    }
}
