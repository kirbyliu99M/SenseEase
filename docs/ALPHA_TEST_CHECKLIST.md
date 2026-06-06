# SenseEase — Alpha-1 Test Checklist

**For:** Kirby · **Generated:** 2026-06-06 (overnight UI-upgrade + backlog run)
**How to use:** work top-to-bottom. Each item is `[ ]` pass / `[~]` needs-tweak /
`[x]` fail. Everything below is **committed locally only** (no remote pushes).
The build is green: `844→845/0/3` tests across the run; `MainWindowSmokeTests`
constructs cleanly.

> **Two kinds of items below:** ✅ **I can't self-certify** (your eyes / your
> machine) and 🔒 **externally blocked** (needs a cert, a binary, or a human
> reviewer). The 🔒 items are *not* faked — they're listed so nothing silently
> looks "done."

---

## 0. Run it

```powershell
# from the repo root
dotnet build SenseEase.Windows.sln
dotnet run --project src/SenseEase.App
```
If the exe is locked from a previous run: `Get-Process SenseEase | Stop-Process -Force` first.

---

## A. UI/UX visual sign-off  ✅ (your eyes)

The big change this run: a full **HP-grade "Crisp Cool — calm-tuned" palette**
(cool near-white canvas `#EEF1F5`, light chrome, white cards, one calm accent
`#3B6FC4` reserved for hero/CTA). Verify each surface reads **calm, legible,
consistent** — *"would this calm a nauseated person, or tax them?"*

### A1 — Dashboard (Home tab)
- [ ] Header is a **light bar** with a hairline divider — **no** royal-blue band; wordmark is dark, subtitle muted.
- [ ] Canvas is cool near-white; cards are white with a soft (non-blue) shadow.
- [ ] The D1 comfort **hero** reads calm; the **ComfortMeter** is near-empty at rest and fills only under real strain.
- [ ] **Profile quick-access pills** (RESILIENT / TYPICAL / SENSITIVE) appear under the hero. **Click each** → it routes through the safety-checked profile path (watch the chat/Event Log for the switch + any safety message). *(New `SelectProfileCommand` — confirm it actually switches.)*
- [ ] Data numerals are **dark** (not blue); blue appears only on the hero/primary buttons/active-tab underline.
- [ ] **Comfort chip** now has a full-colour 1.5px border (readable without relying on fill colour). *(C3-chip — new; eyeball the colour.)*
- [ ] **ComfortMeter caption** "相對於你的個人閾值 / Relative to your personal threshold" + low/high hints read clearly. *(M1 — new.)*

### A2 — Eye-Protection tab  *(H4, committed 5a5a32b)*
- [ ] Leads with the **same calm hero** as the Dashboard (not a raw-telemetry dump).
- [ ] The raw 5-column live metrics are **collapsed** inside "進階即時數據 / Advanced — live metrics"; expand once → numbers still update live.
- [ ] Interception Monitor chart + Session Summary still present and readable.

### A3 — Event Log tab  *(new tab)*
- [ ] "事件記錄 / Event Log" is now its **own tab** (moved off the Dashboard); the table still populates with live entries.

### A4 — Tabs / active state  *(M5)*
- [ ] The active tab has a **2px accent underline** (not colour-only); tab bar sits on a Surface background.

### A5 — Pop-up Card (trigger it: tray click, or Ctrl+Shift+F dev trigger)
- [ ] Card is light/calm; the **primary action** ("緩和畫面") is the calm accent; deep-blue is gone (now the shared accent).
- [ ] **Title outranks the telemetry subline** (bigger/heavier title, quiet mono subline). *(H8.)*
- [ ] **Discomfort ramp** (輕微→中等→明顯→嚴重) is now a **single-hue depth ramp + position indicator** — not four alarming hues. Still reads as increasing severity; colour-blind-safe. *(H7 — new; eyeball it.)*
- [ ] If a proposal with a safety note appears: the **safety note is a bordered chip** with a ⚠ glyph (not bare red text). *(M8 — new.)*
- [ ] Press **Escape** → card dismisses (same as ×). *(C2.)*
- [ ] Tab through the card → a clear **accent focus ring** appears on each control. *(C2.)*

### A6 — Spike popup
- [ ] Now **light** (Surface card, dark text) matching the card — **no** dark `#1A1A1A` panel; strain label is muted clay, never fire-engine red.

### A7 — First-run + calibration-invitation banners  *(M2)*
- [ ] Both are now **light Surface cards** with a hairline border + accent primary button — **no** dark navy cards on the light dashboard. *(First-run only shows on a fresh install — delete `%LOCALAPPDATA%\SenseEase\first-run-complete.flag` to see it.)*

### A8 — Settings tab
- [ ] Accent colour matches the dashboard (was royal blue).
- [ ] The toggles look like calm **switch controls** (rounded track filling accent when on), with hover/focus states + a bigger hit area. *(M16 — new.)*
- [ ] *(Deferred: section grouping M3 — still a single scroll. Flag if you want it next.)*

### A9 — Calibration flow (run the 60s demo / recalibrate)
- [ ] Accent matches the dashboard (was royal blue); Q1–Q5 cards readable.
- [ ] Q2 asks "您主要感覺到哪些**不適**" (not "症狀"). *(§6.)*
- [ ] *(Deferred: the Q3 5× template dedup + layout polish M14.)*

### A10 — Accessibility quick pass
- [ ] Turn on Windows **Reduce Motion** (Settings → Accessibility → Visual effects → Animation effects OFF) → the bar/card stop animating; clinical mask cross-fade still smooth. *(C1.)*
- [ ] **Narrator** on → tab through dashboard/card → controls are announced by name (not "button"). *(H2/L5/L4.)*
- [ ] No alarm-red anywhere; high strain is muted clay. *(H1/M7.)*

