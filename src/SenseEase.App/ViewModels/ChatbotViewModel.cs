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

    /// <summary>Total cap on fake-stream animation duration, regardless of reply length.</summary>
    private const int FakeStreamMaxMs = 1500;
    private const int FakeStreamStepMs = 30;

    /// <summary>Bilingual welcome shown when the transcript is empty — on first
    /// launch (constructor, when prior.Count == 0) and after Clear() so the
    /// chat surface doesn't go blank mid-session. Not persisted; if Clear()
    /// deleted the file, the constructor's empty-prior branch re-shows it on
    /// the next launch.</summary>
    private const string WelcomeMessage =
        "您好，我是 SenseEase AI 助理。請描述您的視覺不適（暈眩、噁心、頭痛、眼睛疲勞），我會為您調整防護設定。\n" +
        "Hi — I'm the SenseEase AI assistant. Describe any visual discomfort and I'll adjust your overlay protection.";

    private bool _isGenerating;
    private string _inputText = string.Empty;
    private string _statusText = string.Empty;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    // ── Calibration card (Stage 3d) ─────────────────────────────────────────
    // The 5-card calibration UI is embedded above the input region in the chat
    // tab. It is NOT a ChatMessage — Messages stays a pure transcript. The host
    // (MainViewModel) constructs and assigns ActiveCalibrationCard then flips
    // IsCalibrationCardActive on; the completion handler clears both and posts
    // a single transcript-summary ChatMessage with the outcome.

    private bool _isCalibrationCardActive;
    public bool IsCalibrationCardActive
    {
        get => _isCalibrationCardActive;
        set => Set(ref _isCalibrationCardActive, value);
    }

    private CalibrationCardViewModel? _activeCalibrationCard;
    public CalibrationCardViewModel? ActiveCalibrationCard
    {
        get => _activeCalibrationCard;
        set => Set(ref _activeCalibrationCard, value);
    }

    // ── Calibration invitation banner (Stage 5a) ────────────────────────────
    // On subsequent demo runs (calibration_count >= 1) the host sets
    // ShowCalibrationInvitationBanner = true at OnDemoVideoFinished instead of
    // auto-opening the card. The banner offers two actions: Start (opens an
    // LLM calibration session via _llmCalibrationFactory) or Skip (just
    // dismisses). Auto-dismisses after 30 s if the user doesn't act.

    private bool _showCalibrationInvitationBanner;
    public bool ShowCalibrationInvitationBanner
    {
        get => _showCalibrationInvitationBanner;
        set
        {
            if (!Set(ref _showCalibrationInvitationBanner, value)) return;
            if (value) StartAutoDismissTimer();
            else       StopAutoDismissTimer();
        }
    }

    // Active LLM calibration session. Bound by the chat tab so the host can
    // surface dialog state (transcript, completion handlers wired up by
    // MainViewModel). Null when no session is active.
    private CalibrationChatbotService? _activeLlmCalibration;
    public CalibrationChatbotService? ActiveLlmCalibration
    {
        get => _activeLlmCalibration;
        set => Set(ref _activeLlmCalibration, value);
    }

    /// <summary>Set by MainViewModel at wire-up time. Constructs and starts an
    /// LLM calibration session given the sensor snapshot the command should
    /// pass to <see cref="CalibrationChatbotService.StartAsync"/>. The factory
    /// is the only thing in this VM that knows about the production
    /// infrastructure types (LlamaServerCalibrationClient, transcript path,
    /// previous-snapshot lookup); separating it lets headless tests inject a
    /// stub. Returning null = "LLM unavailable, do not show a session"; the
    /// commands then no-op without surfacing an error.</summary>
    public Func<CalibrationSensorData, CalibrationChatbotService?>? LlmCalibrationFactory { get; set; }

    /// <summary>Sensor snapshot captured at the most recent demo's end.
    /// MainViewModel sets this in OnDemoVideoFinished before flipping the
    /// banner on. Recalibrate (from the chatbot tab, no recent demo) uses a
    /// zero-valued record instead.</summary>
    public CalibrationSensorData LastDemoSensorData { get; set; } = new(0.0, 0, 0.0);

    private DispatcherTimer? _bannerDismissTimer;
    /// <summary>How long the banner stays visible before auto-dismiss.
    /// Spec §5a: 30 s. Exposed as a public field so headless tests can shrink
    /// the window for the auto-dismiss test (or skip it).</summary>
    public TimeSpan BannerAutoDismissAfter { get; set; } = TimeSpan.FromSeconds(30);

    private void StartAutoDismissTimer()
    {
        StopAutoDismissTimer();
        // Best-effort dispatcher timer. In headless tests (no Application.Current)
        // the timer construction itself works but Tick never fires without a
        // running dispatcher loop — that's acceptable, the auto-dismiss test is
        // marked as a manual-acceptance step in that environment.
        try
        {
            _bannerDismissTimer = new DispatcherTimer { Interval = BannerAutoDismissAfter };
            _bannerDismissTimer.Tick += (_, _) =>
            {
                StopAutoDismissTimer();
                // Only auto-dismiss if still visible — guards against a race
                // where the user clicked Start/Skip just as the timer fired.
                if (_showCalibrationInvitationBanner) ShowCalibrationInvitationBanner = false;
            };
            _bannerDismissTimer.Start();
        }
        catch { /* dispatcher unavailable in this thread — skip */ }
    }

    private void StopAutoDismissTimer()
    {
        try { _bannerDismissTimer?.Stop(); } catch { /* ignore */ }
        _bannerDismissTimer = null;
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (Set(ref _isGenerating, value))
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

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public ICommand SendCommand { get; }
    public ICommand ClearCommand { get; }

    // Stage 5a/5b commands. Constructed in the ctor below.
    public ICommand StartInvitedCalibrationCommand { get; private set; } = null!;
    public ICommand SkipCalibrationInvitationCommand { get; private set; } = null!;
    public ICommand RecalibrateCommand { get; private set; } = null!;

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
        // either don't construct ChatbotViewModel at all (CalibrationApplicator
        // covers them) or pump the dispatcher on an STA thread.
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
            _ => !IsGenerating && !string.IsNullOrWhiteSpace(InputText));
        ClearCommand = new RelayCommand(_ => Clear());

        // Stage 5a/5b — banner + recalibrate commands.
        StartInvitedCalibrationCommand   = new RelayCommand(_ => StartInvitedCalibration());
        SkipCalibrationInvitationCommand = new RelayCommand(_ => SkipCalibrationInvitation());
        RecalibrateCommand               = new RelayCommand(_ => Recalibrate());

        StatusText = chatbot.IsModelAvailable
            ? "Loading Ministral 3B… (first load ~30s on CPU; chat is disabled meanwhile)"
            : $"Model not found at: {AppConfig.ModelPath}";

        _ = InitModelAsync();

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
            Messages.Add(new ChatMessageViewModel(WelcomeMessage, ChatRole.Assistant));
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
            Timestamp = DateTime.Now,
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

    private async Task InitModelAsync()
    {
        try
        {
            await _chatbot.LoadAsync().ConfigureAwait(false);
            _dispatcher.Invoke(() =>
            {
                StatusText = _chatbot.IsModelLoaded
                    ? "Ministral 3B ready"
                    : $"Lexicon fallback (no GGUF found at: {AppConfig.ModelPath})";
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                StatusText = $"Load failed: {ex.GetType().Name}: {ex.Message}";
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
            });
        }
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
                "我無法處理這則訊息，請重新輸入。/ I couldn't process that message — please try rephrasing.",
                ChatRole.Assistant)));
            return;
        }

        // M3 7e+: short-circuit on profile-picker request. User typed
        // something like "我想換 profile" / "show me profiles" without
        // specifying which → push a chat bubble with 3 option cards and
        // skip the LLM round-trip entirely. The bubble's option-click
        // callback fires ProfilePickerSelected, which the host wires to
        // HabituationOverrideApplicator (safety intercept + audit + apply).
        if (HabituationProposalLexicon.TryDetectProfilePickerRequest(text))
        {
            InputText = string.Empty;
            string? currentProfile = LoadHabituationSnapshot?.Invoke()?.Profile;
            _dispatcher.Invoke(() =>
            {
                Messages.Add(new ChatMessageViewModel(text, ChatRole.User));
                PersistMessage(ChatRole.User, text);

                var options = new List<ProfileOption>();
                foreach (var (profile, desc) in HabituationProposalLexicon.ProfileDescriptions)
                {
                    string canonical = profile.ToString().ToUpperInvariant();
                    string label = string.Equals(canonical, currentProfile, StringComparison.OrdinalIgnoreCase)
                        ? $"{canonical}（目前）"
                        : canonical;
                    options.Add(new ProfileOption(label, desc));
                }
                string prompt = currentProfile is null
                    ? "請選擇您想切換的 profile："
                    : $"目前是 {currentProfile}。請選擇您想切換的 profile：";
                Messages.Add(new ChatMessageViewModel(
                    content: prompt,
                    role: ChatRole.Assistant,
                    profileOptions: options,
                    onProfileSelected: picked =>
                    {
                        // The label may include "（目前）" — strip suffix to
                        // get the canonical profile name before forwarding.
                        string canon = picked;
                        int paren = canon.IndexOf('（');
                        if (paren > 0) canon = canon.Substring(0, paren).Trim();
                        ProfilePickerSelected?.Invoke(this, canon);
                    }));
                PersistMessage(ChatRole.Assistant, prompt);
            });
            return; // skip LLM call entirely
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
            string contextualInput = BuildContextualInput(text);

            var result = await _chatbot.SendAsync(contextualInput, CancellationToken.None).ConfigureAwait(false);

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
                        try { obsCb(obs); } catch { /* per-observation failure swallowed */ }
                    }
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
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                if (placeholder is not null) Messages.Remove(placeholder);
                Messages.Add(new ChatMessageViewModel($"[Error] {ex.Message}", ChatRole.Assistant));
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

    /// <summary>v0.9.1 hotfix: host-supplied callback that opens the button-
    /// based calibration card. Set by MainViewModel at wire-up. Null in test
    /// contexts. When null, falls back to the LLM-session factory path (legacy
    /// behaviour). When non-null, Start button on the invitation banner opens
    /// the card UI directly — the LLM-session UI surface was never built, so
    /// the LLM path silently fails for end users. Stage 5a banner + Stage 3d
    /// card share the same completion handlers in MainViewModel, so routing
    /// here gives both first-run and subsequent-run users the same working
    /// calibration UX.</summary>
    public Action? OpenCalibrationCardCallback { get; set; }

    private void StartInvitedCalibration()
    {
        ShowCalibrationInvitationBanner = false;
        // v0.9.1 hotfix: prefer the button-card path. The LLM session has no
        // UI binding — clicking Start used to silently launch an invisible
        // session.
        if (OpenCalibrationCardCallback is { } openCard)
        {
            openCard();
            return;
        }
        StartLlmCalibrationSession(LastDemoSensorData);
    }

    private void SkipCalibrationInvitation()
    {
        ShowCalibrationInvitationBanner = false;
        // Skip is a pure dismiss — no LLM session, no engine mutation, no log
        // bubble. The user can re-enter via the chatbot tab's Recalibrate
        // button if they change their mind.
    }

    private void Recalibrate()
    {
        // Recalibrate entry from the chatbot tab. v0.9.1: also prefer the
        // button-card path when host wired the callback — same UX argument
        // as StartInvitedCalibration. Falls back to LLM session for tests
        // and for the future LLM-UI rebuild.
        ShowCalibrationInvitationBanner = false;
        if (OpenCalibrationCardCallback is { } openCard)
        {
            openCard();
            return;
        }
        StartLlmCalibrationSession(new CalibrationSensorData(0.0, 0, 0.0));
    }

    /// <summary>Construct (via the host-supplied factory) a new LLM
    /// calibration session and surface it as <see cref="ActiveLlmCalibration"/>.
    /// The factory is expected to invoke <see cref="CalibrationChatbotService.StartAsync"/>
    /// itself — the host owns the previousSnapshot lookup and any event
    /// wiring. No-op when the factory is null (test environments without
    /// LLM infrastructure).</summary>
    private void StartLlmCalibrationSession(CalibrationSensorData sensor)
    {
        if (LlmCalibrationFactory is null) return;
        try
        {
            var session = LlmCalibrationFactory(sensor);
            if (session is null) return;
            ActiveLlmCalibration = session;
        }
        catch { /* factory threw — session not created, nothing to clean up */ }
    }

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
            string resetMsg = "⚙ 已重設所有個人化設定 / Overlay settings reset to default";
            return _isOverlayVisible() ? resetMsg : resetMsg + "（開啟 Overlay 以查看 / open the overlay to see it）";
        }

        // Each tuple: (label, formatted-change-string). Only populated for non-zero
        // deltas so the resulting pseudo-table never contains "no change" rows.
        var rows = new System.Collections.Generic.List<(string Label, string Change)>();

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
            }
        }

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
        sb.Append("**系統調整 / System adjustment**\n");
        foreach (var (label, change) in rows)
            sb.Append('`').Append(label.PadRight(labelWidth)).Append(change).Append("`\n");
        if (!_isOverlayVisible())
            sb.Append("\n_開啟 Overlay 以查看 / Open the overlay to see it_");
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
        Messages.Clear();
        try { _streamCts.Cancel(); } catch { /* ignore */ }
        _historyStore.DeleteFile();
        // NE18: the file is gone, so reset the in-session line counter — the welcome
        // bubble re-added below is never persisted, so disk is now empty.
        _persistedLineCount = 0;
        Messages.Add(new ChatMessageViewModel(WelcomeMessage, ChatRole.Assistant));
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

    /// <summary>M3 7e+: raised when the user clicks a profile option in the
    /// chat-bubble picker. Carries the chosen profile name (RESILIENT /
    /// TYPICAL / SENSITIVE). Host validates + applies via
    /// <see cref="HabituationOverrideApplicator"/> and pushes a confirmation
    /// reply back through <see cref="PushAssistantBubble"/>.</summary>
    public event EventHandler<string>? ProfilePickerSelected;

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

    /// <summary>M3 7e+: rich-option cards for the profile picker. Each option
    /// renders as a button with a bold name + small description below
    /// (vs the plain-string FollowupOptions which is name-only). Null when
    /// the message doesn't offer profile picking.</summary>
    public IReadOnlyList<ProfileOption>? ProfileOptions { get; }

    public Visibility ProfileOptionsVisibility =>
        ProfileOptions is { Count: > 0 } && !IsUser ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TypingVisibility => IsTyping ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => IsTyping ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Invoked when the user taps one of the followup option cards.
    /// Receives the chosen option's display string. Null on messages with no
    /// followup options (the visibility binding prevents that path).</summary>
    public Action<string>? OnFollowupSelected { get; }

    public ICommand SelectFollowupCommand { get; }

    /// <summary>M3 7e+: invoked when user taps one of the profile picker option
    /// cards. Receives the picked profile name (RESILIENT / TYPICAL / SENSITIVE).</summary>
    public Action<string>? OnProfileSelected { get; }

    public ICommand SelectProfileCommand { get; }

    public ChatMessageViewModel(
        string content,
        ChatRole role,
        string[]? followupOptions = null,
        Action<string>? onFollowupSelected = null,
        bool isTyping = false,
        IReadOnlyList<ProfileOption>? profileOptions = null,
        Action<string>? onProfileSelected = null)
    {
        _content = content ?? string.Empty;
        Role = role;
        Timestamp = DateTime.Now.ToString("HH:mm");
        FollowupOptions = followupOptions;
        OnFollowupSelected = onFollowupSelected;
        _isTyping = isTyping;
        ProfileOptions = profileOptions;
        OnProfileSelected = onProfileSelected;
        SelectFollowupCommand = new RelayCommand(o =>
        {
            if (o is string s && OnFollowupSelected is not null) OnFollowupSelected(s);
        });
        SelectProfileCommand = new RelayCommand(o =>
        {
            if (o is ProfileOption p && OnProfileSelected is not null) OnProfileSelected(p.Name);
            else if (o is string s && OnProfileSelected is not null) OnProfileSelected(s);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>M3 7e+: a profile picker option rendered inside an assistant
/// chat bubble. Name = the canonical profile enum value (RESILIENT /
/// TYPICAL / SENSITIVE) sent back when the user clicks; Description = the
/// short zh blurb shown beneath the name on the button card so the user
/// knows what each profile means.</summary>
public sealed record ProfileOption(string Name, string Description);
