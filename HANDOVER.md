# SenseEase APP — Handover

**Date:** 2026-05-21
**Build state:** 0 warnings, 0 errors, **75/75 tests passing**
**Runtime state:** App starts cleanly; Mistral 3B GGUF loads in ~25–30s on background thread.

---

## TL;DR

The project is in a healthy state. Phase 1–5 are complete (see [user memory](#user-memory) for the authoritative phase log). This session completed a perf optimization pass, fixed a startup-killing XAML binding bug, added a crash logger + UI smoke test, and started a 4-item runtime-verification round. **Three open items** carry over to your session, listed in priority order in the next section.

The user is using another Claude Code session in parallel to take this work forward. Pick up from "Open Items" below.

---

## Open items (priority order)

### 1. Add UI toggle for `AmbientLightComfortEnabled` ⚠️ blocks two verifications

**Why:** This master flag in [src/SenseEase.App/Configuration/AppConfig.cs:39](src/SenseEase.App/Configuration/AppConfig.cs#L39) gates **the entire ambient-comfort pipeline** — webcam-lux *and* DDC/CI monitor brightness writes. Default is `false` (privacy + hardware safety). There is currently **no UI to flip it**, so the user can't verify webcam-lux fallback (#2) or DDC/CI restore guarantee (#4).

**Suggested implementation:**
- Add a `Checkbox` in the Dashboard "Comfort" / Lux card area.
- Bind to a new `MainViewModel.AmbientLightComfortEnabled` property that reads/writes `AppConfig.AmbientLightComfortEnabled` and triggers the appropriate `LuxComfortController.Start()` / `Shutdown()`.
- **Warning text** next to it: *"Enabling will open your webcam (if no ALS detected) and write monitor brightness via DDC/CI. Settings restore when the app exits."*
- Trigger lux-controller wiring when enabled, shutdown when disabled.

**Where lux is wired:** [MainViewModel.WireLuxComfort](src/SenseEase.App/ViewModels/MainViewModel.cs) — currently called from `MainWindow.SourceInitialized` only if some condition; check whether enable-at-runtime is supported or if you need to restart the controller. The restore-on-exit guarantee already has 6 entry points in [App.xaml.cs](src/SenseEase.App/App.xaml.cs).

**After the toggle is in:** run the manual verification protocol (section below) for items #2 (webcam-lux) and #4 (DDC/CI restore).

---

### 2. Mistral chatbot follow-up question logic (Phase 2.4.1)

**Why:** User reports the current one-shot NLU sometimes guesses severity from vague input ("我覺得怪怪的"). User asked: *"Maybe let the Chatbot ask some followups to measure?"*

**Sketch:**
- Add a new intent value `"Clarify"` to the few-shot examples in [MistralChatbotService.cs](src/SenseEase.Infrastructure/Real/MistralChatbotService.cs) (look for `FEW_SHOT_EXAMPLES`).
- Few-shot examples should teach Mistral to return `{intent:"Clarify", severity:0, reply:"是哪一種不適？(1) 暈眩 (2) 噁心 (3) 眼睛痠"}` when input is ambiguous.
- In [ChatbotViewModel.SendAsync](src/SenseEase.App/ViewModels/ChatbotViewModel.cs), when `result.Intent == "Clarify"`, skip the `ApplyNluResult` call entirely — only render the `Reply` bubble. Next user message resolves normally.
- Add 1 test in [tests/SenseEase.Tests](tests/SenseEase.Tests) asserting that a `Clarify` result does not call `ApplyUserFeedback` / `ApplyShaderAdjustment` on the engine.

~30 LOC total. Won't disturb anything else.

---

### 3. WindowsActivityFilter unit tests (low priority)

Skipped in this session because the singleton + WinEvent hooks + global timer make unit testing expensive without a refactor (would need to inject a clock + abstract the hook). Defer until the test pass rate elsewhere drops.

If you do tackle it: extract the time-window logic in `WithinWindow` + the `SuppressionReason` predicate into a pure function taking `(currentTicks, lastTicks, windowMs)` → bool. Test that. Leave hook installation to integration testing.

---

## Hard constraints (do not break)

1. **Do not change values in [src/SenseEase.Core/Configuration/SenseDefaults.cs](src/SenseEase.Core/Configuration/SenseDefaults.cs).** These are tuned perceptual constants (e.g., `NoiseGateThreshold=8`, `TriggerPressure=85`, `PassiveFlowWeight=2.0`, `WgcMadNormFactor=4.0`, `CursorEmaAlpha=0.55`, VIMS tier debounces). Touching them changes the algorithm output.
2. **Do not change public API of `Core/Abstractions/*` interfaces.** They are the project's contract surface.
3. **`AppConfig.AmbientLightComfortEnabled` default stays `false`.** It mutates monitor hardware via DDC/CI — opt-in only.
4. **Habituation Engine defaults to paused.** Do not unpause without explicit user UI toggle. See `MockInferenceEngine` ctor.
5. **`MockInferenceEngine` is the active inference engine in production** (despite the name). The `Mock` prefix is historical — see [the memory file](#user-memory). Do not "replace it with a real one".
6. **Phase 2.5 (NPU/OpenVINO) and Phase 11 (Packaging/Installer) are explicitly excluded** by user direction. Do not begin work on these without an explicit request.

---

## Recent fixes in this session (don't redo)

Brief context so you don't repeat work:

| Fix | File(s) | Notes |
|---|---|---|
| `VimsTier` TwoWay binding bug killing startup | [MainWindow.xaml:482](src/SenseEase.App/MainWindow.xaml#L482) | Was `<Run Text="{Binding VimsTier}"/>` — `Run.Text` is not a DP and defaults to TwoWay. Now `Mode=OneWay`. This was the actual "cannot start" bug (NOT Mistral, which always loaded fine on background thread). |
| Manual Driver/Passenger toggle was killing auto-detection | [MainViewModel.cs:138-156](src/SenseEase.App/ViewModels/MainViewModel.cs#L138-L156) + [UserActivityModeDetector.OverrideMode](src/SenseEase.Infrastructure/Real/UserActivityModeDetector.cs) | Now a one-shot override; auto polling continues and reasserts on next threshold crossing. |
| Crash logger | [App.xaml.cs:18-86](src/SenseEase.App/App.xaml.cs#L18-L86) | Writes timestamped records to `%LOCALAPPDATA%\SenseEase\crash.log` from Dispatcher / AppDomain / UnobservedTask. Best-effort, never throws inside itself. Check this file before debugging "cannot start" reports. |
| UI smoke test | [tests/MainWindowSmokeTests.cs](tests/SenseEase.Tests/MainWindowSmokeTests.cs) | STA-threaded `Application.Run()` + scheduled close. Pumps the dispatcher for ~700ms and fails on any `DispatcherUnhandledException`. Catches the next `VimsTier`-class regression in CI. |
| RingHistory unit tests | [tests/RingHistoryTests.cs](tests/SenseEase.Tests/RingHistoryTests.cs) | 6 tests covering FIFO + `INotifyCollectionChanged` reset contract. |
| Perf pass (preserves all algorithm outputs) | WGC, Cursor, WindowsActivityFilter, OverlayWindow, MainViewModel, MockInferenceEngine | Ping-pong buffers + precomputed index tables in `WgcSensingProvider`; cached `SendOrPostCallback` in WGC + Cursor; `RingHistory` replacing `ObservableCollection<double>` for chart history; 15Hz dirty-flag chart timer; reused `LinearGradientBrush`+`GradientStop` table in `OverlayWindow`; `_chartTimer.Stop()` on Window.Closed; `WindowsActivityFilter` cursor-delta Interlocked cleanup. Verified by a checker agent, build green, 68→75 tests passing. |
| Loading status text in AI Assistant | [ChatbotViewModel.cs:78-80](src/SenseEase.App/ViewModels/ChatbotViewModel.cs#L78-L80) | Now sets `"Loading Mistral 3B… (first load ~30s on CPU; chat is disabled meanwhile)"` so users don't think it's stuck. |

---

## Manual verification protocol (status snapshot)

The user was working through a 4-check verification when this session paused. Statuses as of handover:

| # | Check | Status | Notes |
|---|---|---|---|
| 1 | **Mistral chatbot roundtrip** | ✅ Confirmed working | User typed Chinese discomfort message, got reply + applied adjustment bubble + actual mask change. They also asked if we can add follow-up questions — that's open item #2 above. |
| 2 | **Webcam-lux fallback** | ⏸ Blocked on master toggle | Webcam never opens because `AmbientLightComfortEnabled=false` and there's no UI. Resolves when open item #1 ships. |
| 3 | **Auto Driver/Passenger** | 🔄 Re-test pending | User pressed the manual toggle button, which (until just now) permanently disabled auto. Just fixed (see "Recent fixes" table). User has not re-verified yet. App is currently running with the fix; ask them to confirm. |
| 4 | **DDC/CI restore guarantee** | ⏸ Blocked on master toggle | Same root cause as #2. Hardware-mutating; the 6 exit paths in `App.xaml.cs` are wired but never exercised under fault conditions. |
| 5 | (Bonus) **Demo Mode "does nothing"** | ℹ Explained | Demo mode swaps to `MockSensingProvider` driven by the OFI slider. User clicked the button without moving the slider, so OFI stayed 0. Not a bug. Consider adding a UX hint on Demo activation? |

When you pick this up: if the user wants to push on #2/#4, start with open item #1 (the master toggle). #3 just needs a yes/no from the user. #5 may be worth a one-line hint in the UI but is low priority.

---

## File pointers (where things live)

```
SenseEase.Windows.sln
src/
  SenseEase.App/                 WPF net8.0-windows10.0.19041.0
    App.xaml.cs                  Lux shutdown + crash logger + 6 exit paths
    MainWindow.xaml(.cs)         3 tabs: Dashboard, 護眼儀表板, AI Assistant
    Views/OverlayWindow.xaml.cs  Chang-Hain 3-zone FOV mask + grid
    Configuration/AppConfig.cs   All runtime flags (read first when working here)
    ViewModels/
      MainViewModel.cs           Composition root, sensing + inference + lux wiring
      ChatbotViewModel.cs        Mistral chat UI logic (NLU → engine adjustments)
    Comfort/                     LuxComfortController + Dxva2DisplayActuator (DDC/CI)
  SenseEase.Core/                Pure logic, no Windows deps
    Abstractions/                Interfaces (ISensingProvider, IInferenceEngine, ...)
    Inference/                   LeakyIntegrator, HabituationEngine, LuxComfortMapper
    Configuration/SenseDefaults  ← DO NOT TOUCH constants here
  SenseEase.Infrastructure/      Concrete impls
    Real/
      CursorVelocityProvider.cs  Win32 GetCursorPos 30Hz
      WgcSensingProvider.cs      Windows.Graphics.Capture pixel-diff 15Hz
      WindowsActivityFilter.cs   Singleton, WinEvent hooks for OS-chrome suppression
      UserActivityModeDetector.cs  Win32 GetLastInputInfo polling, Driver/Passenger hysteresis
      MistralChatbotService.cs   LLamaSharp 0.27, GGUF Q4_K_M
      LightSensorProvider.cs     WinRT LightSensor + webcam fallback path
      WebcamLuxProvider.cs       WinRT MediaCapture, 10s poll, luminance→lux curve
      Dxva2DisplayActuator.cs    DDC/CI VCP 0x10/0x12/0x87
    Mock/
      MockSensingProvider.cs     Slider-driven OFI (Demo mode)
      MockInferenceEngine.cs     ← Production inference engine (name is historical)
    Storage/                     FileAdjustmentLog, FileHabituationStore (JSONL)
tests/
  SenseEase.Tests/               xUnit, net8.0-windows10.0.19041.0 with UseWPF=true
models/mistral-3b/model.gguf     Ministral 3B Instruct 2512 Q4_K_M, ~2 GB
```

---

## Environment

- Windows 11, i7-1165G7 (Tiger Lake, 4c/8t), **16 GB RAM, no GPU, no ALS hardware**
- .NET 8 SDK installed (LLamaSharp 0.27 + LLamaSharp.Backend.Cpu 0.27)
- Not a git repo. No CI. Manual build + test only.
- LLamaSharp 0.27 is the latest on NuGet as of this date; user evaluated Gemma 3 4B as an alternative and decided to **stay on Ministral 3B** (current is sufficient).

Quick commands:
```powershell
dotnet build SenseEase.Windows.sln -c Debug
dotnet test  SenseEase.Windows.sln -c Debug --no-build
dotnet run --project src\SenseEase.App\SenseEase.App.csproj -c Debug --no-build
```

App startup: window appears in ~25s (Mistral loads on background ~30s total). If it doesn't, check `%LOCALAPPDATA%\SenseEase\crash.log` first, then Windows Event Viewer → Application log → "Application Error" / ".NET Runtime".

---

## User profile

- Speaks Traditional Chinese (zh-TW) + English fluently. Mixed-language responses are welcomed.
- Tone: terse, direct, no fluff. Wants honest engineering. *"是否修正"* questions expect a real recommendation, not a hedge.
- Has refused: blind model swaps, unnecessary refactors, features without verification.
- Has approved: multi-agent orchestration (was used for the perf pass), runtime-verification rounds, crash logging.
- Will say "use another Claude session" when context limits are near — don't take it as dissatisfaction.

---

## User memory

The authoritative project state lives in:

```
C:\Users\Kirby Liu\.claude\projects\c--Users-Kirby-Liu-Desktop-SenseEase-APP\memory\
  MEMORY.md                — index, always loaded
  project_senseease.md     — full phase log + key decisions + roadmap
```

Read `project_senseease.md` early in your session — it has the phase-by-phase decisions and tuning rationale that aren't in code comments. Update it when you complete a phase or change a load-bearing decision.

---

## Suggested first move

1. Read `project_senseease.md` to ground yourself in phase context.
2. Ask the user whether they want to start with item #1 (master toggle UI) or item #2 (Mistral follow-ups). #1 unblocks two verifications; #2 is a contained feature add.
3. If picking up #3 (auto-mode re-verification), just ask them to test the running app: press manual toggle, sit idle 8s, watch mode flip back. No code needed.
