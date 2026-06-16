using System;

namespace SenseEase.App.Services;

/// <summary>
/// Phase-1 event-driven feedback: cadence governor for the proactive contextual discomfort
/// check-in. Pure logic (clock-injectable) so the anti-fatigue decision is unit-testable without a
/// WPF window — mirrors <see cref="NotificationCardController"/>'s design.
///
/// <para>Layered ON TOP of the <c>PressureSpikeDetector</c>'s own 30 s cooldown. Two gates:
/// (1) an 8-hour <see cref="Cooldown"/> between ANY two prompts — so the user sees at most a few
/// gentle check-ins a day; (2) a longer <see cref="DismissSuppression"/> quiet window after the
/// user DISMISSES a prompt without answering (a stronger "leave me alone" signal). Both are
/// telemetry-tunable. (A separate per-day cap is unnecessary: an 8 h cooldown already bounds
/// prompts to ≤3/day.)</para>
///
/// <para>In-memory only (resets on app restart), matching <see cref="NotificationCardController"/>.
/// The cooldown + the spike's own 30 s gate prevent bursts; a restart clearing the in-memory
/// last-prompt time is acceptable — the user-facing effect is at most one extra check-in after a
/// relaunch, never a flood.</para>
/// </summary>
public sealed class CheckInGovernor
{
    private readonly Func<DateTime> _clock;
    private DateTime? _lastPromptUtc;
    private DateTime? _suppressedUntilUtc;

    /// <summary>Cooldown between any two check-in prompts. Default 8 hours (user-set 2026-06-16).</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Quiet window after the user DISMISSES a prompt without answering. Default 24 h — a
    /// dismissal is a stronger "leave me alone" signal than the ordinary cooldown.</summary>
    public TimeSpan DismissSuppression { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Constructor with optional clock override. Production passes null →
    /// <see cref="DateTime.UtcNow"/>. Tests inject a deterministic clock.</summary>
    public CheckInGovernor(Func<DateTime>? utcClock = null)
        => _clock = utcClock ?? (() => DateTime.UtcNow);

    /// <summary>True iff a new prompt may be shown right now. Does NOT mutate state — call
    /// <see cref="RecordShown"/> after actually showing one.</summary>
    public bool ShouldPrompt()
    {
        var now = _clock();
        if (_suppressedUntilUtc is { } until && now < until) return false;            // post-dismiss quiet
        if (_lastPromptUtc is { } last && now - last < Cooldown) return false;        // 8 h cooldown
        return true;
    }

    /// <summary>Record that a prompt (a check-in OR the one-time opt-in) was shown — starts the
    /// <see cref="Cooldown"/> from now.</summary>
    public void RecordShown() => _lastPromptUtc = _clock();

    /// <summary>Record that the user DISMISSED a prompt without answering — arms the
    /// <see cref="DismissSuppression"/> quiet window.</summary>
    public void RecordDismissed() => _suppressedUntilUtc = _clock() + DismissSuppression;

    /// <summary>Record that the user ANSWERED — clears any prior dismiss-suppression (engagement is
    /// the opposite signal). The <see cref="Cooldown"/> set by the preceding <see cref="RecordShown"/>
    /// still applies, so the next prompt is still at least <see cref="Cooldown"/> away.</summary>
    public void RecordAnswered() => _suppressedUntilUtc = null;
}
