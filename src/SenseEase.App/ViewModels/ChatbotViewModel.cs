using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SenseEase.App.Configuration;
using SenseEase.App.Localization;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Core.Safety;
using SenseEase.Infrastructure.Real;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.App.ViewModels;

/// <summary>View-model for the AI Assistant chatbot tab.</summary>
public sealed class ChatbotViewModel : INotifyPropertyChanged
{
    private readonly IChatbotService _chatbot;
    private readonly IInferenceEngine _engine;
    private readonly IAdjustmentLog _adjustmentLog;
    private readonly IEventLogStore? _eventLog;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _isOverlayVisible;
    private readonly FileChatHistoryStore _historyStore;
    private CancellationTokenSource _streamCts = new();

    /// <summary>How many recent adjustment / event records to embed in each model prompt.</summary>
    private const int HistoryWindow = 6;

    /// <summary>How many most-recent transcript bubbles to keep on disk. Older lines
    /// are dropped on startup (and opportunistically mid-session) so the file can't
    /// grow without bound.</summary>
    private const int PersistedHistoryMax = 100;

    /// <summary>NE18: how far the on-disk line count may overshoot
    /// <see cref="PersistedHistoryMax"/> before an in-session trim fires. A slack
    /// margin means we don't pay a rewrite on every append once the cap is reached —
    /// we let the tail grow by this many lines, then drop back to the cap in one pass.</summary>
    private const int PersistedHistoryTrimSlack = 40;

    /// <summary>NE18: running count of how many lines we believe are on disk, seeded
    /// from the startup <see cref="FileChatHistoryStore.Load"/> and bumped by each
    /// <see cref="PersistMessage"/>. Lets us decide when to trim without re-reading
    /// the file on every append; the startup load is the "already-loaded list" the
    /// counter reuses. Only ever touched while persisting, which is serialized onto
    /// the dispatcher thread.</summary>
    private int _persistedLineCount;

    /// <summary>NE6: hard ceiling (seconds) on a single interactive LLM call so a
    /// stalled model surfaces a calm retry hint instead of hanging the typing
    /// indicator forever. The HTTP client has its own timeout; this caps the call
    /// cooperatively end-to-end and lets a superseding turn abort it.</summary>
    private const int InteractiveLlmTimeoutSeconds = 60;

    /// <summary>H5: per-session ceiling on OBSERVATION_ADD persistence. The parser
    /// already caps observations at 2 PER RESPONSE; this bounds the TOTAL across a
    /// chat session so a looping / injected model can't slowly fill the profile's
    /// longitudinal memory (a stored prompt-injection vector). Reset by Clear().</summary>
    private const int MaxObservationWritesPerSession = 2;
    private int _observationWritesThisSession;

    /// <summary>Total cap on fake-stream animation duration, regardless of reply length.</summary>
    private const int FakeStreamMaxMs = 1500;
    private const int FakeStreamStepMs = 30;

    /// <summary>Round-3 (Kirby item 5, follow-up context): which shader target the
    /// chatbot last ACTED on (grid / mask / fov), so a bare object-less follow-up like
    /// 「直接關閉」/「關掉」/「off」 can apply OFF to the remembered target instead of
    /// being routed to the LLM (where the live failure produced a wrong-target,
    /// wrong-sign delta). Set by BOTH the lexicon short-circuit (HandleShaderCommand)
    /// and any LLM-applied adjustment (ApplyNluResult). Null = no remembered target.</summary>
    private FollowUpTarget? _lastAdjustTarget;

    /// <summary>How many user turns ago <see cref="_lastAdjustTarget"/> was set,
    /// counted forward each SendAsync. A bare follow-up only inherits the remembered
    /// target within <see cref="FollowUpTargetWindow"/> turns; after that the context
    /// is stale and a bare 「關掉」 goes to the honest-clarify path instead.</summary>
    private int _turnsSinceLastAdjust = int.MaxValue;

    /// <summary>Round-3: a bare object-less off/on follow-up inherits the last target
    /// only within this many user turns (per Kirby: "within the next 2 user turns").</summary>
    private const int FollowUpTargetWindow = 2;

    /// <summary>The shader target a follow-up off/on can refer back to. Mirrors the
    /// subset of <see cref="ShaderParam"/> the chatbot adjusts; kept VM-local so the
    /// follow-up bookkeeping doesn't leak into the Core selector enum.</summary>
    private enum FollowUpTarget { Grid, Mask, Fov }

    /// <summary>UX-19: welcome shown when the transcript is empty — on first launch
    /// (constructor, when prior.Count == 0) and after Clear() so the chat surface doesn't
    /// go blank mid-session. Previously a single stacked zh+en const (both languages on
    /// one bubble, breaking the one-language-per-string rule); now a single-language Pick
    /// chosen at DISPLAY time. Safe to localize at display time because the welcome is
    /// NEVER persisted — if Clear() deleted the file, the constructor's empty-prior branch
    /// re-shows it (re-picked) on the next launch, so no stale-language copy survives.</summary>
    private static string WelcomeMessage() =>
        LocalizationManager.Current.Pick(
            "您好，我是 SenseEase AI 助理。請描述您的視覺不適（暈眩、噁心、頭痛、眼睛疲勞），我會為您調整防護設定。",
            "Hi — I'm the SenseEase AI assistant. Describe any visual discomfort and I'll adjust your overlay protection.");

