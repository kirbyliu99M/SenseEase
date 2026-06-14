> **[LEGACY]** Superseded by [Beta-1.md](../Beta-1.md) (2026-06-14). Retained for historical reference / back-trace; not the current plan.

# SenseEase Alpha‑2 "Core" — Execution Plan (W3–W9)

> Generated 2026‑06‑07 from a parallel survey of the backlog docs (`ALPHA_BACKLOG`,
> `ALPHA_HANDOVER`, `ALPHA_SECURITY_BACKLOG`, `Alpha_1_MASTERPLAN`, `Cross_BACKLOG_AUDIT`,
> `CodeReview‑W1W2`, `CodeReview‑Opus4.8`) + the current engine code. This sequences the
> remaining Alpha‑1 → Alpha‑2 critical path. Nothing here is built until each step is approved.

## Status
- **Done:** W1 (crisis safety), W2 (engine‑data integrity) per git history.
- **Remaining:** W3–W9 — **~84 h** engineering, **~10 weeks** calendar (with 30–50 % churn buffer at each gate + cert lead).
- Branch stays `alpha1-w2-engine-integrity`; local commits only; no push.

## ⛔ Day‑1 wall‑clock blocker — start in parallel NOW
- **W8.0 / H26 — code‑signing certificate.** 2–4 week **external procurement lead**. Recommend **Azure Trusted Signing** (lowest cost, Microsoft‑native; DigiCert is the alternative). This does **not** violate "no packaging until Alpha‑2 ships" — it is procurement only, and packaging will stall on it later if not started now. *Action: Kirby initiates procurement; ~2 h of code/CI integration once the cert arrives.*

## 🔴 Critical safety / integrity items (highest priority inside the stage)
These are correctness/safety, not polish — do first regardless of wave order:
1. **Crisis safety‑floor coverage gaps (H1–H4):** self‑harm phrases bypass the floor for **Simplified Chinese**, **full‑width Latin**, **curly apostrophe** (U+2019 after NFKC), `kms` false‑match on "kilometres", and Han‑idiom false‑positives (`想死`/`跳樓` on benign idioms). Plus: profile‑picker short‑circuit, lexicon‑fallback path, and verbatim persistence to `chat_history.jsonl`/`model_trace.jsonl` **before** the floor runs.
2. **Per‑tick severe‑strike storm (EM1):** constant disk rewrites + event‑log flood + habituation‑offset destruction. Needs edge‑trigger redesign.
3. **Engine snapshot Save silently wipes `longitudinal_observations` (EM2).**
4. **Atomic‑write integrity (NE15–NE23, H16):** fsync‑before‑replace, first‑write TOCTOU, schema‑downgrade guard.

## 🔒 Locked decisions (DO NOT VIOLATE)
1. **Q4.5** — chatbot reset clears shader ONLY, never the habituation offset.
2. **§6 non‑medical red line** — no therapy/tolerance/recovery/optimisation claims anywhere.
3. Grid is **world‑locked** (no viewport‑relative drawing).
4. AppId frozen forever: `DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C`.
5. **PII‑free logs** — only `chat_history`/`calibration_history`/`model_trace` are the designated PII files (and they get trim/clear/redaction in W9).
6. `head_tracker.py` core algorithm untouched — CLI args / env hardening only.
7. Sidecar emits **luma + landmarks‑derived scalars only** (OBS‑3).
8. Cannot break prior work — all existing tests stay green.
9. No commit/push without approval; no `--no-verify`.

## Sequenced waves (~84 h)
| # | Wave | Scope | Est |
|---|------|-------|-----|
| 1–4 | **W3 — Sensing lifecycle + Liveness** | EM3 COM‑teardown serialization, NE10‑12/14/25 + H37‑38 pre‑work, EM4 transient‑exception recovery, Liveness contract + rearming watchdog (`SensingHealthChanged`, Faulted bar/tray state) | ~16 h |
| 5–6 | **W4 — UX‑breakers** | EM5 auto‑expand notification foreground race, NE26 markdown underscore, NE27 expired‑proposal button, NE24 settings single‑instance, NE30 grid opacity, H7 rationale truncation | ~6 h |
| 7–9 | **W5 — Chatbot envelope** | EM6 JSON extraction (stops discarding model text on every `[PROFILE_OVERRIDE]`), NE3‑6 intent routing (shader‑direction words, profile‑switch collision), H5/H6 prompt‑injection + observation sanitization | ~11 h |
| 10–11 | **W6 — Loopback API auth** | H9 random token + api‑key, H10 port‑collision check, H29 spoofed `/health`, H11/H34 calibration user‑confirm + buffer/timeout caps | ~7 h |
| 12–14 | **W7 — Sidecar hardening** | H12/H13 env‑var hijack (drop untrusted env, `--model` as arg), H32 SHA‑256 binary manifest, H35/H36 malformed‑line rate‑limit, H40 bounded config walk | ~9 h |
| 15–18 | **W8 — Installer / supply chain** | **H26 cert (day‑1)**, H27 binary provenance, H28 trim 506 MB cuda/PDB bloat, H19/H20/H21 PDB+path redaction + UTC, H33/H47/H48 dependency pins, H49‑H51 uninstall cleanup | ~17 h |
| 19–21 | **W9 — PII hygiene** | EM7 adjustment‑log PII, H22 handbook‑zip whitelist + EN handbook, H23/H24 calibration trim + clear‑on‑disk‑trace + "Delete all my data" button, H25 uninstall data prompt, H41 trim/rotation/UTC | ~15 h |
| 22 | **W2.0 — verify** | Confirm W2 integrity gates green (EM1, EM2, C5, NE15‑23, H14‑17) — implement if slipped | ~14 h if needed |

## Open decisions (need Kirby)
- **W3 liveness visibility:** subtle corner indicator / text label / tray‑tooltip only / all three. *(Recommend: subtle corner indicator + tray tooltip — preserves the "quietest app" aesthetic while staying discoverable.)*
- **W8 cert provider:** Azure Trusted Signing *(recommended)* vs DigiCert.
- **EM3 teardown:** wait‑handle (per `ALPHA_HANDOVER` §3.3) vs lock‑based.
- **EM6 extraction:** brace‑depth scanner vs `StripAllOverrideBlocks`.

## Suggested entry point
Start with the **🔴 critical safety items** (contained, high‑value, test‑heavy), then **W3** (unblocks the sensing reliability the rest leans on), while the **H26 cert procurement runs in parallel**. Full per‑task approach/acceptance detail is in the prep‑workflow output (run `prep-alpha2-core`).
