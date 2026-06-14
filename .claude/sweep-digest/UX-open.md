# UX — confirmed-open (21)

Coverage: Read fully: all 14 XAML files in src/SenseEase.App (MainWindow.xaml end-to-end, NotificationCardWindow, SettingsContent, SettingsWindow, EventLogWindow, ThemedDialog, SpikePopup, Overlay, theme + dark palette, App.xaml); MainViewModel.cs, ChatbotViewModel.cs, MainWindow.xaml.cs, NotificationCardWindow.xaml.cs, SettingsContent.xaml.cs, TrayIconService.cs, TierColorPalette labels, converters, LocalizationManager/LocExtension, AccessibilityHelper, AppConfig, UserPreferences, ClickThrough interop, SafetyFloor crisis strings. Verified prior-audit items now fixed (banned hexes, ComfortMeter source, NE24/NE27/NE28/NE30, EM5, MessageBox→ThemedDialog). Not examined in depth: OverlayWindow.xaml.cs rendering internals, dormant PersistentBarWindow, BarStub/ValidationModeBanner, MarkdownInlines, ThemeManager, EventLogWindow.xaml.cs, App.xaml.cs startup.

#### UX-01 [P1] Chatbot directs users to a profile switcher that no longer exists anywhere in the UI
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/ViewModels/ChatbotViewModel.cs:369-384 | effort: S | carriedOver: False
- evidence: Canned reply: "You can switch your comfort profile (Resilient/Typical/Sensitive) in the main window's Settings". But the dashboard profile-pills card was removed 2026-06-09 (MainWindow.xaml:365-367 comment), SettingsContent.xaml has no profile control, and the in-chat ProfileOptions picker (MainWindow.xaml:936-970) is dead — no caller ever passes profileOptions.
- current: Any user asking about profiles is told to use a Settings control that does not exist; the only profile entry point (SelectProfileCommand) is unreachable.
- intended: Honesty-UX: every instruction points at a real affordance; profile changes were supposed to be 'a deliberate dashboard action' (Reading A).
- recommendation: Either (a) rewrite the canned reply to honestly describe auto-profiling ("SenseEase adjusts this automatically; sensitivity protection is in Settings → Data & privacy"), or (b) re-add a profile selector to Settings and keep the reply. Also delete the dead ProfileOptions ItemsControl template and orphaned ProfilePill style.
- verdict: isReal=True fixed=False conf=high | Verified: canned reply (ChatbotViewModel.cs:374-375) points to Settings; SettingsContent.xaml has no profile control; pills removed per MainWindow.xaml:365-367; no caller passes profileOptions, so the 936-970 picker and SelectProfileCommand are unreachable. Reply became stale when pills were removed 2026-06-09.

#### UX-02 [P1] Pop-up card chat replies — including the crisis safety-floor response — are clipped to ~2 lines of 10px text
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Views/NotificationCardWindow.xaml:685-692 | effort: M | carriedOver: False
- evidence: ChatStatusText: FontSize=10, MaxHeight=32, TextWrapping=Wrap, no scroll. MainWindow.xaml.cs:1188-1228 returns the FULL assistant reply (first bubble) into this block. SafetyFloor.CrisisResponseEn (~230 chars; hotlines at the tail) cannot fit in 32px at 10px — '988 / findahelpline.com' gets clipped invisibly.
- current: A user who types into the card's chat face (including a crisis utterance) sees a truncated 10px fragment; hotline numbers can be cut off at the exact moment they matter.
- intended: A1 locked: crisis hotline text is fixed and must be fully presented; replies should be legible (12px caption floor).
- recommendation: Remove MaxHeight, raise to ≥12px, let the card grow downward (it already supports SizeToContent). For the crisis path specifically, detect SafetyFloor response and render it in a dedicated full-width, non-truncating panel — or open the main chat tab.
- verdict: isReal=True fixed=False conf=high | Verified: ChatStatusText FontSize=10/MaxHeight=32/Wrap/no scroll (xaml:685-692); NotificationCardWindow.xaml.cs:1366 assigns full reply; MainWindow.xaml.cs:1188-1228 returns it; CrisisResponseEn ~233 chars with hotlines at tail — hard-clipped at ~2 lines. Still present at HEAD.

