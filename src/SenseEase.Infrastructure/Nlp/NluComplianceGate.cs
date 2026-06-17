using System;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Nlp;

/// <summary>
/// The single deterministic COMPLIANCE GATE for the chatbot NLU. Mirrors the B5
/// <c>AdaptationGate</c> shape — untrusted-in / trusted-out, pure (no I/O, no engine, no
/// dispatcher, no localization), fail-closed — applied to the per-turn NLU decision rather
/// than the long-run adaptation proposal.
///
/// <para>Both decision engines feed ONE gate, so a rule can never hold on one path and
/// silently miss the other:</para>
/// <list type="bullet">
///   <item>the LLM path (<c>MistralChatbotService.TryParseModelDecision</c>) — the model proposes;</item>
///   <item>the lexicon fallback (<c>MistralChatbotService.BuildFallbackResult</c>, when the
///   LLM is down/loading) — the lexicon proposes.</item>
/// </list>
/// <para>The gate disposes: the reset-cue upgrade, promote/demote on named-vs-unnamed
/// symptom, non-actuating-intent zeroing, the model-engaged + v0.9.1 all-zeros delta policy,
/// the safe delta clamps, the empty-reply fallback, and the followup-payload
/// sanitize/downgrade.</para>
///
/// <para>NOT in this gate (different actuation contract / pre-decision short-circuits, left
/// in <c>ChatbotViewModel.SendAsync</c> + <c>MistralChatbotService.SendAsync</c>): the
/// SafetyFloor crisis interception, the <c>ShaderCommandLexicon</c> slash/strict commands and
/// their 20% mask floor, the bare-off clarify, and the H6 sanitisation. The gate only ever
/// sees an already-formed decision for the <c>IChatbotService</c> path; it does not govern
/// MainViewModel's legacy feedback box.</para>
/// </summary>
internal static class NluComplianceGate
{
    /// <summary>Which decision engine formed the proposal. Almost every rule applies to both
    /// uniformly; the ONE source-specific rule is the "Unknown → offer a symptom picker"
    /// Case-1 clarify, which is a <see cref="Lexicon"/>-only DECISION (when the dumb fallback
    /// can't classify, asking is its best move). The <see cref="Model"/> handles Unknown by
    /// replying conversationally, so applying Case 1 there would wrongly discard its answer.
    /// All SAFETY rules (demote, clamps, non-actuating zeroing, the question-guard) are
    /// source-agnostic.</summary>
    internal enum DecisionSource { Model, Lexicon }

    /// <summary>
    /// A raw, untrusted NLU proposal from either decision engine. The actuating deltas are
    /// NULLABLE on purpose: <c>null</c> means "the engine did not propose this axis" (vs an
    /// explicit <c>0</c>), which the model-engaged check and the v0.9.1 all-zeros hotfix
    /// depend on. The lexicon fallback passes its deltas as <c>null</c> (not engaged) so the
    /// gate takes the <c>DeriveAdjustments</c> branch exactly as before. <see cref="Intent"/>
    /// is already <c>NormalizeIntent</c>'d + whitelisted by the caller.
    /// </summary>
    internal readonly record struct RawNluDecision(
        string Intent,
        int Severity,
        string? Reply,
        int? FovDelta,
        double? MaskDepthDelta,
        double? GridVisibilityDelta,
        string? FollowupQuestion,
        string[]? FollowupOptions,
        DecisionSource Source = DecisionSource.Model);

