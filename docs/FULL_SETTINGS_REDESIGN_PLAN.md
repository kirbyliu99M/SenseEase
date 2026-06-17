# Full Settings — redesign plan + inventory (2026-06-18)

Purpose: the new edge/popup panel has matured; the **Full Settings** window (`SettingsContent.xaml`,
hosted by `SettingsWindow`) is the last big surface still in the old idiom. This doc (1) inventories
**everything currently wired into it** so you can decide what stays, and (2) proposes a redesign +
the **legacy "pop-up bar" (NotificationCard) demolition** plan. Nothing here is executed yet — mark it
up and I'll build from your decisions.

---

## 1. Inventory — what's wired today (mark KEEP / CUT / MERGE)

Legend: **Live** = wired & functional · **Cond** = present but hidden behind a condition · **Dead** = orphaned.

### A. General
| Control (Zh / En) | Wired to | Effect | Status | Reco |
|---|---|---|---|---|
| Appearance: 自動/淺色/深色 (Auto/Light/Dark) | `ThemeManager.SetMode` → `UserPreferences.ThemeMode` | live theme flip | Live | **KEEP** |
| Language: 繁體中文 / English | `LocalizationManager.Language` + `LanguageModeRaw` | live UI language | Live | **KEEP** |

### B. Behaviour
| Control | Wired to | Effect | Status | Reco |
|---|---|---|---|---|
| 讓 SenseEase 主動提醒 / remind me proactively (`ToggleAuto`) | `UserPreferences.AutoExpandNotifications` | auto-expands the **notification card** on moderate+ discomfort | Live | **CUT?** — tied to the card being demolished (see §3); likely obsolete |
| 關閉時最小化到系統匣 / Minimise to tray on close | `UserPreferences.MinimizeToTrayOnClose` | X→tray vs quit | Live | **KEEP** |
| 開機自動啟動 / Run at startup | `AutoStartRegistry` + `RunAtWindowsStartup` | Windows startup entry | Live | **KEEP** |
| 喚出熱鍵 / Reveal hotkey (display + 錄製/Record) | `UserPreferences.RevealHotkey` + `SetRevealHotkey` host delegate | re-registers the global hotkey | Live | **KEEP** |

### C. Protection / engine
| Control | Wired to | Effect | Status | Reco |
|---|---|---|---|---|
| 啟用漸進適應 / Graded exposure (`ToggleGradedExposure`) | `SetGradedExposure` host delegate (engine pause) + `HabituationEnabled` fallback | the SOLE user path to pause/resume habituation | Live | **KEEP** (note: **duplicated** in the popup quick-settings — they sync via `UserPreferences.Changed`) |
| 啟用頭部追蹤 / Enable head tracking (`ToggleHeadTrackingEnabled`) | `HeadTrackingEnabled` + `AppConfig` + restart chip | spawns sidecar next launch; FOV mask tracks head | Live (**restart**) | **KEEP** |
| 需重新啟動 / Restart chip (`RestartButton`) | `OnRestartClicked` | restart to apply head-tracking | Cond (shows when toggle≠startup value) | **KEEP** |
| 追蹤模式 平衡/完整 (Follow mode Balanced/Full) | `HeadTrackingFollowMode` + `AppConfig` | per-frame mask follow behaviour | Live | **KEEP** |
| Head-tracking health row (●/○/✕) | `GetHeadTrackingHealth` → `SidecarHeadPoseReader.Health` | live sidecar status | Cond (hidden w/o reader) | **KEEP** |

### D. Data & privacy
| Control | Wired to | Effect | Status | Reco |
|---|---|---|---|---|
| 記錄使用中的應用程式 / Per-app attribution (`TogglePerAppAttribution`) | `PerAppAttributionEnabled` + live `WindowsActivityFilter` | stamps foreground app on discomfort (on-device) | Live | **KEEP** |
| 動態較多時問我的感受 / Proactive check-ins (`ToggleProactiveCheckIns`) | `ProactiveCheckInsConsent` (tri-state) | gates the spike check-in prompt | Live | **KEEP** (but its surface moves card→chat, §3) |
| 依回報自動微調 / Adaptive auto-tune (`ToggleAdaptiveAutoTune`) | `AdaptiveAutoTuneConsent` (tri-state) | B5 capped auto-adjust, announced in chat | Live | **KEEP** |
| 匯出健康報告 / Export health report | `ClinicalExportService.WriteJson` | de-identified JSON | Live | **KEEP** |
| 匯出原始紀錄 CSV / Export raw CSV | `SymptomEventCsvExporter` | raw check-in CSV | Live | **KEEP / VERIFY** (overlap with health report?) |
| 清除所有資料並重設 / Clear all data | `ClearAllUserData()` (6 files) | wipes user data, keeps habituation | Live | **KEEP** |
| 暈眩防護（敏感模式）/ Vestibular protection (`VestibularProtectionToggle`) | `SetVestibularProtection` host delegate | dizziness mode | Cond (auto-profiling only) | **VERIFY** — only in auto-profiling builds |
| 重設個人化狀態 / Reset personalisation (`ResetStateButton`) | `ResetRequested` → `MainViewModel.ResetStateCommand` | clears transient tweaks | Cond (auto-profiling only) | **VERIFY** |

