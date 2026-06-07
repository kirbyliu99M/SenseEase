# Resolution — Honest Benefit Legibility

> Generated 2026-06-05 by the senseease-rebrand-refine workflow (8 agents, adversarially reviewed). Draft for review — no code changed, nothing committed.

I have all the facts. Let me verify the exact surface for the pop-up Card title format and where a "whenever" line could live (NotificationCardWindow), and confirm the StatusPill/StatBox components exist as named in the design system. I have enough — the StatBox (§5.x ListRow/StatBox) and Card surfaces are confirmed. Let me write the decision-grade section.

Here is the decision-grade markdown section.

---

## Honest Benefit Legibility — Quiet Guardian (no dashboard, no fabricated numbers)

### 0. Problem framing (why "12 minutes of motion you didn't absorb" is banned)

A Quiet Guardian protects silently, so the user has almost no felt evidence the app is working — the classic "invisible-shield" legitimacy gap. The temptation is to manufacture a vivid efficacy number ("12 minutes of motion you didn't absorb"). That specific claim is **banned** because it is a **fabricated quasi-clinical efficacy figure**: the app never measures "motion absorbed by your vestibular system," has no dose-response model, and the phrase implies a health outcome we cannot evidence. Every benefit statement here is instead **derived only from already-persisted state**, carries **no health/efficacy claim**, and **invents no figure**.

