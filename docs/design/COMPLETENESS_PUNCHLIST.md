> **[LEGACY]** Superseded by [Beta-1.md](../../Beta-1.md) (2026-06-14). Retained for historical reference / back-trace; not the current plan.

# SenseEase — Rebrand Completeness Punch-list

<!-- Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed. -->

> Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed.

I now have complete, decision-grade evidence across every surface. Writing the spec section.

---

## Alpha-1 Rebrand — Missing & Under-Specified Surfaces (Prioritized Punch-List)

This section enumerates everything the visual + copy pillars do **not** yet cover for a *total* tray-resident WPF rebrand. Every claim is grounded in the current `src/SenseEase.App` source. Priorities: **P0** = ships-broken or brand-breaking if skipped; **P1** = visible quality gap an Alpha tester will hit; **P2** = polish/edge-hardening deferrable inside Alpha.

### 0. Foundational gaps that block every other surface

These are not "surfaces" — they are the substrate the whole rebrand assumes exists but doesn't.

| Pri | Gap | Evidence | Spec needed (one line) |
|-----|-----|----------|------------------------|
| **P0** | **No resource dictionary / theme system at all.** `App.xaml` `Application.Resources` is empty (`App.xaml:5-10`); every window redeclares its own `Card`/`PrimaryBtn`/`RoundBtn` styles inline (`MainWindow.xaml:21-107`, `CalibrationCardView.xaml:22-116`, `NotificationCardWindow.xaml:16-67`). | Define `Themes/Calm.Light.xaml`, `Calm.Dark.xaml`, `Calm.HighContrast.xaml` as merged `ResourceDictionary` in `App.xaml`; every token (`AccentBrush=#3A6EA5`, `CanvasBrush=#F6F7F9`, `ClayHigh=#B65C4E`, type ramp, radius 8/12/16, 3-level shadow) is a `DynamicResource` key so theme swap is live. |
| **P0** | **No runtime theme switch / Reduce-Motion / High-Contrast plumbing.** Zero hits for `HighContrast`/`ReduceMotion` in app code; `SystemParameters.HighContrast` never read. The typing-dots `Storyboard` (`MainWindow.xaml:717-743`) and any future animation have no master kill-switch. | Spec a `ThemeService` (Light/Dark/HighContrast/System) + `MotionService.ReduceMotion` master flag wired to `SystemParameters.HighContrast` and to a Settings toggle; all `DoubleAnimation`/`Storyboard`/transition durations route through `120/200/280ms` motion tokens that collapse to `0` when Reduce-Motion is on. |
| **P0** | **No localization layer.** Strings are hardcoded bilingual literals `"中文 / English"` inline everywhere (e.g. `MainWindow.xaml:186-189`, `TrayIconService.cs:118-124`, `SettingsContent.xaml:44-272`, `NotificationCardWindow.xaml:343-357`). No `.resx`, no `ResourceManager`. | Spec a `Strings.zh-Hant.resx` / `Strings.en.resx` keyed-resource layer + a language picker in Settings; define whether Alpha ships *stacked bilingual* (zh primary, en secondary line) as the canonical pattern or single-locale, and the exact two-line type treatment (13px primary / 11px secondary, secondary at `TextSecondary` token). |
| **P0** | **No per-monitor DPI awareness.** No `app.manifest` is tracked (`git ls-files` shows only `.csproj`); csproj has no `<ApplicationManifest>`/dpiAwareness. WPF default = System-DPI → blurry on 125/150% secondary monitors and on DPI change. | Add `app.manifest` declaring `PerMonitorV2`; spec how the 7 topmost/`Maximized` windows (Overlay, Bar, Card, Spike, Validation, Demo) recompute geometry on `DpiChanged` / `DisplaySettingsChanged`. |

### 1. States not designed for any surface (empty / loading / error / permission / offline)

The pillars cover the *happy path* only. None of these states have visual or copy specs.