#### UX-03 [P1] Paused vs Faulted liveness states are now confusable: bar disabled, tray fault copy says 'paused' and offers a fake 'retry' affordance
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Services/TrayIconService.cs:496-512 | effort: M | carriedOver: False
- evidence: PersistentBarWindow (carrier of the dashed-vs-solid outline contract) is disabled (AppConfig.cs:233 PersistentBarEnabled=false). SetFaultIndicator shows "Sensing paused — tap to retry" — same word as the user-pause state "已暫停 · 點此恢復" (line 474) — and left-click only opens the notification card; nothing retries. Tray icon itself never changes on fault.
- current: A dead sensing pipeline and a deliberate user pause read nearly identically (both 'paused', menu-text only); the promised tap-to-retry action doesn't exist.
- intended: Locked liveness contract: Paused and Faulted must never be confusable; honest affordances only.
- recommendation: Give the tray icon a distinct fault treatment (e.g. grey lens + hollow dot), change fault copy to e.g. "偵測中斷，自動重新連線中 / Sensing interrupted — reconnecting" (no fake 'tap to retry'), keep 'Paused by you' wording exclusive to user pause. Reflect Faulted in the card too.
- verdict: isReal=True fixed=False conf=high | Verified: bar off (AppConfig.cs:233, by design), fault copy uses 'paused' + 'tap to retry' (TrayIconService.cs:502-505); status item IsEnabled=false (unclickable), left-click only opens card, recovery is automatic (WgcSensingProvider.ScheduleFaultRecovery), icon unchanged on fault. Confusability + fake affordance are genuine.

#### UX-04 [P2] Tray context menu is mixed-language: 設定 / 退出 SenseEase / 已暫停·點此恢復 stay Chinese in English mode
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Services/TrayIconService.cs:147, 150, 474 | effort: S | carriedOver: False
- evidence: settingsItem Header="設定" and quitItem Header="退出 SenseEase" are hardcoded (no LocalizationManager.Pick); SetPowerSaveIndicator writes zh-only "已暫停 · 點此恢復" with a comment acknowledging it is 'zh-only'. Status band label and Open Dashboard ARE localized.
- current: English-mode users see a half-Chinese tray menu, including the safety-relevant paused status.
- intended: CONTENT_AND_VOICE: one language per string; whole surfaces follow the language toggle.
- recommendation: Wrap the three strings in LocalizationManager.Current.Pick(...) and re-pick them in RefreshLanguageDependentLabels (the plumbing already exists for the other items).
- verdict: isReal=True fixed=False conf=high | Verified: 設定 (line 147) and 退出 SenseEase (line 150) hardcoded; power-save header zh-only (line 474) with comment 'intentionally left as-is' (acknowledged deferral, still violates one-language policy). RefreshLanguageDependentLabels localizes other items, confirming the gap.

#### UX-05 [P2] Card proposal summary and expiry countdown are zh-only with raw engine jargon, despite the file's own i18n fix note
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Views/NotificationCardWindow.xaml.cs:323-336, 388-390 | effort: S | carriedOver: False
- evidence: FormatProposalSummary returns "把 sensitivity 調整為 {v}" / "把 severe-strike-limit 調整為 {v}" (zh + internal jargon, no English variant); UpdateExpiryText returns "剩 {h} 小時 {mm} 分" / "剩 {mm}:{ss}" zh-only. The 2026-06-09 Loc() fix covered chat status/safety-note but missed these.
- current: English users see Chinese proposal text; all users see internal parameter names ('severe-strike-limit') on a consumer surface.
- intended: Plain-language, localized proposal copy; internal terms never on user-facing status surfaces.
- recommendation: Route both through Loc(zh,en) and replace jargon with the established plain vocabulary (e.g. "調整防護敏感度為…" / "Adjust protection sensitivity to …").
- verdict: isReal=True fixed=False conf=high | Verified: FormatProposalSummary (lines 323-336) zh-only with raw jargon ('sensitivity', 'severe-strike-limit'); UpdateExpiryText (lines 388-390) zh-only countdown. Adjacent strings ('Expired', 'AI suggests') use Loc/Pick, so these were missed by the 2026-06-09 i18n pass.

