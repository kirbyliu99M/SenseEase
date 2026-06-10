# SenseEase — Security & Privacy Posture

**Status:** Alpha-1. This document states the security model, the data SenseEase
holds, the hardening that has landed, and the items still open before the public
cut. It is the companion to [ALPHA_SECURITY_BACKLOG.md](ALPHA_SECURITY_BACKLOG.md)
(item-level tracking) and the master plan's §6.1 / §6.5 audit gates
([Alpha_1_MASTERPLAN.md](Alpha_1_MASTERPLAN.md)).

> **One-line posture:** SenseEase is a **local-only** desktop app. No user data
> leaves the machine. The camera is **off by default**. Vision (head-tracking)
> and the language model both run **on-device**; nothing is uploaded.

---

## 1. Threat model

SenseEase runs as a normal (non-elevated) user-mode Windows app. The realistic
adversaries we design against:

| Adversary | Concern | Mitigation summary |
|---|---|---|
| **Same-user local process** | Spoofing the loopback LLM, reading state files, tampering with the on-disk calibration/profile to change behaviour | Loopback auth (open), state clamps on load, atomic writes, integrity manifest (open) |
| **Malicious/edited state file** | Hand-edited JSON forces a privileged UX path or unbounded values | Range clamps + tamper checks on load (CalibrationCount, Delta, SchemaVersion roll-forward) |
| **Hostile/wedged sidecar** | A compromised head-tracker process floods or OOMs the host over stdout | Bounded line reader (64 KB cap, `MaxDepth`), env-var stripping, rate-limited diagnostics |
| **Forensic / over-the-shoulder** | Logs leak the build-engineer username, the tester's path, timezone, or raw user text | `PathMap` + runtime path redaction, UTC timestamps, no raw utterances persisted |
| **Supply chain** | A tampered model/binary is loaded from a user-writable location | Integrity manifest + signed installer (open) |

**Out of scope for Alpha-1:** kernel-level adversaries, physical DMA attacks,
and a fully-compromised OS. SenseEase cannot defend a machine the attacker
already owns at ring 0.

---

## 2. Data SenseEase holds (all local)

Everything lives under `%LOCALAPPDATA%\SenseEase\`. There is **no server, no
account, no telemetry upload**.

| File | Contents | Sensitivity |
|---|---|---|
| `habituation.json` | engine offset only | low |
| `habituation_profile.json` | profile + strike count + calibration count + state | medium (behavioural) |
| `chat_history.jsonl` | assistant conversation turns | **high (free text)** — trimmed; crisis turns persisted as `[REDACTED: safety_floor]` |
| `calibration_history.jsonl` | calibration Q&A transcript | **high (free text)** — trimmed (last 200) |
| `adjustments.jsonl` | comfort adjustments | medium — raw utterance field is **no longer persisted** (`[JsonIgnore]`) |
| `model_trace.jsonl` | model decision trace | medium — gated; rotated |
| `diag\wgc_diag.txt` | sensing diagnostics | low — moved out of `%TEMP%`, rotated at 5 MB, path-redacted |

**Camera frames are never written to disk** and never leave the head-tracker
process. The sidecar emits only an allowlisted numeric DTO
(`t_ms, cx, cy, luma, face_detected, face_present, yaw_deg, pitch_deg`) — enforced
by `SidecarFrameDtoSchemaTests` (forbids any `landmark/eye/blendshape/image`
field name).

---

## 3. On-device AI

- **Language model:** runs via a bundled `llama-server` on **loopback only**
  (127.0.0.1). No prompt or reply is sent off-machine.
- **Head tracking:** a PyInstaller-packaged Python sidecar (MediaPipe) processes
  webcam frames in-process and emits only the numeric pose DTO above.
- **Crisis safety floor:** self-harm phrases are intercepted *before* the model
  (bilingual lexicon, NFKC + curly-apostrophe folding) and answered with a fixed
  crisis message; the user's raw text is redacted before any persistence. Safety
  testing is done with **synthetic inputs in the dev environment only** — never by
  exercising crisis phrases against the live model.

---

## 4. Hardening that has landed (Alpha-1)

- **Path / identity redaction (H19):** `Directory.Build.props` sets
  `Deterministic` + `DeterministicSourcePaths` + `PathMap` so PDB-embedded source
  paths don't leak the build username; `LogCrash` also redacts `\Users\<name>\`
  at runtime.
- **Sidecar isolation (H12/H35/H36):** strips hijackable env vars
  (`SENSEEASE_FACE_LANDMARKER_MODEL`, `PYTHONIOENCODING`, `PYTHONUTF8`,
  `PYTHONLEGACYWINDOWSSTDIO`), forces UTF-8 stdout, reads stdout with a bounded
  64 KB line cap + `MaxDepth=8`, and rate-limits malformed-line diagnostics.
- **Diagnostics relocation (H51/H36):** `wgc_diag*.txt` moved from `%TEMP%` to
  `%LOCALAPPDATA%\SenseEase\diag\` with 5 MB rotation.
- **PII posture (EM7/H43):** the raw `Utterance` field is `[JsonIgnore]` — never
  serialised to `adjustments.jsonl`; load rejects `|Delta| > 100`.
- **State integrity:** `AtomicJsonWriter` (write-through flush + atomic rename,
  tmp cleanup on failure); `SchemaVersion` roll-forward accepts `[1, Current]` and
  defaults only out-of-range; `CalibrationCount` clamped `[0,1000]` and reset on a
  no-timestamp tamper signal (H44).
- **Path resolution (H40):** the parent-directory walk-up for model/sidecar paths
  is gated behind `#if DEBUG`; release builds resolve only `BaseDirectory`-adjacent.
