using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.Configuration;
using SenseEase.App.Localization;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UX-07 (carried) — chat failure / loading status copy.
///
/// <para>Pins the three UX-07 contracts on <see cref="ChatbotViewModel"/>:
/// <list type="bullet">
///   <item>The user-visible <c>StatusText</c> (model-missing, lexicon fallback,
///   load failure) is calm plain copy in the active language — it NEVER carries an
///   absolute drive-letter path (the model path embeds the Windows username) nor the
///   raw exception text from a failed load.</item>
///   <item>The generic <c>SendAsync</c> catch surfaces the calm "unavailable" bubble,
///   never the old <c>[Error] {ex.Message}</c> passthrough.</item>
///   <item>The probe-path / exception DETAIL is routed to the Event Log instead
///   (DEAD-06 first half: <c>AppConfig.ModelPathProbes</c> wired into the Event-Log
///   entry), redacted of the user-profile path segment so the durable forensic trail
///   stays PII-free.</item>
/// </list>
/// Plus the CW-12 persisted-timestamp contract: <c>PersistMessage</c> stamps UTC.</para>
///
/// <para>The data-root seam (CW-9) routes the VM's chat_history.jsonl write into a
/// per-class temp subdir so these tests never touch the developer's real
/// %LOCALAPPDATA%\SenseEase data.</para>
/// </summary>
[Collection("TestDataRoot")]
public class ChatbotViewModelStatusCopyTests
{
    private readonly string _dataRoot;

    public ChatbotViewModelStatusCopyTests()
    {
        _dataRoot = TestDataRootFixture.UseClassSubdir(nameof(ChatbotViewModelStatusCopyTests));
    }

    // A drive-letter path (C:\..., D:/...) — the absolute-path leak UX-07 forbids on
    // the user-visible status line. Matches both Windows separators.
    private static readonly Regex DriveLetterPath = new(@"[A-Za-z]:[\\/]", RegexOptions.Compiled);

    private static void AssertNoLeakedPath(string s)
    {
        Assert.DoesNotMatch(DriveLetterPath, s);
        Assert.DoesNotContain(@"Users\", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GGUF", s, StringComparison.OrdinalIgnoreCase);
    }

    // ── Stubs ──────────────────────────────────────────────────────────────

    /// <summary>Configurable LLM stub: choose whether the model "is available", and
    /// optionally make LoadAsync / SendAsync fault to drive the failure paths.</summary>
    private sealed class StubChatbotService : IChatbotService
    {
        private readonly Func<Task>? _loadOverride;
        private readonly Func<Task<NluResult>>? _sendOverride;

        public StubChatbotService(
            bool isModelAvailable,
            Func<Task>? loadOverride = null,
            Func<Task<NluResult>>? sendOverride = null)
        {
            IsModelAvailable = isModelAvailable;
            _loadOverride = loadOverride;
            _sendOverride = sendOverride;
        }