#### UX-06 [P2] Hero status chip shows raw engine token 'Driver'/'Passenger'; Advanced strings English-only
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/MainWindow.xaml:316, 439-448, 464-471, 678-688 | effort: S | carriedOver: False
- evidence: Hero pill binds {Binding Mode} whose values are literal "Driver"/"Passenger" (MainViewModel.cs:1003; UserActivityModeDetector emits these strings). Also hardcoded English: "Mask ACTIVE"/"Mask inactive", "Hidden"/"Visible", "OFF"/"ACTIVE", ActivityStatus "Suppressed: …", AutoModeText "Auto · {Mode} · idle Ns".
- current: The primary calm hero surfaces an unexplained English jargon token in both languages; diagnostic strings ignore the language toggle.
- intended: Hero copy is plain-language app-action; one language per string (Driver/Passenger semantics may be reworded, never inverted).
- recommendation: Map Mode → localized plain labels at the view layer (e.g. 操作中(輕度防護)/Active use · 觀看中(完整防護)/Watching) via a converter; localize the Advanced diagnostic strings or label them as developer-only English consistently.
- verdict: isReal=True fixed=False conf=high | Confirmed: MainWindow.xaml:316 binds raw Mode ('Driver'/'Passenger' from UserActivityModeDetector, default MainViewModel.cs:1003). 'Mask ACTIVE', 'Hidden/Visible', 'OFF/ACTIVE', 'Suppressed: …' (MVM:849), 'Auto · {Mode} · idle' (MVM:858) all hardcoded English in an otherwise bilingual loc:T UI.

#### UX-07 [P2] Chat failure/loading states are English-only jargon and leak full file paths; raw exceptions surface as chat bubbles
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/ViewModels/ChatbotViewModel.cs:213-215, 282, 297-307, 628 | effort: S | carriedOver: True
- evidence: StatusText values: "Model not found at: {full path}", "Loading Ministral 3B… (first chat ~30s on CPU)", "Lexicon fallback (no GGUF found at: …)", "Load failed: {ex.GetType().Name}: {ex.Message}"; catch-all adds "[Error] {ex.Message}" as an assistant bubble.
- current: zh-mode users get English tech jargon ('GGUF', 'Lexicon fallback') with absolute paths (username) in the UI; errors read as alarming raw exceptions.
- intended: Calm, localized failure states (COMPLETENESS_PUNCHLIST §1 P0: LLM offline/loading failure-state UX).
- recommendation: Localize via Pick(); replace jargon with plain copy ("助理正在準備，第一次約需 30 秒 / The assistant is getting ready…"; "目前無法使用助理，仍可使用快速指令 / Assistant unavailable — quick commands still work"); move the probe-path detail behind the Event Log; never show raw ex.Message in a bubble.
- verdict: isReal=True fixed=False conf=high | Confirmed verbatim at lines 213-215, 282, 297-299, 307, 628. Full AppConfig.ModelPath leaks in two StatusText strings; raw ex.Message becomes an assistant bubble. The cancel path (606-622) was localized via Pick() but the generic catch was not — gap is real, not fixed.

#### UX-08 [P2] Head-tracking Settings section: permanent 'Restart needed' warning chip, radios active while feature off, no webcam/privacy copy
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Views/SettingsContent.xaml:343-396 | effort: S | carriedOver: False
- evidence: RestartButton ("需重新啟動 / Restart needed", amber warning styling) has no Visibility logic — it shows on every Settings open even when nothing changed (xaml.cs has no toggle-tracking). FollowMode radios are never IsEnabled-bound to ToggleHeadTrackingEnabled. The '頭部追蹤與隱私' section contains zero text about the webcam or local-only processing.
- current: UI claims a restart is needed when it isn't (honesty violation); inert-looking-but-live radios; opt-in consent context missing at the point of decision.
- intended: B1: webcam opt-in with restart note tied to an actual pending change; UI never lies about state.
- recommendation: Show RestartButton only after HeadTrackingEnabled differs from the value loaded at app start; disable the follow-mode radios while tracking is off; add one caption line: "開啟後會使用視訊鏡頭，影像僅在本機處理，不會儲存或上傳 / Uses your webcam; processed locally, never stored or uploaded."
- verdict: isReal=True fixed=False conf=high | Confirmed: RestartButton (xaml 359-371) has no Visibility/pending-change logic in XAML or code-behind; radios (382-395) never IsEnabled-gated; no webcam/privacy copy in section (only a diagnostic string at line 545). Comment shows badge-to-button was intentional, but always-on 'Restart needed' label remains misleading.

