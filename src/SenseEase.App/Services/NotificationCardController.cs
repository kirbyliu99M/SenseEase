using System;
using System.Collections.Generic;
using SenseEase.App.Views;

namespace SenseEase.App.Services;

/// <summary>
/// Pop-up sprint Task 4: pure-logic controller for the notification card's
/// mute state. Owns the "don't show this kind for 6 hours" gate so the
/// decision logic is unit-testable without spinning up a WPF window.
///
/// <para>Mute is keyed by <see cref="TierColorPalette.TierBand"/>: muting
/// the Medium 級 band suppresses auto-expansion for tier-up events into 6/7,
/// independent of VeryHigh or VeryLow mutes. This matches spec §2 ("該類型靜音
/// 6 小時" — "this kind muted 6 h"); we treat colour band as "kind".</para>
///
/// <para>Mute does NOT block user-initiated card opens. Spec is explicit:
/// "User 主動展開永遠可行，無論 toggle 狀態" — clicking the bar or tray
/// always shows the card regardless of mute. See <see cref="ShouldAutoShow"/>
/// docstring; the host calls this ONLY before auto-show paths, never
/// before user-click paths.</para>
///
/// <para>In-memory only in Task 4. Persistence (across app restarts) lands
/// in Task 5 alongside the Toggle A / Toggle B settings storage.</para>
/// </summary>
public sealed class NotificationCardController
{
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<TierColorPalette.TierBand, DateTime> _muteUntilUtc = new();

    /// <summary>How long a single "don't show this kind" click silences the
    /// matching band. Default 6 hours per spec. Public-set so tests can
    /// shrink it to make assertions deterministic without time travel.</summary>
    public TimeSpan MuteDuration { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Constructor with optional clock override. Production passes
    /// null → <see cref="DateTime.UtcNow"/>. Tests inject a deterministic
    /// clock so mute expiry assertions are not flaky.</summary>
    public NotificationCardController(Func<DateTime>? utcClock = null)
    {
        _clock = utcClock ?? (() => DateTime.UtcNow);
    }

    /// <summary>True iff an auto-show for the given band should proceed.
    /// False = the band is currently muted (within the 6 h window from the
    /// most recent <see cref="Mute"/> call). The host MUST call this only
    /// for auto-show paths (tier-up auto-expand, spike auto-expand) — NOT
    /// for user-initiated card opens (bar click, tray left-click), which
    /// always show regardless of mute per spec.</summary>
    public bool ShouldAutoShow(TierColorPalette.TierBand band)
    {
        if (_muteUntilUtc.TryGetValue(band, out var until))
        {
            return _clock() >= until;
        }
        return true;
    }

    /// <summary>Records a "don't show this kind for 6 h" click. Sets the
    /// mute-until-utc for the band to <c>clock() + MuteDuration</c>.
    /// Subsequent calls overwrite (refresh the window) — there's no
    /// "extend" semantic; each click resets the timer.</summary>
    public void Mute(TierColorPalette.TierBand band)
    {
        _muteUntilUtc[band] = _clock() + MuteDuration;
    }

    /// <summary>Returns the mute-until-UTC for a band, or null if the band
    /// has never been muted (or the mute has expired and been
    /// garbage-collected). Diagnostic accessor — not used in production
    /// flow but pinned by tests.</summary>
    public DateTime? MutedUntilUtc(TierColorPalette.TierBand band)
    {
        if (!_muteUntilUtc.TryGetValue(band, out var until)) return null;
        if (_clock() >= until)
        {
            _muteUntilUtc.Remove(band);
            return null;
        }
        return until;
    }

    /// <summary>Drops all mute state. Used by tests; production may use it
    /// in the future when Toggle A is flipped (no-mute-window-makes-sense
    /// if auto-expand is off entirely), but Task 4 doesn't call it.</summary>
    public void ClearAllMutes() => _muteUntilUtc.Clear();
}
