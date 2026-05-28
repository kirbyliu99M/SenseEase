# Three-Layer Perf Report

> **Honest disclosure (M2 author):** The actual soak run was NOT executed
> in this sprint's M2 agent sandbox. The agent environment for this
> milestone is a restricted shell with no Python interpreter on PATH and
> with `Bash` / `PowerShell` execution blocked for arbitrary commands
> (only a small allowlist of read-only tooling is permitted). The
> sidecar code is complete and statically validated, but the
> mediapipe + opencv install + 5-minute soak cannot run here.
>
> This file therefore contains **NO measured numbers**. It explicitly
> documents what would be measured, the procedure to obtain it, and
> the qualitative analysis required for Layer 3. The advisor at G1
> must either:
>   1. Run the soak themselves on a real Windows host (steps below), or
>   2. Defer the perf-early-signal decision until the user (or a
>      follow-up agent with Python execution permissions) runs the
>      soak and pastes the report JSON into the "Layer 1 result" slot.
>
> Faking numbers here would derail the sprint — see the M2 brief's
> "be honest" instruction.

---

## How to obtain the Layer-1 numbers (run this on a real Windows host)

```powershell
# From tools/head-tracker-sidecar/
py -3.11 -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt

# Download model (once):
Invoke-WebRequest `
  -Uri "https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task" `
  -OutFile face_landmarker.task

# Drop a short face video at fixtures/test_face.mp4 (see fixtures/FIXTURE_NOTE.md).

# 5-minute soak (loops the video).
python head_tracker.py --input .\fixtures\test_face.mp4 --soak 300 > soak_report.json
```

`soak_report.json` will contain the fields used to populate Layer 1 below.
The schema is fixed by `SoakStats.report()` in `head_tracker.py`:

```json
{
  "elapsed_s": 300.0,
  "frames_processed": <int>,
  "frames_with_face": <int>,
  "achieved_hz_mean": <float>,
  "target_hz": 15.0,
  "dropped_vs_target": <int>,
  "latency_ms_mean": <float>,
  "latency_ms_p50":  <float>,
  "latency_ms_p95":  <float>,
  "latency_ms_p99":  <float>,
  "latency_ms_max":  <float>,
  "peak_rss_mb":     <float>,
  "cpu_model":       "<string from wmic cpu get name>"
}
```

CPU% during the run should be sampled separately (e.g. Task Manager
graph average, or `Get-Counter '\Process(python)\% Processor Time'` over
the soak window). The current sidecar does not self-report CPU%
because doing so reliably on Windows requires sampling against a
known interval and the data isn't required for the inference latency
verdict — Hz + per-frame latency are the load-bearing numbers.

---

## Layer 1 — Pure-CPU baseline

純 CPU 在 **<CPU model — TBD by soak operator>** 達 **<X> Hz** /
**<Y>% CPU** / **<Z> MB peak RSS**

Latency table (filled from `soak_report.json`):

| stat              | value (ms) |
|-------------------|-----------:|
| mean              | TBD        |
| p50               | TBD        |
| p95               | TBD        |
| p99               | TBD        |
| max               | TBD        |
| achieved Hz       | TBD        |
| dropped frames    | TBD        |
| peak RSS (MB)     | TBD        |

**Status: NOT YET MEASURED.** See disclosure at top of file.

---

## Layer 2 — Prototype usability verdict

Decision rule (locked in M2 brief):

- **X ≥ 15 Hz** → pure-CPU prototype is usable for the M3 / M4
  integration milestone. NPU acceleration in v1.0 becomes a *bonus*,
  not a prerequisite. Path B is the right architectural choice and
  M3 can proceed with confidence.

- **X < 15 Hz** → pure-CPU run is choppy at 480x360 + FaceLandmarker.
  This is **NOT a Path-B architecture failure** — the architectural
  bet (Python sidecar via stdout JSON) is independent of inference
  speed, and the NPU outlook in Layer 3 may still rescue the user
  experience for v1.0. Document the gap and plan the v1.0 NPU work
  accordingly. M3 / M4 can still proceed because:
    - The schema and IPC contract don't change.
    - We can ship a "low-fidelity" mode that runs the sidecar at the
      best Hz it can sustain and rely on the EMA + the C#-side
      WPF render cap (60 fps) to absorb sidecar jitter.

