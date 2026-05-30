# M7 Perf Report — three-layer framing (LLM hot)

**Hardware**: AMD Ryzen AI 9 HX 370 w/ Radeon 890M (12 cores / 24 threads,
50 TOPS XDNA 2 NPU available but NOT engaged per locked decision —
prototype is pure-CPU).

**Date**: 2026-05-30 03:43:48 — 03:45:13 UTC (~85 seconds, 17 × 5s windows).

**Configuration**:
- `HeadTrackingEnabled = true` (Balanced follow mode, escape hatch enabled)
- Sidecar holds webcam at 480×360 → MediaPipe FaceLandmarker @ 15 Hz target
- Dashboard runs WGC sensing (15 Hz) + Mistral 3B local inference (CPU-heavy)
  alongside head tracking
- M6 SidecarLuxProvider active (lux from sidecar JSON, no separate MediaCapture)
- 4-5 chatbot messages sent over the soak window, each triggering Mistral 3B
  inference (~5-10s of CPU-heavy work). Natural head motion throughout.

## Per-window raw data

```
window  achievedHz  sidecarHz  comment
1       63.31        4.79      startup tail (sidecar coming up, ~1s of samples)
2       75.43       14.97
3       75.36       14.99
4       55.97       14.99      LLM-hot dispatcher contention dip
5       74.20       15.00
6       82.32       14.99      LLM idle
7       67.14       15.19
8       71.71       14.98
9       69.48       14.97
10      68.52       14.98
11      78.32       14.98
12      71.17       14.95
13      83.92       15.18      LLM idle (peak render)
14      69.73       14.98
15      82.70       14.98
16      82.60       14.96
17      76.42       14.96
```

## Steady-state (excluding window 1 startup tail)

| Metric           | Min   | Max   | Mean  | Spread |
|------------------|-------|-------|-------|--------|
| Sidecar Hz       | 14.95 | 15.19 | 15.00 | 0.24   |
| Render Hz (WPF)  | 55.97 | 83.92 | 74.04 | 27.95  |

## Layer 1 — pure-CPU baseline under full load

**Verdict**: ✅ GREEN

純 CPU 在 **AMD Ryzen AI 9 HX 370** + LLM hot 達 **15.00 Hz sidecar / ~74 Hz
WPF render** / sidecar peak RSS ~165 MB (unchanged from G1 baseline).

Sidecar Hz mean **15.00**, 0.24 Hz spread across 16 steady-state windows
covering a mix of LLM-hot (Mistral 3B inference active) and LLM-idle
moments. This is **essentially identical to G1 isolated (14.988 Hz) and
M5.1-C full-active without LLM (15.04 Hz)**.

Conclusion: LLM CPU load does NOT eat sidecar's per-frame budget. Python
sidecar runs on its own process / core; Mistral 3B inference runs on
the LLamaSharp worker threads on different cores; the OS scheduler
gives sidecar's MediaPipe work its scheduled slot.

## Layer 2 — prototype usability under realistic load

**Verdict**: ✅ GREEN

End-to-end head-tracking smoothness under LLM-hot full load:
- Sidecar EMA + adaptive curve unchanged from M5.1-C
- Per-frame CompositionTarget.Rendering interpolation unchanged from M5.1-C
- The new variable is WPF render rate: **75 Hz mean, 56 Hz worst case**
  (vs M5.1-C's ~165 Hz when LLM was idle)
- 56 Hz = 17.9 ms/frame, still well above human smoothness threshold
  (24 fps perceived smooth, 60 Hz premium feel)
- Under LLM-hot the visual experience drops from "165 Hz native" to
  "75 Hz mean" — a real degradation but still subjectively smooth

The 75 Hz render mean (vs 165 Hz monitor refresh) reflects WPF dispatcher
sharing time with Mistral inference + WGC capture pipeline. It is NOT
a head-tracking-specific perf problem — every WPF UI element renders
at this rate under LLM-hot, and the 56 Hz floor is still gaming-grade
smoothness for a comfort overlay.

The sidecar EMA-driven lag remains the M5.1-C-optimized values
(190-1850 ms depending on velocity). Render-rate dip does not change
lag — it changes only the temporal granularity of brush.Center mutation,
and 56 Hz granularity (17.9 ms) is fine.

## Layer 3 — v1.0 NPU outlook

**Verdict**: ✅ BONUS (no longer load-bearing for viability)

NPU acceleration (ONNX Runtime DirectML provider OR AMD Ryzen AI SDK)
is still architecturally ready in Path B — sidecar's JSON output contract
is inference-device-independent; switching to NPU only touches sidecar
internal MediaPipe initialization (a `BaseOptions(delegate=GPU)` change
in the Tasks API), not the C# consumption side.

But the **role of NPU has shifted**:
- Originally: NPU was the v1.0 path that would rescue sidecar Hz if CPU
  couldn't sustain 15 Hz under LLM-hot load
- M7 shows: CPU sustains 15 Hz under LLM-hot just fine
- New v1.0 role for NPU: free the CPU core sidecar currently uses, giving
  that core back to WPF compositing → render rate could climb from
  ~75 Hz back to ~165 Hz under LLM-hot
- This is a **smoothness optimization for high-refresh-rate monitors**,
  not a viability requirement

## Locked decisions preserved

Path B (sidecar + JSON IPC) chosen at G1 has been validated by M7:
- Sidecar runs independently of LLM CPU load (separate process / cores)
- JSON contract decouples inference device from C# consumer
- NPU path remains architecturally available without C# changes

Engine API not extended, Snapshot schema unchanged, head-tracking flag
defaults to false. Q2 ship-preview decision NOT triggered.