| Pri | Surface · State | Evidence | Spec needed |
|-----|-----------------|----------|-------------|
| **P0** | **AI Assistant — LLM offline / model-not-found / loading.** `MistralChatbotService.LoadError` exists (`MistralChatbotService.cs:238,316`) and silently falls back to a lexicon (`:378`); the chat tab only has a generic `StatusText` (`MainWindow.xaml:882`). No "model is loading", "AI offline — using simple rules", or retry affordance. | Spec 3 chat banner states: *Loading model* (skeleton + calm spinner), *Offline/degraded* (clay-tinted inline notice + "simple replies" copy, never an error-red toast), *Ready*. Define exact copy + which token. |
| **P0** | **Webcam permission-denied / no-webcam (head-tracking).** Head-tracking toggle in `SettingsContent.xaml:119-158` has no failure UI; `SidecarHeadPoseReader` can fail to spawn with no user-facing state. | Spec permission-denied and no-camera-found states for the head-tracking row: inline status pill ("Camera unavailable — mask won't follow gaze"), auto-revert toggle, link to OS privacy settings. |
| **P0** | **Screen-capture (WGC) permission/unsupported.** `WgcSensingProvider` failures route only to `wgc_diag.txt` (`TrayIconService.cs:96`); there is **no user-visible** "sensing unavailable" state. The app's CORE input can silently die. | Spec a tray-badge + first-run notice for "Screen sensing unavailable on this PC/GPU" with the fallback (cursor-velocity-only) clearly stated. |
| **P1** | **Dashboard / Insights — empty & first-session states.** Event Log (`MainWindow.xaml:455-474`), Session Summary (`:593-634`), and the live chart (`:574`) render bare/zeroed with no empty-state copy on a fresh install. | Spec empty-states: "No events yet — SenseEase is watching quietly"; zero-value session cards; chart placeholder. |
| **P1** | **Demo video — load failure / no codec.** `DemoVideoWindow.xaml:37-42` has a bare red `ErrorText` (`#FF6B6B`) with no copy and no recovery action. | Spec the demo-failure card: calm copy, "Skip to calibration questions" fallback button, retry. |
| **P2** | **Offline / first-run-with-no-profile in chat & calibration.** `MockInferenceEngine.cs:106` treats missing profile as "upgrade scenario" silently. | Spec the "no calibration yet" assistant greeting + CTA into calibration. |

### 2. Tray, chrome & system-integration surfaces

