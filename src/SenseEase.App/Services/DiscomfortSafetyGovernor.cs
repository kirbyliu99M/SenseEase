using System;
using System.Collections.Generic;

namespace SenseEase.App.Services;

/// <summary>
/// Item 5 — a small, clock-injectable in-memory throttle. When the user reports discomfort
/// repeatedly in a short window, the chatbot should advise RESTING THE EYES rather than piling on
/// more auto-actuation. This is ADVICE, not a hard block: the user can keep talking; only the
/// triggering turn's auto-adjustment is held. The genuine self-harm / crisis floor is a separate,
/// upstream concern (SafetyFloor) — this governor is purely about over-adjustment fatigue.
///
/// <para>Mirrors <c>CheckInGovernor</c>'s pure, clock-injectable style so it is trivially unit
/// testable with a fake clock.</para>
/// </summary>
public sealed class DiscomfortSafetyGovernor
{
    private readonly Func<DateTime> _clock;
    private readonly Queue<DateTime> _events = new();

    /// <summary>Rolling window over which repeated reports are counted (default 5 min).</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Count within the window that triggers the rest advice (default 3).</summary>
    public int Threshold { get; set; } = 3;

    public DiscomfortSafetyGovernor(Func<DateTime>? utcClock = null)
        => _clock = utcClock ?? (() => DateTime.UtcNow);

    /// <summary>Record a discomfort report and return true iff this record reaches the threshold
    /// within the rolling window — i.e. the chatbot should advise rest THIS turn.</summary>
    public bool RecordAndShouldAdvise()
    {
        var now = _clock();
        _events.Enqueue(now);
        Trim(now);
        return _events.Count >= Threshold;
    }

    /// <summary>Current count within the window WITHOUT recording — lets other surfaces read the
    /// recent discomfort load (e.g. to bias the companion's tone).</summary>
    public int CountInWindow()
    {
        Trim(_clock());
        return _events.Count;
    }

    /// <summary>Forget all recorded events (e.g. when the chat is cleared / a fresh session begins).</summary>
    public void Reset() => _events.Clear();

    private void Trim(DateTime now)
    {
        while (_events.Count > 0 && now - _events.Peek() > Window) _events.Dequeue();
    }
}
