# Head Tracking — Privacy (頭部追蹤 — 隱私)

> Repo-level privacy document for the SenseEase head-tracking feature.
> Elevates the OBS-3 rules from
> [tools/head-tracker-sidecar/PRIVACY.md](../tools/head-tracker-sidecar/PRIVACY.md)
> to the project root and adds user-facing context (opt-in default,
> OS-level visibility, NPU roadmap distinction).

The head-tracking feature drives the Chang-Hain FOV mask center
(`cx`, `cy`) from MediaPipe face landmark data captured by a Python
sidecar process. Because the input source is the user's webcam, the
feature is held to a stricter privacy contract than the rest of
SenseEase.

This document is the single source of truth for what the
head-tracking pipeline does and does not do with face / camera data.
Any code change touching the sidecar's emitted schema, persistence
behaviour, or IPC surface MUST be reviewed against this document and
the OBS-3 hard rules below.

---

## 1. Default = OFF (opt-in privacy / 預設關閉)

`AppConfig.HeadTrackingEnabled` defaults to `false`. The Python
sidecar process is NOT spawned and the webcam is NOT opened until the
user explicitly opts in.

**Locked design decision** (head-tracking sprint, sprint-setup
decisions §4 / 2026-05-28):

> AppConfig.HeadTrackingEnabled = false by default — opt-in privacy.

The opt-in is exposed in the Settings window under the
"頭部追蹤 / Head Tracking" section as a single checkbox. The change
takes effect at the next app launch (see §5 below for the restart
rationale).

Until the user opts in, the head-tracking codepath is dead:

- No webcam open / no `MediaCapture` initialisation.
- No Python sidecar process spawn.
- `IHeadPoseReader` resolves to `DefaultHeadPoseReader` (returns
  `null` from `ReadLatest`).
- The FOV mask centre stays hardcoded at (0.5, 0.5) — identical to
  the v0.9 baseline.

---

## 2. OBS-3 hard rules (五大不可違反原則)

These five rules are non-negotiable. Any code change violating any of
them is a release blocker.

### 2.1 Derived-only IPC schema

The sidecar's per-frame JSON line on stdout emits ONLY these fields:

| Field            | Type          | Always emitted        | Purpose                                       |
|------------------|---------------|-----------------------|-----------------------------------------------|
| `t_ms`           | int           | yes                   | monotonic timestamp, ms since sidecar start   |
| `cx`             | float [0, 1]  | yes                   | smoothed + clamped FOV mask center X          |
| `cy`             | float [0, 1]  | yes                   | smoothed + clamped FOV mask center Y          |
| `luma`           | float [0, 1]  | yes                   | frame mean luma (Rec. 601 Y), normalised      |
| `face_detected`  | bool          | yes                   | true when MediaPipe returned a transformation |
| `face_present`   | float {0, 1}  | yes                   | 1.0 when face landmark set returned, else 0.0 |
| `yaw_deg`        | float         | `--debug` only        | pre-smoothing raw yaw (UX tuning)             |
| `pitch_deg`      | float         | `--debug` only        | pre-smoothing raw pitch (UX tuning)           |

NEVER emitted, under any flag:

- Raw landmark coordinates (nose_x, eye positions, the 478 landmarks
  MediaPipe produces internally).
- Image data of any kind (frame bytes, base64, JPEG, encoded preview).
- MediaPipe blendshape outputs (facial-expression coefficients).

### 2.2 No frame buffer persists

The webcam frame is:

1. Decoded by OpenCV into an in-memory `numpy` array.
2. Passed to `mp.tasks.vision.FaceLandmarker.detect_for_video()`.
3. Used for mean-luma computation (Rec. 601 Y).
4. Dropped on the next iteration (Python garbage-collected).

The sidecar does NOT:

- Write frames to disk.
- Stream frames over any socket / pipe / HTTP.
- Display a debug preview window in default operation.

### 2.3 Sidecar process boundary

Raw image data lives ONLY in the sidecar process memory. The C# host
never receives a frame, a landmark, or anything more granular than
the derived JSON schema above.

When the sidecar process dies (normal exit, taskkill, app shutdown):