| Pri | Surface | Evidence | Spec needed |
|-----|---------|----------|-------------|
| **P0** | **Tray context menu — not in the file map / pillars.** Built imperatively in `TrayIconService.cs:103-148` with raw `MenuItem`s, hardcoded bilingual headers, no icons, no theming, default OS menu chrome. Tooltip is the bare string `"SenseEase"` (`:138`). | Spec the menu: ordered items + glyphs, the dynamic status line (`VIMS Tier N · 舒緩/中度/高壓`, `:269`) reworded to plain-language comfort state, themed flyout (or accept native), and a richer tooltip showing current comfort state. |
| **P0** | **Native `MessageBox.Show` dialogs are unstyled OS chrome.** Restart confirm, restart-fail, autostart-fail, and vestibular-safety warning all use Win32 `MessageBox` (`SettingsContent.xaml.cs:161,205,260,277,301`). These break the "quietest app on the machine" promise with system-red error icons. | Spec a branded in-app `CalmDialog` (modal sheet) to replace all 5 `MessageBox` call-sites; define copy, button order, and the safety-warning variant (clay, not alarm-red). |
| **P0** | **Restart-required flow.** Currently a yellow badge-button `"需重新啟動 / 立即重啟"` (`SettingsContent.xaml:134-146`) using off-palette `#FFF4D6/#E5A841/#8B5A00`, plus a `cmd /c timeout 2 & start` handoff (`SettingsContent.xaml.cs:288-295`). | Spec a calm inline "restart to apply" affordance using comfort tokens, and the confirm dialog as a `CalmDialog`; define the 2s-handoff loading state (no console flash, "Restarting SenseEase…"). |
| **P1** | **Toasts / transient confirmations.** No toast system exists; the Card's `ChatStatusText`/`StatusText` (`NotificationCardWindow.xaml:422`, `MainWindow.xaml:882`) is the only ack channel. Actions like "Muted 6h", "Preset applied", "Calibration saved" have no consistent confirmation. | Spec a single bottom-anchored `CalmToast` component (auto-dismiss, Reduce-Motion aware, max 1 visible) and the catalog of toast events + copy. |
| **P1** | **Tooltips are inconsistent and bilingual-crammed.** Some controls have rich tooltips (`MainWindow.xaml:311`, `NotificationCardWindow.xaml:328`), most have none; `AutomationProperties` = 0 occurrences anywhere. | Spec a tooltip + `AutomationProperties.Name/HelpText` standard for every interactive control (screen-reader is currently entirely absent). |
| **P2** | **`BarStubWindow.xaml` / `ValidationModeBanner.xaml` — unassigned dev surfaces still shippable.** `BarStubWindow.xaml:20` paints `#FFFF0000` adversarial stimulus; `ValidationModeBanner.xaml:21-36` uses `Consolas` monospace engineering text on `#E6201418`. Neither is in the pillar file map. | Decide: gate behind a dev flag for Alpha, OR rebrand the validation banner to calm tokens + plain copy. Confirm `BarStubWindow` is excluded from release builds. |

### 3. The core protective field & ambient surfaces

| Pri | Surface | Evidence | Spec needed |
|-----|---------|----------|-------------|
| **P0** | **FOV Overlay escape-hatch / peek affordance is invisible & reportedly broken.** Escape hatch is a Settings toggle (`SettingsContent.xaml:191-209`) triggered by double-tap Ctrl — but the dev note at `SettingsContent.xaml:221-234` states **both** hotkeys are "non-functional in current builds." There is zero on-overlay visual cue that a peek is available or active. | Spec a discoverable, *always-visible* peek affordance (e.g. a small corner handle or a documented gesture with on-screen hint) + the visual treatment of the 30%-transparency peek state + its 3s countdown, and confirm the hotkey is fixed for Alpha. |
| **P1** | **FOV mask + grid have no rebrand spec for the mask color/falloff vocabulary.** `OverlayWindow.xaml` is an empty shell (`FovMask` rectangle painted in code-behind); grid lives in `GridCanvas`. Mask color/opacity ramp is not in the token set. | Spec the mask gradient stops, the 3-zone radii, grid stroke token, and how high-contrast theme affects the mask (it must NOT invert). |
| **P1** | **Ambient bar tier colors are code-owned and off-palette.** `PersistentBarWindow.xaml:30` default `#26666666`; tier brushes set imperatively in `TierColorPalette` / `PersistentBarWindow.xaml.cs`. Spike popup uses alarm scheme `#CC1A1A1A` + `#FF9B6B` (`SpikePopupWindow.xaml:13,24`). | Spec the bar's 3 comfort-tier fills from the new tokens (Steady/Rising/High), and rebrand the Spike popup off the dark-alarm palette to the Calm dark surface. |
| **P2** | **Tray icon glyphs per tier.** `TierColorPalette.IconForTier_Win` generates HICONs (`TrayIconService.cs:147,250`) — colors not in the token set; colorblind-safe shape differentiation absent. | Spec colorblind-safe tray icon set (shape + color per tier, not color-only). |

### 4. Accessibility, input & internationalization

