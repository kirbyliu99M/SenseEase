using System;

namespace SenseEase.App.Services;

/// <summary>
/// Item 1 — a small, clock-injectable cooldown for the "start the comfort overlay?" consent prompt.
/// After the user declines / dismisses, the prompt is suppressed for <see cref="Cooldown"/> so a
/// flickering engine mask can't nag them; otherwise it may ask on each fresh engagement. In-memory
/// (resets on restart), mirroring <c>CheckInGovernor</c>.
/// </summary>
public sealed class OverlayConsentGovernor
{
    private readonly Func<DateTime> _clock;
    private DateTime? _cooldownUntilUtc;

    /// <summary>Quiet window after a decline / dismiss before the prompt may show again.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(10);

    public OverlayConsentGovernor(Func<DateTime>? utcClock = null)
        => _clock = utcClock ?? (() => DateTime.UtcNow);

    /// <summary>True when a fresh prompt is allowed (no active cooldown).</summary>
    public bool ShouldAsk() => !(_cooldownUntilUtc is { } until && _clock() < until);

    /// <summary>User agreed → clear any cooldown so the gate behaves normally next time.</summary>
    public void RecordAccepted() => _cooldownUntilUtc = null;

    /// <summary>User declined / dismissed → start the quiet window.</summary>
    public void RecordDeclinedOrDismissed() => _cooldownUntilUtc = _clock() + Cooldown;
}