**HARD RULE (paste verbatim into the locked doc):**
> Every number shown to the user MUST be read back from a persisted artifact under `%LocalAppData%\SenseEase\` and labelled as what it literally is (a setting, a count, a relaxation offset). No clinical, efficacy, symptom-reduction, or "motion absorbed/prevented" claim. No figure that is not a direct read of persisted state. No volatile session counter may be framed as cumulative benefit (see §2). When in doubt, describe the **mechanism**, not the **outcome**.

---

### 1. Inventory: which persisted quantities can honestly back a benefit line — and which cannot

Source of truth is `habituation_profile.json` (`AppConfig.cs:157`, schema `HabituationProfileSnapshot.cs`) and `habituation.json` (`AppConfig.cs:184`). Both are restored at construction (`MockInferenceEngine.cs:111`, `:123–142`).

**HONEST — usable (persisted, user-meaningful, no efficacy claim):**

| Quantity | Persisted field / source | Range & meaning | Honest framing |
|---|---|---|---|
| **Earned clear-zone growth** | `HabituationProfileSnapshot.HabituationOffsetPp` (`:64`) ← `HabituationEngine._offset` (`HabituationEngine.cs:59`), snapshotted via `Snapshot()` (`:424`) | 0 .. cap (cap = 30/25/15pp by profile, `HabituationProfile.cs:144,158,172`) | "Your clear zone has gradually widened by **N points** as you've stayed calm." This is literally the FOV widening the engine earned — a **mechanism** statement, not a health claim. |
| **Progress toward full re-habituation** | `HabituationOffsetPp / HabituationOffsetCapPp` → `HabituationLevel` 0–100 (`:60`, engine `HabituationLevel` `HabituationEngine.cs:100`) | 0–100 | "About **X%** of the way to where the app steps back." Honest because it is a ratio of two persisted values. |
| **Personal sensitivity scale** | `PersonalPressureScale` (`:160`), clamped [0.7,1.5] (`CalibrationProcessor.cs:25–26`) | 0.7–1.5; 1.0 = neutral | "Tuned to your **personal sensitivity** from your check-in." Descriptive only. |
| **Calibration recency / count** | `CalibrationCount` (`:123`), `LastCalibrationAtIso` (`:129`) | int, ISO date | "Last personal check-in: **{date}**." Factual. |
| **Current comfort tier (live)** | `CurrentVimsTier` (`:74`) — the single comfort scalar | 1–10 | Used for the *felt-absence* line (§3), never logged as a streak. |
| **Profile identity** | `Profile` (`:52`), `ProfileSource` (`:56`) | RESILIENT/TYPICAL/SENSITIVE | "Set to your **{profile}** comfort profile." Descriptive. |

**BANNED — cannot back a benefit line:**

| Quantity | Why it cannot be a benefit number |
|---|---|
| `CumulativePressureLoad` (`MainViewModel.cs:884,1391`) | **Volatile (in-memory only, no store)** — resets every launch; it is a rough pressure integral with no units a user understands. Framing it as "load absorbed" is exactly the banned fabrication. |
| `SessionFovIntercepts` (`MainViewModel.cs:858,1387`) | **Volatile**, per-session, counts mask rising-edges. Presenting it as "times we protected you" implies a prevented-harm efficacy claim and resets on restart → looks like regression. |
| OFI / `Pressure` / `LeakyIntegrator` / FMS / FOV radius live readouts | These are the **demoted telemetry** (LOCKED #1). They are engineering signals, not benefits, and must stay behind the Advanced/Clinician view. |
| `_demoPeakPressure*`, `_demoMaskActivations` (`MainViewModel.cs:1380–1382`) | Demo-scoped, volatile, exist only to seed a calibration preset. |
| Any "% time in tier ≤ 3 this week" | **Cannot be honestly computed:** there is **no persisted per-tier time histogram**. The event log is capped at 20 entries (`SenseDefaults.cs:24`) and stores lifecycle snapshots, not a time-weighted tier distribution. Implementing it would require **new persistence** (a behavior change — see §6) and is therefore **out of scope for Alpha-1**. Do not ship this line until that store exists. |
| `SevereStrikeCount`, `last_spike_at_iso` | Health-adjacent (Medium-high privacy class, `TECH_CAPABILITY_MAP.md §3`). Surfacing spike counts to the user reads as symptom tracking → efficacy/health territory. Keep internal. |

**Net:** in Alpha-1 the only honest, persisted, benefit-shaped quantity is **earned clear-zone growth** (`HabituationOffsetPp` + its ratio `HabituationLevel`), plus **descriptive** personalization facts (scale, profile, last check-in date). Everything that *feels* like efficacy is either volatile or unmeasured.

---

### 2. The legibility channels (NOT an always-open dashboard)

Three low-key channels, in priority order. None is a persistent panel; all respect LOCKED #1 (Quiet Guardian) and LOCKED #3 (no decorative motion).

#### Channel A — "Quiet status" line in the tray menu header (always reachable, never open)

The tray menu already paints a status header + tooltip (`TrayIconService.cs:147,156`). Add **one** descriptive line under the existing header, read once on menu-open from the restored snapshot. This is the lowest-key home for legibility: it appears only when the user deliberately opens the menu.

- **Data source:** `HabituationOffsetPp` (`HabituationProfileSnapshot.cs:64`) and `Profile` (`:52`), already loaded at construction. **No new persistence.**
- **Gate:** show the growth clause only when `HabituationOffsetPp >= 1` (a real, earned, ≥1pp change). Below that, show only the profile line. Never show "0 points."

#### Channel B — Felt-absence line inside the pop-up Card (when the Card is already up)

The Card already surfaces tier (`NotificationCardWindow.cs:467`). When it auto-collapses after 60s of calm (`:45,512`), append **one** quiet reassurance line that names the *mechanism*, not an outcome. This is the "felt-absence" framing: the user only sees it when they happened to glance at the Card and nothing bad is happening.

- **Data source:** live `CurrentVimsTier` (`:74`) for the present-tense calm state; `HabituationOffsetPp` for the optional growth tag. No new state.
- **Constraint:** static text, fades with the existing 220ms/5s Card animations (LOCKED #3 — no *new* motion).

#### Channel C — Opt-in "Whenever" reflection (manual, behind Settings; never auto-pops)

A single, opt-in screen reachable from the Card's Settings gear (`NotificationCardWindow` Settings, `TECH_CAPABILITY_MAP.md §2`) labelled "Your progress." It is the only place any number larger than a one-liner appears, and the user must navigate to it — it never interrupts. This is where MainWindow's demoted view can host it without violating Quiet Guardian (LOCKED #1: MainWindow stays instantiable; this is an Advanced-adjacent, opt-in reflection, not the always-open telemetry dashboard).

- **Data sources (all persisted, all descriptive):** `HabituationOffsetPp` + `HabituationLevel` (growth + % to step-back), `HabituationOffsetCapPp` (headroom), `Profile`/`ProfileSource`, `PersonalPressureScale`, `LastCalibrationAtIso`/`CalibrationCount`.
- **Hard exclusions on this screen:** no OFI/pressure/integrator/FMS/FOV-radius live charts (those stay in Advanced/Clinician), no spike counts, no streaks, no time-in-tier (unmeasured).

---

### 3. Copy patterns (EN + 繁中) — each with its exact data source cited

All copy is **descriptive of mechanism**. Tokens: `Type.Body` (14/20) min, headings `Type.Heading`/`TextPrimary #1A2230`, helper `Type.Caption`/`TextSecondary #525B6B` (`DESIGN_SYSTEM.md §2, §1.1`). Render via `Theme/SenseEaseTheme.xaml` pack-URI merge, **never App.Resources** (LOCKED constraint; `App.xaml` empty by design).