- **Bounded resources:** chat-history huge-line guard (skip > 64 KB) + opportunistic
  trim; action-log FIFO soft-cap; longitudinal-observation dedup fix.

---

## 4.1 Integrity gate — current scope and known limits (SEC-03)

SenseEase generates a SHA-256 manifest of a small set of high-value binaries at
build time (`install.manifest.json`, produced by `scripts/stage-pre-alpha-assets.ps1`).
At startup, the C# host verifies each listed entry before calling `Process.Start`
on the head-tracker sidecar and the LLM server.

**What the gate currently guards:**

- Accidental file corruption (partial copy, disk error, mid-install power-loss).
- A wrong build being accidentally staged (hash mismatch surfaces immediately at
  launch rather than silently misbehaving at runtime).

**What the gate does NOT guard (known limits):**

| Limit | Reason |
|---|---|
| **Absent manifest = pass.** If `install.manifest.json` is missing, the startup check silently skips rather than refusing to launch. A local attacker can remove the file to suppress the check. | Fail-open chosen deliberately for Alpha so a corrupt staging run does not permanently brick testers' installs; will be reviewed before the public cut. |
| **Manifest is unsigned and user-writable.** The manifest itself lives under `%LOCALAPPDATA%\Programs\SenseEase\` alongside the binaries it describes. A local attacker who can write to that directory can replace both a binary and its manifest entry, so the check cannot detect targeted tampering by a same-user adversary. | This is the fundamental limit of a manifest that lives next to what it verifies. Authenticode code-signing (H26) is the correct fix; it moves trust to an out-of-band certificate chain the attacker cannot easily forge. |
| **Coverage is partial.** The current manifest covers the two main executable stubs (`llama-server.exe`, `head_tracker.exe`) and the model file. The ~150 PyInstaller `_internal/` DLLs, the ggml/llama support DLLs, and `face_landmarker.task` are not yet hashed. A trojanised sidecar Python extension (`.pyd`) would not be detected. | PKG-06 sweep — extending hash coverage to `llama*.dll`, `mtmd.dll`, `libomp*.dll`, the `_internal/` tree, and `face_landmarker.task` — is scheduled for this wave (Wave B1). Once that lands the "partial coverage" row will be removed from this table. |

**Summary:** the integrity gate guards against accidental corruption and wrong
builds, not against a local attacker who already has write access to the install
directory. Full tamper resistance (delete-to-bypass closed, coverage complete,
binaries Authenticode-signed) lands with H26 code signing, which is on the
procurement track for the public cut — see §5 below.

---

## 5. Open items before the public cut (tracked, not yet landed)

These are intentionally **not** auto-applied — each needs an external action or a
manual smoke-test that can't be run from CI:

| Item | Why it's open |
|---|---|
| **Code-signing certificate (H26)** | External procurement (Azure Trusted Signing / DigiCert). Long-lead; order day 1. Until signed, the installer trips SmartScreen. |
| **Loopback LLM auth (H9/H10/H39)** | `--api-key` + port-in-use probe + direct-launch (drop the `cmd /c` wrapper). Changes process launch — needs a manual chatbot smoke-run on the real machine. |
| **Integrity manifest (H32)** | SHA-256 of bundled binaries, verified before `Process.Start`. Build-time + launch-gate work. |
| **Single-instance mutex (H30/H31)** | `Local\` namespace + `initiallyOwned:false` + abandoned-mutex handling. Needs a second-instance launch smoke-test. |
| **Symlink guards (H50)** | Reject reparse-points before write/delete on every state path. |
| **Dependency bumps (H33/H47/H48)** | llama.cpp ≥ b9500, `opencv-python` (CVE-2025-53644), pinned `pyinstaller` — require re-downloading binaries + rebuilding the sidecar bundle. |
| **Installer hygiene (H25/H28/H49)** | Trim unused runtime backends (~500 MB), uninstall removes `%LOCALAPPDATA%\SenseEase\` + the HKCU Run key. |
| **Uninstall / kill-switch (H53)** | Optional recall channel. |

See [ALPHA_SECURITY_BACKLOG.md](ALPHA_SECURITY_BACKLOG.md) for the full item list
and acceptance criteria; §6.1 of the master plan defines the audit gate.

---

## 6. Reporting

This is pre-release software for a closed tester pool. Security concerns should
go to the maintainer (Kirby) directly. There is no public disclosure process yet;
one will be added at the v1.0 cut alongside the signed-installer release.