- `landmarker.close()` runs in `finally` → MediaPipe state destroyed.
- OS reclaims the OpenCV image buffers held in the Python process.
- No file artifacts remain on disk.

OBS-2 hard test verified this end-to-end at G3 (sprint, 2026-05-29):
user ran `Stop-Process -Force` on the sidecar process; the C# host
stayed alive, the overlay snapped back to centre within 2 s, and no
file or network artifact survived.

### 2.4 No analytics / no telemetry / no network

The sidecar makes ZERO outbound network calls. The MediaPipe
FaceLandmarker model is loaded from a local
`face_landmarker.task` file shipped alongside the sidecar script.

There is no:

- "Phone-home" HTTP request.
- Cloud-inference API.
- Crash-reporting service.
- Update check.

The MediaPipe `.task` model is downloaded once during the sidecar's
venv setup (documented in
[tools/head-tracker-sidecar/README.md](../tools/head-tracker-sidecar/README.md))
and lives on the user's disk thereafter.

### 2.5 Forbidden additions (any of these = release blocker)

Code review must reject any change that introduces any of:

- A field carrying landmark coordinates (e.g. `nose_x`,
  `eye_left[]`, `landmarks[]`).
- A field carrying image buffer data (base64, JPEG, raw bytes).
- `output_face_blendshapes=True` in the MediaPipe FaceLandmarker
  options (see `make_face_landmarker` in
  `tools/head-tracker-sidecar/head_tracker.py`).
- File writes of frames, landmarks, or any face-derived data under
  any flag, including `--debug`.
- HTTP / WebSocket / named-pipe / shared-memory channels that
  transmit face-derived data out of the sidecar process.

---

## 3. User-control / visibility (使用者可觀察)

The opt-in is the first layer of user control. The second layer is
"can the user verify the head tracker is doing what it claims?"

### 3.1 Settings UI

`Settings → 頭部追蹤 / Head Tracking` contains:

- A `啟用頭部追蹤 / Enable Head Tracking` checkbox (the master
  opt-in). The checkbox is annotated `需重新啟動 / Requires restart`
  because the `IHeadPoseReader` and `LightSensorProvider` factory
  bindings happen at app startup (see §5).
- A `Follow mode` radio group: `Balanced` (default) /
  `Full follow` (opt-in). Runtime-changeable (the overlay reads the
  mode per frame). See
  [head-tracking-sprint-state.md](../../.claude/memory/head-tracking-sprint-state.md)
  M5.1-B for the clinical rationale.
- An `Enable temporary transparency` checkbox (the Ctrl+Ctrl
  double-tap escape hatch). Runtime-changeable.

### 3.2 OS-level webcam indicator

When head-tracking is ON, Windows displays its **system-painted**
"SCREEN CAPTURE" / camera-in-use indicator. This is a Windows OS
overlay; SenseEase does NOT paint it and CANNOT suppress it.

Equivalently, the user can verify the webcam is in use via
Task Manager → Privacy → Camera or via
Settings → Privacy → Camera → "Recently accessed apps".

### 3.3 Sidecar process visibility

The sidecar runs as a child Python process. The user can see it via:

```powershell
Get-Process -Name python | Where-Object { $_.Path -like '*head-tracker-sidecar*' }
```

Killing the sidecar with `Stop-Process -Force` is a supported user
action: the C# host catches the exit (OBS-2 hard test, verified at
G3) and the overlay falls back to (0.5, 0.5) within 2 s.

This is the operational definition of "user can stop face capture at
any moment" — closing the SenseEase main window also kills the
sidecar via the `Process.Kill` path in `SidecarHeadPoseReader.
DisposeAsync`.

### 3.4 Vestibular interaction — full-Disable policy (RULED 2026-06-06, Q3 = B)

**Policy (supersedes the prior "always-on for vestibular" stance):** any
user — **including a vestibular-diagnosed user** — can one-tap **fully
disable all protection** (mask / sidecar / chatbot / adaptive fit), with
**no confirmation, no warning, and no background recovery prompt**. Disable
is the clean off-state of the control; the user re-enables from the bar or
Settings at any time (a visible off-state affordance, *not* a nag). This is
the unified Disable specified in
[`SenseEase-Adaptive-Fit-Layer-v1.md` §7](SenseEase-Adaptive-Fit-Layer-v1.md);
the behaviour lands in **P2 (Alpha-1)** — until P2 ships, the current
escape-hatch code (below) stands.