**Channel A — tray header line.** Source: `HabituationOffsetPp` ≥ 1 (`HabituationProfileSnapshot.cs:64`), `Profile` (`:52`). `{N}` = `Math.Round(HabituationOffsetPp)`.

- EN (growth present): `Your clear view has gently widened by {N} points since you started.`
- EN (no growth yet): `Watching quietly. Your {profile} comfort settings are active.`
- 繁中（已成長）：`自您開始以來，您的清晰範圍已輕柔地拓寬了 {N} 個刻度。`
- 繁中（尚未成長）：`安靜守護中。您的「{profile}」舒適設定已啟用。`

**Channel B — Card felt-absence line.** Source: live `CurrentVimsTier ≤ 3` (`:74`); optional growth tag from `HabituationOffsetPp`.

- EN: `Nothing needed right now — the screen's been calm. The app stays out of your way until it doesn't.`
- EN (with growth tag, only if `HabituationOffsetPp ≥ 1`): `…and your clear view is a little wider than when you began.`
- 繁中：`目前無需介入，畫面一直很平穩。在需要之前，App 會安靜地待在一旁。`
- 繁中（含成長標註）：`……而且您的清晰範圍比起初稍微寬了一些。`

**Channel C — opt-in reflection screen.** Each stat is a `StatBox`/`ListRow` (`DESIGN_SYSTEM.md §ListRow/StatBox`): number = `Type.Heading` `TextPrimary`, sub-label = `Type.Caption` `TextSecondary`. No color-only meaning (LOCKED accessibility direction).

- Title — EN: `Your progress, quietly` · 繁中：`您的進展，靜靜地`
- Growth stat. Source `HabituationOffsetPp` (`:64`).
  EN value `+{N} pts`, label `Clear view widened` · 繁中 標籤 `清晰範圍拓寬`
- Progress-to-step-back. Source `HabituationLevel` = offset/cap (`:60`).
  EN value `{X}%`, label `Toward the app stepping back` · 繁中 標籤 `距離 App 退場`
- Personal tuning. Source `PersonalPressureScale` (`:160`).
  EN `Tuned to your sensitivity` (show "more sensitive"/"less sensitive"/"balanced" by `<1.0`/`>1.0`/`=1.0`, **not** the raw 0.7–1.5 float) · 繁中：`已依您的敏感度校準`
- Last check-in. Source `LastCalibrationAtIso` (`:129`) / `CalibrationCount` (`:123`).
  EN `Last check-in: {date} ({count} total)` · 繁中：`上次檢視：{date}（共 {count} 次）`
- Mandatory honesty footer (both channels C and the doc):
  EN: `These come from your own settings on this device. SenseEase doesn't track symptoms or claim a health benefit.`
  繁中：`以上數據皆來自此裝置上您自己的設定。SenseEase 不追蹤症狀，也不宣稱任何健康療效。`

**Banned-phrase guard (lint into copy review):** reject any string containing "absorbed", "prevented", "reduced your", "motion you didn't", "symptoms", "less sick", "{N} minutes" — these are the fabricated-efficacy family.

---

### 4. Where each surfaces (explicitly NOT a dashboard)