#### UX-09 [P2] 'Clear all my data and reset' button label overstates scope — chat transcripts and calibration history are NOT deleted
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Views/SettingsContent.xaml.cs:400-444 | effort: M | carriedOver: False
- evidence: OnClearAllDataClicked deletes behaviour log, index, observations, model_trace only. chat_history.jsonl and calibration_history.jsonl (the designated content-bearing PII files) survive. Button text: "清除所有資料並重設 / Clear all my data and reset" (SettingsContent.xaml:454).
- current: A user who clicks 'Clear ALL my data' keeps their full chat transcript on disk; the helper text narrows the claim but the button is the headline.
- intended: Honesty-UX: a shown action is labelled as what it literally does; H24's intent is a complete PII clear path.
- recommendation: Either include chat_history + calibration_history in this path (with the existing confirm dialog enumerating them), or rename the button "清除長期行為紀錄 / Clear behaviour model" and point users to the chat Clear button for transcripts.
- verdict: isReal=True fixed=False conf=high | Confirmed: handler (xaml.cs 400-444) only runs ClearUserModel (behavior log/index/observations, UserModelIndexStore.cs:77-92) + model_trace delete. ChatHistoryPath/CalibrationHistoryPath untouched. Button 'Clear all my data and reset' (xaml:454) overstates; dialog/helper text do narrow the claim, mitigating but not removing the issue.

#### UX-10 [P2] Typography floor (no 10/11px user-facing text) violated in 24+ places across live surfaces
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Views/SettingsContent.xaml:266, 299-312, 449, 478 (+ NotificationCardWindow.xaml 204, 232, 279, 357, 412, 451, 624, 688) | effort: M | carriedOver: True
- evidence: Grep: 24 FontSize="10|11" occurrences in 4 XAML files — all Settings helper captions (11px), card captions/rationale/safety-note (11px), card expiry + chat status (10px), SpikePopup hint (10px). Theme/SenseEaseTheme.xaml:75 declares "Floor: 14 body, 12 caption — no 10/11px text anywhere".
- current: Most explanatory copy the app ships — including the safety-note callout — renders below the locked legibility floor for an audience that may be nauseated.
- intended: DESIGN_SYSTEM locked floor: body 14px, caption 12px minimum.
- recommendation: Sweep all FontSize 10/11 to 12 (Type.Caption) and remove the local sizes in favour of Style={StaticResource Type.Caption}; add the orphan-size lint the audit proposed (H1) to CI grep.
- verdict: isReal=True fixed=False conf=high | Confirmed: 25 FontSize=10/11 hits across 4 XAML files including card rationale/safety-note and Settings captions; SenseEaseTheme.xaml:74-75 declares the locked floor 'no 10/11px text anywhere'. Violation of the project's own locked design system, not by-design. | corr: 25 occurrences (not 24): also ValidationModeBanner.xaml:33; SpikePopup hint is SpikePopupWindow.xaml:101; NotificationCardWindow lines are 452 (not 451), 626 (not 624), plus uncited 436/440.

