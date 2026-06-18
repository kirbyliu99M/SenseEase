using System;

namespace SenseEase.App.Services;

/// <summary>
/// Item 1 / R3 #5 — a small, clock-injectable cooldown for the "start the comfort overlay?" consent
/// prompt. The cooldown is timed from the ASK (when the bubble appears), so it runs in parallel with
/// an approved overlay session and is typically elapsed by the time the session ends — a fresh
/// session can then ask again. A decline also (re)starts it so a flickering engine can't nag. Plus a
/// <see cref="StartupQuiet"/> window after launch so the bubble can't flash while the engine settles.
/// In-memory (resets on restart), mirroring <c>CheckInGovernor</c>.
/// </summary>
public sealed class OverlayConsentGovernor
{
    private readonly Func<DateTime> _clock;
    private readonly DateTime _createdUtc;
    private DateTime? _cooldownUntilUtc;

    /// <summary>Quiet window timed from the ASK (and re-armed on decline) before re-prompting.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>R3 additional #1: no prompt within this window after launch (avoids a start-up flash
    /// while the engine/overlay settle). ~60 s.</summary>
    public TimeSpan StartupQuiet { get; set; } = TimeSpan.FromSeconds(60);

    public OverlayConsentGovernor(Func<DateTime>? utcClock = null)
    {
        _clock = utcClock ?? (() => DateTime.UtcNow);
        _createdUtc = _clock();
    }

    /// <summary>True when a fresh prompt is allowed (past the startup-quiet window AND not cooling down).</summary>
    public bool ShouldAsk()
    {
        var now = _clock();
        if (now - _createdUtc < StartupQuiet) return false;
        return !(_cooldownUntilUtc is { } until && now < until);
    }

    /// <summary>Call when the bubble is shown — the cooldown is timed from here (R3 #5).</summary>
    public void RecordAsked() => _cooldownUntilUtc = _clock() + Cooldown;

    /// <summary>User agreed → the overlay session owns re-asking now; keep the from-ask cooldown so a
    /// brief session that ends and re-rises within the window doesn't immediately re-prompt.</summary>
    public void RecordAccepted() { }

    /// <summary>User declined / dismissed → (re)start the quiet window from now.</summary>
    public void RecordDeclinedOrDismissed() => _cooldownUntilUtc = _clock() + Cooldown;
}
