using System;

namespace SenseEase.Core.Inference;

/// <summary>
/// Back-compat parse for a persisted / chatbot-proposed <see cref="HabituationProfile"/> string.
/// B6 Stage 4a renamed the enum members (SENSITIVE→<see cref="HabituationProfile.Reactive"/>,
/// TYPICAL→<see cref="HabituationProfile.Adapting"/>; <see cref="HabituationProfile.Resilient"/>
/// kept) and reordered them low→high adaptive capacity. Files written before the rename hold the
/// OLD canonical strings (RESILIENT/TYPICAL/SENSITIVE); this helper aliases them to the new members
/// so existing <c>habituation_profile.json</c> snapshots — and any chatbot proposal echoing the old
/// vocabulary — still load. New writes emit the NEW canonical strings (REACTIVE/ADAPTING/RESILIENT)
/// via <c>CurrentProfile.ToString().ToUpperInvariant()</c>.
/// <para>Drop-in replacement for <c>Enum.TryParse&lt;HabituationProfile&gt;(raw, ignoreCase:true, out _)</c>
/// at every profile-string READ site; preserves the same <c>bool</c> + <c>out</c> contract (returns
/// false / default on null / blank / unknown, so callers keep their existing fail behaviour).</para>
/// </summary>
public static class HabituationProfileAlias
{
    /// <summary>Parse a profile name, accepting BOTH the new members and the pre-Stage-4a legacy
    /// strings. Case-insensitive; trims surrounding whitespace.</summary>
    public static bool TryParse(string? raw, out HabituationProfile profile)
    {
        profile = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        switch (trimmed.ToUpperInvariant())
        {
            // Legacy (pre-Stage-4a) canonical strings → the renamed members.
            case "SENSITIVE": profile = HabituationProfile.Reactive;  return true;
            case "TYPICAL":   profile = HabituationProfile.Adapting;  return true;
            case "RESILIENT": profile = HabituationProfile.Resilient; return true; // identity, kept explicit (design §0)

            // New canonical strings (REACTIVE/ADAPTING/RESILIENT) and any other member → direct parse.
            default: return Enum.TryParse(trimmed, ignoreCase: true, out profile);
        }
    }
}
