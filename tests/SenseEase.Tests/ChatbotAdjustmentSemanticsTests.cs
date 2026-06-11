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
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Round-3 (Kirby item 5) — chatbot ADJUSTMENT SEMANTICS. Pins the deterministic
/// grid OFF/ON short-circuit (「關閉網格」 and friends resolve to gv=0 ABSOLUTE without
/// ever reaching the LLM), the bare object-less off/on FOLLOW-UP context (「直接關閉」
/// after a grid adjustment turns the grid off; a bare 「關掉」 with no prior context
/// asks honestly), and the FEATHER REMOVAL from the chatbot semantic layer (the system
/// prompt no longer mentions feather; the feedback lexicon never derives a feather
/// delta). The safety-floor-first ordering is unaffected — it stays line-1 of SendAsync.
/// </summary>
[Collection("TestDataRoot")]
public class ChatbotAdjustmentSemanticsTests
{
    public ChatbotAdjustmentSemanticsTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(ChatbotAdjustmentSemanticsTests));
    }

    /// <summary>LLM stub that RECORDS whether SendAsync was invoked, so a test can assert
    /// "no LLM call". IsModelLoaded=true so any path that DID reach the model would run
    /// synchronously (and trip the flag). IsModelAvailable=true so the lazy-load path is
    /// exercised exactly as production.</summary>
    private sealed class RecordingChatbotService : IChatbotService
    {
        public int SendCount;
        public bool LoadCalled;
        public bool IsModelAvailable => true;
        public bool IsModelLoaded => true;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() { LoadCalled = true; return Task.CompletedTask; }
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SendCount);
            // If the LLM is ever (wrongly) reached for these utterances, return a
            // recognisable reply so the assertion failure is legible.
            return Task.FromResult(new NluResult { Intent = "Unknown", Response = "LLM-SHOULD-NOT-RUN" });
        }
        public void ClearHistory() { }
        public void Dispose() { }
    }

    private sealed class StubAdjustmentLog : IAdjustmentLog
    {
        public readonly List<AdjustmentRecord> Records = new();
        public void Append(AdjustmentRecord record) => Records.Add(record);
        public IReadOnlyList<AdjustmentRecord> All() => Records;
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private static (ChatbotViewModel vm, RecordingChatbotService bot, MockInferenceEngine engine)
        BuildVm()
    {
        var bot = new RecordingChatbotService();
        var engine = new MockInferenceEngine();
        var vm = new ChatbotViewModel(
            chatbot: bot,
            engine: engine,
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => true);
        return (vm, bot, engine);
    }

    private static void WithLanguage(UILanguage lang, Action body)
    {
        var prior = LocalizationManager.Current.Language;
        try { LocalizationManager.Current.Language = lang; body(); }
        finally { LocalizationManager.Current.Language = prior; }
    }

    // ── 1. Deterministic grid OFF short-circuit (the live failure) ───────────

    [Theory]
    [InlineData("關閉網格")]   // the exact screenshot utterance: 關閉 + 網格
    [InlineData("關掉網格")]
    [InlineData("隱藏網格")]
    [InlineData("移除格線")]
    [InlineData("網格關閉")]   // noun-first order
    [InlineData("turn off the grid")]
    [InlineData("disable grid")]
    [InlineData("hide the grid")]
    public void GridOff_Utterance_SetsGridVisibilityZero_NoLlmCall(string input)
        => WithLanguage(UILanguage.ChineseTraditional, () =>
        {
            var (vm, bot, engine) = BuildVm();
            // Precondition: grid starts at its non-zero profile default.
            Assert.True(engine.Current.Shader.GridVisibility > 0);

            vm.InputText = input;
            vm.SendCommand.Execute(null); // resolves synchronously (local path)

            Assert.Equal(0.0, engine.Current.Shader.GridVisibility, 6); // ABSOLUTE off
            Assert.Equal(0, bot.SendCount);  // the LLM was NEVER dispatched
            Assert.False(bot.LoadCalled);    // not even the lazy model load fired
        });

    [Fact]
    public void GridOff_DeltaNeverApplied_LandsAtAbsoluteZero_NotANudge()
    {
        // Regression guard for the screenshot bug: the old path applied Grid -0.10 (a
        // DELTA), leaving the grid at 0.65 instead of 0. The deterministic path must
        // land EXACTLY at 0 in one turn regardless of the starting value.
        var (vm, _, engine) = BuildVm();
        engine.SetShaderParam(ShaderParam.GridVisibility, 0.90); // start high
        vm.InputText = "關閉網格";
        vm.SendCommand.Execute(null);
        Assert.Equal(0.0, engine.Current.Shader.GridVisibility, 6);
    }

    // ── 2. Grid ON short-circuit (restore default) ───────────────────────────

    [Theory]
    [InlineData("開啟網格")]
    [InlineData("打開網格")]
    [InlineData("顯示格線")]
    [InlineData("show the grid")]
    [InlineData("enable grid")]
    public void GridOn_Utterance_RestoresDefault_NoLlmCall(string input)
        => WithLanguage(UILanguage.ChineseTraditional, () =>
        {
            var (vm, bot, engine) = BuildVm();
            engine.SetShaderParam(ShaderParam.GridVisibility, 0.0); // grid currently off

            vm.InputText = input;
            vm.SendCommand.Execute(null);

            Assert.Equal(ShaderParams.DefaultGridVisibility, engine.Current.Shader.GridVisibility, 6);
            Assert.Equal(0, bot.SendCount);
        });

    // ── 3. Bare object-less follow-up resolves to the remembered target ──────

    [Fact]
    public void BareOff_AfterGridAdjustment_TurnsGridOff_NoLlmCall()
        => WithLanguage(UILanguage.ChineseTraditional, () =>
        {
            var (vm, bot, engine) = BuildVm();

            // Turn 1: an explicit grid action (grid ON via the lexicon) sets the
            // remembered target to Grid and leaves the grid at its non-zero default.
            vm.InputText = "打開格線";
            vm.SendCommand.Execute(null);
            Assert.Equal(ShaderParams.DefaultGridVisibility, engine.Current.Shader.GridVisibility, 6);

            // Turn 2: a BARE 「直接關閉」 with no object inherits the Grid target → gv=0.
            vm.InputText = "直接關閉";
            vm.SendCommand.Execute(null);

            Assert.Equal(0.0, engine.Current.Shader.GridVisibility, 6);
            Assert.Equal(0, bot.SendCount); // still no LLM round-trip on either turn
        });

    [Fact]
    public void BareOff_NoPriorContext_AsksHonestly_NoAdjustment_NoLlmCall()
        => WithLanguage(UILanguage.ChineseTraditional, () =>
        {
            var (vm, bot, engine) = BuildVm();
            double gridBefore = engine.Current.Shader.GridVisibility;

            vm.InputText = "關掉"; // bare off, nothing acted on before
            vm.SendCommand.Execute(null);

            // No adjustment was applied (we don't know what to turn off).
            Assert.Equal(gridBefore, engine.Current.Shader.GridVisibility, 6);
            Assert.Equal(0, bot.SendCount); // honest clarify is local — no LLM
            // The assistant asked a clarifying question rather than guessing.
            var lastAssistant = vm.Messages.Last(m => m.Role == ChatRole.Assistant);
            Assert.Contains("哪一項", lastAssistant.Content);
            Assert.DoesNotContain("LLM-SHOULD-NOT-RUN", lastAssistant.Content);
        });

    [Fact]
    public void BareOff_AfterWindowExpires_AsksHonestly_NotStaleTarget()
        => WithLanguage(UILanguage.ChineseTraditional, () =>
        {
            var (vm, bot, engine) = BuildVm();

            // Turn 1: grid ON (remembered target = Grid, set this turn).
            vm.InputText = "打開格線";
            vm.SendCommand.Execute(null);

            // Two intervening NON-adjusting turns push the context out of the 2-turn
            // window. Use bare shader-show queries that don't move any target.
            vm.InputText = "show settings"; vm.SendCommand.Execute(null);
            vm.InputText = "show settings"; vm.SendCommand.Execute(null);

            double gridBefore = engine.Current.Shader.GridVisibility;

            // Turn 4: a bare 「關掉」 is now STALE — must NOT silently turn the grid off.
            vm.InputText = "關掉";
            vm.SendCommand.Execute(null);

            Assert.Equal(gridBefore, engine.Current.Shader.GridVisibility, 6);
            Assert.Equal(0, bot.SendCount);
        });

    [Fact]
    public void BareOff_AfterMaskAdjustment_DeclinesHonestly_PointsToSettings()
        => WithLanguage(UILanguage.ChineseTraditional, () =>
        {
            var (vm, bot, engine) = BuildVm();

            // Turn 1: a mask change via the lexicon sets the remembered target = Mask.
            vm.InputText = "/mask 50";
            vm.SendCommand.Execute(null);
            double maskAfterSet = engine.Current.Shader.MaskDepth;

            // Turn 2: bare 「關掉」 → mask has NO off; reply honestly, do NOT lower the mask.
            vm.InputText = "關掉";
            vm.SendCommand.Execute(null);

            Assert.Equal(maskAfterSet, engine.Current.Shader.MaskDepth, 6); // unchanged
            Assert.Equal(0, bot.SendCount);
            var lastAssistant = vm.Messages.Last(m => m.Role == ChatRole.Assistant);
            Assert.Contains("設定", lastAssistant.Content); // points to Settings
        });

    // ── 4. Bare magnitude follow-ups still reach the LLM ─────────────────────

    [Fact]
    public void IsBareOffFollowup_MagnitudeFollowups_AreNotBareOff()
    {
        // 「更低 / lower / 再低一點」 are NOT off/on requests — they must remain LLM-bound
        // (the static classifier returns false so SendAsync does not short-circuit them).
        Assert.False(ChatbotViewModel.IsBareOffFollowup("更低"));
        Assert.False(ChatbotViewModel.IsBareOffFollowup("再低一點"));
        Assert.False(ChatbotViewModel.IsBareOffFollowup("lower"));
        Assert.False(ChatbotViewModel.IsBareOffFollowup("a bit less"));
    }

    [Theory]
    [InlineData("關掉")]
    [InlineData("直接關閉")]
    [InlineData("off")]
    [InlineData("turn it off")]
    public void IsBareOffFollowup_BareOffPhrases_AreBareOff(string input)
        => Assert.True(ChatbotViewModel.IsBareOffFollowup(input));

    [Theory]
    [InlineData("關閉網格")]   // object-bearing → not BARE (handled by the lexicon path)
    [InlineData("turn off the grid")]
    public void IsBareOffFollowup_ObjectBearing_AreNotBareOff(string input)
        => Assert.False(ChatbotViewModel.IsBareOffFollowup(input));

    // ── 5. Feather removed from the chatbot semantic layer ───────────────────

    [Fact]
    public void SystemPrompt_DoesNotMentionFeather()
    {
        // String-scan the live NLU contract: the chatbot must neither describe nor
        // request feather. (The /feather slash command + engine knob are separate and
        // intentionally untouched.)
        string prompt = MistralChatbotService.SystemPromptForTests;
        Assert.DoesNotContain("feather", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("邊緣太硬")]        // the old SofterEdge cue
    [InlineData("the mask edge is too sharp")]
    [InlineData("soften the edge")]
    [InlineData("I have a really bad migraine, the screen is killing me")] // strongest discomfort
    public void FeedbackLexicon_NeverDerivesAFeatherDelta(string input)
    {
        var (_, _, hintFeather, _) =
            SenseEase.Infrastructure.Nlp.FeedbackLexicon.ClassifyShaderHints(input);
        Assert.Equal(0.0, hintFeather, 6);

        var (intent, _) = SenseEase.Infrastructure.Nlp.FeedbackLexicon.Classify(input);
        int sev = SenseEase.Infrastructure.Nlp.FeedbackLexicon.SeverityBucket(input);
        var (_, _, _, derivedFeather) =
            SenseEase.Infrastructure.Nlp.FeedbackLexicon.DeriveAdjustments(intent, sev, input);
        Assert.Equal(0.0, derivedFeather, 6);
    }
}
