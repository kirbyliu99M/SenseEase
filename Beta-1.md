# SenseEase — Beta-1 Upgrade Plan

**Status:** ACTIVE (current plan, from 2026-06-14). Supersedes [Improvement_plan_BETA.md](Improvement_plan_BETA.md) (now legacy).
**Inputs:** the max-effort beta-gate review ([docs/BETA_GATE_REVIEW_2026-06-14.md](docs/BETA_GATE_REVIEW_2026-06-14.md) — 64 findings: 1 blocker, 21 high, 21 medium, 21 low; 59 confirmed) + a 6-theme web-grounded research dossier.
**Decision authority:** Kirby, 2026-06-14. New ratified decisions **D-16…D-21** below carry the same weight as D-1…D-15 (recorded in the legacy plan §1.5).

Expert testing passed; the app is cleared to move toward Beta. This plan defines the path to get there.

---

## 0. Upgrade path (Kirby's order)

```
Phase 0 — Fix bugs            (stabilize: blocker + protection-integrity cluster; no new features)
Phase 1 — Shared foundation   (one EventLog schema + foreground-app capture + 5★/FMS rating LIVE → start collecting)
Phase 2 — Themes 3 → 4 → 2    (overlay system-UI · head-tracker stability · per-app OFI attribution)
Phase 3 — Theme 5 export      (de-identified clinical export + analysis — needs accumulated data)
Phase 4 — Theme 1 grid        (content-adaptive grid/mask — LAST, highest QA scrutiny)
```

**Why the FMS rating moves into Phase 1 (the one refinement to "themes 2-4 then 1"):** the "70%" claim is being replaced by a per-user *measured* comfort delta (protection ON vs OFF). That contrast data takes **weeks to accumulate** — it is the only clock that cannot be compressed. The rating widget is small, but it must go live early so Phase 3 has data to export/analyze. It shares the EventLog schema with Theme 2, so it is built with the foundation.

**Single highest-leverage build:** the one structured **EventLog schema** (Phase 1). It unblocks Themes 2, 5, the export goal, and starts the data clock. Build it once; Themes 2 and 5 become thin writers into it.

---

## 1. Ratified decisions (D-16…D-21)

| # | Decision |
|---|----------|
| **D-16** | **Per-app attribution granularity** = store **category + raw app original name**, **strictly on-device** — this data is **never off-device processed**. Opt-in; inherits the head-tracking privacy contract (default-OFF, single Settings toggle, explicit GDPR-Article-9 consent, H24 "clear all data" + H25 uninstall coverage). |
| **D-17** | **Clinical export** = ALLOWED, but **de-identified + aggregate/overall form + pure-numeric only**. No raw app names, no utterances ever leave the device. (Consistent with D-16: raw names live locally; only de-identified numeric aggregates export.) |
| **D-18** | **Retire the population "70% reduction" claim.** Replace with an in-app **5-star experience/comfort rating (Teams-post-call style) on the MAIN CONSOLE**, reported as a per-user measured comfort **trend + protection ON-vs-OFF delta** — never a population promise. (Rationale: two independent research dossiers found the core mechanism — dynamic FOV reduction — has peer-reviewed **null / ~30%** results; a fixed 70% is unsupported. See §4 honesty note.) |
| **D-19** | **FMS 0-20 is the internal scale**; the 5-star UI maps onto it (5★→FMS 0-2 … 1★→FMS 16-20; stars high = good = FMS low). The question is **comfort/symptom-anchored** ("how did your eyes/head feel?"), **not** generic app-satisfaction, with a **disclaimer** stating it asks about recent discomfort symptoms — so it stays an **FMS-aligned self-report** (not a claim to be *the* validated FMS instrument). Store FMS value + `source=stars` + `scaleType=fms20`; feed the dormant `UserIntensityStats.FmsAggregate` slot. Advanced view may expose a finer 0-20 slider. |
| **D-20** | **Multi-person head-tracking policy** = **largest + most-central + sticky** face wins (= the seated user). |
| **D-21** | **Daily NL→FMS analysis** = a per-day FMS *estimate* derived **on-device** from the chatbot's natural-language feedback (existing `FeedbackLexicon` severity + symptom mentions + `UserModelRetrospectiveAnalyzer`). Labeled **estimated (conversation-derived)**, kept strictly separate from the **measured (PRO)** star self-report. Secondary/corroborating only (NL has self-selection bias — users chat more when unwell), and it lets the explicit prompt fire less often. Depends on fixing the analyzer-freeze bug (review finding #16) first. |