#### UX-11 [P2] Contrast failures: TextTertiary ≈3.1:1 on white for 11-12px captions; Accent-on-SurfaceMuted buttons ≈3.7:1 — theme claims AA
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Theme/SenseEaseTheme.xaml:34-35, 87-90 | effort: S | carriedOver: True
- evidence: TextTertiary #8A93A3 on Surface #FFFFFF computes ≈3.1:1 (needs 4.5:1 at caption sizes); GhostBtn/followup buttons use Accent #4B6EF5 text on SurfaceMuted #EDEFF3 ≈3.7:1 at 12px SemiBold. Header comment (line 25) asserts "All text pairs clear WCAG AA 4.5:1" — false for these pairs.
- current: Helper captions and ghost-button labels fail WCAG AA in light mode; the failure is masked by an incorrect in-file claim.
- intended: WCAG 2.2 AA floor (locked); audit C3 required darkening Tertiary.
- recommendation: Darken TextTertiary to ~#6B7585 (≈4.6:1) and AccentText-on-muted to #3552D9 (AccentHover) for ghost buttons; re-verify dark palette pairs; correct or enforce the in-file AA claim with a token contrast test.
- verdict: isReal=True fixed=False conf=high | Math verified: #8A93A3 on white = 3.10:1; #4B6EF5 on #EDEFF3 = 3.76:1. TextTertiary used at 10-12px (SettingsContent.xaml:478,556 etc.). Line-25 'All text pairs clear WCAG AA 4.5:1' claim is false. Still present today. | corr: GhostBtn (Accent-on-SurfaceMuted, 13px SemiBold not 12px) lives in MainWindow.xaml:87-90, not SenseEaseTheme.xaml:87-90. Color tokens and the false AA claim are SenseEaseTheme.xaml:25-35.

#### UX-12 [P2] Overlay baseline intensity (card +/- and dashboard 遮罩調節) is session-only — silently resets to off/L1 on every restart
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/ViewModels/MainViewModel.cs:1162-1184, 1215-1238 | effort: S | carriedOver: False
- evidence: _persistentOverlayEnabled / _persistentOverlayBaselineTier are plain VM fields; grep of Configuration/ shows no persisted fields for them. The card's primary +/- control (NudgeIntensityLevel) writes only these fields. HudLeft/Top by contrast ARE persisted.
- current: A user who sets 'Keep overlay visible' + baseline Lv.6 (the one effortless comfort action) loses it on restart with no notice — matching the class of 'my settings reset themselves' reports with a real code cause.
- intended: User-chosen comfort baseline survives restarts (atomic-writes pattern already exists in UserPreferences).
- recommendation: Add persistent_overlay_enabled + persistent_overlay_baseline_tier to UserPreferences (schema v4 additive), hydrate in MainViewModel ctor, save on change. Keep Kirby's default-OFF for fresh installs.
- verdict: isReal=True fixed=False conf=high | Confirmed: PersistentOverlayEnabled/BaselineTier are plain VM fields (MainViewModel.cs:1162/1175), no persistence keys in Configuration, no ctor hydration; HudLeft/Top ARE persisted (UserPreferences.cs:204). NudgeIntensityLevel writes only these. Real code cause — AV file-lock cannot explain this one.

#### UX-13 [P2] First-run experience is now a void: onboarding banner removed, AppConfig.IsFirstRun dead code, no in-app explanation of what SenseEase does
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/MainWindow.xaml:284-285 | effort: M | carriedOver: False
- evidence: Comment: "First-run welcome banner REMOVED (2026-06-08): it was the only entry to the calibration video demo". Grep shows AppConfig.IsFirstRun/MarkFirstRunComplete (AppConfig.cs:160-174) have zero consumers. A fresh user gets the dashboard, a vestibular diagnosis question bar, and nothing explaining protection/tray-resident behaviour.
- current: No onboarding at all; the first thing a new user is asked is a medical-adjacent question with no framing; tray-minimize behaviour (close ≠ quit) is unexplained until the handbook.
- intended: Quiet-Guardian first-run: answer 'Am I protected?' in plain language Day 1.
- recommendation: Add a one-card, dismissible first-run hero strip keyed off the existing IsFirstRun flag: what the overlay does, that closing minimizes to tray, where Settings live; sequence it BEFORE the vestibular prompt. Delete or use the dead flag either way.
- verdict: isReal=True fixed=False conf=medium | Evidence accurate: removal comment MainWindow.xaml:284-285; AppConfig.cs:160-174 IsFirstRun has zero consumers; vestibular prompt (xaml:162-187) is the first ask. Banner removal was deliberate (calibration demo excised), so the gap is partly a design decision; dead flag is fact. Chatbot welcome bubble is the only partial mitigation.