---

## B. Feature / behaviour test  ✅ (master plan §7)

- [ ] **Fresh install + onboarding** — tone reads calm; webcam-off-by-default is disclosed; first-run is the light banner.
- [ ] **60s calibration demo** — plays; suggests a personalised preset; **re-run accumulates** (doesn't wipe prior calibration).
- [ ] **Pop-up Card under real strain** — one primary action; single calm ramp; the card **survives** the foreground poll when auto-popped; all three proposal buttons **disable on expiry**. *(NE27 fixed.)*
- [ ] **Liveness / fault (D3)** — force a sensing fault → the **Faulted** state surfaces calmly and is **visibly distinct from Paused** (outline shape, not hue); self-heal fires; the fault banner now says "reconnecting"/"restored" (no "recovery"). Silent-death → visible ≤ 5s.
- [ ] **Chatbot** — a reply never shows the system-adjustment table or a half-streamed fragment as the bot's message. *(NE28 fixed.)* Underscores mid-token aren't italicised. *(NE26.)*
- [ ] **Settings persistence** — toggles persist across restart; auto-start Run-key self-heals if the exe path changed. *(NE22.)*
- [ ] **5-min 1080p video soak** — no offset wipe. *(iGPU stutter is a known Alpha-2 item — record, don't block.)*

**Safety-test discipline (hard rule):** do **NOT** type crisis / self-harm
phrases into the live chatbot. The safety floor is verified with synthetic
inputs in the dev tests only.

---

## C. Needs your decision / sign-off  ✅

- [ ] **Palette** — is "Crisp Cool — calm-tuned" right, or do you want a tweak (warmer? a different accent)? It's all tokens, so a change is a few lines.
- [ ] **D6 — Day-1 protection default-ON** *(not flipped — needs your call):* the master plan M7 wants the calm field ON by default at baseline tier 1 (0% alpha at rest). This changes resting behaviour, so I left it for you. Say the word and I'll do the atomic two-flip + the "0.00 alpha at rest" test.
- [ ] **Comfort-chip / ramp colours** — eyeball the new chip borders + single-hue ramp; tell me if any colour feels off.
- [ ] **Settings grouping (M3)** + **calibration template dedup (M14)** — larger restructures I deferred; want them?

---

## D. Externally blocked — can't be done from here  🔒

These need an external action or a machine I can't drive. Tracked in
[ALPHA_SECURITY_BACKLOG.md](ALPHA_SECURITY_BACKLOG.md) + [SECURITY.md](SECURITY.md).

- 🔒 **Code-signing cert (H26)** — procure (Azure Trusted Signing / DigiCert); long-lead. Until signed, the installer trips SmartScreen.
- 🔒 **Loopback LLM auth (H9/H10/H39)** — I can implement, but it changes process launch → needs a **manual chatbot smoke-run** on your machine to verify llama-server still starts.
- 🔒 **Single-instance mutex (H30/H31)** — needs a **second-instance launch smoke-test**.
- 🔒 **Integrity manifest (H32)**, **symlink guards (H50)** — implementable; want them in a "runtime-risk security" wave (with a smoke checklist)?
- 🔒 **Dependency bumps (H33 llama.cpp / H47 opencv / H48 pyinstaller)** — need re-downloading binaries + rebuilding the PyInstaller sidecar bundle.
- 🔒 **Installer hygiene (H25/H28/H49)** — trim ~500 MB unused backends; uninstall removes `%LOCALAPPDATA%\SenseEase\` + the HKCU Run key. Needs a clean-VM install smoke.
- 🔒 **Expert copy review** — OT 葉淨維 (mechanism copy) + ophthalmology 陳韵臻 (eye-safety copy). The doc they read (`HABITUATION-PROFILE.md §1.2`) was citation-corrected this run (commit 1137185).
- 🔒 **User test on dGPU + iGPU** — the §7 scenario suite on both hardware classes, with the benefit-legibility comprehension test (no tester may read the copy as a medical claim).

---

## E. What landed this run (newest first)

| Commit | What |
|---|---|
| `58693f9` | UI polish: H7 single-hue ramp · M8 safety chip · M1 meter caption · C3-chip borders · M16 switches |
| `1b2d860` | §6/M0 banned-phrase lint gate + cleaned user-facing copy |
| `e3527fc` | Palette-consistency sweep (card/spike/banners/settings/calibration → tokens); orphan-hex 204→151 |
| `548a3e1` | docs/SECURITY.md (M8 deliverable) |
| `1137185` | HABITUATION-PROFILE.md §1.2 citation fix (pre expert review) |
| `6aa09b5` | HP-grade "Crisp Cool" palette + Dashboard refresh (light header, Event-Log tab, profile pills) |
| `5a5a32b` | Eye-Protection tab re-tier (hero + Advanced) |
| `623f82e` | Backlog wave 5 (NE28/H44/NE18/B5) |
| `c77c96a` | Backlog wave 4 (constants/NE18/H51/H36/NE13/G-TRAIN) |
| `701bb71` | Backlog wave 3 (#024AD8→Accent, M5/H2/L5/L7/A5) |
| `6ff2aa3` | Backlog wave 2 (+48 tests, security H19/H12/H35/H36/H40) |
| `9b85a11` | Backlog wave 1 (NE3/NE17/NE19/NE20/NE22/NE24/NE27/NE29/EM5-10/EM7/H23/H34/L4) |
| `c3dccd9` `57db9de` | C3 contrast · C2 focus ring + Escape routes |

**Test count:** ~788 → **845 passing** / 3 skipped, 0 failing. Orphan-hex lint
ratcheted 247 → **150**. Banned-phrase lint live.