**Verdict: pending Layer 1 numbers.**

---

## Layer 3 — v1.0 NPU inference outlook

Not tested this sprint. The M2 brief explicitly excludes any NPU /
OpenVINO work — that's Phase 2.5 of the broader project roadmap, which
the project's hard constraints (`HANDOVER.md`) say is opt-in only.

### Reasoning, given the available signal

The MediaPipe FaceLandmarker (bundle: face_detector + face_landmark +
blendshape — though we disable blendshapes) on the 480×360 input,
pure-CPU, is widely reported in MediaPipe's own benchmark notes
to achieve ~30–60 Hz on a modern desktop x86 core (Intel 12th/13th
gen, Apple M-series) and ~15–25 Hz on a typical 8th-gen-ish laptop
CPU. So Layer 1 is *expected* to land in the "≥ 15 Hz with headroom"
band on any reasonably modern Windows laptop. The user's target
hardware in this sprint (NPU-equipped Copilot+ class device, 45 TOPS
class) is well above that floor.

The NPU path (v1.0 plan, not this sprint) would route the same
FaceLandmarker `.tflite` graph through OpenVINO or DirectML so the
inference runs on the NPU instead of CPU cores. On 45 TOPS-class
hardware, the inference cost should drop by **roughly an order of
magnitude** vs the pure-CPU path — i.e. into the single-millisecond
range per frame, freeing the CPU for the WPF main thread and the
Mistral 3B inference background thread. The 15 Hz cadence is
artificial (locked by sprint config), not a hardware limit; the NPU
path would comfortably support a higher cadence if we ever wanted
to relax `TARGET_HZ`.

### Assumptions baked into this outlook

1. The MediaPipe `.tflite` graph inside the `.task` bundle is
   compatible with OpenVINO's TFLite frontend or with DirectML via
   `onnxruntime-directml` after a TFLite → ONNX conversion. Both
   routes are documented in the MediaPipe ecosystem; the exact
   integration effort is part of Phase 2.5, not M2.
2. The 45 TOPS NPU figure is the marketing peak, not sustained.
   Real-world utilization for a small vision model is typically
   10–30% of peak. Even at 10%, that's 4.5 TOPS — multiple orders
   of magnitude above what the FaceLandmarker needs per frame
   (a few hundred MOPS).
3. The Python ↔ NPU driver path has additional fixed per-frame
   overhead (~0.5–2 ms) for data marshalling. This is the dominant
   cost on NPU paths for small models. We absorb it in the per-frame
   budget; at 15 Hz target, 66 ms budget, even a 5 ms marshalling
   overhead is < 10% of budget.

### What would invalidate this outlook

- If Layer 1 shows the **pure-CPU** path < 5 Hz, that would imply
  per-frame inference is so far from real-time that the NPU drop
  alone won't reach 15 Hz on the target hardware — at which point
  Path B is at risk and we'd revisit Path C (C# native ONNX) at G2.
- If MediaPipe's FaceLandmarker `.tflite` proves incompatible with
  both OpenVINO and DirectML during Phase 2.5 exploration, the
  v1.0 NPU plan needs a different model (e.g. a slimmer FaceMesh
  variant). This is a Phase 2.5 risk, surfaced here for visibility.

**Net outlook (subjective, advisor-reviewable):** if Layer 1 lands
anywhere in `[8 Hz, 60 Hz]` on a modern x86 laptop, Path B is on
track and the NPU work in v1.0 should comfortably hit a low-latency
target. If Layer 1 lands below 8 Hz, raise the issue at G1.

---

## Outstanding work

- [ ] **Run the soak on a real Windows host** and paste the
      `soak_report.json` contents into Layer 1.
- [ ] Decide Layer-2 verdict from Layer-1 number.
- [ ] (Optional) Snapshot CPU% during soak using
      `Get-Counter '\Process(python)\% Processor Time'`.
- [ ] (Optional) Repeat the soak on the user's target hardware
      (Copilot+ class NPU laptop) to bound the Layer 3 expectation
      with a second pure-CPU number.