#### UX-14 [P2] Notification card unreachable by keyboard; icon targets below 44px
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Views/NotificationCardWindow.xaml:378-390, 572-583 | effort: M | carriedOver: True
- evidence: Card HWND gets permanent WS_EX_NOACTIVATE (ClickThrough.cs:76-96) — there is no keyboard path to focus it (Escape handler at xaml.cs:1074 only works after a mouse click into the chat box). CloseBtn and FlipBtn are 22×22; RoundPillButtonStyle +/- are 30×30 (44×44 target per audit H2).
- current: Keyboard-only and switch users cannot dismiss, adjust, or answer proposals on the card at all; small pointer targets for everyone else.
- intended: WCAG keyboard operability on every interactive surface; 44×44 targets on card icon buttons.
- recommendation: Add a global hotkey or tray-menu item that focuses/activates the card when visible (SetForegroundWindow works despite NOACTIVATE); enlarge hit areas via transparent padding to 44×44 while keeping the 22px glyphs.
- verdict: isReal=True fixed=False conf=high | Confirmed: EnableNoActivate called at xaml.cs:506 (permanent WS_EX_NOACTIVATE); no hotkey/tab path to focus the card; CloseBtn and FlipBtn 22x22 (xaml:378-390, 572-583), RoundPillButtonStyle 30x30 (xaml:115-122). NOACTIVATE is intentional quiet-guardian design but the keyboard-operability gap is real. | corr: ClickThrough.cs path is src/SenseEase.App/Interop/ClickThrough.cs (lines 76-96 correct). Escape handler is OnKeyDown at NotificationCardWindow.xaml.cs:1074.

#### UX-15 [P2] Event Log mixes UTC and local timestamps in the same visible table
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/ViewModels/ChatbotViewModel.cs:1092-1101 | effort: S | carriedOver: True
- evidence: LogShaderCommand sets Timestamp = DateTime.UtcNow; every other UI entry uses EventLogEntry's default DateTime.Now (EventLogEntry.cs:6). EventLogWindow displays Timestamp.ToString("HH:mm:ss") for both.
- current: Slash-command entries appear 8 hours off (Taiwan) from adjacent entries in the user-visible Event Log — looks like the log is broken.
- intended: One consistent clock in the user-visible table (UTC ISO-8601 is the persisted-log rule; display should be uniformly local).
- recommendation: Use DateTime.Now for the UI-row timestamp here (or convert ToLocalTime in the Time display helper) and keep the persisted UTC discipline at the store layer where W1W2 #12 is being fixed.
- verdict: isReal=True fixed=False conf=high | Confirmed: ChatbotViewModel.cs:1094 UtcNow vs EventLogEntry default DateTime.Now (line 6); Time renders HH:mm:ss with no conversion, so UTC rows sit 8h off local rows in the same visible table. Engine habituation rows affected too, so fix should cover all UtcNow appenders. | corr: Broader than stated: MockInferenceEngine.cs:1138,1602,1620 also set Timestamp=DateTime.UtcNow, and store entries mirror into the UI table (MainViewModel.cs:718-726). Display helper is EventLogEntry.Time (SenseEase.Core/Models/EventLogEntry.cs:20).

#### UX-16 [P3] Window title bars don't follow the language toggle (English-only main, zh-only Settings/Event Log)
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Views/SettingsWindow.xaml:6 | effort: S | carriedOver: False
- evidence: SettingsWindow Title="SenseEase 設定"; EventLogWindow.xaml:5 Title="SenseEase — 事件記錄"; MainWindow.xaml:7 Title="SenseEase — Visual Comfort Dashboard". None localized (loc:T works on Title via binding).
- current: Alt-Tab/taskbar/title chrome shows the wrong language for half the windows in either mode.
- intended: One language per string across all user-visible chrome.
- recommendation: Bind Title via {loc:T} (it returns a Binding) or set in code-behind from LocalizationManager with a PropertyChanged re-pick.
- verdict: isReal=True fixed=False conf=high | Verified: SettingsWindow.xaml:6 zh-only, EventLogWindow.xaml:5 zh-only, MainWindow.xaml:7 English-only. No code-behind sets .Title anywhere. LocExtension.ProvideValue returns a Binding (LocExtension.cs:30-38), so {loc:T} on Title works. Not antivirus-related; genuine localization gap.