        public bool IsModelAvailable { get; }
        // Never reports "loaded" — the lexicon-fallback / unavailable copy path.
        public bool IsModelLoaded => false;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() => _loadOverride?.Invoke() ?? Task.CompletedTask;
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default)
            => _sendOverride?.Invoke()
               ?? Task.FromResult(new NluResult { Intent = "Unknown", Response = "ok" });
        public void ClearHistory() { }
        public void Dispose() { }
    }

    private sealed class StubAdjustmentLog : IAdjustmentLog
    {
        public void Append(AdjustmentRecord record) { }
        public IReadOnlyList<AdjustmentRecord> All() => Array.Empty<AdjustmentRecord>();
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private static ChatbotViewModel BuildVm(StubChatbotService chatbot, IEventLogStore? eventLog = null)
        => new ChatbotViewModel(
            chatbot: chatbot,
            engine: new MockInferenceEngine(),
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => false,
            eventLog: eventLog);

    /// <summary>Run <paramref name="body"/> with the UI language pinned, restoring the
    /// process-wide singleton afterwards (it is shared across the whole test run).</summary>
    private static void WithLanguage(UILanguage lang, Action body)
    {
        var prior = LocalizationManager.Current.Language;
        try
        {
            LocalizationManager.Current.Language = lang;
            body();
        }
        finally
        {
            LocalizationManager.Current.Language = prior;
        }
    }

    // ── 1. Model-missing status copy (constructor) ──────────────────────────

    [Theory]
    [InlineData(UILanguage.ChineseTraditional)]
    [InlineData(UILanguage.English)]
    public void ModelMissing_StatusText_IsCalmCopy_NoPathNoJargon(UILanguage lang)
    {
        WithLanguage(lang, () =>
        {
            var vm = BuildVm(new StubChatbotService(isModelAvailable: false));

            // Calm plain copy is shown (the status border is meant to surface this state).
            Assert.True(vm.HasStatus);
            // The user NEVER sees the absolute model path / GGUF jargon.
            AssertNoLeakedPath(vm.StatusText);
            // The two languages produce DIFFERENT copy (one language per string).
            string expected = lang == UILanguage.English
                ? "Assistant unavailable — quick commands still work"
                : "目前無法使用助理，仍可使用快速指令";
            Assert.Equal(expected, vm.StatusText);
        });
    }

    [Fact]
    public void ModelMissing_ProbePaths_RoutedToEventLog_Redacted()
    {
        var eventLog = new InMemoryEventLogStore();
        _ = BuildVm(new StubChatbotService(isModelAvailable: false), eventLog);

        // DEAD-06: the where-we-looked detail lands in the Event Log, not the UI.
        var entry = eventLog.Recent().FirstOrDefault(e => e.Category == "Chatbot.ModelDiagnostic");
        Assert.NotNull(entry);

        // The probe paths run under the test bin (…\Users\<dev>\…). PathRedactor keeps the
        // "\Users\" prefix but replaces the ACCOUNT NAME with "<redacted>" — so the
        // no-PII-in-logs invariant means the developer's real username never appears, even
        // though the literal "\Users\" segment survives by design.
        string userName = Environment.UserName;
        if (!string.IsNullOrEmpty(userName))
            Assert.DoesNotContain($@"\Users\{userName}\", entry!.Message, StringComparison.OrdinalIgnoreCase);

        // ModelPathProbes is populated by AppConfig's static init (the module initializer
        // already touched AppConfig), so at least the in-bin probe was wired into the line.
        if (AppConfig.ModelPathProbes.Count > 0)
        {
            Assert.Contains("probed:", entry!.Message, StringComparison.Ordinal);
            // A probe path UNDER \Users\ must have been redacted to the placeholder.
            bool anyUserPath = AppConfig.ModelPathProbes.Any(
                p => p.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase));
            if (anyUserPath)
                Assert.Contains(@"\Users\<redacted>\", entry!.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 2. Failing LOAD via the seam ────────────────────────────────────────

    [Theory]
    [InlineData(UILanguage.ChineseTraditional)]
    [InlineData(UILanguage.English)]
    public void LoadFailure_StatusText_IsUnavailableCopy_NeverRawException(UILanguage lang)
    {
        // Drive a FAILING load through the seam: IsModelAvailable=true gets us past the
        // constructor's not-found branch, and LoadAsync faults with a sentinel message
        // that must NEVER reach the user-visible status line.
        const string sentinel = "SENTINEL_RAW_EXCEPTION_a91f";
        var eventLog = new InMemoryEventLogStore();
        var vm = BuildVm(
            new StubChatbotService(
                isModelAvailable: true,
                loadOverride: () => Task.FromException(new InvalidOperationException(sentinel)),
                // Empty reply on the subsequent lexicon-fallback send keeps the render on
                // the synchronous (non-fake-stream) path — no background animation thread.
                sendOverride: () => Task.FromResult(new NluResult { Intent = "Unknown", Response = string.Empty })),
            eventLog);

        WithLanguage(lang, () =>
        {
            // A model-available VM starts with an empty status; the failing load flips it
            // to the calm unavailable copy. SendCommand drives EnsureModelLoadedAsync →
            // InitModelAsync synchronously (the faulted task + current-thread dispatcher).
            vm.InputText = "hello";
            vm.SendCommand.Execute(null);

            string expected = lang == UILanguage.English
                ? "Assistant unavailable — quick commands still work"
                : "目前無法使用助理，仍可使用快速指令";
            Assert.Equal(expected, vm.StatusText);

            // The raw exception text never appears on the status line or in any bubble.
            Assert.DoesNotContain(sentinel, vm.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("Load failed", vm.StatusText, StringComparison.OrdinalIgnoreCase);
            AssertNoLeakedPath(vm.StatusText);
            Assert.DoesNotContain(vm.Messages, m => m.Content.Contains(sentinel, StringComparison.Ordinal));
        });

        // The exception DETAIL is preserved — in the Event Log only.
        Assert.Contains(eventLog.Recent(),
            e => e.Category == "Chatbot.ModelDiagnostic" && e.Message.Contains(sentinel, StringComparison.Ordinal));
    }

    // ── 3. Generic SendAsync catch — calm bubble, not [Error] {ex.Message} ───

    [Fact]
    public void SendFailure_GenericCatch_ShowsCalmBubble_NotRawError()
    {
        const string sentinel = "SENTINEL_SEND_FAILURE_7c20";
        var eventLog = new InMemoryEventLogStore();
        var vm = BuildVm(
            new StubChatbotService(
                isModelAvailable: true,
                sendOverride: () => Task.FromException<NluResult>(new InvalidOperationException(sentinel))),
            eventLog);

        WithLanguage(UILanguage.English, () =>
        {
            vm.InputText = "please adjust the overlay";
            vm.SendCommand.Execute(null);

            // No "[Error] ..." bubble and no raw exception text anywhere in the transcript.
            Assert.DoesNotContain(vm.Messages, m => m.Content.Contains("[Error]", StringComparison.Ordinal));
            Assert.DoesNotContain(vm.Messages, m => m.Content.Contains(sentinel, StringComparison.Ordinal));
            // The calm unavailable copy is shown as the assistant reply instead.
            Assert.Contains(vm.Messages,
                m => m.Role == ChatRole.Assistant && m.Content == "Assistant unavailable — quick commands still work");
            Assert.False(vm.IsGenerating);
            Assert.DoesNotContain(vm.Messages, m => m.IsTyping);
        });

        // The detail is preserved in the Event Log.
        Assert.Contains(eventLog.Recent(),
            e => e.Category == "Chatbot.ModelDiagnostic" && e.Message.Contains(sentinel, StringComparison.Ordinal));
    }

    // ── 4. Loading copy localizes, no jargon ────────────────────────────────

    [Theory]
    [InlineData(UILanguage.ChineseTraditional, "助理正在準備，第一次約需 30 秒")]
    [InlineData(UILanguage.English, "The assistant is getting ready — first time takes ~30s")]
    public void LoadingStatus_Localizes_NoModelNameNoPath(UILanguage lang, string expected)
    {
        // Model available so the FIRST send kicks the lazy load and sets the loading line.
        // EnsureModelLoadedAsync sets the loading StatusText SYNCHRONOUSLY, then awaits the
        // load — we hold the load open with a gate so the loading line is the observable
        // status when Execute returns.
        //
        // DEADLOCK ANATOMY (found via --blame-hang; do not regress): leaving the gate
        // pending makes xUnit's AsyncTestSyncContext wait FOREVER on the async-void send
        // chain at test end; completing it WITHOUT pumping strands InitModelAsync's
        // _dispatcher.Invoke on this thread's never-pumped dispatcher. The only safe
        // teardown is BOTH: fail the gate, then pump this thread's dispatcher until the
        // chain drains (see finally).
        var loadGate = new TaskCompletionSource();
        var vm = BuildVm(new StubChatbotService(
            isModelAvailable: true,
            loadOverride: () => loadGate.Task));

        try
        {
            WithLanguage(lang, () =>
            {
                vm.InputText = "hi";
                vm.SendCommand.Execute(null); // sets the loading StatusText, then awaits the gate

                Assert.Equal(expected, vm.StatusText);
                AssertNoLeakedPath(vm.StatusText);
                Assert.DoesNotContain("Ministral", vm.StatusText, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            loadGate.SetException(new TimeoutException("test teardown — load aborted"));
            // Drain: InitModelAsync's catch posts to this thread's dispatcher; pump until
            // the status leaves the loading line (the unavailable copy lands), then settle.
            PumpDispatcherUntil(() => vm.StatusText != expected, timeoutMs: 5000);
            PumpDispatcherUntil(() => false, timeoutMs: 100); // settle residual queued work
        }
    }

    /// <summary>Pumps the CURRENT thread's (lazily-created) dispatcher with nested
    /// <see cref="System.Windows.Threading.DispatcherFrame"/>s until <paramref name="done"/>
    /// or timeout. Required whenever a test completes a task that resumes a pool-thread
    /// continuation calling <c>_dispatcher.Invoke</c> back onto this test thread.</summary>
    private static void PumpDispatcherUntil(Func<bool> done, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(10); // give the pool-thread continuation a beat to enqueue more work
        }
    }

    // ── 5. CW-12: persisted chat timestamps are UTC ─────────────────────────

    [Fact]
    public void PersistMessage_StampsUtc_OnDisk()
    {
        string historyPath = AppConfig.ChatHistoryPath;
        Assert.StartsWith(_dataRoot, historyPath, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(historyPath)) File.Delete(historyPath);

        // A slash-command short-circuit persists the user turn synchronously without
        // needing the LLM. "/grid off" is resolved locally by the shader lexicon.
        var vm = BuildVm(new StubChatbotService(isModelAvailable: true));
        var before = DateTime.UtcNow.AddSeconds(-5);
        vm.InputText = "/grid off";
        vm.SendCommand.Execute(null);
        var after = DateTime.UtcNow.AddSeconds(5);

        Assert.True(File.Exists(historyPath), "the slash-command turn must persist on disk");
        var persisted = new FileChatHistoryStore(historyPath).Load();
        Assert.NotEmpty(persisted);

        var userTurn = persisted.First(m => m.Role == ChatRole.User);
        // CW-12: the persisted record is stamped in UTC (Kind == Utc round-trips through
        // the JSONL as a trailing 'Z'), and its value sits in the UtcNow window — a
        // DateTime.Now (local-kind) stamp would deserialize as Local kind.
        Assert.Equal(DateTimeKind.Utc, userTurn.Timestamp.Kind);
        Assert.InRange(userTurn.Timestamp, before, after);
    }
}