    private bool _isGenerating;
    private bool _isPaused;
    private string _inputText = string.Empty;
    private string _statusText = string.Empty;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    // ── Calibration CARD members REMOVED (2026-06-08) ───────────────────────
    // The 5-card Q&A, the H11 confirm card, the invitation banner, the LLM
    // calibration session + its auto-dismiss timer, and ShowCalibrationEntry /
    // Recalibrate were dead, unreachable UI and are gone. (Engine-side
    // CalibrationProcessor / CalibrationResult are kept — see MainViewModel.)

    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (Set(ref _isGenerating, value))
            {
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
                // NE6: the Cancel affordance is only live during an in-flight turn, so
                // re-evaluate its executability whenever generation starts/stops.
                ((RelayCommand)CancelGenerationCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>P2 (Pause/Resume mode-shift): when true, the chatbot is gated —
    /// <see cref="SendCommand"/>.CanExecute returns false while the sensing
    /// pipeline is paused. Set by MainWindow.ApplyPowerSave: true on pause,
    /// false on resume. Flipping it re-evaluates the command's executability.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (Set(ref _isPaused, value))
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
        }
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (Set(ref _inputText, value))
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (Set(ref _statusText, value)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatus))); }
    }

    /// <summary>True only while there is a transient status worth surfacing (loading /
    /// load-failure / model-missing). The dashboard binds the status border's Visibility
    /// to this so the box collapses entirely when idle — Kirby flagged the steady-state
    /// "Ministral 3B ready" badge as needless chrome, so the ready/standby state now
    /// clears StatusText to empty and the border disappears.</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(_statusText);

    public ICommand SendCommand { get; }
    public ICommand ClearCommand { get; }

    /// <summary>NE6: cancel the in-flight interactive LLM turn. Cancels the same
    /// <see cref="_streamCts"/> that <see cref="SendAsync"/> links the model call into
    /// (via <c>callCts</c>), so the awaited <c>SendAsync</c> unwinds through the existing
    /// <see cref="OperationCanceledException"/> catch — the typing indicator is removed
    /// and the existing localized cancelled-state copy ("回應花的時間有點久…" /
    /// "That took a while…") renders, exactly as the 60 s ceiling path does. CanExecute is
    /// true only while a turn is generating, so the button collapses when there's nothing
    /// to cancel.</summary>
    public ICommand CancelGenerationCommand { get; }


    /// <summary>M2 (Phase 6c): host-provided lookup for "is Power Save
    /// currently active?". Used by <see cref="BuildContextualInput"/> to
    /// emit <c>power_save_active: true</c> in the <c>[habituation_state]</c>
    /// block when the sensing pipeline is paused, so the LLM knows the
    /// tier/pressure data is stale. Null = treat as inactive (the path
    /// taken when ChatbotViewModel is constructed outside MainWindow,
    /// e.g. tests).</summary>
    public Func<bool>? IsPowerSaveActive { get; set; }

    /// <summary>M2 (Phase 6c): host-provided lookup for the persisted
    /// habituation profile snapshot. ChatbotViewModel can't take a
    /// direct dependency on FileHabituationProfileStore without dragging
    /// Infrastructure into the VM layer, so the host wires this lambda
    /// at construction time. Null = block omitted entirely (graceful for
    /// pre-calibration / test contexts).</summary>
    public Func<HabituationProfileSnapshot?>? LoadHabituationSnapshot { get; set; }

    /// <summary>UM5: host-wired reader for the daily behaviour aggregates
    /// (behavior_log.jsonl) used to build the [behavior_trend] block. Same Infra-out-
    /// of-the-VM pattern as <see cref="LoadHabituationSnapshot"/>. Null = block omitted
    /// (graceful for test contexts / no behaviour history).</summary>
    public Func<IReadOnlyList<DailyBehaviorAggregate>>? LoadBehaviorAggregates { get; set; }

    public ChatbotViewModel(
        IChatbotService chatbot,
        IInferenceEngine engine,
        IAdjustmentLog adjustmentLog,
        Func<bool> isOverlayVisible,
        IEventLogStore? eventLog = null)
    {
        _chatbot = chatbot;
        _engine = engine;
        _adjustmentLog = adjustmentLog;
        _eventLog = eventLog;
        // Production: bound to the WPF UI thread's dispatcher.
        // Headless tests: Application.Current is null, so we fall back to the
        // CALLING thread's dispatcher (xunit's). Async methods that call
        // _dispatcher.Invoke still work; the tests that exercise async paths
        // either don't construct ChatbotViewModel at all or pump the dispatcher
        // on an STA thread.
        // Prefer the app's UI dispatcher in production. Use it ONLY when the
        // constructing thread actually owns it (CheckAccess) and it is not shutting
        // down. Under xUnit, a prior WPF test on a parallel STA thread can leave an
        // Application.Current behind whose dispatcher belongs to another (now dead or
        // parked) thread — Invoke on it either throws (dead) or BLOCKS forever
        // (parked, never pumped), silently dropping or hanging the crisis floor's
        // bubble-add. Falling back to the current thread's live dispatcher keeps the
        // crisis Invoke synchronous in tests. In production the VM is constructed on
        // the UI thread, so CheckAccess() is true and behaviour is unchanged.
        var appDispatcher = Application.Current?.Dispatcher;
        _dispatcher = (appDispatcher is not null
                       && appDispatcher.CheckAccess()
                       && !appDispatcher.HasShutdownStarted)
            ? appDispatcher
            : Dispatcher.CurrentDispatcher;
        _isOverlayVisible = isOverlayVisible;

        SendCommand = new RelayCommand(async _ => await SendAsync(),
            _ => !IsGenerating && !IsPaused && !string.IsNullOrWhiteSpace(InputText));
        ClearCommand = new RelayCommand(_ => Clear());
        // NE6: cancel only does anything while a turn is actually in flight.
        CancelGenerationCommand = new RelayCommand(_ => CancelGeneration(), _ => IsGenerating);


        // Idle/standby shows NOTHING (Kirby: the "ready" badge is needless chrome). The
        // status border only appears for actionable states: a transient "loading…" on the
        // first chat, a load failure, or a missing-model state.
        // UX-07: the user-visible status is calm plain copy in the active language — it
        // NEVER carries the absolute model path (which embeds the Windows username) or
        // tech jargon ("GGUF"). The diagnostic probe paths go to the Event Log only
        // (DEAD-06: wire ModelPathProbes into the Event-Log detail), redacted of the
        // user-profile segment so the durable forensic trail stays PII-free.
        StatusText = chatbot.IsModelAvailable ? string.Empty : AssistantUnavailableText();
        if (!chatbot.IsModelAvailable)
            LogModelDiagnostic("model not found", AppConfig.ModelPathProbes);

        // Lazy model load (startup speed): the GGUF no longer loads at app startup —
        // EnsureModelLoadedAsync kicks it on the FIRST chat message that reaches the LLM
        // (SendAsync). Launch stays fast and the heavy model read / AV scan only happens
        // when the user actually chats. Safety-floor + slash-command + profile-picker
        // short-circuits never need the model, so they stay instant.

        // Phase 4: persist transcript across restarts. Load existing history first;
        // if any prior bubbles exist they replace the welcome (returning user — the
        // welcome bubble would be noise above their previous conversation).
        _historyStore = new FileChatHistoryStore(AppConfig.ChatHistoryPath);
        var prior = _historyStore.Load();
        if (prior.Count > PersistedHistoryMax)
        {
            _historyStore.Trim(PersistedHistoryMax);
            prior = _historyStore.Load();
        }
        // NE18: seed the in-session line counter from what we just loaded so the
        // opportunistic trim in PersistMessage can fire without re-reading the file.
        _persistedLineCount = prior.Count;

        if (prior.Count > 0)
        {
            foreach (var m in prior)
                Messages.Add(new ChatMessageViewModel(m.Content, m.Role));
        }
        else
        {
            Messages.Add(new ChatMessageViewModel(WelcomeMessage(), ChatRole.Assistant));
        }
    }

    /// <summary>Persist a non-transient bubble to the chat-history file. Safe to call
    /// for any real message; typing-indicator placeholders must NOT pass through here.</summary>
    private void PersistMessage(ChatRole role, string content)
    {
        if (string.IsNullOrEmpty(content)) return;
        _historyStore.Append(new ChatMessage
        {
            Role = role,
            Content = content,
            // CW-12: stamp PERSISTED records in UTC so the on-disk transcript shares one
            // clock domain with the durable event log / engine writers (all UtcNow). The
            // user-visible bubble timestamp is computed separately at bubble-construction
            // time (ChatMessageViewModel.Timestamp = DateTime.Now.ToString("HH:mm")) and
            // the rehydrate path never reads this persisted value, so display stays local
            // and unchanged.
            Timestamp = DateTime.UtcNow,
        });

        // NE18: trim opportunistically once the file overshoots the cap by the slack
        // margin, so a long single session can't grow the transcript without bound
        // (the startup-only trim used to leave in-session appends unbounded). We track
        // the on-disk count in _persistedLineCount rather than re-reading after every
        // append; only the periodic Trim re-reads. After a trim the file holds at most
        // PersistedHistoryMax lines, so we snap the counter back to that cap.
        if (++_persistedLineCount > PersistedHistoryMax + PersistedHistoryTrimSlack)
        {
            _historyStore.Trim(PersistedHistoryMax);
            _persistedLineCount = PersistedHistoryMax;
        }
    }

    // Lazy-load guard: InitModelAsync runs at most once, on the first chat message that
    // reaches the LLM (see SendAsync). Interlocked so concurrent first-sends can't double-load.
    private int _modelInitStarted;
    private Task _modelInitTask = Task.CompletedTask;

    private Task EnsureModelLoadedAsync()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _modelInitStarted, 1, 0) == 0)
        {
            // UX-07: calm plain loading copy in the active language — no model name /
            // "CPU" / "GGUF" jargon on the user-facing status line.
            StatusText = AssistantLoadingText();
            _modelInitTask = InitModelAsync();
        }
        return _modelInitTask;
    }

    private async Task InitModelAsync()
    {
        try
        {
            await _chatbot.LoadAsync().ConfigureAwait(false);
            // Capture loaded-state off the dispatcher so the diagnostic log (below) runs
            // whether or not a UI dispatcher is live; it's pure data, no UI touch.
            bool loaded = _chatbot.IsModelLoaded;
            if (!loaded)
                LogModelDiagnostic("lexicon fallback (no model loaded)", AppConfig.ModelPathProbes);
            _dispatcher.Invoke(() =>
            {
                // UX-07: loaded OK → clear the transient "loading…" line back to empty (no
                // steady-state "ready" badge). The lexicon-fallback case surfaces the calm
                // "unavailable" copy — NOT the GGUF probe path (which leaks the username);
                // that diagnostic went to the Event Log above instead.
                StatusText = loaded ? string.Empty : AssistantUnavailableText();
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            // UX-07: the raw exception (type + message) is a diagnostic — it goes to the
            // Event Log only, redacted of any user-profile path segment. The user sees the
            // same calm "unavailable" copy, never "Load failed: <exception>".
            LogModelDiagnostic($"load failed: {ex.GetType().Name}: {ex.Message}", null);
            _dispatcher.Invoke(() =>
            {
                StatusText = AssistantUnavailableText();
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
            });
        }
    }

    // ── UX-07: calm, plain, single-language status / failure copy ───────────
    // One language per string via LocalizationManager.Pick, zh-first to match the
    // app's primary language. NONE of these carry an absolute path, the model name,
    // or "GGUF"/"Lexicon" jargon — that detail lives in the Event Log (see
    // LogModelDiagnostic). The transient "loading" line and both unavailable lines
    // are the only chat statuses the user ever sees.

    /// <summary>First-chat "getting ready" status (~30 s on a cold model load).</summary>
    private static string AssistantLoadingText() =>
        LocalizationManager.Current.Pick(
            "助理正在準備，第一次約需 30 秒",
            "The assistant is getting ready — first time takes ~30s");

    /// <summary>Single calm line for every "assistant can't run right now" state —
    /// model missing, lexicon fallback, load failure, or a failed chat turn. Points
    /// the user at the quick commands, which never need the model.</summary>
    private static string AssistantUnavailableText() =>
        LocalizationManager.Current.Pick(
            "目前無法使用助理，仍可使用快速指令",
            "Assistant unavailable — quick commands still work");

    /// <summary>UX-07 / DEAD-06: route a model/chat diagnostic to the Event Log ONLY —
    /// never to a user-visible surface. Closes DEAD-06's first half by wiring
    /// <see cref="AppConfig.ModelPathProbes"/> (the where-we-looked list) into the
    /// Event-Log detail entry. Every probe path / exception string is run through
    /// <see cref="PathRedactor.Redact"/> so the durable forensic JSONL keeps the
    /// project-wide no-PII-in-logs invariant (the probe paths embed the Windows
    /// username). Category prefix is "Chatbot." so FileEventLogStore persists it.
    /// Best-effort: an audit-write failure must never break the chat / load path.</summary>
    private void LogModelDiagnostic(string summary, IReadOnlyList<string>? probes)
    {
        if (_eventLog is null) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append(PathRedactor.Redact(summary));
            if (probes is { Count: > 0 })
            {
                sb.Append(" | probed: ");
                for (int i = 0; i < probes.Count; i++)
                {
                    if (i > 0) sb.Append(" ; ");
                    sb.Append(PathRedactor.Redact(probes[i]));
                }
            }
            _eventLog.Append(new EventLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category  = "Chatbot.ModelDiagnostic",
                Message   = sb.ToString(),
            });
        }
        catch { /* best-effort; an audit-write failure must never break the chat turn */ }
    }

    private async Task SendAsync()
    {
        string text = InputText.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Alpha-1 W1 (H1/H2/H4): clinical self-harm safety floor — the FIRST
        // thing SendAsync does, before the profile-picker short-circuit, the
        // lexicon fallback, and any LLM dispatch. A user in crisis can never be
        // routed to the profile-picker UI or a profile-switch Card. The trigger
        // phrase is NEVER persisted: chat_history gets a redacted placeholder
        // (H2), not the raw text. The bubble still shows the user's own words in
        // the live session; only the on-disk record is redacted.
        if (SafetyFloor.Trips(text, out string crisisResponse))
        {
            InputText = string.Empty;
            _dispatcher.Invoke(() =>
            {
                Messages.Add(new ChatMessageViewModel(text, ChatRole.User));
                PersistMessage(ChatRole.User, SafetyFloor.RedactedPlaceholder);
                Messages.Add(new ChatMessageViewModel(crisisResponse, ChatRole.Assistant));
                PersistMessage(ChatRole.Assistant, crisisResponse);
            });
            return; // no picker, no lexicon, no LLM — crisis response only
        }

        // H6: sanitize the live user turn before it can reach the LLM prompt or
        // the persisted history. The safety floor above already saw the ORIGINAL
        // text, so crisis detection is never weakened. This folds newlines and
        // strips the app's own context-block tokens so typed input can't forge a
        // fake [habituation_state] / [RecentAdjustments] block (prompt injection).
        text = SanitizeUserTurn(text);
        if (string.IsNullOrEmpty(text))
        {
            // Review (low): don't let an all-tokens / all-structure message vanish
            // silently — every other early-return path surfaces a bubble, so this
            // one should too rather than clearing the box into the void.
            InputText = string.Empty;
            _dispatcher.Invoke(() => Messages.Add(new ChatMessageViewModel(
                SenseEase.App.Localization.LocalizationManager.Current.Pick(
                    "我無法處理這則訊息，請重新輸入。",
                    "I couldn't process that message — please try rephrasing."),
                ChatRole.Assistant)));
            return;
        }

        // Round-3 follow-up context: count this real (non-crisis, non-empty) user turn
        // forward so the bare off/on follow-up window can expire. Saturating add so a
        // very long session never overflows. Set/reset of _lastAdjustTarget itself
        // happens where an adjustment is actually applied (HandleShaderCommand /
        // ApplyNluResult), which zeroes the counter for the NEXT turn.
        if (_turnsSinceLastAdjust < int.MaxValue) _turnsSinceLastAdjust++;

        // M3 7e+ / Alpha-2 audit fix: the user typed something like "我想換 profile" /
        // "show me profiles". The chatbot can NO LONGER live-switch the habituation profile
        // (Reading A, 2026-06-06: the SwitchProfile override path is deliberately severed —
        // profile changes must be a deliberate dashboard action, not chatbot-driven). The old
        // in-chat picker routed every selection into that severed no-op and surfaced "切換失敗"
        // to the user (a guaranteed-fail UI; flagged "defunct — product follow-up" in
        // Phase8AcceptanceTests.ScenarioE). So instead of a doomed picker, acknowledge and
        // redirect to the dashboard's working profile pills (OnUserProfileSelected →
        // SetHabituationProfile). No LLM round-trip. (The old in-chat picker's
        // ProfilePickerSelected event + OnChatbotProfilePickerSelected handler — the unfired
        // dead code this redirect replaced — were removed in the LLM-calibration cleanup.)
        if (HabituationProposalLexicon.TryDetectProfilePickerRequest(text))
        {
            InputText = string.Empty;
            bool en = LocalizationManager.Current.IsEnglish;
            // CW-3 (honesty): profiles are auto-managed — there is no user-facing
            // switcher. Describe the auto-management plainly rather than pointing
            // at a manual switch that no longer exists. Only the sensitivity-
            // protection on/off switch lives in Settings.
            string reply = en
                ? "SenseEase manages your comfort profile automatically — no manual switching needed. The sensitivity-protection switch lives in Settings."
                : "SenseEase 會依您的使用情況自動調整防護設定，不需要手動切換。敏感度（前庭）保護的開關在 設定 裡。";
            _dispatcher.Invoke(() =>
            {
                Messages.Add(new ChatMessageViewModel(text, ChatRole.User));
                PersistMessage(ChatRole.User, text);
                Messages.Add(new ChatMessageViewModel(reply, ChatRole.Assistant));
                PersistMessage(ChatRole.Assistant, reply);
            });
            return; // skip LLM call entirely
        }

        // ── G1: strict-language + slash-command shader control (Alpha-2) ─────
        // AFTER the SafetyFloor short-circuit AND the profile-picker interceptor,
        // BEFORE the LLM/NLU dispatch. A high-confidence "/grid off" / "把格線完全
        // 關掉" resolves INSTANTLY and absolute-set (bypassing the delta-only
        // [-0.3,+0.3] LLM path) → no 5-30 s iGPU round-trip. SetShaderParam /
        // ResetShaderToProfileDefault are on IInferenceEngine now (no cast needed).
        var shaderCmd = ShaderCommandLexicon.Parse(text, LocalizationManager.Current.IsEnglish);
        if (shaderCmd.Kind != ShaderCommandKind.None)
        {
            InputText = string.Empty;
            HandleShaderCommand(shaderCmd, text);
            return; // local reply only — no LLM
        }

        // ── Round-3 (Kirby item 5): bare object-less off/on FOLLOW-UP ─────────
        // A bare 「直接關閉」/「關掉」/「off」/「turn it off」 with NO target noun is
        // ambiguous on its own (which is why the live failure let the LLM pick a wrong
        // target + wrong sign). Resolve it DETERMINISTICALLY against the last target we
        // acted on, within a 2-turn window. Grid → gv=0 (the /grid off path). Mask/Fov
        // have no off — reply honestly that protection can't be turned off from chat and
        // point at Settings. Bare 「更低/lower」-type follow-ups are NOT object-less off/on
        // requests (IsBareOffFollowup is false for them) so they still reach the LLM.
        bool bareOff = IsBareOffFollowup(text);
        if (bareOff
            && _lastAdjustTarget is FollowUpTarget target
            && _turnsSinceLastAdjust <= FollowUpTargetWindow)
        {
            InputText = string.Empty;
            HandleBareOffFollowup(target, text);
            return; // deterministic — no LLM
        }
        if (bareOff)
        {
            // Bare off/on with NO remembered target (or the context is stale): we don't
            // know what to turn off. Ask honestly rather than guess — guessing is exactly
            // the wrong-target failure this change removes.
            InputText = string.Empty;
            bool en = LocalizationManager.Current.IsEnglish;
            string reply = en
                ? "Which setting would you like to turn off? You can say \"turn off the grid\", or use Settings for sensitivity protection."
                : "您想關閉哪一項設定？可以說「關閉網格」，或在 設定 裡調整敏感度保護。";
            _dispatcher.Invoke(() =>
            {
                Messages.Add(new ChatMessageViewModel(text, ChatRole.User));
                PersistMessage(ChatRole.User, text);
                Messages.Add(new ChatMessageViewModel(reply, ChatRole.Assistant));
                PersistMessage(ChatRole.Assistant, reply);
            });
            return; // honest clarify — no LLM, no adjustment
        }

        // Cancel any in-flight fake-stream animation from the prior turn so its
        // dispatcher updates can't overwrite this turn's bubble content.
        try { _streamCts.Cancel(); } catch { /* ignore */ }
        _streamCts.Dispose();
        _streamCts = new CancellationTokenSource();
        var streamToken = _streamCts.Token;

        InputText = string.Empty;
        IsGenerating = true;

        ChatMessageViewModel? placeholder = null;
        _dispatcher.Invoke(() =>
        {
            Messages.Add(new ChatMessageViewModel(text, ChatRole.User));
            PersistMessage(ChatRole.User, text);
            // Typing-indicator bubble: rendered as animated dots by the DataTemplate,
            // not as literal text. Removed when the real reply arrives. NOT persisted.
            placeholder = new ChatMessageViewModel(string.Empty, ChatRole.Assistant, isTyping: true);
            Messages.Add(placeholder);
        });

        try
        {
            // Lazy model load: kick + await the GGUF load on the FIRST message that reaches
            // the LLM (the safety-floor / slash-command / profile-picker short-circuits above
            // never need it). The typing-indicator bubble is already showing, so the one-time
            // ~load wait reads as "thinking". Idempotent — later turns no-op instantly.
            await EnsureModelLoadedAsync().ConfigureAwait(false);

            string contextualInput = BuildContextualInput(text);

            // NE6: thread cancellation + a 60 s ceiling into the interactive LLM
            // call, replacing the previous uncancellable CancellationToken.None.
            // Linked to streamToken so a superseding turn aborts it; CancelAfter
            // gives a hard interactive cap surfaced as a calm retry hint (see the
            // OperationCanceledException catch) instead of the HttpClient timeout
            // bubbling up as a raw "[Error] A task was canceled".
            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(streamToken);
            callCts.CancelAfter(TimeSpan.FromSeconds(InteractiveLlmTimeoutSeconds));
            var result = await _chatbot.SendAsync(contextualInput, callCts.Token).ConfigureAwait(false);

            // M3 7e: extract write-side structured tokens BEFORE the reply
            // reaches the UI. The parser produces zero, one or both of:
            //   - a ProfileOverrideProposal  → ProposalReceived event for host
            //   - 0..2 OBSERVATION_ADD lines → LongitudinalObservationCallback
            // Run on RawModelOutput (the full LLM output) when present so
            // override blocks survive in BOTH cases:
            //   • Correctly-placed blocks AFTER the JSON envelope — invisible
            //     in result.Response (which is just the envelope's "reply"
            //     field).
            //   • Malformed model output where the envelope failed to parse
            //     and result.Response is a lexicon-fallback string with no
            //     trace of the original blocks.
            // Falls back to result.Response (the legacy path) when the
            // service doesn't carry raw — e.g. headless tests, lexicon-only
            // mode, safety-floor short-circuit. Stripping is applied to
            // result.Response only — the visible bubble never contains
            // block markers regardless of where the parser ran.
            string parseSource = !string.IsNullOrEmpty(result.RawModelOutput)
                ? result.RawModelOutput!
                : result.Response;
            var parsedOverride = HabituationOverrideParser.TryParse(parseSource);
            string visibleReply = HabituationOverrideParser.StripAllOverrideBlocks(result.Response);
            // If the strip ate the entire reply, fall back to the raw text
            // so the user sees SOMETHING in the chat (defensive — a model
            // that emits ONLY a structured block with no human prose is
            // misbehaving but shouldn't surface as an empty bubble).
            if (string.IsNullOrWhiteSpace(visibleReply))
                visibleReply = result.Response;

            ChatMessageViewModel? assistantBubble = null;
            string assistantContentForPersist = string.Empty;
            string deferredSummary = string.Empty;
            bool fakeStream = false;

            _dispatcher.Invoke(() =>
            {
                if (placeholder is not null) Messages.Remove(placeholder);
                string summary = ApplyNluResult(result, text);

                // Followup-requesting replies get rendered with option cards. The
                // assistant text is the question itself (FollowupQuestion); if the
                // model only set Response, fall back to that.
                bool hasFollowup = result.FollowupOptions is { Length: > 0 }
                                && !string.IsNullOrWhiteSpace(result.FollowupQuestion ?? visibleReply);
                if (hasFollowup)
                {
                    string q = !string.IsNullOrWhiteSpace(result.FollowupQuestion)
                        ? result.FollowupQuestion!
                        : visibleReply;
                    // Followup questions are NOT fake-streamed (must show with their option cards).
                    Messages.Add(new ChatMessageViewModel(
                        q,
                        ChatRole.Assistant,
                        followupOptions: result.FollowupOptions,
                        onFollowupSelected: AnswerWithFollowup));
                    PersistMessage(ChatRole.Assistant, q);
                }
                else
                {
                    // Only fake-stream lexicon-mode replies. When the real model is
                    // generating, Phase 5 will deliver true incremental streaming.
                    fakeStream = !_chatbot.IsModelLoaded && !string.IsNullOrEmpty(visibleReply);
                    if (fakeStream)
                    {
                        assistantBubble = new ChatMessageViewModel(string.Empty, ChatRole.Assistant);
                        Messages.Add(assistantBubble);
                        assistantContentForPersist = visibleReply;
                        // Persist the reply BEFORE the summary so reload order matches
                        // the rendered order. (Also acts as crash safety for the
                        // fake-stream window — file has the full reply even if the
                        // animation aborts.)
                        PersistMessage(ChatRole.Assistant, visibleReply);
                    }
                    else
                    {
                        Messages.Add(new ChatMessageViewModel(visibleReply, ChatRole.Assistant));
                        PersistMessage(ChatRole.Assistant, visibleReply);
                    }
                }

                // M3 7e: dispatch parser outputs AFTER the visible reply lands
                // in Messages so the user sees the assistant text first, then
                // the Card surfaces. Inside the dispatcher.Invoke so handlers
                // can touch UI directly without re-marshalling.
                if (parsedOverride.Observations.Count > 0
                    && LongitudinalObservationCallback is { } obsCb)
                {
                    foreach (var obs in parsedOverride.Observations)
                    {
                        // H5: hard-cap OBSERVATION_ADD persistence at 2 PER SESSION.
                        // The parser already caps at 2 per response; this bounds the
                        // TOTAL across the session so a looping / injected model can't
                        // slowly fill the profile's longitudinal memory.
                        if (_observationWritesThisSession >= MaxObservationWritesPerSession) break;
                        try { obsCb(obs); }
                        catch { continue; } // a failed write doesn't consume the budget
                        _observationWritesThisSession++;
                    }
                }

                // Phase 7 (RESUME_ACKNOWLEDGE): the user confirmed IN CHAT that they're ready
                // to continue from a tier>=8 PausedAwaitingUserConfirm check-in. Act ONLY on an
                // explicit user_confirmed:true (the structured contract carries the user's quote
                // for the audit trail). Safe: the engine method is a guarded no-op unless it is
                // actually awaiting confirm, resume continues the habituation PROGRAM (not a
                // protection on/off), and it is reactive to this user turn — so a stray ack is
                // harmless and the user can re-pause at any time.
                if (parsedOverride.ResumeAck is { UserConfirmed: true } resumeAck
                    && AcknowledgeResumeCallback is { } resumeCb)
                {
                    try { resumeCb(resumeAck.UserQuote); }
                    catch { /* best-effort; a failed resume must never break the chat turn */ }
                }

                // M3 reliability: if the LLM didn't emit a parseable
                // PROFILE_OVERRIDE block, fall back to the deterministic
                // lexicon. Catches explicit user requests like "調鬆 sensitivity"
                // / "switch profile to RESILIENT" / "raise strike limit" that
                // the LLM might miss due to small-model structured-output
                // unreliability. Lexicon is intentionally narrow — only
                // explicit-request phrasings, no inference from ambiguous
                // text. The LLM's natural reply still surfaces in the bubble;
                // the lexicon just guarantees the structured proposal lands.
                ProfileOverrideProposal? proposalToSurface = parsedOverride.Proposal;
                if (proposalToSurface is null)
                {
                    var snap = LoadHabituationSnapshot?.Invoke();
                    proposalToSurface = HabituationProposalLexicon.TryDetect(text, snap);
                }
                if (proposalToSurface is not null)
                {
                    ProposalReceived?.Invoke(this, proposalToSurface);
                }

                if (summary.Length > 0)
                {
                    // Persist the summary now so reload order stays user→reply→summary
                    // and a mid-animation crash doesn't lose it. The VISIBLE bubble is
                    // deferred until the fake-stream finishes (see below) so it never
                    // races ahead of the reply that explains it.
                    PersistMessage(ChatRole.Assistant, summary);
                    if (fakeStream)
                        deferredSummary = summary;
                    else
                        Messages.Add(new ChatMessageViewModel(summary, ChatRole.Assistant));
                }
            });

            if (fakeStream && assistantBubble is not null)
            {
                // Run the animation, THEN flip the deferred summary into the visible
                // collection on the dispatcher. Order: reply animation completes →
                // summary appears, matching what the user expects to read top-down.
                string summaryToShow = deferredSummary;
                _ = Task.Run(async () =>
                {
                    await FakeStreamAsync(assistantBubble, assistantContentForPersist, streamToken)
                          .ConfigureAwait(false);
                    if (streamToken.IsCancellationRequested) return;
                    if (summaryToShow.Length > 0)
                        _dispatcher.Invoke(() =>
                            Messages.Add(new ChatMessageViewModel(summaryToShow, ChatRole.Assistant)));
                });
            }
        }
        catch (OperationCanceledException)
        {
            // NE6: the interactive call hit the 60 s ceiling (or a newer turn
            // superseded it). TaskCanceledException from HttpClient.Timeout also
            // lands here (it derives from OperationCanceledException). Surface a
            // calm bilingual retry hint, never a raw "[Error] canceled", and
            // always clear the typing indicator.
            _dispatcher.Invoke(() =>
            {
                if (placeholder is not null) Messages.Remove(placeholder);
                Messages.Add(new ChatMessageViewModel(
                    LocalizationManager.Current.Pick(
                        "回應花的時間有點久，請再試一次。",
                        "That took a while to answer — please try again."),
                    ChatRole.Assistant));
            });
        }
        catch (Exception ex)
        {
            // UX-07: never surface the raw exception text as an assistant bubble. The
            // user-visible bubble is the calm "unavailable" copy; the technical detail
            // (exception type + message) goes to the Event Log only, redacted of any
            // user-profile path segment.
            LogModelDiagnostic($"chat turn failed: {ex.GetType().Name}: {ex.Message}", null);
            _dispatcher.Invoke(() =>
            {
                if (placeholder is not null) Messages.Remove(placeholder);
                Messages.Add(new ChatMessageViewModel(AssistantUnavailableText(), ChatRole.Assistant));
            });
        }
        finally
        {
            _dispatcher.Invoke(() =>
            {
                if (placeholder is not null && Messages.Contains(placeholder))
                    Messages.Remove(placeholder);
                IsGenerating = false;
            });
        }
    }

    /// <summary>NE6: user-triggered cancel of the in-flight interactive turn. Cancels the
    /// shared <see cref="_streamCts"/> — the live model call is linked into it (see the
    /// <c>callCts</c> in <see cref="SendAsync"/>), so the awaited call faults with
    /// <see cref="OperationCanceledException"/> and unwinds through the existing catch:
    /// the typing placeholder is removed and the localized cancelled-state copy renders.
    /// No-op when nothing is generating. Best-effort: a Cancel on an already-disposed CTS
    /// (turn just finished) is swallowed so the click can never throw.</summary>
    private void CancelGeneration()
    {
        if (!IsGenerating) return;
        try { _streamCts.Cancel(); } catch { /* CTS may already be disposed if the turn just ended */ }
    }

    /// <summary>
    /// Animate <paramref name="full"/> into <paramref name="bubble"/>.Content in
    /// chunks so the typing-indicator gets visible airtime in lexicon-only mode.
    /// Total wall-clock duration is capped at <see cref="FakeStreamMaxMs"/>; chunk
    /// size scales with response length to honour that cap for long replies.
    /// Cooperatively cancellable so a follow-up Send aborts the prior animation.
    /// </summary>
    private async Task FakeStreamAsync(ChatMessageViewModel bubble, string full, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(full)) return;
        int maxSteps = Math.Max(1, FakeStreamMaxMs / FakeStreamStepMs);
        int chunkSize = Math.Max(3, (int)Math.Ceiling(full.Length / (double)maxSteps));

        try
        {
            for (int i = 0; i < full.Length; i += chunkSize)
            {
                if (ct.IsCancellationRequested) return;
                int end = Math.Min(full.Length, i + chunkSize);
                string slice = full.Substring(0, end);
                _dispatcher.Invoke(() => { if (!ct.IsCancellationRequested) bubble.Content = slice; });
                if (end >= full.Length) break;
                await Task.Delay(FakeStreamStepMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        finally
        {
            // On cancellation, snap to the full string so the UI never gets stuck
            // mid-word if the user immediately sends a new message.
            if (!ct.IsCancellationRequested)
                _dispatcher.Invoke(() => bubble.Content = full);
        }
    }

    /// <summary>
    /// Invoked when the user taps one of the followup option cards. Treats the
    /// chosen option as the next user message and runs it through SendAsync, so
    /// the model sees the option verbatim (and the picked card persists in the
    /// transcript as a normal "You: <option>" bubble). Public so the spike popup
    /// window can route its option clicks through the same path.
    /// </summary>
    public void SubmitFollowupAnswer(string option)
    {
        if (string.IsNullOrWhiteSpace(option) || IsGenerating) return;
        InputText = option;
        // Fire-and-forget — SendCommand handles its own dispatcher work.
        if (SendCommand.CanExecute(null)) SendCommand.Execute(null);
    }

    // Keep the old private name as a thin shim so call sites in the same VM
    // (the option-card constructor) read naturally.
    private void AnswerWithFollowup(string option) => SubmitFollowupAnswer(option);

    // ── Stage 5a/5b — banner + recalibrate command implementations ──────────


    /// <summary>
    /// Inject a system-initiated assistant message that opens a followup picker.
    /// Used by the VIMS-spike popup path: the spike detector decides the bot
    /// should proactively ask "what are you feeling right now?" rather than
    /// waiting for the user to type. Safe to call from any thread.
    /// </summary>
    public void RaiseFollowup(string question, string[] options)
    {
        if (string.IsNullOrWhiteSpace(question) || options is null || options.Length == 0) return;
        _dispatcher.Invoke(() =>
        {
            Messages.Add(new ChatMessageViewModel(
                question,
                ChatRole.Assistant,
                followupOptions: options,
                onFollowupSelected: AnswerWithFollowup));
            PersistMessage(ChatRole.Assistant, question);
        });
    }

    /// <summary>B5: surface a SUBTLE, system-styled adaptation notice in the chat — the user-facing
    /// transparency for an auto-applied protection tweak (Kirby's "tell the user in the chatbox" policy).
    /// A plain assistant line, no picker. Marshals to the UI thread; best-effort (a notice failure must
    /// never disturb the background tuning loop). Safe to call from any thread.</summary>
    public void AddAdaptationNotice(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            _dispatcher.Invoke(() =>
            {
                Messages.Add(new ChatMessageViewModel(text, ChatRole.Assistant));
                PersistMessage(ChatRole.Assistant, text);
            });
        }
        catch { /* advisory — never disturb the caller */ }
    }

    /// <summary>
    /// Build the state-annotated model input. Prepends terse [RecentAdjustments] and
    /// [RecentEvents] blocks (oldest-first, capped at <see cref="HistoryWindow"/>) so
    /// the chatbot NLU can reason about past sessions for analytical questions like
    /// "what caused the dizziness in the last session?". Empty sources are gracefully
    /// omitted (new install -> no prefix), keeping the previous behaviour as a baseline.
    /// </summary>
    // M2 (Phase 6e): visibility bumped to internal so SenseEase.Tests can
    // call BuildContextualInput directly and assert on the embedded
    // [habituation_state] block content. Production callers all live in
    // the App project so internal-vs-private is invisible at runtime;
    // SenseEase.App.csproj already has InternalsVisibleTo SenseEase.Tests.
    internal string BuildContextualInput(string text)
    {
        var sb = new StringBuilder();
        AppendHistoryPrefix(sb);

        // M2 (Phase 6c): inject [habituation_state] block so the LLM has
        // user-context to ground its replies — current tier, habituation
        // level, profile, calibration history, longitudinal observations,
        // recent trajectory summary, power-save state, medical advisory.
        // Empty string when no profile snapshot exists (pre-calibration);
        // omitted entirely in that case to save tokens.
        var snapshot = LoadHabituationSnapshot?.Invoke();
        string trajectory = HabituationTrajectoryBuilder.BuildSummary(_eventLog);
        bool powerSave = IsPowerSaveActive?.Invoke() ?? false;
        string habBlock = HabituationStateBlockBuilder.BuildBlock(snapshot, trajectory, powerSave);
        if (!string.IsNullOrEmpty(habBlock))
        {
            sb.Append(habBlock).Append('\n');
        }

        // UM5: inject the [behavior_trend] block (longer-term exposure / adaptation
        // arc from behavior_log) AFTER [habituation_state]. Omitted entirely with
        // fewer than 7 days of data, or when no host reader is wired (test contexts).
        var behaviorRows = LoadBehaviorAggregates?.Invoke();
        string trendBlock = LongitudinalSummaryBuilder.BuildBlock(behaviorRows);
        if (!string.IsNullOrEmpty(trendBlock))
        {
            sb.Append(trendBlock).Append('\n');
        }

        var sh = _engine.Current.Shader;
        var bl = _engine.BaselineShader;
        sb.Append("[SenseEaseState mode=").Append(_engine.Current.Mode)
          .Append(" ofi=").Append(_engine.Current.RawOpticalFlowIndex.ToString("F1"))
          .Append(" pressure=").Append(_engine.Current.Pressure.ToString("F1"))
          .Append(" fov=").Append(_engine.Current.FovRadiusPercent)
          .Append(" mask=").Append(_engine.Current.MaskActive ? "on" : "off")
          .Append(" shader(maskDepth=").Append(sh.MaskDepth.ToString("F2"))
          .Append(" gridVis=").Append(sh.GridVisibility.ToString("F2"))
          .Append(" feather=").Append(sh.FeatherWidth.ToString("F2")).Append(')')
          .Append(" defaults(maskDepth=").Append(bl.MaskDepth.ToString("F2"))
          .Append(" gridVis=").Append(bl.GridVisibility.ToString("F2"))
          .Append(" feather=").Append(bl.FeatherWidth.ToString("F2")).Append(")]\n")
          .Append("User: ").Append(text);
        return sb.ToString();
    }

    /// <summary>
    /// Append [RecentAdjustments] and [RecentEvents] sections (oldest-first), if either has data.
    /// Each line is terse and human-readable so it doubles as a debug log in the transcript.
    /// </summary>
    private void AppendHistoryPrefix(StringBuilder sb)
    {
        var adjustments = _adjustmentLog?.All();
        if (adjustments is { Count: > 0 })
        {
            int skip = Math.Max(0, adjustments.Count - HistoryWindow);
            sb.AppendLine("[RecentAdjustments]");
            for (int i = skip; i < adjustments.Count; i++)
            {
                var r = adjustments[i];
                string sign = r.Delta >= 0 ? "+" : string.Empty;
                string utter = string.IsNullOrEmpty(r.Utterance) ? string.Empty : $" \"{Sanitize(r.Utterance)}\"";
                sb.Append(r.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append(' ').Append(r.Parameter)
                  .Append(' ').Append(sign).Append(r.Delta.ToString("0.##"))
                  .Append(' ').Append(r.Source)
                  .AppendLine(utter);
            }
        }

        var events = _eventLog?.Recent(); // most-recent-first
        if (events is { Count: > 0 })
        {
            int take = Math.Min(HistoryWindow, events.Count);
            sb.AppendLine("[RecentEvents]");
            for (int i = take - 1; i >= 0; i--) // render oldest-first
            {
                var e = events[i];
                sb.Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append(' ').Append(e.Category)
                  .Append(" \"").Append(Sanitize(e.Message)).Append('"')
                  .AppendLine();
            }
        }
    }

    private static string Sanitize(string s)
        => string.IsNullOrEmpty(s) ? string.Empty : s.Replace('"', '\'').Replace("\r", " ").Replace("\n", " ");

    // H6: tokens that, echoed verbatim into the prompt, let a user spoof the
    // app's own structured context blocks (fake history / state injection).
    private static readonly string[] PromptInjectionTokens =
    {
        "[RecentAdjustments]", "[RecentEvents]", "[SenseEaseState", "[habituation_state]",
        "[/habituation_state]", "[INST]", "[/INST]", "<|", "|>",
        "User:", "System:", "Assistant:",
    };

    private const int MaxUserTurnLength = 600;

    /// <summary>H6: defang a live user turn before it reaches the LLM prompt /
    /// chat history. Newlines→spaces (so a multi-line payload can't forge block
    /// structure), strip the app's own context-block tokens, collapse whitespace,
    /// cap length. Runs AFTER the safety floor so crisis detection always sees the
    /// user's original words. Internal for unit coverage.</summary>
    internal static string SanitizeUserTurn(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        // Review (H6 hardening): NFKC-fold first so fullwidth / confusable variants
        // of the block tokens (e.g. ［habituation_state］ U+FF3B/U+FF3D) collapse to
        // their ASCII forms and are caught by the strip list below. Mirrors the
        // SafetyFloor's own NFKC normalization.
        string t = s.Normalize(System.Text.NormalizationForm.FormKC)
                     .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        foreach (var tok in PromptInjectionTokens)
            t = t.Replace(tok, " ", StringComparison.OrdinalIgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length > MaxUserTurnLength)
            t = t.Substring(0, MaxUserTurnLength).TrimEnd();
        return t;
    }

    /// <summary>
    /// Apply an NLU result to the engine, persist every adjustment for personalization,
    /// and return a one-line human summary of what was ACTUALLY applied (not merely
    /// requested), for an in-chat feedback bubble.
    /// </summary>
    private string ApplyNluResult(NluResult result, string utterance)
    {
        if (result.Intent == "ResetRequest")
        {
            _engine.ResetPersonalization();
            _adjustmentLog.Append(new AdjustmentRecord
            {
                Parameter = "Reset", Source = "reset", Utterance = utterance,
            });
            AdjustmentApplied?.Invoke(this, EventArgs.Empty);
            string resetMsg = "⚙ " + LocalizationManager.Current.Pick(
                "已重設所有個人化設定", "Overlay settings reset to default");
            if (_isOverlayVisible()) return resetMsg;
            // UX-19: ASCII parens in the EN string (the zh string keeps full-width parens
            // to match its CJK punctuation). A mixed full-width "（…）" in English mode read
            // as a rendering glitch.
            return resetMsg + LocalizationManager.Current.Pick(
                "（開啟 Overlay 以查看）", " (open the overlay to see it)");
        }

        // Each tuple: (label, formatted-change-string). Only populated for non-zero
        // deltas so the resulting pseudo-table never contains "no change" rows.
        var rows = new System.Collections.Generic.List<(string Label, string Change)>();

        // Round-3 follow-up context: the target THIS turn actually moved, decided once
        // at the end so a stale prior-turn target can't bleed through. Grid is preferred
        // (only target with a real OFF), then mask, then fov.
        FollowUpTarget? appliedTarget = null;

        if (result.FovDelta != 0)
        {
            int oldOffset = _engine.UserFovOffset;
            // D5: a tightening request that is NOT a "ReportDiscomfort" complaint is a
            // standing preference — apply the FOV but don't charge the habituation ledger.
            _engine.ApplyUserFeedback(result.FovDelta, isDeliberatePreference: result.Intent != "ReportDiscomfort");
            int newOffset = _engine.UserFovOffset;
            int appliedFov = newOffset - oldOffset;
            if (appliedFov != 0)
            {
                _adjustmentLog.Append(new AdjustmentRecord
                {
                    Parameter = "FovRadius", Delta = appliedFov,
                    OldValue = oldOffset, NewValue = newOffset,
                    Source = "user", Utterance = utterance,
                });
                rows.Add(("FOV", $"{(appliedFov > 0 ? "+" : "")}{appliedFov}%"));
                appliedTarget = FollowUpTarget.Fov;
            }
        }

        // CW-2 CLOSED (D-2, Beta A2): MaskDepth + FeatherWidth are now rendered live by
        // the single-pass field, so no "recorded-but-not-rendered" caveat is appended —
        // every applied shader row is truthfully live.
        bool hasShaderDelta = result.MaskDepthDelta != 0
                           || result.GridVisibilityDelta != 0
                           || result.FeatherWidthDelta != 0;
        if (hasShaderDelta)
        {
            var before = _engine.Current.Shader.Clone();
            _engine.ApplyShaderAdjustment(result.MaskDepthDelta, result.GridVisibilityDelta, result.FeatherWidthDelta);
            var after = _engine.Current.Shader.Clone();
            LogShader("MaskDepth",      result.MaskDepthDelta,      before.MaskDepth,      after.MaskDepth,      utterance);
            LogShader("GridVisibility", result.GridVisibilityDelta, before.GridVisibility, after.GridVisibility, utterance);
            LogShader("FeatherWidth",   result.FeatherWidthDelta,   before.FeatherWidth,   after.FeatherWidth,   utterance);

            double appliedMaskDepth      = after.MaskDepth      - before.MaskDepth;
            double appliedGridVisibility = after.GridVisibility - before.GridVisibility;
            double appliedFeatherWidth   = after.FeatherWidth   - before.FeatherWidth;

            if (appliedMaskDepth != 0)
                rows.Add(("Mask depth", $"{(appliedMaskDepth > 0 ? "+" : "")}{appliedMaskDepth:0.00}"));
            if (appliedGridVisibility != 0)
                rows.Add(("Grid",       $"{(appliedGridVisibility > 0 ? "+" : "")}{appliedGridVisibility:0.00}"));
            if (appliedFeatherWidth != 0)
                rows.Add(("Feather",    $"{(appliedFeatherWidth > 0 ? "+" : "")}{appliedFeatherWidth:0.00}"));

            // CW-2 CLOSED (D-2, Beta A2): MaskDepth + FeatherWidth now render live via the
            // single-pass field — every row above is a truthful, applied change. No interim
            // "recorded only" caveat.

            // Round-3 follow-up context: grid wins (only target with a real OFF), then
            // mask. (Feather is no longer in the chatbot's vocabulary, so it never sets
            // a follow-up target.)
            if (appliedGridVisibility != 0)      appliedTarget = FollowUpTarget.Grid;
            else if (appliedMaskDepth != 0)      appliedTarget = FollowUpTarget.Mask;
        }

        // Round-3: commit the target THIS turn moved so a bare 「直接關閉」/「off」 next
        // turn resolves against it deterministically instead of asking the model again.
        if (appliedTarget is FollowUpTarget t)
        {
            _lastAdjustTarget = t;
            _turnsSinceLastAdjust = 0;
        }

        AdjustmentApplied?.Invoke(this, EventArgs.Empty);

        if (rows.Count == 0) return string.Empty;

        // MarkdownInlines (Views/MarkdownInlines.cs) only supports bold / italic /
        // inline-code / bullets / linebreaks — it does NOT render markdown tables.
        // So we emit a backtick-wrapped monospace pseudo-table: the label column is
        // padded to a fixed width so values align vertically in the chat bubble.
        int labelWidth = 0;
        foreach (var r in rows) if (r.Label.Length > labelWidth) labelWidth = r.Label.Length;
        labelWidth += 2; // gutter between columns

        var sb = new StringBuilder();
        sb.Append("**").Append(LocalizationManager.Current.Pick("系統調整", "System adjustment")).Append("**\n");
        foreach (var (label, change) in rows)
            sb.Append('`').Append(label.PadRight(labelWidth)).Append(change).Append("`\n");
        // CW-2 CLOSED (D-2, Beta A2): mask-depth / feather render live via the single-pass
        // field now — no "recorded only" caveat; the rows above are all truthfully applied.
        if (!_isOverlayVisible())
            sb.Append("\n_").Append(LocalizationManager.Current.Pick(
                "開啟 Overlay 以查看", "Open the overlay to see it")).Append('_');
        return sb.ToString();
    }

    private void LogShader(string parameter, double delta, double oldValue, double newValue, string utterance)
    {
        if (delta == 0) return;
        _adjustmentLog.Append(new AdjustmentRecord
        {
            Parameter = parameter, Delta = delta,
            OldValue = oldValue, NewValue = newValue,
            Source = "user", Utterance = utterance,
        });
    }

    private void Clear()
    {
        _chatbot.ClearHistory();
        _chatbot.ClearOnDiskTrace(); // H24 (PII): wipe the on-disk model trace too
        Messages.Clear();
        try { _streamCts.Cancel(); } catch { /* ignore */ }
        _historyStore.DeleteFile();
        // NE18: the file is gone, so reset the in-session line counter — the welcome
        // bubble re-added below is never persisted, so disk is now empty.
        _persistedLineCount = 0;
        // H5: Clear() starts a fresh session → restore the observation-write budget.
        _observationWritesThisSession = 0;
        Messages.Add(new ChatMessageViewModel(WelcomeMessage(), ChatRole.Assistant));
    }

    /// <summary>Raised after an NLU result mutates the shared engine, so the host can refresh rendering.</summary>
    public event EventHandler? AdjustmentApplied;

    /// <summary>M3 7e: raised when the chatbot's reply contains a parseable
    /// <c>[PROFILE_OVERRIDE]</c> block. The host (MainViewModel) validates
    /// via <see cref="HabituationOverrideProcessor"/>, surfaces the Card if
    /// the proposal is accepted, and routes the user's Accept/Decline/Later
    /// click into <see cref="HabituationOverrideApplicator"/>. Raised on
    /// the dispatcher thread so the handler can touch UI directly.</summary>
    public event EventHandler<ProfileOverrideProposal>? ProposalReceived;

    /// <summary>M3 7e/7g: invoked once per <c>[OBSERVATION_ADD]</c> block the
    /// parser extracts. Host wires this to <c>LongitudinalObservationWriter.Append</c>
    /// so chatbot accumulations land in <c>habituation_profile.json</c>.
    /// Null = no host wiring (test contexts).</summary>
    public Action<string>? LongitudinalObservationCallback { get; set; }

    /// <summary>Phase 7 (RESUME_ACKNOWLEDGE): invoked when the LLM relays an explicit
    /// user_confirmed:true that the user is ready to continue from a tier>=8
    /// PausedAwaitingUserConfirm check-in. Carries the user's confirming quote for the audit
    /// trail. Host wires this to <c>MockInferenceEngine.AcknowledgeAndResumeHabituation</c>
    /// (a guarded no-op unless actually awaiting confirm). Null = no host wiring (test contexts).</summary>
    public Action<string>? AcknowledgeResumeCallback { get; set; }

    /// <summary>M3 7e+: host-side helper for the profile picker reply path.
    /// Pushes an assistant message into <see cref="Messages"/> on the
    /// dispatcher thread (so MainViewModel can confirm or report a safety
    /// block without re-implementing the message-add boilerplate).</summary>
    public void PushAssistantBubble(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        _dispatcher.Invoke(() =>
        {
            Messages.Add(new ChatMessageViewModel(content, ChatRole.Assistant));
            PersistMessage(ChatRole.Assistant, content);
        });
    }

    /// <summary>Round-3 follow-up context: record the shader target an absolute-set
    /// command just acted on, and zero the staleness counter, so a bare object-less
    /// off/on follow-up next turn resolves against it. FeatherWidth is intentionally
    /// dropped — it is no longer part of the chatbot's adjustment vocabulary (it can
    /// still be set via the /feather power-user slash command, but that is not a
    /// follow-up target a bare 「關掉」 should ever land on).</summary>
    private void RememberAdjustTarget(ShaderParam param)
    {
        FollowUpTarget? t = param switch
        {
            ShaderParam.GridVisibility => FollowUpTarget.Grid,
            ShaderParam.MaskDepth      => FollowUpTarget.Mask,
            ShaderParam.FovRadius      => FollowUpTarget.Fov,
            _                          => null,   // FeatherWidth → no follow-up target
        };
        if (t is FollowUpTarget target)
        {
            _lastAdjustTarget = target;
            _turnsSinceLastAdjust = 0;
        }
    }

    /// <summary>Round-3: true when the utterance is a BARE OFF follow-up with NO
    /// target noun — 「直接關閉」/「關掉」/「off」/「turn it off」 and close variants.
    /// Object-bearing utterances (those naming grid / mask / fov / a noun) are NOT bare
    /// and are handled by <see cref="ShaderCommandLexicon"/> upstream; magnitude
    /// follow-ups (「更低」/「lower」) are NOT off requests and keep going to the LLM.
    /// Whole-utterance (trim + lowercase + strip trailing punctuation), so a longer
    /// sentence that merely contains "off" does not match. Internal for unit coverage.</summary>
    internal static bool IsBareOffFollowup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string norm = text.Trim().ToLowerInvariant()
            .TrimEnd('.', '。', '!', '！', '?', '？', ' ', '\t');
        return norm switch
        {
            // zh bare-off
            "關閉" or "關掉" or "直接關閉" or "直接關掉" or "把它關掉" or "幫我關掉" or
            "關閉它" or "關掉它" or
            // en bare-off
            "off" or "turn off" or "turn it off" or "turn that off" or "disable it" or
            "hide it" or "remove it"
                => true,
            _ => false,
        };
    }

    /// <summary>Round-3: apply a bare off/on follow-up to the REMEMBERED target. Grid
    /// has a real OFF (gv=0, the same absolute-set path /grid off uses). Mask and FOV
    /// have NO off — protection cannot be turned off from chat, so reply honestly and
    /// point at Settings (mirrors the existing "mask off" rejection voice). No LLM.</summary>
    private void HandleBareOffFollowup(FollowUpTarget target, string userText)
    {
        bool en = LocalizationManager.Current.IsEnglish;

        if (target == FollowUpTarget.Grid)
        {
            // Reuse the exact /grid off path: absolute gv=0 + the honest confirmation
            // the slash path already ships. Build the command via the lexicon so the
            // reply copy and the audit pattern id stay in one place.
            var cmd = ShaderCommandLexicon.Parse("/grid off", en);
            HandleShaderCommand(cmd, userText);
            return;
        }

        // Mask / FOV: no off from chat. Honest decline, pointed at Settings.
        string reply = target == FollowUpTarget.Mask
            ? (en
                ? "The peripheral mask is core protection and can't be turned off from chat. You can adjust sensitivity protection in Settings."
                : "周邊遮罩是核心保護，無法從對話關閉。您可以在 設定 裡調整敏感度保護。")
            : (en
                ? "The field-of-view protection can't be turned off from chat. You can adjust it in Settings."
                : "視野保護無法從對話關閉。您可以在 設定 裡調整。");

        _dispatcher.Invoke(() =>
        {
            Messages.Add(new ChatMessageViewModel(userText, ChatRole.User));
            PersistMessage(ChatRole.User, userText);
            Messages.Add(new ChatMessageViewModel(reply, ChatRole.Assistant));
            PersistMessage(ChatRole.Assistant, reply);
        });
    }

    /// <summary>G1: execute a parsed shader slash-command / strict-language command
    /// LOCALLY (no LLM). Echoes the user turn, applies the engine action, pushes the
    /// command's local reply, and logs an audit entry with the pattern id + resolved
    /// param + old/new value. No PII — the raw utterance is NEVER logged (pattern id
    /// only), preserving the project-wide no-PII-in-logs invariant.</summary>
    private void HandleShaderCommand(ShaderCommand cmd, string userText)
    {
        // Capture the pre-change value for the audit log (before the engine mutates).
        var beforeShader = _engine.Current.Shader;
        int beforeFov = _engine.Current.FovRadiusPercent;

        string reply = cmd.Reply;
        double oldValue = 0, newValue = 0;

        switch (cmd.Kind)
        {
            case ShaderCommandKind.SetParam when cmd.Param is ShaderParam p:
                oldValue = p switch
                {
                    ShaderParam.MaskDepth      => beforeShader.MaskDepth,
                    ShaderParam.GridVisibility => beforeShader.GridVisibility,
                    ShaderParam.FeatherWidth   => beforeShader.FeatherWidth,
                    ShaderParam.FovRadius      => beforeFov,
                    _                          => 0,
                };
                newValue = cmd.Value;
                _engine.SetShaderParam(p, cmd.Value);
                // Round-3 follow-up context: remember the target we just acted on so a
                // bare 「直接關閉」/「off」 next turn resolves against it (no LLM guess).
                RememberAdjustTarget(p);
                break;

            case ShaderCommandKind.ResetShader:
                _engine.ResetShaderToProfileDefault();
                // A reset clears the transient shader state — there is no single
                // "last target" to turn off afterward, so drop any remembered context.
                _lastAdjustTarget = null;
                _turnsSinceLastAdjust = int.MaxValue;
                break;

            case ShaderCommandKind.Show:
            {
                var s = _engine.Current.Shader;
                reply = ShaderCommandLexicon.ComposeShowReply(
                    _engine.Current.FovRadiusPercent, s.MaskDepth, s.GridVisibility, s.FeatherWidth);
                break;
            }

            case ShaderCommandKind.Help:
            case ShaderCommandKind.Reject:
                // reply already holds the help text / rejection reason.
                break;
        }

        // Echo the user's turn (mirrors the other local-reply short-circuits), then
        // push the local reply — both persisted, no LLM round-trip.
        _dispatcher.Invoke(() =>
        {
            Messages.Add(new ChatMessageViewModel(userText, ChatRole.User));
            PersistMessage(ChatRole.User, userText);
        });
        PushAssistantBubble(reply);

        LogShaderCommand(cmd, oldValue, newValue);
    }

    /// <summary>G1 audit: one IEventLogStore entry per accepted/rejected shader
    /// command. Category distinguishes the slash path from the strict-language path
    /// (pattern-id prefix "slash." vs "nl."). Payload carries pattern id + resolved
    /// param + old/new value ONLY — never the raw utterance (no-PII rule).</summary>
    private void LogShaderCommand(ShaderCommand cmd, double oldValue, double newValue)
    {
        if (_eventLog is null) return;
        string category = cmd.PatternId.StartsWith("slash.", StringComparison.Ordinal)
            ? "Chatbot.SlashCommand"
            : "Chatbot.StrictLanguage";
        string paramName = cmd.Param?.ToString() ?? "—";
        string msg = cmd.Kind switch
        {
            ShaderCommandKind.SetParam =>
                $"pattern={cmd.PatternId} param={paramName} {oldValue:0.###}→{newValue:0.###}",
            ShaderCommandKind.ResetShader => $"pattern={cmd.PatternId} reset shader→profile default",
            ShaderCommandKind.Show        => $"pattern={cmd.PatternId} show",
            ShaderCommandKind.Help        => $"pattern={cmd.PatternId} help",
            ShaderCommandKind.Reject      => $"pattern={cmd.PatternId} REJECTED",
            _                             => $"pattern={cmd.PatternId}",
        };
        try
        {
            _eventLog.Append(new EventLogEntry
            {
                Timestamp    = DateTime.UtcNow,
                Category     = category,
                Message      = msg,
                Ofi          = _engine.Current.RawOpticalFlowIndex,
                Pressure     = _engine.Current.Pressure,
                FovRadius    = _engine.Current.FovRadiusPercent,
                MaskSnapshot = _engine.Current.MaskActive ? "ON" : "off",
            });
        }
        catch { /* best-effort; an audit-write failure must never break the chat turn */ }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        return true;
    }
}