#### UX-17 [P3] Reduced-motion gating incomplete: typing-dots loop and scrollbar fades always animate
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/MainWindow.xaml:861-894 | effort: S | carriedOver: True
- evidence: The chat typing indicator runs an infinite RepeatBehavior="Forever" opacity storyboard with no AccessibilityHelper.ReducedMotion() gate; SenseEaseTheme.xaml:368-389 AutoHideScroll fade triggers likewise. Card entrance/exit and tab cross-fade ARE gated (good).
- current: With OS 'animation effects' off, decorative pulsing dots still loop for the entire LLM wait (~30s on first load) on a motion-sensitivity product.
- intended: Audit C1: every decorative storyboard gated on Reduce Motion (clinical mask cross-fade exempt).
- recommendation: Gate BeginStoryboard on ReducedMotion() (swap to a static '…' ellipsis text when reduced); the scrollbar fade can simply snap opacity.
- verdict: isReal=True fixed=False conf=high | Verified: MainWindow.xaml:869-888 typing-dots RepeatBehavior=Forever, no ReducedMotion gate; SenseEaseTheme.xaml:368-389 scrollbar fades ungated. Project's own docs require this (UIUX_UPGRADE_PLAN.md:136/163 'typing dots become static …'; ALPHA_TEST_CHECKLIST C1). Other storyboards gated via AccessibilityHelper in code-behind.

#### UX-18 [P3] Dead/inert UI debris: suppression-status pill invisible, orphaned styles, permanently-collapsed cards
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/MainWindow.xaml:639-640, 692-718, 94-104, 752 | effort: S | carriedOver: False
- evidence: Eye-Care 'Advanced — live metrics' Expander is hardcoded Visibility="Collapsed", yet it is the ONLY consumer of ActivityStatus/AutoModeText — the comment at 692 still claims it lets 'users see when OFI sensing is being intentionally paused'. ProfilePill style (94) orphaned; Session Summary card (752) Collapsed; hidden legacy Engage/Mute buttons in the card.
- current: The 'why isn't sensing reacting?' explanation (typing/drag suppression) is invisible everywhere; dead styles/cards bloat the largest XAML file.
- intended: Either surface diagnostic affordances or remove them; XAML reflects shipped UI.
- recommendation: Move the ActivityStatus row into the Dashboard's Advanced expander (it answers a real user question), then delete the collapsed Eye-Care expander, Session Summary card, ProfilePill style and legacy buttons (keeping VM properties used by tests).
- verdict: isReal=True fixed=False conf=high | Core facts verified: Expander at 639-640 Collapsed and is the only XAML consumer of ActivityStatus/AutoModeText; ProfilePill (line 94) has zero usages; Session Summary card (752) Collapsed. Suppression-status explanation is invisible everywhere. Two evidence locations slightly off (see correction). | corr: The 'intentionally paused' comment is in MainViewModel.cs:845/1111, not MainWindow.xaml:692 (which says 'Cross-component status row'). Legacy Engage/Mute buttons are in NotificationCardWindow.xaml:593-596, deliberately kept Collapsed so event wiring compiles (per in-file comment).