| Channel | Surface | Trigger | Frequency |
|---|---|---|---|
| A — quiet status | Tray menu header (`TrayIconService.cs:147`) | User opens tray menu | Only on deliberate open |
| B — felt-absence | Pop-up Card, on calm auto-collapse (`NotificationCardWindow.cs:512`) | Card already visible + `tier ≤ 3` | At most once per Card lifecycle |
| C — reflection | Opt-in "Your progress" view via Card Settings gear → (may host inside demoted MainWindow Advanced-adjacent route) | User navigates in | Never auto; manual only |

No always-open window. No new tray balloon. No scheduled "weekly report" pop (that would violate Quiet Guardian and risk a recurring interruption). If a cadence is ever wanted, make it a **passive** refresh of Channel A's text on menu-open — not a notification.

---

### 5. Recommended final design

**Ship Channel A + Channel B in Alpha-1. Defer Channel C to Alpha-1.x as opt-in.** Rationale:

1. **Zero engine/persistence change.** A and B read only `HabituationOffsetPp`, `Profile`, and live `CurrentVimsTier` — all already restored at construction (`MockInferenceEngine.cs:111,123–142`). No new files, no schema bump, no behavior change, no user sign-off needed.
2. **Honest by construction.** The only number shown is the literal earned FOV offset (`HabituationEngine._offset`, `:59`) and its ratio — a mechanism, not an efficacy claim. The Q4.5 invariant is untouched: we **read** `_offset`, never write/reset it (we do not call `Reset()`, `Snapshot()` is read-only `:424`).
3. **Quiet Guardian-true.** Both channels appear only when the user has *already* chosen to look (tray menu / a visible Card). Nothing new auto-pops. Static text only (LOCKED #3).
4. **Graceful zero-state.** When `HabituationOffsetPp < 1` (brand-new user, or a user who hasn't earned a step), the growth clause is suppressed and only the descriptive profile line shows — so we never display "0 points" or imply failure.

**Channel C** stays opt-in and behind a deliberate tap so MainWindow's demotion (LOCKED #1) is respected; it's the one place richer (still descriptive) stats live, and it reuses persisted snapshot fields with no new state.

---

### 6. Behavior-change flags (require user sign-off — keep minimal)

- **NONE required for the recommended Alpha-1 scope (Channels A + B).** They are pure reads of already-persisted state.
- **`HabituationOffsetPp` is restored from `habituation.json` only as raw offset** (`HabituationEngine.Restore` `:414`, snapshot is offset+timestamp `:424`). To show "since you started," Channel A needs an **anchor date**. The honest anchor that *already exists* is `LastCalibrationAtIso`/first `CalibrationCount` increment — use it; do **not** add a new "first-run date" field unless the product owner approves (that would be a **persistence change → behavior change → sign-off**).
- **"% time in tier ≤ 3 this week"** and any streak: **BLOCKED.** Requires a new persisted time-in-tier histogram (the event log is capped at 20 and is not time-weighted, `SenseDefaults.cs:24`). Adding it is a flagged persistence/behavior change and a Medium-privacy data addition; **defer past Alpha-1** and only build if explicitly signed off.
- **Do not** persist or surface `SessionFovIntercepts`/`CumulativePressureLoad`; promoting them to persisted "benefit" counters is both a behavior change and a fabrication risk — explicitly out of scope.

---

Relevant files (absolute): `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.Core\Models\HabituationProfileSnapshot.cs` (persisted benefit fields), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.Core\Inference\HabituationEngine.cs:59,97,100,424` (`_offset` / `HabituationOffset` / `HabituationLevel` / `Snapshot`), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.Core\Inference\HabituationProfile.cs:144,158,172` (offset caps), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\ViewModels\MainViewModel.cs:858,884,1387,1391` (the volatile, NON-usable session stats), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.Infrastructure\Mock\MockInferenceEngine.cs:111,123-142,795-796` (restore + calibration-count persistence), `c:\Users\kirby\Desktop\SenseEase APP\docs\design\DESIGN_SYSTEM.md` (Calm tokens: `Type.Body`, `TextSecondary #525B6B`, `StatBox`/`ListRow`).