### E. Maintenance
| Control | Wired to | Effect | Status | Reco |
|---|---|---|---|---|
| 開啟事件記錄 / Open event log (`EventLogButton`) | `OpenEventLog` host delegate | opens EventLogWindow | Cond (hidden in no-VM popup path) | **KEEP** |
| 重設偏好設定 / Reset preferences | `UserPreferences.ResetToDefaults()` | resets theme/lang/toggles/HUD pos | Live | **KEEP** |

### F. Dead / orphaned (safe to drop)
- **Developer test hotkeys** section — `Visibility=Collapsed`, the Ctrl+Shift+H sweep is non-functional (D-10). **CUT** (or keep collapsed).
- Already-deleted-but-key-lingers prefs: `BarTrayColorEnabled` (D-10), escape hatch (D-10), `VestibularWarningShown` (D-14). No UI; ignorable in old json. **No action** unless a schema bump.

---

## 2. Proposed redesign (for your approval)

Re-group into the new glass idiom with **5 clear sections** (collapsible), dedupe, and demote rarely-used:

1. **General** — Appearance, Language.
2. **Behaviour** — Minimise-to-tray, Run-at-startup, Reveal hotkey.
3. **Protection** — Graded exposure, Head tracking (enable + mode + health + restart). *Option:* also surface
   錨點格線 / 常駐遮罩 here (currently popup-only) for a complete one-stop panel — your call.
4. **Privacy & data** — per-app attribution, proactive check-ins, adaptive auto-tune, exports, clear data, event log.
5. **Reset** — reset preferences (+ reset-state if auto-profiling).

Open questions for you (the "verify whether needed"):
- **Q1.** `ToggleAuto` (auto-expand the notification card) — **cut** with the card demolition? (recommended)
- **Q2.** Keep BOTH graded-exposure surfaces (Full Settings + popup quick-settings), or popup-only?
- **Q3.** Surface 錨點格線/常駐遮罩 in Full Settings too, or keep them popup-only?
- **Q4.** The vestibular / reset-state group only appears in "auto-profiling" builds — is auto-profiling a shipping mode, or cut that group?
- **Q5.** Two export buttons (clinical JSON + raw CSV) — keep both or merge?
- **Q6.** Drop the collapsed Developer-hotkeys section entirely?

Tell me KEEP/CUT/MERGE per row + answers to Q1–Q6, and I'll build the redesigned panel in the glass style.

---

## 3. Legacy "pop-up bar" (NotificationCard) demolition plan

"The original pop-up bar" = **`NotificationCardWindow`** (the bottom-right card the tray single-click used
to open). The tray click is **already rewired** to the new panel (commit d523e58). Full removal is gated
because the card still backs two engine flows — pull these fuses **one at a time**, each its own commit + green gate:

**Current callers of `ShowNotificationCardForCurrentTier` / the card (`MainWindow.xaml.cs`):**
- ~~tray `LeftClicked`~~ → **done** (now `RevealShellPanel`).
- `PressureSpikeDetected` (~:190) → spike check-in on the card.
- `_persistentBar.Clicked` (~:1543) → opens the card.
- `SurfaceProposalAsCard` / proposals (~:1631) → `ActiveProposal` lives on the card (`Accept/Decline/Defer/Expire`).

**Fuse order (proposed):**
1. **F-N1 — Reroute the spike check-in to chat.** `PressureSpikeDetected` → surface the FMS check-in as a
   chatbot question via the existing `ChatbotViewModel.RaiseFollowup` path (the feedback-as-chat the reshape
   plan intended), gated by the same `CheckInGovernor` + `ProactiveCheckInsConsent`. *Gate:* a gated spike
   surfaces in the popup chat, not the card.
2. **F-N2 — Reroute habituation proposals to chat.** `ActiveProposal` Accept/Decline/Defer become inline
   chat followups (`SubmitFollowupAnswer` → existing `Accept/Decline/DeferActiveProposal`). *Gate:* a proposal
   appears + resolves in chat; engine state matches the old card path.
3. **F-N3 — Re-point the legacy persistent-bar click.** `_persistentBar.Clicked` → `RevealShellPanel` (or
   retire the persistent bar too — separate decision; the user only greenlit the card). *Gate:* clicking the
   old bar reveals the new panel.
4. **F-N4 — Delete `NotificationCardWindow` + `NotificationCardController`** + `ShowNotificationCardForCurrentTier`
   + the `AutoExpandNotifications` setting (Q1). *Gate:* build 0/0, full suite green, no references remain.

**Dependencies / risks:** F-N1/F-N2 touch the B5 feedback loop + `CheckInGovernor`; verify the chat-followup
path is fully wired before deleting the card. The `AutoExpandNotifications` Full-Settings toggle dies with F-N4.
Do NOT bundle the **PersistentBarWindow** retirement here unless you greenlight it (you only named the pop-up card).

**Confirm before I start:** which fuse to pull first, and whether the persistent bar is in scope.
