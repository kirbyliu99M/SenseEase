# Final Spec — Pop-up Card

> Generated 2026-06-05 by the senseease-rebrand-refine workflow (8 agents, adversarially reviewed). Draft for review — no code changed, nothing committed.

I have everything I need. The existing VISUAL_SPEC draft is strong but predates the LOCKED decisions (it keeps the discomfort ramp and chat behind a disclosure). The task is to FINALIZE under the locked direction: defer chat entirely, collapse the 4-cell ramp to a single sequential ramp, single dual-language primary, and clearly mark stays-vs-goes. Let me verify one more thing — whether removing the chat input from the visual tree would break the code-behind (which references `ChatInputBox`/`ChatSendBtn`/`ChatStatusText` heavily) and whether `OnQuickDiscomfortClicked` stuffs text into `ChatInputBox`.

The code-behind (`TrySendChatAsync`, `OnQuickDiscomfortClicked`, `OnIsVisibleChanged` lines 378-386) all reference `ChatInputBox`, `ChatSendBtn`, `ChatStatusText` directly — so those named elements cannot simply be deleted from XAML without code-behind changes. This is a key migration constraint. I have enough to write the final spec.

```markdown
# Notification / Pop-up Card — FINAL Alpha-1 Spec (Quiet Guardian)  *** USER-PRIORITY SURFACE ***

> Supersedes the draft at `docs/design/VISUAL_SPEC.md:1021–1223`. That draft predates the 2026-06-05 LOCKED product decisions; it parked the chat input and the 4-cell ramp behind a "More options" disclosure. This FINAL spec applies the locked direction: the chat input is **deferred to Alpha-2** (hard-blocked on `SafetyFloor.cs`, Just-Talk decision #4), and the four-hue alarm scale collapses to a **single calm sequential discomfort ramp**. Paste this in as the locked Card section.
> Surface: `src/SenseEase.App/Views/NotificationCardWindow.xaml` (+ `.xaml.cs`). Light-themed already; this is decluttering + retokenizing + triple-coding. The only behavior changes are explicitly flagged below (BC-1…BC-4) and need product sign-off.

---

## 1. Purpose & posture

The Card is the **single moment of contact** between the silent guardian and a user who may already feel unwell. Under LOCKED decision #1 (Quiet Guardian) the app protects silently and the user rarely interacts — so when the Card *does* appear it must answer exactly one question with near-zero cognitive load:

> "Something changed on my screen — how do I make it ease, right now?"

One job: (a) name the comfort state in plain language (icon+label+color, never hue alone), and (b) offer **one effortless primary action that eases the view immediately**. The current card stacks up to six co-equal sections (`NotificationCardWindow.xaml:87–94`); that is the exact failure mode for a nauseated user. The FINAL card is **four zones at rest**, no disclosure drawer, no chat.

---

## 2. Stays vs. goes for Alpha-1 (decision table)

| Current section (file:line) | Alpha-1 fate | Rationale (LOCKED ref) |
|---|---|---|
| Title `TitleText` (`:121`) | **STAYS**, plain-language copy (BC-1) | x:Name pinned by 2 tests; telemetry demoted (Decision #1, VP-2) |
| Body `BodyText` (`:220`) | **STAYS**, paired with a `Status.Chip` | x:Name pinned; band description retained |
| Proposal panel `ProposalPanel` (`:134–217`) | **STAYS**, restyled; appears only when `ActiveProposal≠null` | Already conditional; Just-Talk proposals are the chatbot's *output* surface and remain valid in Alpha-1 (proposals can arrive from background pattern-detection, not only from a chat turn). EM5/NE27/H7 all live here — keep intact. |
| 4-hue discomfort scale (`:236–306`) | **STAYS as ONE sequential ramp** (BC-2) | Decision: "SINGLE calm sequential discomfort ramp (one hue deepening) — NOT 4 hues." Categorical alarm ramp forbidden by DESIGN_SYSTEM. |
| Primary action `EngageBtn` "啟動保護" (`:345`) | **STAYS**, becomes the lone hero "Ease my view now / 緩和畫面" (BC-3) | One clear primary action (Decision #1); retire loud orange `#FF9B6B`. |
| `OpenDashboardBtn` (`:340`), `MuteBtn` (`:356`) | **STAYS**, demoted to quiet secondaries | "snooze + open-details as quiet secondaries." |
| `PowerSaveToggle` (`:323`) | **STAYS**, quiet secondary (re-skin to a switch) | Sensing pause is a legitimate Quiet-Guardian control; keep, de-emphasize. |
| Settings gear / close (`:99–120`) | **STAYS**, icon QuietButtons w/ accessible names | A11y gap (AutomationProperties=0). |
| **Chat input row `ChatInputBox`/`ChatSendBtn`/`ChatStatusText` (`:368–430`)** | **GOES (DEFERRED to Alpha-2)** (BC-4) | LOCKED #4: "Just Talk" front door is HARD-BLOCKED on `SafetyFloor.cs` (does not exist) → Alpha-2. Alpha-1 ships Quiet Guardian. An always-visible "talk to the bot while nauseated" field contradicts minimal-cognitive-load and the deferred Just-Talk path. |

