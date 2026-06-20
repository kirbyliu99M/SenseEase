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

    /// <summary>Cooldown after an EXPLICIT "Not now" decline — a strong signal, so re-prompt slowly.
    /// (Kirby 2026-06-20: decline → 30 min.)</summary>
    public TimeSpan DeclineCooldown { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Cooldown when the prompt went UNANSWERED (it was retracted because the engine mask
    /// subsided before the user tapped). A weaker signal than a decline, so re-ask sooner. Also the
    /// baseline timed from the ask. (Kirby 2026-06-20: no answer → 3 min.)</summary>
    public TimeSpan NoAnswerCooldown { get; set; } = TimeSpan.FromMinutes(3);

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

    /// <summary>Call when the bubble is shown — sets the BASELINE short cooldown from the ask (so a granted
    /// session that ends, or a flickering engine, doesn't immediately re-prompt). An explicit decline or an
    /// unanswered-dismiss overrides it below.</summary>
    public void RecordAsked() => _cooldownUntilUtc = _clock() + NoAnswerCooldown;

    /// <summary>User agreed → the overlay session owns re-asking now; keep the short from-ask baseline.</summary>
    public void RecordAccepted() { }

    /// <summary>User explicitly tapped "Not now" → the LONG cooldown (a deliberate decline).</summary>
    public void RecordDeclined() => _cooldownUntilUtc = _clock() + DeclineCooldown;

    /// <summary>The prompt was retracted UNANSWERED (mask subsided before the user tapped) → the SHORT
    /// cooldown, timed from now, so it re-asks sooner than an explicit decline.</summary>
    public void RecordDismissedUnanswered() => _cooldownUntilUtc = _clock() + NoAnswerCooldown;

    /// <summary>W-E (2026-06-20): re-seed the decline cooldown from a PERSISTED last-decline time so an
    /// explicit "Not now" survives a restart (this governor is otherwise in-memory). No-op when the user
    /// never declined or when the <see cref="DeclineCooldown"/> window has already elapsed — so a stale
    /// timestamp can never suppress a fresh ask forever.</summary>
    public void RestoreDeclineCooldown(DateTime? lastDeclineUtc)
    {
        if (lastDeclineUtc is not { } t) return;
        var until = t.ToUniversalTime() + DeclineCooldown;
        if (until > _clock()) _cooldownUntilUtc = until;
    }
}