    /// <summary>
    /// Run the untrusted proposal through every deterministic compliance rule and return the
    /// trusted, deliverable <see cref="NluResult"/>. Pure; never throws on ordinary input.
    /// </summary>
    internal static NluResult Check(RawNluDecision d, string userText)
    {
        string intent = d.Intent;
        int severity = Math.Clamp(d.Severity, 1, 5);
        string? reply = d.Reply;
        string? followupQuestion = d.FollowupQuestion;
        string[]? followupOptions = d.FollowupOptions;

        // Belt-and-suspenders: if lexicon detects reset cue, upgrade intent.
        var (_, _, _, reset) = FeedbackLexicon.ClassifyShaderHints(userText);
        if (reset && intent != "ResetRequest") intent = "ResetRequest";

        // FollowupRequest veto: if the user already named a specific symptom
        // (headache / dizzy / nausea / eye strain), asking "which symptom?"
        // is the wrong move. Promote to ReportDiscomfort so the actuator
        // path applies deltas; suppress the card payload so the UI doesn't
        // show option chips on top of an actionable input. Keep the model's
        // reply text — it usually still reads sensibly once stripped of the
        // "which one?" framing because the model already saw the symptom.
        if (intent == "FollowupRequest"
            && FeedbackLexicon.MentionsSpecificSymptom(userText))
        {
            intent = "ReportDiscomfort";
            followupQuestion = null;
            followupOptions = null;
        }
        // Inverse of the promote: a discomfort report that names NO specific symptom is the
        // card-policy "discomfort + unnamed symptom" case — ASK which sensation instead of actuating
        // on a guess. Tester: "I don't know, but I feel weird with my eyes" silently cut the FOV. The
        // lexicon fallback already does this via TryBuildFollowup Case 2; mirror it on the model path
        // so a model that classifies vague unease as ReportDiscomfort is corrected before any delta.
        else if (intent == "ReportDiscomfort"
            && !FeedbackLexicon.MentionsSpecificSymptom(userText))
        {
            var (clarifyQ, clarifyOpts) = FeedbackLexicon.TryBuildFollowup(userText, "ReportDiscomfort", severity);
            if (clarifyQ is not null)
            {
                intent = "FollowupRequest";
                followupQuestion = clarifyQ;
                followupOptions = clarifyOpts;
                reply = clarifyQ; // the model's "I've adjusted…" reply no longer applies — ask, don't claim
            }
        }
        // Case 1 (LEXICON source only): the lexicon couldn't classify the input → offer a
        // symptom picker instead of a useless generic reply. This is the lexicon engine's
        // DECISION, not a compliance check: the model handles Unknown by replying, so applying
        // it to the model path would discard the model's answer. Folding it here gives the
        // single TryBuildFollowup call site (BuildFallbackResult no longer calls it itself).
        else if (d.Source == DecisionSource.Lexicon && intent == "Unknown")
        {
            var (clarifyQ, clarifyOpts) = FeedbackLexicon.TryBuildFollowup(userText, "Unknown", severity);
            if (clarifyQ is not null)
            {
                intent = "FollowupRequest";
                followupQuestion = clarifyQ;
                followupOptions = clarifyOpts;
                reply = clarifyQ;
            }
        }

        // Round-3 (Kirby item 5): feather is NOT part of the chatbot's adjustment
        // vocabulary anymore. The chatbot LLM path NEVER applies a feather delta —
        // any stray feather_width_delta the model emits is ignored (fw stays 0). The
        // /feather slash command, the engine knob and the A2 renderer wiring are
        // unaffected (those don't go through this NLU path).
        const double fw = 0.0;

        // AnalyzeRequest / FollowupRequest are non-actuating — never derive deltas.
        int fov; double md, gv;
        if (intent == "AnalyzeRequest" || intent == "FollowupRequest")
        {
            fov = 0; md = 0; gv = 0;
        }
        else
        {
            // Personalized path: when the engine emitted explicit deltas, trust
            // them (clamped to safe ranges); otherwise fall back to the lexicon's
            // severity-bucket defaults. Any one ACTUATING field being non-null counts
            // as "engaged", at which point we apply ALL provided values (missing
            // ones implicitly 0) — mixing engine+lexicon per-field would produce
            // incoherent combinations. (feather_width_delta is intentionally NOT an
            // "engaged" signal — it is ignored entirely.)
            bool modelProvidedDeltas = d.FovDelta.HasValue
                || d.MaskDepthDelta.HasValue
                || d.GridVisibilityDelta.HasValue;

            // v0.9.1 hotfix: belt-and-suspenders for the Phase 6d prompt
            // regression. Mistral 3B, when the [habituation_state] block
            // is present, began emitting intent=ReportDiscomfort with ALL
            // FOUR deltas explicitly set to 0 — i.e. "engaged" per the
            // check above but the values are all zero so nothing actuates.
            // Treat that as not-engaged and fall back to the lexicon's
            // severity-bucket defaults. The conditions are narrow (only
            // ReportDiscomfort, only when ALL four are exactly 0, only when
            // severity ≥ 2) so legitimate "tiny nudge" responses where the
            // model deliberately chooses zeros aren't accidentally amplified.
            bool reportedDiscomfortWithAllZeros =
                intent == "ReportDiscomfort"
                && severity >= 2
                && modelProvidedDeltas
                && (d.FovDelta            ?? 0) == 0
                && (d.MaskDepthDelta      ?? 0) == 0.0
                && (d.GridVisibilityDelta ?? 0) == 0.0;

            if (modelProvidedDeltas && !reportedDiscomfortWithAllZeros)
            {
                fov = Math.Clamp(d.FovDelta ?? 0,            -30, 0);
                md  = Math.Clamp(d.MaskDepthDelta ?? 0,      -0.3, 0.3);
                gv  = Math.Clamp(d.GridVisibilityDelta ?? 0, -0.3, 0.3);
                // fw intentionally NOT set from the engine — feather is dropped.
            }
            else
            {
                // DeriveAdjustments already returns feather=0 (chatbot vocabulary
                // dropped it), so discard its 4th component.
                (fov, md, gv, _) = FeedbackLexicon.DeriveAdjustments(intent, severity, userText);
            }
        }

        string response = (reply ?? string.Empty).Trim();
        if (response.Length == 0)
            response = FeedbackLexicon.BuildFallbackResponse(userText, intent);

        // Sanitize followup payload: trust the proposal only if it gave both a
        // question AND 2-4 short options when intent is FollowupRequest.
        // If it claimed FollowupRequest but failed to populate options,
        // fall back to the lexicon-built picker so the UI never sees a stuck
        // "no options, no progress" state.
        string? finalFollowupQ = null;
        string[]? finalFollowupOpts = null;
        if (intent == "FollowupRequest")
        {
            if (!string.IsNullOrWhiteSpace(followupQuestion) &&
                followupOptions is { Length: >= 2 and <= 4 })
            {
                finalFollowupQ = followupQuestion!.Trim();
                finalFollowupOpts = Array.ConvertAll(followupOptions,
                    o => (o ?? string.Empty).Trim());
            }
            else
            {
                var (q, opts) = FeedbackLexicon.TryBuildFollowup(userText, intent, severity);
                if (q is null)
                {
                    // Claimed FollowupRequest on input the lexicon can't justify
                    // clarifying — downgrade to Unknown rather than ship a broken
                    // card. Keeps the visible reply but drops the picker.
                    intent = "Unknown";
                }
                else
                {
                    finalFollowupQ = q;
                    finalFollowupOpts = opts;
                    if (string.IsNullOrWhiteSpace(response)) response = q;
                }
            }
        }

        return new NluResult
        {
            Intent = intent,
            FovDelta = fov,
            MaskDepthDelta = md,
            GridVisibilityDelta = gv,
            FeatherWidthDelta = fw,
            Response = response,
            FollowupQuestion = finalFollowupQ,
            FollowupOptions = finalFollowupOpts,
        };
    }
}