**Net at rest:** the visible target count drops from **7 interactive controls** (4 discomfort + 3 action, plus toggle + gear + close) to **the primary + 3 quiet secondaries** (dashboard, mute, power-save) + gear/close, with the discomfort ramp present as one labeled control group and the proposal panel only when a proposal exists.

---

## 3. Redesign — layout top-to-bottom

**Container.** `Window` unchanged (transparent, `Topmost`, `ShowActivated=False`, `Width=480` — positioning math at `.xaml.cs:343` depends on 480; do **not** change; `SizeToContent="Height"` stays so it reflows at text scaling). Root `Border` → `Card`: `Surface` bg, `Radius.lg` (16), `Pad.Card` (24), `Elev.3` (neutral black Y8/Blur28/0.14 — retires the bespoke `BlurRadius=14/Depth=3/Op=0.18` at `:83–84`), 1px `Separator` border only in HC. Keep `MouseMove="OnCardMouseMove"` on this root (idle-timer reset, `.xaml.cs:486`).

**Zone A — Header** (`Grid.Row=0`, keep `DockPanel`)
- `TitleText` → `Type.Heading` (16/22 SemiBold) `TextPrimary`. **BC-1 (behavior change — copy/logic, needs sign-off + test update):** drop the telemetry title. Replace the `.xaml.cs:467` format string with plain language keyed off `_lastBand`: Grey → "畫面平穩 / All calm", Amber → "視覺壓力上升 / Strain is building", Red → "正在為你緩和 / Easing your view". The `N/10` tier + raw pressure move to a `Type.Mono` 13px `TextSecondary` **sub-line only** (Decision #1 demotes the telemetry; VP-2). Power-save title/body (`ApplyContentToUi`, `.xaml.cs:446–449`) unchanged.
- Gear → Fluent `Icon.Settings` (20px `TextSecondary`); close → Fluent `Icon.Dismiss` (retire the `⚙`/`×` glyphs at `:111/:101`). Both `Btn.Quiet` icon variant, `MinWidth/MinHeight 44` (up from 22×22), `AutomationProperties.Name` = "設定 / Settings" and "關閉 / Close".

**Zone B — Proposal panel** (`Grid.Row=1`, `ProposalPanel`, conditional; the hero when present)
- Inner `Border` → `Card.Compact`: `SurfaceMuted` bg, `Radius.md` (12), `Pad` 16, 1px `Separator` — retire the second palette (`#F2F6FE`/`#C3D2EE`/`#1E5BB0` at `:137–148`).
- `ProposalHeaderText` (`:144`) → `Type.CaptionStrong` `TextSecondary` + `Icon.Lightbulb`; copy "AI 建議 / AI suggests" (retire the `U+1F4A1` emoji per CONTENT_AND_VOICE §3.2). **H7 (KEEP):** the "AI suggests:" italic prefix, the 120-char rationale truncation, and the injection-line stripping stay — that logic lives on the parse/host side feeding `ProposalRationaleText`; do not loosen it.
- `ProposalSummaryText` → `Type.BodyStrong` `TextPrimary`. `ProposalRationaleText` → `Type.Caption` (12, **up from 11**) `TextSecondary` italic.
- `ProposalSafetyNoteText` → `Type.Caption` on `ComfortHighWeak` chip w/ text `#8A3A2E` (6.0:1 AA) + `Icon.Warning` — **muted clay `ComfortHigh #B65C4E`, never alarm red** (Guardrail A5). Stays `Collapsed` by default (`.xaml.cs:163`). Locked copy: "⚠ 這項建議會把保護調得比你目前的設定低，所以先保留著。 / Holding off — that would lower your protection below what's comfortable."
- Buttons: Accept → `Btn.Primary` (`Accent` fill, copy "接受 / Accept"); Decline → `Btn.Ghost` (copy "先不要 / Not now" — softer than "拒絕"); Later → `Btn.Quiet` ("等一下 / Later"). `ProposalExpiryText` → `Type.Caption` (12, **up from 10**) `TextSecondary`.
- **NE27 (KEEP + verify):** on expiry, disable **all three** proposal buttons and null the tombstone. The current expiry branch (`.xaml.cs:202–204`) disables Accept + Later but **leaves `ProposalDeclineBtn` enabled** — NE27 fix must add `ProposalDeclineBtn.IsEnabled = false;` (and null `_activeProposal`) in that branch. This is a tracked Alpha-1 fix (`ALPHA_HANDOVER.md:211`); the restyle must not regress it. Decline-on-expiry currently fires `ProposalDeclined` + collapses the panel (`.xaml.cs:242–248`) — keep disabled.

**Zone C — Comfort state** (`Grid.Row=2`, wraps `BodyText`)
- One `Status.Chip` (triple-coded) above `BodyText`. Variant by `TierColorPalette.BandForTier`: Grey → Steady (`Icon.Checkmark`, `ComfortSteadyWeak` bg / `#1F5249` text), Amber → Rising (`Icon.AlertTriangle`, `ComfortRisingWeak` / `#7A4E16`), Red → High (`Icon.ShieldCheck`, `ComfortHighWeak` / `#8A3A2E`). **Icon + label + color — never hue alone** (D2; fixes the color-only-status defect). `BodyText` keeps the exact `DescriptionForBand` strings (`.xaml.cs:478–484`) as `Type.Body` `TextPrimary` (those strings are not test-pinned but are the user-facing body — leave byte-identical unless flagged).

**Zone D — Primary action** (`Grid.Row=3`) — *the one effortless action*
- A **single full-width** `Btn.Primary`: **"緩和畫面 / Ease my view now"**, wired to the existing `EngageClicked` event. **BC-3 (copy/skin change):** keep `x:Name="EngageBtn"` and `OnEngageClicked` (`.xaml.cs:533`) **verbatim**; change only `Content` + style. Retire `#FF9B6B`/`#101418` (`:348–349`) → `Accent` fill, `TextOnAccent`, `Radius.sm`, `Elev.1`, `Type.BodyStrong`, `MinHeight 44`. This is the visually dominant element. (Host action unchanged: `SetSensingModeToWgc()` + `OverlayVisible=true`, `MainWindow.xaml.cs:850–859` — names what it does, matching the "Ease" copy; VP-3.)

**Zone E — Quiet secondaries** (`Grid.Row=4`) — snooze + open-details
- `OpenDashboardBtn` → `Btn.Quiet` "開啟主視窗 / Open dashboard" (retire `PrimaryDeepBlueButtonStyle`; keep x:Name + `OnOpenDashboardClicked`).
- `MuteBtn` → `Btn.Quiet` "6 小時內安靜 / Quiet for 6 hours" (keep x:Name + `OnMuteClicked`; copy must **not** imply persistence across restarts — mute is in-memory + band-scoped, `NotificationCardController.cs:63`, Guardrail D4).
- `PowerSaveToggle` → `Toggle.Switch` (a `ToggleButton`-derived control — see migration pin 2) labeled "暫停感測 / Pause sensing" + `Icon.Power`, `TextSecondary`. Keep x:Name + `Checked`/`Unchecked` → `OnPowerSaveToggled` (`.xaml.cs:329–330`). Tooltip stays the sensing-pause framing (`.xaml.cs:328`), never "battery" (D4 intent).

**Zone F — Discomfort ramp** (`Grid.Row=5`) — *one calm sequential ramp*
- Eyebrow "現在感覺如何？ / How does it feel?" → `Type.CaptionStrong` (12, **up from 11**, retire `#5A6478` at `:238`) `TextSecondary`.
- Replace the **four-hue alarm grid** (`:242–305`) with one `Scale.Segmented`: a 4-cell `ItemsControl` over a static model `{ Tag 1–4, Label, Icon }`. **BC-2 (visual change; no engine impact):** all four cells share **one hue, deepening by step** — `ComfortSteadyWeak` → `ComfortRisingWeak` → a mid step → `ComfortHighWeak` **as a sequential ramp of a single comfort hue family** (deepening fill + deepening text), **not** the current 4 categorical hues (blue/amber/orange/clay at `:253/267/281/295`). Cells get position + label + a small fill-step **icon** (e.g. one→four filled dots, or a single waveform deepening) so the ramp is **colorblind-safe by position+label+icon**, not hue. Copy (CONTENT_AND_VOICE §3.2): 一點點 / A little · 有一些 / Some · 蠻明顯 / A lot · 受不了 / Too much. `Radius.sm`, `MinHeight 44`, rendered as a keyboard radio-group.
- **Behavior pin:** each cell must still carry `Tag="1".."4"` and route `Click`→`OnQuickDiscomfortClicked`; that handler reads `btn.Tag` (`.xaml.cs:663–670`) and stuffs `QuickDiscomfortPhrases[severity]` (`.xaml.cs:648–655`) through `TrySendChatAsync`. The 1→4 severity→phrase mapping and the `ApplyUserFeedback`/`NotifyDiscomfort` chain it ultimately drives must stay intact (see "Chat-defer caveat" — the discomfort ramp depends on the same send path the chat box used).

---

## 4. ASCII wireframe (FINAL — rest state)

```
┌────────────────────────────────────────────────────────┐  Card: Surface · Radius.lg(16) · Pad.Card(24) · Elev.3
│  視覺壓力上升 / Strain is building   [⚙ 設定] [✕ 關閉]   │  Zone A  Title=Type.Heading TextPrimary
│  第 6 級 · 壓力指數 41                                   │          sub-line=Type.Mono 13 TextSecondary (demoted telemetry)
│                                                        │
│  ┌─ 💡 AI 建議 / AI suggests ──────────────────────┐   │  Zone B  ProposalPanel — only if ActiveProposal≠null
│  │ 把保護稍微調強一點                                │   │          Card.Compact · SurfaceMuted · Radius.md
│  │ AI suggests: …(≤120 chars, italic)   剩 01:45   │   │          summary=BodyStrong · rationale=Caption italic (H7)
│  │ [ 接受 / Accept ]  先不要 / Not now   等一下      │   │          Accept=Primary · Decline=Ghost · Later=Quiet
│  └─────────────────────────────────────────────────┘   │          (all 3 disable on expiry — NE27)
│                                                        │
│  ▲ 視覺壓力上升 / Rising                                 │  Zone C  Status.Chip (icon+label+color, ComfortRising)
│  偵測到中度視覺壓力。建議短暫休息或暗化螢幕。              │          BodyText=Type.Body TextPrimary (DescriptionForBand)
│                                                        │
│  ┌──────────────────────────────────────────────────┐ │  Zone D  PRIMARY — full-width · Accent · MinHeight 44
│  │            緩和畫面 / Ease my view now             │ │          → EngageClicked  ← the hero
│  └──────────────────────────────────────────────────┘ │
│                                                        │
│  開啟主視窗      6 小時內安靜      [⏻ 暫停感測 ]          │  Zone E  three Btn.Quiet / Toggle.Switch · TextSecondary
│                                                        │
│  現在感覺如何？ / How does it feel?                      │  Zone F  eyebrow Type.CaptionStrong
│  ┌────────┬────────┬────────┬────────┐                 │          Scale.Segmented — ONE deepening hue
│  │ ·一點點 │ ··有一些│ ···蠻明顯│····受不了│                 │          position + label + dot-fill icon (colorblind-safe)
│  └────────┴────────┴────────┴────────┘                 │          each cell Tag=1..4 → OnQuickDiscomfortClicked
└────────────────────────────────────────────────────────┘
   ── REMOVED for Alpha-1 (deferred to Alpha-2 / SafetyFloor) ──
   ✗ inline chat box "跟 SenseEase 說話"   ✗ 送出 button   ✗ ChatStatusText
```

---

## 5. Motion & Reduce-Motion (mask-only motion held)

- **Show:** fade-in 0→1 over `Dur.base` (200ms; was 220 hardcoded `.xaml.cs:370`), `Ease.calmOut`. No slide/scale.
- **Idle collapse:** fade 1→0 over `FadeDuration` (5s, keep); reversible-on-mousemove unchanged (`.xaml.cs:486–498`).
- **Chip / segmented selection / switch thumb:** `Dur.fast` (120ms).
- **Reduce-Motion** (`manualToggle OR NOT SystemParameters.ClientAreaAnimation`): all collapse to instant via the `Dur.ui` `{DynamicResource}`. 
- **No mask-driving motion on this card.** The only routine motion in the product is the clinical FOV mask cross-fade in `OverlayWindow` (`MaskSmoothingFactor=0.25`, LOCKED #3). The comfort chip is **static** — no breathing/pulsing (Guardrail A4, LOCKED #3).

---

## 6. Accessibility (AutomationProperties currently 0 across src)

| Element | `AutomationProperties.Name` / setting |
|---|---|
| Window | "SenseEase 通知 / SenseEase notification" |
| `TitleText` | LiveSetting=Polite (announce band change on update) |
| Settings / Close | "設定 / Settings" · "關閉 / Close" |
| `Status.Chip` (Zone C) | full phrase, e.g. "視覺壓力上升，建議休息 / Rising strain, take a break" |
| Primary `EngageBtn` | "緩和畫面 / Ease my view now" |
| `OpenDashboardBtn` · `MuteBtn` | "開啟主視窗 / Open dashboard" · "6 小時內安靜 / Quiet for 6 hours" |
| `PowerSaveToggle` | "暫停感測 / Pause sensing" |
| Segmented cells 1–4 | "一點點 / 有一些 / 蠻明顯 / 受不了 不適" as one radio group |
| Proposal Accept/Decline/Later | "接受 / 先不要 / 等一下" |
| `ProposalSafetyNoteText` | LiveSetting=Assertive |

- **Focus order (tab):** Title(skip) → [Proposal Accept→Decline→Later when visible] → **Ease my view now** → Open dashboard → Mute → Pause sensing → segmented 1–4 (one radio group) → Settings → Close. Primary reachable in ≤1 tab.
- **Contrast (tokens):** `TextPrimary #1A2230`/`Surface` 15.3:1; `TextSecondary #525B6B`/`Surface` 7.4:1 (retires `#5A6478`/`#7A8499`/`#9AA3B0`); `TextOnAccent`/`Accent` 4.9:1; high chip `#8A3A2E`/`ComfortHighWeak` 6.0:1 (retires bespoke `#8C2F23`). All 10/11px captions raised to 12 floor (`:159/212/238` and removed chat 10px).
- **Colorblind-safe:** comfort = icon+label+color; discomfort = single hue + position + label + dot-fill icon. Survives HC (system colors win; meaning in icon+label).
- **Min sizes:** all targets `MinHeight/MinWidth 44` (close/gear up from 22×22); body ≥14, caption ≥12; no fixed text `Height` → card reflows, never clips, at 100–225% OS scaling.

---

## 7. Migration notes (cannot break prior works)

**Hard test pins — `tests/SenseEase.Tests/NotificationCardWindowTests.cs` (5 STA tests):**
1. **x:Names that MUST survive verbatim** (tests read `.Text`/`.Visibility`/`.IsEnabled`/`.IsChecked` and `RaiseEvent(ButtonBase.ClickEvent)`): `TitleText`, `BodyText`, `PowerSaveToggle`, `ProposalPanel`, `ProposalSummaryText`, `ProposalRationaleText`, `ProposalAcceptBtn`, `ProposalDeclineBtn`, `ProposalSafetyNoteText` (`tests:40/57–58/74/95/107–110/138/167/197–199`). Restyle freely; **renaming/removing any breaks CI.**
2. **`PowerSaveToggle` must stay `ToggleButton`-derived exposing `IsChecked`** (test `:74/77` sets `IsChecked`, expects `PowerSaveToggled`). Build `Toggle.Switch` as a styled `CheckBox`/`ToggleButton`, not a custom non-toggle control. Keep `Checked`/`Unchecked` wiring.
3. **Title-string test is touched by BC-1.** `NotificationCardWindow_SetContent_FormatsNumericTitle` asserts the exact `"VIMS 視覺壓力　第 3 級 / 10　·　壓力指數 27"` (`tests:40`). The plain-language title is a **test-touching copy change — update this assertion in lockstep, flag in the PR.** Power-save title/body test (`:57–58`) must stay byte-identical — the BC-1 copy change applies to the **normal-state** branch only (`ApplyContentToUi` `_hasContent` path, `.xaml.cs:467`), not the power-save branch (`.xaml.cs:448–449`).
4. **`ApplyTemplate()` reliance:** tests call `ApplyTemplate()` then touch named elements — keep all named elements resolvable from the template (no lazy/virtualized container that defers realization).

**Event/property contract — keep every public member the host (`MainWindow.xaml.cs`, `MainViewModel.cs`) consumes:** `EngageClicked`, `MuteClicked`, `PowerSaveToggled`, `ProposalAccepted/Declined/Deferred/Expired`, `ActiveProposal`, `ShowProposalApplyResult`, `SetContent`, `IsPowerSaveActive`, `SendChatMessage`, `HasVestibularDiagnosis`, `OnPreferencesChanged`, `IdleTimeout`, `FadeDuration`, `GetMonitorRectInPhysicalPixels`. All handler signatures unchanged.

**Chat-defer caveat (BC-4 — the load-bearing migration risk).** The chat row is being **removed from the visual tree**, but its named elements are referenced by code-behind that the discomfort ramp still uses:
- `OnQuickDiscomfortClicked` writes `ChatInputBox.Text` then calls `TrySendChatAsync` (`.xaml.cs:668–669`); `TrySendChatAsync` reads `ChatInputBox.Text`, toggles `ChatSendBtn`/`ChatInputBox.IsEnabled`, writes `ChatStatusText` (`.xaml.cs:695–735`); `OnIsVisibleChanged` resets all three (`.xaml.cs:378–386`).
- **Therefore you cannot simply delete `ChatInputBox`/`ChatSendBtn`/`ChatStatusText`.** Two safe options, both behavior-preserving for the discomfort ramp:
  - **(A, recommended) Keep the three elements but `Visibility="Collapsed"` + `IsHitTestVisible=False`** (or move them into a non-rendered `<x:Array>`-style hidden host) so code-behind keeps compiling and the discomfort ramp still routes through `TrySendChatAsync` unchanged. The user sees no chat affordance; the send pipeline is reused internally. Smallest diff, lowest risk.
  - **(B) Refactor `OnQuickDiscomfortClicked` / `TrySendChatAsync`** to call the host `SendChatMessage` directly with `QuickDiscomfortPhrases[severity]` (no `ChatInputBox` round-trip) and surface acks via a new dedicated `Type.Caption` status line under the ramp; then delete the chat box. Larger diff, removes the dead controls. **Defer the public `SendChatMessage` wiring decision to whoever owns the discomfort-send path** — note the in-card text field is the *user-typed* Just-Talk surface (Alpha-2); the *button-driven* discomfort report is Alpha-1-valid and must keep working.
- Either way, the discomfort-report path ultimately reaches `ApplyUserFeedback(fov_delta<0) → _habituation.NotifyDiscomfort` (`MockInferenceEngine.cs:292–311`). **Do not alter that mapping** — a user-tightening discomfort report is *supposed* to erode the habituation `_offset`; that is product-correct. Never call `_habituation.Reset()` for any card change (Q4.5 invariant).

**EM5 (KEEP — do not regress).** The auto-surfaced card is hidden within 500 ms by `OnForegroundCheckTick.Hide()` (`.xaml.cs:410–424`). The tracked fix (`ALPHA_HANDOVER.md:209`) gates that `Hide()` on a `_userHasInteracted` flag (set in `OnCardMouseMove`) or skips the foreground poll while `ActiveProposal != null` — so an auto-popped card on a band crossing survives long enough to be read. The restyle must not touch the foreground-poll logic; if you add the disclosure-free layout, the auto-pop scenario is the *primary* use case and EM5 must be fixed alongside or the headline feature is dead.

**Theme plumbing (LOCKED WPF pattern).** Merge `Theme/SenseEaseTheme.xaml` into **this Window's own `.Resources`** via pack URI — **never** `App.Resources` (`MainWindowSmokeTests` boots a bare `new Application()`; App-level resources never load). Keep the `Window.Resources` block; swap `PrimaryDeepBlueButtonStyle` (`:31–67`) usages for `Btn.Primary`/`Btn.Quiet`, then delete the local style. Retire the `VisualBrush` placeholder hack (`:398–412`) along with the chat box.

**Positioning / sensing invariants.** Keep `Width=480` (`.xaml.cs:343`), `SizeToContent="Height"`, `WindowStartupLocation="Manual"`, transparency/topmost flags, and `OnCardMouseMove` on the root. Removing the chat row and the disclosure **shrinks** `ActualHeight`; the card registers its rect via `GetMonitorRectInPhysicalPixels` (`.xaml.cs:741`) for the WGC exclude mask — ensure the host re-registers the exclude rect after the new (smaller) layout settles (host calls it on render). Flag to engineering: any height change must re-fire exclude-rect registration so the card stays out of the optical-flow read (Guardrail C2).

**"No orphan hex" gate.** This file carries ~40 inline hex literals; the chat removal + token migration drives them toward 0. Allowlist during migration, remove when green.

**Tests touched by this redesign:**
- `NotificationCardWindowTests.cs:40` — **WILL change** (BC-1 title string). Update assertion in lockstep.
- `NotificationCardWindowTests.cs:57–58, 74, 95, 104–113, 138, 167, 197–199` — **must stay green** (power-save override, toggle event, proposal toggle/accept/decline, blocked-apply). The restyle preserves their x:Names/events, so they pass unchanged.
- `NotificationCardControllerTests.cs` — **untouched** (pure mute logic; copy change to MuteBtn doesn't reach it).
- No other test instantiates this window.

---

## 8. Behavior changes requiring product sign-off (summary)

| ID | Change | Touches | Risk |
|---|---|---|---|
| **BC-1** | Plain-language title (drop "VIMS…壓力指數"), demote telemetry to a sub-line | `.xaml.cs:467`; **test `:40`** | Low; copy + 1 test update |
| **BC-2** | 4-hue alarm scale → ONE sequential single-hue ramp (icon+label) | XAML `:242–305` only; **no engine change** | Low; visual-only, Tag/handler contract preserved |
| **BC-3** | Primary "啟動保護" → "緩和畫面 / Ease my view now", retire orange→Accent | `EngageBtn` content/style; handler/event unchanged | Low; host action unchanged |
| **BC-4** | **Remove inline chat input** (defer Just-Talk to Alpha-2 / SafetyFloor) | XAML `:368–430`; code-behind `ChatInputBox`/`ChatSendBtn`/`ChatStatusText` refs at `:378–386, 668–669, 695–735` | **Med** — discomfort ramp reuses the chat-send path; use migration option (A) collapse-don't-delete to de-risk |

---

## 9. Recommendation

**Ship the FINAL layout above** (four zones at rest: Header → conditional Proposal → Comfort-state chip → single Primary; quiet secondaries row; one sequential discomfort ramp), with the chat input **deferred to Alpha-2** via migration **option (A) (collapse, don't delete)** so the discomfort-report pipeline keeps working with the smallest, lowest-risk diff. This satisfies every LOCKED decision: one clear primary ("緩和畫面 / Ease my view now"), one calm sequential discomfort ramp (single deepening hue, icon+label, colorblind-safe), plain reassuring EN+繁中 copy, a clean four-zone hierarchy, snooze + open-dashboard + pause-sensing as quiet secondaries, and Just-Talk chat parked behind the Alpha-2 SafetyFloor gate. It keeps EM5/NE27/H7 intact (and EM5/NE27 must be applied in the same change, since the auto-pop + proposal flows are the Card's primary scenarios), holds the mask-only-motion line, and ships with the Status tokens, AutomationProperties, and 12px caption floor the Quiet-Guardian posture demands.
```

Key files referenced: `src/SenseEase.App/Views/NotificationCardWindow.xaml`, `src/SenseEase.App/Views/NotificationCardWindow.xaml.cs`, `src/SenseEase.App/MainWindow.xaml.cs` (host wiring `:750–908`), `src/SenseEase.App/Services/NotificationCardController.cs`, `tests/SenseEase.Tests/NotificationCardWindowTests.cs`, `docs/design/VISUAL_SPEC.md:1017–1223` (draft this supersedes), `docs/design/DESIGN_SYSTEM.md` (Calm tokens), `docs/design/CONTENT_AND_VOICE.md:895–928` (copy table), `docs/design/HARD_CONSTRAINTS.md:104` (D4), `docs/ALPHA_HANDOVER.md:209–215` (EM5/NE27/H7).