| Pri | Gap | Evidence | Spec needed |
|-----|-----|----------|-------------|
| **P0** | **Color-only status everywhere.** Status conveyed purely by dot fill: `MainWindow.xaml:255-279` (`#B0BFD8`→`#E54747`), `:543-557` (`#F0832E`→`#2BA672`). Fails WCAG 1.4.1. | Spec icon/text+color status tokens (Steady/Rising/High get glyph + label, never bare dot). |
| **P0** | **Below-floor contrast on essential text.** `#7B8DB0` is the default `CardLabel` foreground (`MainWindow.xaml:75`) used for live metric labels and status lines on `#E2ECFF`/white; `#9AB6E0` (`:194,815,845`) used for secondary copy. Both fail AA for body text. | Spec replacement: secondary text token at ≥4.5:1 on Canvas; AAA where it's body. |
| **P1** | **No keyboard-shortcut model or focus-trap spec.** Only `Return`→Send key bindings exist (`MainWindow.xaml:904`); modal-less Card/Spike/Calibration have no Esc-to-dismiss, no Tab order, no focus restoration. | Spec global + per-surface shortcuts, focus traps for the Card/Spike/Calibration/CalmDialog, and visible focus-ring token. |
| **P1** | **Body-size floor violations.** Many essential labels at 10-12px (`CardLabel`=12px `:76`; chat timestamps/author 10px `:693,705`; Card chat status 10px `NotificationCardWindow.xaml:425`). North-star floor is 14px body. | Spec which strings are "body" (→14px) vs "metadata/caption" (→12px min) and apply ramp. |
| **P2** | **RTL / locale formatting.** No `FlowDirection` handling anywhere; numbers/dates use invariant `StringFormat`. | Spec whether Alpha commits to RTL (likely defer) and the locale number/date formatting policy. |

### 5. Lifecycle: first-launch, upgrade, uninstall

| Pri | Gap | Evidence | Spec needed |
|-----|-----|----------|-------------|
| **P0** | **First-run welcome banner is the known dark-card-on-light-dashboard clash.** `MainWindow.xaml:177-205` paints `#0E1320` dark card with `#3D5AFE`/`#FF9B6B`/`#9AB6E0` on the `#E2ECFF` dashboard; same dark scheme on the calibration-invite banner (`:831-863`). | Spec the rebuilt onboarding as a proper first-run *sheet/flow* in Calm light tokens (replace banner), including demo-video framing copy. |
| **P1** | **First-launch vs upgrade are indistinguishable in UX.** First-run gated only by `first-run-complete.flag` (`AppConfig.cs:167`); `UserPreferences`/profile schema-version upgrade is silent (`UserPreferences.cs:54-212`, `FileHabituationProfileStore.cs:10-13`). No "what's new" or migration notice. | Spec an upgrade path: detect version delta, show an optional calm "What's new in Alpha" card; define behavior when schema is from a *newer* build (currently → silent defaults reset, data-loss risk). |
| **P1** | **Uninstall / data-deletion UX missing.** `AutoStartRegistry` mentions per-user cleanup (`AutoStartRegistry.cs:16`) but there is no in-app "Delete my data" action and no spec for what the installer removes (`%LocalAppData%\SenseEase` profile, calibration, prefs, logs). For a privacy-positioned app this is a trust gap. | Spec a Settings → "Reset / Delete all SenseEase data" action (with `CalmDialog` confirm) listing exactly what's deleted, plus the uninstaller's data-handling copy. |

### 6. Multi-monitor, DPI & dark-mode edge cases

