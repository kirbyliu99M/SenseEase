using System.Collections.Generic;
using System.Linq;
using System.Text;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

/// <summary>
/// M2 (Phase 6a/6b): builds the <c>[habituation_state]</c> block injected
/// into the chatbot prompt so the LLM has user-context to ground its
/// replies. Pure logic — no I/O, no dispatcher, no engine references.
///
/// <para>Format is the token-diet variant locked in advisor Section 4.7:
/// short snake_case keys, optional fields omitted when null / empty / false,
/// max 5 recent observations, terse one-line trajectory summary. Target
/// budget ~120 tokens so it fits inside Mistral 3B's ~4K context window
/// alongside the existing <c>[RecentAdjustments]</c> + <c>[RecentEvents]</c>
/// blocks and the user message itself.</para>
///
/// <para>The block opens with <c>[habituation_state]</c> and closes with
/// <c>[/habituation_state]</c>. MistralChatbotService's SystemPrompt
/// (Phase 6d) documents how the LLM should consume it — primarily as
/// natural-language grounding, NEVER echoed verbatim to the user.</para>
/// </summary>
public static class HabituationStateBlockBuilder
{
    /// <summary>Soft cap on emitted block length in characters. ~4 chars
    /// per token → ~150 tokens. Beyond this, the builder progressively
    /// drops optional sections (longitudinal_observations first, then
    /// trajectory_summary truncation). Used by <see cref="BuildBlock"/>'s
    /// belt-and-suspenders guard.</summary>
    public const int MaxBlockChars = 600;

    /// <summary>Max longitudinal observations included even before the
    /// length guard kicks in. Most recent first.</summary>
    public const int MaxObservations = 5;

    /// <summary>
    /// Build the <c>[habituation_state]</c> block from the given inputs.
    /// Returns an empty string when <paramref name="snapshot"/> is null —
    /// the chatbot caller should skip prepending the block entirely in
    /// that case rather than emit an empty marker pair.
    /// </summary>
    /// <param name="snapshot">The persisted profile snapshot (Schema v2).
    /// Null = no calibration has run yet; block omitted.</param>
    /// <param name="trajectorySummary">One-line natural-language summary
    /// of the user's habituation arc over the last N days. Null / empty
    /// → field omitted. Built by <c>HabituationTrajectoryBuilder</c>
    /// (Phase 6c) from the IEventLogStore.</param>
    /// <param name="powerSaveActive">True iff the user has Power Save
    /// engaged. Field is only emitted when true — saves tokens for the
    /// (common) inactive case.</param>
    public static string BuildBlock(
        HabituationProfileSnapshot? snapshot,
        string? trajectorySummary,
        bool powerSaveActive)
    {
        if (snapshot is null) return string.Empty;

        var sb = new StringBuilder(capacity: 256);
        sb.Append("[habituation_state]\n");
        sb.Append("tier: ").Append(snapshot.CurrentVimsTier).Append('\n');
        sb.Append("level: ").Append(snapshot.HabituationLevel.ToString("F1")).Append('\n');
        sb.Append("profile: ").Append(snapshot.Profile).Append('\n');
        sb.Append("state: ").Append(snapshot.EngineState).Append('\n');

        if (snapshot.AwaitingUserConfirm)
        {
            sb.Append("awaiting_user_confirm: true\n");
        }

        if (powerSaveActive)
        {
            sb.Append("power_save_active: true\n");
        }

        if (snapshot.CalibrationCount > 0)
        {
            sb.Append("calibrations: ").Append(snapshot.CalibrationCount).Append('\n');
        }

        if (!string.IsNullOrEmpty(snapshot.LastCalibrationAtIso))
        {
            // Just the date portion — full ISO timestamp is unnecessary for
            // chatbot reasoning ("6 months ago" vs "yesterday" is sufficient).
            string date = snapshot.LastCalibrationAtIso.Length >= 10
                ? snapshot.LastCalibrationAtIso[..10]
                : snapshot.LastCalibrationAtIso;
            sb.Append("last_cal: ").Append(date).Append('\n');
        }

        if (snapshot.MedicalAdvisoryShown is not null)
        {
            // Lowercase to match advisor's example ("vestibular" not
            // "Vestibular"). Enum.ToString is PascalCase by default.
            sb.Append("advisory: ")
              .Append(snapshot.MedicalAdvisoryShown.ToString()!.ToLowerInvariant())
              .Append('\n');
        }

        // PersonalPressureScale default is 1.0; include only when calibration
        // shifted it. Saves a line for never-calibrated users.
        if (System.Math.Abs(snapshot.PersonalPressureScale - 1.0) > 0.01)
        {
            sb.Append("scale: ")
              .Append(snapshot.PersonalPressureScale.ToString("F2"))
              .Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(trajectorySummary))
        {
            sb.Append("trajectory: ").Append(trajectorySummary!.Trim()).Append('\n');
        }

        if (snapshot.LongitudinalObservations is { Count: > 0 })
        {
            sb.Append("recent_observations:\n");
            // Most-recent first, max MaxObservations entries. The list is
            // append-ordered (oldest first) so we reverse + take.
            var recent = snapshot.LongitudinalObservations
                                 .AsEnumerable()
                                 .Reverse()
                                 .Take(MaxObservations);
            foreach (var obs in recent)
            {
                // H5 (defense-in-depth): even with write-time sanitization, encode
                // structural chars on EMIT so any pre-existing on-disk payload can't
                // break out of the list — brackets → parens, newlines → spaces.
                string safe = obs.Replace('[', '(').Replace(']', ')').Replace('\r', ' ').Replace('\n', ' ');
                sb.Append("  - ").Append(safe).Append('\n');
            }
        }

        sb.Append("[/habituation_state]");

        // Belt-and-suspenders length guard. If the block somehow exceeds
        // MaxBlockChars (very long observations, very long trajectory),
        // truncate from the END (cuts observations first, then closes
        // with the marker). This is a safety net — observation entries
        // ARE individually short-capped (80 chars per Phase 7g spec) so
        // hitting MaxBlockChars in practice would require >7 observations
        // which the MaxObservations cap already prevents.
        if (sb.Length > MaxBlockChars)
        {
            string content = sb.ToString();
            const string Closer = "\n[/habituation_state]";
            content = content[..(MaxBlockChars - Closer.Length)] + Closer;
            return content;
        }

        return sb.ToString();
    }
}