#### UX-19 [P3] Chat welcome bubble is stacked bilingual; English strings contain full-width punctuation
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/ViewModels/ChatbotViewModel.cs:80-82, 869-873 | effort: S | carriedOver: False
- evidence: WelcomeMessage is a const containing both zh and en lines shown regardless of the language toggle; ApplyNluResult appends "（open the overlay to see it）" with full-width parens in the English branch.
- current: First assistant message violates one-language-per-string; English summary uses CJK punctuation.
- intended: CONTENT_AND_VOICE: one language per string, picked by the toggle.
- recommendation: Split WelcomeMessage into zh/en consts picked via LocalizationManager at display time (it is never persisted, so the swap is safe); fix the parens.
- verdict: isReal=True fixed=False conf=high | Verified: ChatbotViewModel.cs:80-82 WelcomeMessage const stacks zh+en regardless of toggle; lines 872-873 English branch uses full-width parens '（open the overlay to see it）'. No fix in git log through 3bb2a7e. Genuine CONTENT_AND_VOICE violation.

#### UX-20 [P3] Live feature flags with no UI affordance: tray tier-colour and escape hatch (escape hatch also reportedly non-functional)
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Views/SettingsContent.xaml:272-273, 398-399, 515 | effort: M | carriedOver: True
- evidence: Comments: "Bar/Tray colour toggle removed (2026-06-09)" and "escape-hatch section removed entirely (2026-06-09)" — but BarTrayColorEnabled (default false) still gates the tray accent dot (TrayIconService.cs:397) and HeadTrackingEscapeHatchEnabled=true still arms double-tap-Ctrl (AppConfig.cs:331; OverlayWindow.xaml.cs:523). The dev hotkey table at 515 is hidden because the hotkeys were reported non-functional (2026-05-31 note).
- current: Users can't opt into the tier-coloured tray or disable/discover the mask-peek hotkey except by editing JSON; the only documentation of the hotkey is a hidden panel; the hotkey itself may not fire.
- intended: Settings exist for every persisted user-facing behaviour, or the dead flags are retired (punchlist: escape hatch non-functional + no peek affordance).
- recommendation: Decide per flag: re-add a simple Settings switch, or hard-retire the flag and code path. If keeping the escape hatch, fix the double-Ctrl detection first and surface a peek affordance on the card.
- verdict: isReal=True fixed=False conf=high | Verified: BarTrayColorEnabled (default false, UserPreferences.cs:104) gates TrayIconService.cs:397 + PersistentBarWindow.xaml.cs:95; escape hatch default true, dev-hotkey panel hidden at SettingsContent.xaml:515 with in-code admission double-Ctrl doesn't fire end-to-end. Caveat: toggle removals were explicit user requests (2026-06-09); hotkey bug already tracked as beta follow-up. | corr: AppConfig.cs is src/SenseEase.App/Configuration/AppConfig.cs (not Core); line 331 correct. Escape-hatch trigger check is OverlayWindow.xaml.cs:525.

#### UX-21 [P3] Proposal-blocked safety note styling deviates: 'System intervention:' prefix on model rationale uses 11px italic and a clay alarm-adjacent chip without localization of the reason text
- file: C:/Users/kirby/Desktop/SenseEase APP/src/SenseEase.App/Views/NotificationCardWindow.xaml.cs:296-303 | effort: S | carriedOver: False
- evidence: ShowProposalApplyResult writes Loc("系統介入：","System intervention: ") + result.Reason — Reason comes from the Applicator in whatever language it was authored (engine-side strings), so the visible note can be half-translated; the note renders at FontSize=11 (xaml:232).
- current: Blocked-proposal explanations can appear as mixed-language 11px text inside the safety callout.
- intended: A5: blocked proposals stay visible with a calm, readable SafetyNote.
- recommendation: Localize Applicator reason codes via a small code→Pick(zh,en) map at the card layer; bump the callout to 12px.
- verdict: isReal=True fixed=False conf=high | Mixed-language gap is real: Applicator Reason strings (HabituationOverrideApplicator.cs:97,154,165,173,182) are hardcoded English; card prepends localized prefix at xaml.cs:298, so zh mode shows half-translated note at 11px (xaml:232). Localization sweeps (7924174, 12f9fef) missed these. Chip/italic styling complaints are by-design or misread. | corr: Safety note is NOT italic (italic is on ProposalRationaleText, xaml:205, by design H7); chip is intentionally calm-clay per M8 comment (xaml:209-214), not alarm-adjacent. Core code line is xaml.cs:298. Severity P3 stands.