public sealed class ChatMessageViewModel : INotifyPropertyChanged
{
    private string _content;
    private bool _isTyping;

    public ChatRole Role { get; }
    public string Author => Role == ChatRole.User ? "You" : "SenseEase AI";
    public bool IsUser => Role == ChatRole.User;
    public string Timestamp { get; }
    public Visibility UserVisibility => IsUser ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AssistantVisibility => IsUser ? Visibility.Collapsed : Visibility.Visible;

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
        }
    }

    /// <summary>
    /// When true, the bubble shows an animated "thinking" indicator instead of
    /// the Content text. Used by the placeholder bubble during model/lexicon work
    /// so the user has visible feedback that something is happening.
    /// </summary>
    public bool IsTyping
    {
        get => _isTyping;
        set
        {
            if (_isTyping == value) return;
            _isTyping = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTyping)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypingVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentVisibility)));
        }
    }

    /// <summary>2–4 quick-reply option strings shown as clickable cards beneath the
    /// assistant message. Null/empty when no followup is offered.</summary>
    public string[]? FollowupOptions { get; }

    public Visibility FollowupVisibility =>
        FollowupOptions is { Length: > 0 } && !IsUser ? Visibility.Visible : Visibility.Collapsed;

    // CW-3: the in-chat profile picker (ProfileOptions / SelectProfileCommand /
    // OnProfileSelected) and the ProfileOption record were removed — profiles are
    // auto-managed, no user-facing switcher returns, and nothing ever constructed
    // these. The dashboard's working profile pills live on MainViewModel.

    public Visibility TypingVisibility => IsTyping ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => IsTyping ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Invoked when the user taps one of the followup option cards.
    /// Receives the chosen option's display string. Null on messages with no
    /// followup options (the visibility binding prevents that path).</summary>
    public Action<string>? OnFollowupSelected { get; }

    public ICommand SelectFollowupCommand { get; }

    public ChatMessageViewModel(
        string content,
        ChatRole role,
        string[]? followupOptions = null,
        Action<string>? onFollowupSelected = null,
        bool isTyping = false)
    {
        _content = content ?? string.Empty;
        Role = role;
        Timestamp = DateTime.Now.ToString("HH:mm");
        FollowupOptions = followupOptions;
        OnFollowupSelected = onFollowupSelected;
        _isTyping = isTyping;
        SelectFollowupCommand = new RelayCommand(o =>
        {
            if (o is string s && OnFollowupSelected is not null) OnFollowupSelected(s);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