**Non-negotiable across D-17/18/19/21 — measured vs estimated separation:** three signal tiers, labeled everywhere (schema, UI, export):
| Tier | Source | Label | Role |
|---|---|---|---|
| Primary | 5★ explicit self-report → FMS 0-20 | **measured (PRO)** | the headline for the ON/OFF delta |
| Secondary | daily NL analysis | **estimated** | corroboration; reduces prompt friction |
| Tertiary | spike_count / OFI / mask activations | **proxy (sensor)** | exposure/context |

Export presents measured and estimated **separately**; they are never merged into one number.

---

## 2. Shared infrastructure (build once, consume thin)

**A — Foreground-app capture.** The `EVENT_SYSTEM_FOREGROUND` hwnd already arrives but is discarded ([WindowsActivityFilter.cs:221](src/SenseEase.Infrastructure/Real/WindowsActivityFilter.cs#L221)). Stop discarding it → resolve PID → category + raw name (D-16). Serves Theme 2 directly and Theme 1's later content-attribution. (UWP apps are hosted by `ApplicationFrameHost.exe` — walk child windows / resolve AUMID.)

**B — One EventLog schema.** Versioned, with an `Exportable` field, `DeletableOnClear=true`, routed through `%LOCALAPPDATA%\SenseEase` so H24/H25 already cover it. Records share `ts_utc`, `context_tier`, `intervention_state` (on/off/baseline), `foreground_key`, and a `source`/`measured` dimension. This is what lets the HP-valuable join exist: *"video-type apps push this user to FMS-moderate; with the mask ON at matched tier their FMS fell X→Y."* Themes 2 and 5 write into it; do not invent three ad-hoc files.

**Privacy contract (mandatory for every new sensing feature):** default-OFF opt-in, derived-only, on-device, single Settings toggle, explicit consent, H24/H25 coverage — per [docs/HEAD-TRACKING-PRIVACY.md](docs/HEAD-TRACKING-PRIVACY.md).

---

## 3. Phase detail

### Phase 0 — Fix bugs (stabilize first)
Full list + verifier reasoning in [docs/BETA_GATE_REVIEW_2026-06-14.md](docs/BETA_GATE_REVIEW_2026-06-14.md). Fix the "protection silently fails" cluster first (most dangerous for a clinical tool):

- **🔴 BLOCKER #1** — Resolution/DPI/orientation change freezes WGC sensing to OFI→0 and the liveness watchdog is blind (keys off `_lastPollAt`, not `_lastFrameProcessedAt`) → VIMS protection silently off forever. [WgcSensingProvider.cs:830](src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs#L830)
- **#5** Vestibular personalization never reaches actual protection (mask/FOV/tier still use hardcoded `TriggerPressure=85`). [MockInferenceEngine.cs:1959](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs#L1959)
- **#7** SENSITIVE-profile silent Band-DOWN weakens protection (selector flag is ON in beta). [DeterministicSetSelector.cs:66](src/SenseEase.Core/AdaptiveFit/DeterministicSetSelector.cs#L66)
- **#10** Face-loss still reports `Healthy`; **#3** null-pose center-snap; **#4** stale-active mislabel; **#8** warmup OFI baseline pollution.
- **#16** RetrospectiveAnalyzer permanently freezes after trim (blocks D-21); **#18** Reset-preferences leaves the autostart registry value; **#9** orphan sidecar process on Dispose timeout; **#21/#22** installer integrity (python312.dll uncovered) + sidecar hash skip; **#14** zh grid-negation bypass; **#15** discomfort fades the grid instead of deepening it.

Gate (full test suite) after each cluster; UVHS for any visual change.

### Phase 1 — Shared foundation + rating live
- Build **EventLog schema** (Shared Infra B) and **foreground-app capture** (Shared Infra A).
- Ship the **5★/FMS comfort rating** on the main console (D-18/D-19) with the symptom-anchored disclaimer; feed `FmsAggregate`; **start accumulating ON/OFF contrast data**.
- Stand up the **daily NL→FMS estimate** (D-21) once finding #16 is fixed.

### Phase 2 — Themes 3 → 4 → 2
- **Theme 3 (overlay = system UI), ~15 lines, ready-now:** add `WS_EX_TOOLWINDOW` + `WS_EX_NOACTIVATE`, `ShowActivated="False"`; optional `WDA_EXCLUDEFROMCAPTURE` (keeps the dimming mask out of screenshots/recordings/Recall). Fixes the Alt-Tab phantom + focus-steal (findings #2/#19/#20). **Honest ceiling:** a DWM-composited topmost window can never outrank true exclusive-fullscreen or MPO hardware-overlay video (the new Windows 11 Media Player fullscreen case) — document it; UIAccess super-topmost would need code-signing + Program Files and still loses to MPO.
- **Theme 4 (head-tracker stability):** `RunningMode.IMAGE → VIDEO` + monotonic timestamps (halves the ~41% dropout), `num_faces=3` + `select_primary_face()` per D-20, per-frame try/except, exposure hardening. Verify via the `--diag` CSV (dropout 0.59→>0.95), not eyes. Folds in findings #3/#4/#10/#11/#12/#13.
- **Theme 2 (per-app OFI attribution):** on the foundation; category + raw name on-device (D-16); explicit first-run consent.

### Phase 3 — Theme 5 export + analysis
De-identified, aggregate, pure-numeric HP export (D-17): baseline vs outcome FMS, ON/OFF contrast, exposure dose, habituation-offset trajectory; measured and estimated presented separately. Needs the data accumulated since Phase 1.

### Phase 4 — Theme 1 grid/mask content-adaptivity (LAST)
Coarse spatial OFI map (block-wise MAD in the existing inner loop — near-free) → regional mask depth + grid presence. **Highest QA scrutiny:** most likely to re-introduce the trembling the overlay just eliminated — route through the existing quantize/hysteresis/snap/EMA gates, default-OFF flag, UVHS per scenario. Literature does **not** guarantee efficacy (null results exist) — ship beta-flagged-experimental, validate per-user.

---

## 4. Clinical-honesty note (the "70%")

Two independently-researched dossiers converged on the same evidence: **dynamic FOV reduction — SenseEase's core mechanism — has at least one peer-reviewed study showing no significant symptom reduction** (Springer VR 2024, doi:10.1007/s10055-024-01071-3; arXiv 2403.07992), and analogous interventions cluster around **~30%**, not 70%. There is **no validated MCID/RCI for the SSQ**, so a fixed "70%" has no clinical-standard backing. Per **D-18**, the product reframes to a **per-user, honestly-measured** comfort delta. To substantiate even that, **patient-reported outcomes are required** (the 5★/FMS self-report is the PRO; sensor proxies and NL estimates corroborate but cannot substitute).

Scales considered (research): **FMS** (Keshavarz & Hecht 2011, single 0-20, in-exposure — chosen, D-19), MISC (0-10 anchored), SSQ (16-item pre/post, the de-facto standard but too long for repeated sampling), VIMSSQ (trait susceptibility — optional one-time onboarding stratifier).

---

## 5. Carry-over / external items (not part of the phase tree)

- **machine-4 fullscreen-MPO** (Windows 11 Media Player can't be covered) — verified as a display-pipeline ceiling, not a bug; Theme 3 documents it. Tester guidance: borderless-windowed playback.
- **Installer v0.11.6** — the current v0.11.5 installer predates the Phase-0 fixes; rebuild on Kirby's explicit confirmation (installer builds always require manual confirmation).
- **UVHS round-10 trembling verdict** — outstanding from the overlay sprint.
- **Flaky crisis-redaction file-IO test** under parallel contention — watch; fix if it bites a gate.
- **12×50 selector pillars 3+4** (heartbeat fuel FEAT-16 + active-set observability) + per-set Scale ruling — feed Theme 1.

---

## 6. Open questions for Kirby

1. Consent UX: standalone first-run prompt vs Settings-only toggle (GDPR explicit-consent for health-adjacent inference leans to the former).
2. Deliberate brief mask-OFF windows to measure the ON/OFF delta — acceptable? (Without it, only the weaker confounded pre/post design is possible.)
3. Rating prompt cadence/triggers (spike-driven vs idle nudge vs session-end) and the interruption budget vs the existing propose-confirm card.
4. EventLog retention window (default 30-day trend horizon?).
5. Theme 1 beta scope: experimental-flagged in beta, or deferred? Block-MAD map (8×4) vs cheaper center/periphery zones; content drives mask depth, grid presence, or both?