**Informed-choice risk note (Kirby 拍板, 2026-06-06):** this formally
**retracts** the earlier promise that vestibular protection cannot be turned
off. Rationale for the change: it matches the app's intuitive usage model
(turn it off cleanly, leave nothing running). The VIMS risk a
vestibular-diagnosed user takes on while protection is fully disabled is
**borne by the user**. This trade-off may be challenged at ATCC / OT /
vestibular-specialist review; it is retained here in full for traceability.
See also [SAFETY-PROVISIONS.md §3.1](SAFETY-PROVISIONS.md).

> **Superseded rationale (kept for traceability):** the original stance
> force-disabled the *temporary-transparency escape hatch* for vestibular
> profiles (`Q5RiskFactors` contains `VestibularDiagnosed`) — Settings greyed
> out the checkbox and the Ctrl+Ctrl chord no-op'd — because the FOV mask *is*
> the VIMS protection and even a 3-second 30 % window could surface symptoms.
> The full-Disable ruling above replaces that "always-on for diagnosed users"
> position; the escape-hatch code remains as-is until P2 unifies it under the
> single Disable control. The
> [POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md](POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md)
> "tunable cap rather than binary disable" follow-up is moot under this ruling.

---

## 4. NPU roadmap — iGPU vs NPU distinction (v1.0 路線圖)

The target-hardware class for head tracking is 45 TOPS+ Windows
devices (AMD Ryzen AI 9 HX 370 used during M7 perf measurement). The
prototype shipped in this sprint is **pure-CPU**: MediaPipe runs on
the Python sidecar's CPU thread, the 50 TOPS XDNA 2 NPU is not
engaged at all.

M7 measured 15.00 Hz mean sidecar throughput under LLM-hot full load
(see [baseline/m7-perf-report.md](../baseline/m7-perf-report.md)).
The NPU is therefore no longer load-bearing for the prototype's
viability — it is a v1.0 smoothness optimisation, not a perf rescue.

There are two acceleration paths the v1.0 roadmap can pursue. They
are **not the same thing**, and "switch on NPU" is NOT a one-line
delegate flip.

### 4.1 Path A — iGPU via MediaPipe `delegate=GPU`

The MediaPipe Tasks Python API exposes a `BaseOptions` delegate
parameter:

```python
options = vision.FaceLandmarkerOptions(
    base_options=python.BaseOptions(
        model_asset_path='face_landmarker.task',
        delegate=python.BaseOptions.Delegate.GPU,  # vs CPU
    ),
    ...
)
```

On the M7 test hardware this routes inference to the **integrated
Radeon 890M GPU**. Effects:

- Frees the CPU core sidecar currently uses → that core goes back to
  WPF compositing → render rate climbs from ~75 Hz under LLM-hot
  back toward the ~165 Hz native rate.
- Trivial code change (one-line delegate flip + a venv that has the
  GPU-enabled MediaPipe wheel).
- Does NOT touch the 50 TOPS XDNA 2 NPU.

Path A is the smaller-step optimisation. It is iGPU, not NPU.

### 4.2 Path B — Real NPU engagement via ONNX / Vitis AI

Real engagement of the 50 TOPS XDNA 2 NPU requires switching the
sidecar's inference runtime away from MediaPipe's TFLite path. Two
candidate runtimes:

1. **ONNX Runtime with the DirectML Execution Provider**, targeting
   the NPU. Requires converting the MediaPipe FaceLandmarker
   `face_landmarker.task` (TFLite model) to ONNX format
   (`tflite2onnx` or similar tooling, then validation).
2. **AMD Ryzen AI SDK** (Vitis AI ONNX runtime, AMD's first-party
   path for XDNA 2 NPU acceleration). Also ONNX-based; AMD ships
   pre-compiled DPUs and Vitis AI compiler tooling.

Both require:

- Model format change (`.tflite` → `.onnx`).
- Runtime change (MediaPipe Tasks → ONNX Runtime or Vitis AI).
- Re-validation of accuracy + latency on the AMD NPU.
- Possibly re-implementation of MediaPipe's post-processing
  (landmark refinement, pose head-matrix derivation) which currently
  lives inside the MediaPipe Tasks shim.