| Pri | Gap | Evidence | Spec needed |
|-----|-----|----------|-------------|
| **P0** | **Topmost windows assume a single primary monitor.** Card/Spike/Validation/Bar all use `WindowStartupLocation="Manual"` with code-computed positions (`NotificationCardWindow.xaml:14`, `SpikePopupWindow.xaml:12`, etc.); no per-monitor placement rule. Overlay/Demo are `WindowState="Maximized"` (single screen). | Spec multi-monitor placement: which monitor each surface targets, behavior when the user has 2-3 monitors, and overlay coverage (all screens vs active screen). |
| **P1** | **Dark-mode is unhandled — the app is light-only with stray dark cards.** Two clashing palettes confirmed (light console `#E2ECFF` vs dark popup `#0E1320`/`#CC1A1A1A`). No OS dark-mode detection. | Spec full dark theme token mapping for all 12 surfaces + OS-theme-follow option; resolve the dark/light banner clash. |
| **P2** | **DPI 125/150 layout reflow.** Fixed `Width="480"` Card (`NotificationCardWindow.xaml:12`) and `MaxWidth="360"` Spike (`SpikePopupWindow.xaml:14`) with grid-packed bilingual buttons (`:340-364`) risk clipping at 150% — historically the Power-Save label *was* clipped (note at `:318-322`). | Spec min/max widths + wrap behavior at 150% for Card and Spike; verify the 4-button discomfort row (`NotificationCardWindow.xaml:242-305`) reflows. |

### 7. Surfaces / copy tracks in the file map not yet owned

| Pri | Item | Evidence | Spec needed |
|-----|------|----------|-------------|
| **P1** | **CalibrationCardView duplicated 5× template collapse.** Q3 scale repeats a ~30-line `RadioButton.Template` five times (`CalibrationCardView.xaml:221-354`); the card also redeclares MainWindow's styles locally (`:24-28` comment admits it). | Spec the single `SegmentedScale` control + shared style dictionary so the duplication is removed in the rebrand, not preserved. |
| **P1** | **Header / mode badge copy is engineering telemetry.** Header subtitle "OS-level Preventive Visual Comfort Prototype" (`MainWindow.xaml:124`); mode badge cycles `DEMO MODE`/`SCREEN CAPTURE` (`:134-138`). Dashboard headline is OFI/VIMS Pressure/Leaky Integrator/FMS (`:213-352`). | Spec the plain-language comfort-state headline that replaces the telemetry grid, and where (if anywhere) raw telemetry lives (collapsed "advanced" disclosure). |
| **P2** | **Habituation engine copy.** Inline `再適應 Habituation Engine` + `HabituationButtonText` binding (`MainWindow.xaml:414-431`); no rebrand copy for progress narrative. | Spec the weeks-long tolerance-building progress language + the toggle's on/off copy. |
| **P2** | **Driver/Passenger auto-mode copy.** Surfaced only as `AutoModeText` binding (`MainWindow.xaml:561`) with no explanation of what the mode means to a user. | Spec user-facing names + one-line explanations for the auto-mode states. |

### Suggested sequencing

**P0 cluster (do first, unblocks all):** ① resource-dictionary + token system, ② theme/Reduce-Motion/High-Contrast service, ③ localization layer, ④ DPI manifest, ⑤ `CalmDialog` (kills all `MessageBox`), ⑥ the offline-LLM / webcam-denied / WGC-unavailable state specs, ⑦ first-run onboarding rebuild, ⑧ escape-hatch fix + visible peek affordance, ⑨ color-only-status and contrast remediation.

**Relevant files for the rebrand owner:**
`c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\App.xaml` (empty resources — token home),
`...\MainWindow.xaml`, `...\Views\NotificationCardWindow.xaml`, `...\Views\CalibrationCardView.xaml`, `...\Views\SpikePopupWindow.xaml`, `...\Views\PersistentBarWindow.xaml`, `...\Views\OverlayWindow.xaml`, `...\Views\DemoVideoWindow.xaml`, `...\Views\SettingsContent.xaml`, `...\Views\ValidationModeBanner.xaml`, `...\Views\BarStubWindow.xaml`,
`...\Services\TrayIconService.cs` (tray menu/tooltip), `...\Views\SettingsContent.xaml.cs` (MessageBox call-sites :161/:205/:260/:277/:301), `...\Configuration\AutoStartRegistry.cs` (uninstall/data scope), `...\SenseEase.App.csproj` (no manifest).
