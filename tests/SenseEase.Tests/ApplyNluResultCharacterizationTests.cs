using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.Localization;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Stage 0 of the NluComplianceGate consolidation (2026-06-18): a CHARACTERIZATION net that pins
/// the CURRENT behaviour of <see cref="ChatbotViewModel"/>.ApplyNluResult BEFORE the refactor moves
/// the question-guard out of it. ApplyNluResult is a private WPF-VM method with no direct coverage
/// today, so these tests drive it through the public <c>SendCommand</c> with a scripted
/// <see cref="IChatbotService"/> (IsModelLoaded=true → the synchronous render path, same harness
/// shape as ChatbotViewModelInjectionHardeningTests). They give the upcoming Stage C edit a
/// regression net for:
/// <list type="bullet">
///   <item>the question-guard zeroing — and that a suppressed turn writes NO adjustment record
///   (which protects the B5 learning loop's manual-tighten clock that reads Source="user"/FovRadius);</item>
///   <item>the guard's ReportDiscomfort scope (a non-discomfort intent is NOT question-suppressed);</item>
///   <item>ResetRequest → ResetPersonalization + the reset record + the overlay-invisible suffix;</item>
///   <item>the adjustment-log record format (Parameter / Source / Utterance / APPLIED delta);</item>
///   <item>the follow-up target memory set on the MODEL path (the uncovered side);</item>
///   <item>the empty-summary, non-veto branch.</item>
/// </list>
/// These assert behaviour AS-IS; they must keep passing unchanged through Stages A–D.
/// </summary>
[Collection("TestDataRoot")]
public class ApplyNluResultCharacterizationTests
{
    public ApplyNluResultCharacterizationTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(ApplyNluResultCharacterizationTests));
    }

    /// <summary>LLM stub returning a scripted NluResult per turn. IsModelLoaded=true so the VM
    /// takes the synchronous (non-fake-stream) path and SendAsync completes on the test thread.
    /// A FRESH result object is produced per call so the VM's in-place mutation of the result
    /// (the question-guard zeroing) never bleeds across turns.</summary>
    private sealed class ScriptedChatbotService : IChatbotService
    {
        private readonly Func<NluResult> _next;
        public int SendCount;
        public ScriptedChatbotService(Func<NluResult> next) => _next = next;
        public bool IsModelAvailable => true;
        public bool IsModelLoaded => true;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() => Task.CompletedTask;
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SendCount);
            return Task.FromResult(_next());
        }
        public void ClearHistory() { }
        public void Dispose() { }
    }

    private sealed class RecordingAdjustmentLog : IAdjustmentLog
    {
        public readonly List<AdjustmentRecord> Records = new();
        public void Append(AdjustmentRecord record) => Records.Add(record);
        public IReadOnlyList<AdjustmentRecord> All() => Records;
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private static (ChatbotViewModel vm, ScriptedChatbotService bot, MockInferenceEngine engine, RecordingAdjustmentLog log)
        BuildVm(Func<NluResult> next, bool overlayVisible = true)
    {
        var bot = new ScriptedChatbotService(next);
        var engine = new MockInferenceEngine();
        var log = new RecordingAdjustmentLog();
        var vm = new ChatbotViewModel(
            chatbot: bot,
            engine: engine,
            adjustmentLog: log,
            isOverlayVisible: () => overlayVisible);
        return (vm, bot, engine, log);
    }

    private static void WithLanguage(UILanguage lang, Action body)
    {
        var prior = LocalizationManager.Current.Language;
        try { LocalizationManager.Current.Language = lang; body(); }
        finally { LocalizationManager.Current.Language = prior; }
    }

    private static bool AnyAssistant(ChatbotViewModel vm, string fragment)
        => vm.Messages.Any(m => m.Role == ChatRole.Assistant && m.Content.Contains(fragment));

    // ── 1. Delivery of a question-suppressed result: no actuation, surfaces the veto note ───
    //    Post-Stage-C the question-guard DECISION lives in NluComplianceGate (it zeroed the
    //    deltas and set SuppressedAsQuestion — see NluComplianceGateTests). ApplyNluResult is
    //    pure delivery: it actuates nothing, writes NO record (protects the learning loop's
    //    manual-tighten clock), and surfaces the localized veto note from the flag.

    [Theory]
    [InlineData(UILanguage.English, "looked like a question")]
    [InlineData(UILanguage.ChineseTraditional, "這看起來像是一個問題")]
    public void SuppressedAsQuestionResult_SurfacesVetoCopy_NoActuation(UILanguage lang, string vetoFragment)
        => WithLanguage(lang, () =>
        {
            var (vm, bot, engine, log) = BuildVm(() => new NluResult
            {
                Intent = "ReportDiscomfort",
                FovDelta = 0, MaskDepthDelta = 0.0, GridVisibilityDelta = 0.0, // gate already zeroed
                SuppressedAsQuestion = true,
                Response = "Are you feeling dizzy? Tell me if you'd like a change.",
            });

            vm.InputText = "do you think I'm dizzy now?";
            vm.SendCommand.Execute(null);

            Assert.Equal(0, engine.UserFovOffset);
            Assert.Empty(log.Records); // no spurious adjustment record
            Assert.True(AnyAssistant(vm, vetoFragment));
            Assert.Equal(1, bot.SendCount);
        });

    // ── 2. A real (non-interrogative) report actuates, and logs the APPLIED FOV delta ───────

    [Fact]
    public void RealReport_NotInterrogative_AppliesDeltas_AndLogsFovRecord()
        => WithLanguage(UILanguage.English, () =>
        {
            var (vm, _, engine, log) = BuildVm(() => new NluResult
            {
                Intent = "ReportDiscomfort",
                FovDelta = -8,
                MaskDepthDelta = 0.06,
                Response = "Tightening a little.",
            });

            vm.InputText = "I'm dizzy";
            vm.SendCommand.Execute(null);

            Assert.Equal(-8, engine.UserFovOffset); // FOV actually moved (0 → -8, within [-60,0])
            var fov = log.Records.SingleOrDefault(r => r.Parameter == "FovRadius");
            Assert.NotNull(fov);
            Assert.Equal("user", fov!.Source);
            Assert.Equal("I'm dizzy", fov.Utterance);
            Assert.Equal(-8.0, fov.Delta, 6); // logs the APPLIED delta (old offset was 0)
            Assert.True(AnyAssistant(vm, "System adjustment"));
        });

    // ── 3. The question-guard is scoped to ReportDiscomfort: a non-discomfort intent on an ──
    //    interrogative turn is NOT suppressed (deltas apply, no veto copy).

    [Fact]
    public void Inquiry_NonDiscomfortIntent_IsNotQuestionSuppressed()
        => WithLanguage(UILanguage.English, () =>
        {
            var (vm, _, engine, _) = BuildVm(() => new NluResult
            {
                Intent = "AnalyzeRequest", // not ReportDiscomfort → guard does not apply
                FovDelta = -8,
                Response = "Here's what I see.",
            });

            vm.InputText = "do you think I'm dizzy now?";
            vm.SendCommand.Execute(null);

            Assert.Equal(-8, engine.UserFovOffset);        // applied — guard did not fire
            Assert.False(AnyAssistant(vm, "looked like a question"));
        });

    // ── 4. ResetRequest → ResetPersonalization + reset record + overlay-invisible suffix ────

    [Theory]
    [InlineData(true)]   // overlay visible → no "open the overlay" suffix
    [InlineData(false)]  // overlay hidden  → suffix appended
    public void ResetRequest_ResetsAndLogs_SuffixTracksOverlayVisibility(bool overlayVisible)
        => WithLanguage(UILanguage.English, () =>
        {
            var (vm, _, _, log) = BuildVm(
                () => new NluResult { Intent = "ResetRequest", Response = "" },
                overlayVisible: overlayVisible);

            vm.InputText = "I'd like to start fresh";
            vm.SendCommand.Execute(null);

            var reset = log.Records.SingleOrDefault(r => r.Parameter == "Reset");
            Assert.NotNull(reset);
            Assert.Equal("reset", reset!.Source);

            Assert.True(AnyAssistant(vm, "Overlay settings reset to default"));
            // The "open the overlay to see it" suffix appears ONLY when the overlay is hidden.
            Assert.Equal(!overlayVisible, AnyAssistant(vm, "open the overlay to see it"));
        });

    // ── 5. Follow-up target memory is set on the MODEL path (the previously-uncovered side) ──
    //    A grid delta applied via a scripted result lets a bare 「直接關閉」 next turn resolve
    //    to the grid deterministically (no second LLM round-trip).

    [Fact]
    public void ModelPathGridDelta_SetsTarget_BareOffNextTurn_TurnsGridOff_NoSecondLlmCall()
        => WithLanguage(UILanguage.ChineseTraditional, () =>
        {
            var (vm, bot, engine, _) = BuildVm(() => new NluResult
            {
                Intent = "ReportDiscomfort",
                GridVisibilityDelta = -0.10, // moves the grid → appliedTarget = Grid
                Response = "好的。",
            });
            // Prime Current.Shader so it aliases the live _shader (production does this on the
            // first Update() tick; a fresh test engine never ticks). Establishes a known start.
            engine.SetShaderParam(ShaderParam.GridVisibility, 0.60);
            Assert.True(engine.Current.Shader.GridVisibility > 0);

            vm.InputText = "things feel pretty intense right now"; // reaches the stub
            vm.SendCommand.Execute(null);
            Assert.Equal(1, bot.SendCount);

            vm.InputText = "直接關閉"; // bare off → inherits the remembered Grid target
            vm.SendCommand.Execute(null);

            Assert.Equal(0.0, engine.Current.Shader.GridVisibility, 6); // grid OFF
            Assert.Equal(1, bot.SendCount); // turn 2 was deterministic — no LLM
        });

    [Fact]
    public void ModelPathMaskDelta_SetsTarget_BareOffNextTurn_DeclinesHonestly_PointsToSettings()
        => WithLanguage(UILanguage.ChineseTraditional, () =>
        {
            var (vm, bot, engine, _) = BuildVm(() => new NluResult
            {
                Intent = "ReportDiscomfort",
                MaskDepthDelta = 0.10, // moves the mask → appliedTarget = Mask
                Response = "好的。",
            });
            // Prime Current.Shader so it aliases the live _shader (see the grid test); known start.
            engine.SetShaderParam(ShaderParam.MaskDepth, 0.50);

            vm.InputText = "things feel pretty intense right now";
            vm.SendCommand.Execute(null);
            double maskAfterTurn1 = engine.Current.Shader.MaskDepth;
            Assert.True(maskAfterTurn1 > 0.50); // the model-path mask delta actually applied
            Assert.Equal(1, bot.SendCount);

            vm.InputText = "關掉"; // bare off → mask has no OFF → declines, points to Settings
            vm.SendCommand.Execute(null);

            Assert.Equal(maskAfterTurn1, engine.Current.Shader.MaskDepth, 6); // mask unchanged
            Assert.Equal(1, bot.SendCount);
            Assert.True(AnyAssistant(vm, "設定")); // points to Settings
        });

    // ── 6. Empty-summary, non-veto branch: a non-actuating, non-interrogative turn produces ─
    //    no "System adjustment" table and no veto copy, and touches nothing.

    [Fact]
    public void NonActuating_NotInquiry_ProducesNoSummary_NoVeto_NoChange()
        => WithLanguage(UILanguage.English, () =>
        {
            var (vm, _, engine, log) = BuildVm(() => new NluResult
            {
                Intent = "Unknown",
                Response = "Happy to help.",
            });
            double maskBefore = engine.Current.Shader.MaskDepth;

            vm.InputText = "hello there";
            vm.SendCommand.Execute(null);

            Assert.Equal(0, engine.UserFovOffset);
            Assert.Equal(maskBefore, engine.Current.Shader.MaskDepth, 6);
            Assert.Empty(log.Records);
            Assert.False(AnyAssistant(vm, "System adjustment"));
            Assert.False(AnyAssistant(vm, "looked like a question"));
        });
}