This is a substantial migration, not a delegate flag. It is the right
v1.0 path if the goal is "use the NPU and free the iGPU" but it is
explicitly NOT a one-line change.

### 4.3 Why this distinction matters

Future readers of this document might assume "NPU = `delegate=NPU`"
the way `delegate=GPU` exists. There is no `delegate=NPU` in
MediaPipe Tasks Python. The CPU vs GPU axis is a MediaPipe-internal
choice; the CPU vs NPU axis requires changing the inference engine.

Both paths are deferred to v1.0+. M7 confirmed they are not
load-bearing for the prototype.

---

## 5. Why HeadTrackingEnabled requires app restart (重新啟動原因)

The Settings UI annotates the HeadTracking checkbox with
"需重新啟動 / Requires restart". This is intentional — the flag is
read at a startup-bound code site:

1. `MainViewModel.CreateHeadPoseReader()` — a static factory that
   instantiates either `DefaultHeadPoseReader` (flag off, returns
   null) or `SidecarHeadPoseReader` (flag on, spawns Python). The
   factory runs ONCE at MainViewModel construction; flipping the
   flag at runtime does not rebuild the reader.

A future v1.0 iteration could rebuild the reader on flag flip but
the prototype keeps the constraint simple: flip the flag, restart,
get the new behaviour.

> **v0.11 note:** an earlier draft of this doc also referenced a
> second startup-bound site, `MainViewModel.BuildAmbientLightProvider()`
> / `SidecarLuxProvider`, which piggybacked the sidecar's luma JSON
> field to drive Display Comfort without opening a second webcam.
> That entire Display Comfort feature was decommissioned at v0.11
> (see [CHANGELOG.md](../CHANGELOG.md) v0.11 "Removed"); the sidecar
> still emits the `luma` field in its JSON protocol (algorithm core
> is locked) but no C# consumer reads it.

---

## 6. Auditing / verification (稽核方式)

Anyone (user, reviewer, security auditor) can verify the OBS-3 rules
are held by:

1. **Schema check** — `tools/head-tracker-sidecar/head_tracker.py`
   line search for `json.dumps`. The emitted payload must match
   §2.1's allowlist exactly. Run:

   ```powershell
   Select-String -Path tools\head-tracker-sidecar\head_tracker.py -Pattern 'json.dumps' -Context 0,10
   ```

2. **Network check** — search for any `requests`, `urllib`, `http`,
   `socket`, `websocket` imports in the sidecar:

   ```powershell
   Select-String -Path tools\head-tracker-sidecar\*.py -Pattern 'import (requests|urllib|http|socket|websocket)'
   ```

   Should return zero hits.

3. **Filesystem check** — search for `open(...,` write mode in the
   sidecar code path:

   ```powershell
   Select-String -Path tools\head-tracker-sidecar\head_tracker.py -Pattern "open\(.*'w"
   ```

   Should match only the soak-report path (`--soak` flag), which by
   design writes ZERO face data (only timing aggregates + CPU model
   info).

4. **Live process check** — when SenseEase head-tracking is on, run
   `Get-Process` per §3.3. The sidecar should be a single Python
   process with command line referencing `head_tracker.py`. Killing
   it should leave the C# host alive and the overlay re-centred.

If any of these checks fail, treat it as a release blocker per
§2.5.

---

## 7. Related documents

- [tools/head-tracker-sidecar/PRIVACY.md](../tools/head-tracker-sidecar/PRIVACY.md)
  — sidecar-internal privacy rules (OBS-3 source of truth).
- [tools/head-tracker-sidecar/PERF.md](../tools/head-tracker-sidecar/PERF.md)
  — perf measurement methodology.
- [baseline/m7-perf-report.md](../baseline/m7-perf-report.md) — final
  three-layer perf verdict.
- [docs/SAFETY-PROVISIONS.md](SAFETY-PROVISIONS.md) — broader safety
  posture (vestibular protections, chatbot guardrails).
- [docs/POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md](POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md)
  — cross-sprint risks (including the escape-hatch × vestibular
  interaction).
