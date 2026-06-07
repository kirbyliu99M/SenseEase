# Alpha‑2 — Parallel Ship Orchestration (execution blueprint)

> Generated 2026‑06‑07 from a 10‑agent code‑grounded audit of the full plan. This is the
> **collision‑safe** execution map: which waves run concurrently, who owns each hot file,
> and the build+test gates. Source of truth for the sprint.

## 0. Reality check (audit finding)
Much of Alpha‑2 is **already implemented** (W1/W2 + prior work). The audit confirms code‑complete + tested for: the **entire crisis safety‑floor** (`SafetyFloor.cs` — Simplified Han, NFKC + full‑width fold, curly‑apostrophe, `kms`/`想死`/`跳樓` false‑positive removal, `[REDACTED]` persistence, parity tests), most of **W4**, EM7/H32/H35/H36/H40 in **W7**, H19/H20/H21/H51 in **W8**, EM7/H23 in **W9**. For those, the agents **verify + add missing tests**, not re‑implement. Real *new* code is a focused subset (below).

## 1. The only two items that can't fully land today (external‑gated)
1. **H26 — code‑signing cert** (2–4 wk procurement). The `.iss` SignTool directive + signing sequence are written today; the **actual signing** runs when the cert arrives. The unsigned build is fully functional + testable now.
2. **H11 — calibration confirmation card** is *not* externally blocked — I'll design+build it today (small confirm card, existing card patterns + ui‑ux‑pro‑max + EN/繁中 via the new `{loc:T}`). No dependency.

Everything else ships today.

## 2. File‑conflict matrix (one owner per hot file — never edited concurrently)
| File | Waves | Owner → handoff order |
|---|---|---|
| `Infrastructure/Real/MistralChatbotService.cs` | W5,W6,W8,W9 | **W5** (EM6/NE3/NE6) → W6 (H9/H10/H29/H34) → W9 (H41) |
| `App/ViewModels/ChatbotViewModel.cs` | safety,W5,W9 | **safety** (floor@373, redact@379) → W5 (H6 sanitize) → W9 (delete‑data) |
| `Core/Inference/HabituationOverrideParser.cs` | safety,W4,W5 | **safety** (H7) → W4/W5 read sanitized |
| `App/ViewModels/MainViewModel.cs` | W3,W6 | **W3** (liveness) → W6 (calib‑result handling) |
| `Infrastructure/Real/SidecarHeadPoseReader.cs` | W3,W7,W8 | **W7** (H12/H13/H32/H35‑36) ; W3 recovery hooks on distinct methods |
| `Infrastructure/Nlp/FeedbackLexicon.cs` | safety,W5 | **W5** (NE4 sign fix) |
| `Infrastructure/Storage/LongitudinalObservationWriter.cs` | W2,W5 | **W2** (race) → W5 (H5 sanitize) |
| `Infrastructure/Real/CalibrationChatbotService.cs` | safety,W9 | **safety** (H4 floor@146) → W9 (H23 trim) |
| `Infrastructure/Storage/FileHabituationProfileStore.cs` | W2,W9 | **W2** (D schema guard) → W9 (H24 freetext strip) |
| `App/Configuration/AppConfig.cs` | W7,W9 | **W7** (H40) → W9 (H41 flag) |
| `installer/SenseEase-v0.11.iss` | W8,W9 | **W8** (H26/H28/H49) → W9 (H25 prompt) |
| `Infrastructure/Real/WgcDiagLog.cs` | W8,W9 | **W9** (UTC/trim) ; W8 verifies relocation |

Disjoint (free parallel): all `tests/*`, `MainWindow.xaml.cs` (W3 liveness vs W4 EM5 = distinct methods), `MockInferenceEngine.cs` (W2), `head_tracker.py` (W7), the handbooks (W9).

## 3. Execution DAG — 4 layers, build+test gate between each
**Layer 1 (~29 agents): safety‑floor‑verify + W2.0‑integrity + W3‑sensing.** Fully disjoint subsystems. New code: EM3/EM4 teardown+recovery, the liveness contract + rearming watchdog. Gate: `SafetyFloorTests`, `HabituationTests`, `WgcSensingProviderTests` + 100× Power‑Save stress.

**Layer 2 (~15 agents): W4‑ux + W5‑chatbot.** New code: NE3 network fallback, NE4 shader‑sign, NE5 collision, NE6 streaming cap, H5/H6 injection. Gate: `NotificationCardWindowTests`, `W5IntentTests`, `W5InjectionHardeningTests`.

**Layer 3 (~27 agents): W6‑loopback + W7‑sidecar + W8‑installer(code).** New code: H9 Bearer+/props gate, H10 port check, H29 `--no-webui`, H34 buffer caps, **H11 confirm card**, H13 `--model` arg, H27 provenance, H28 bloat trim, H21 direct‑Process wrapper, H33 llama bump, H47/H48 dep pins, H49/H50. H26 SignTool written (signing post‑cert). Gate: `SidecarHeadPoseReaderTests`, `SecurityHardeningTests`, smoke.

**Layer 4 (~8 agents): W9‑pii.** New: H24 "Delete all my data" button + `ClearOnDiskTrace`, H25 uninstall prompt, H41 UTC/rotation/trim, **EN handbook**. Final gate: full `dotnet test` + PII grep sweep.

**Peak concurrency:** ~40–50 agents within a layer; layers sequential (gated).

## 4. Cadence
Per wave: find → fix (file‑partitioned per the matrix; worktree isolation only where a hot file forces it) → adversarial‑verify → **scratch build + targeted tests** → **one commit per wave** (standard footer) → surface findings to Kirby between layers. No push; branch `alpha1-w2-engine-integrity`.

## 5. Locked decisions enforced throughout
Q4.5 · §6 non‑medical · grid world‑locked · AppId frozen · PII‑free logs · `head_tracker.py` core untouched · sidecar luma+landmarks only · existing tests stay green · Reading‑A sever. Adaptive‑Fit 50‑set activation is **M1‑gated, out of this sprint** (flag stays OFF).
