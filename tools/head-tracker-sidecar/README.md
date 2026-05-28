# SenseEase Head-Tracker Sidecar (Path B)

A standalone Python sidecar that drives the Chang-Hain FOV mask center
`(cx, cy)` from head pose using **MediaPipe FaceLandmarker**. It captures
a webcam (or a test video), runs face inference at ~15 Hz, decomposes the
facial 4×4 transformation matrix to yaw / pitch / roll, maps yaw / pitch
to `(cx, cy)`, computes per-frame mean luma, and emits one JSON line per
frame on stdout.

This sidecar is the **M2** deliverable of the SenseEase head-tracking
sprint. C# integration (M3 / M4) is **not** part of this milestone.

## Status

- [x] Webcam + video-file capture at 480×360
- [x] MediaPipe FaceLandmarker with `output_facial_transformation_matrixes`
- [x] Yaw / pitch decomposition (Tait-Bryan, intrinsic XYZ)
- [x] EMA smoothing α=0.18 on cx, cy independently
- [x] Sign-flipped pitch → cy mapping (see code comment)
- [x] Clamp `cx, cy` to `[0, 1]`
- [x] Mean-luma normalized to `[0, 1]`
- [x] One-line JSON output per frame (schema-locked, see `PRIVACY.md`)
- [x] `--soak SECONDS` perf-report mode (suppresses per-frame JSON)

## Locked parameters

| parameter            | value   | unit       |
|----------------------|---------|------------|
| capture resolution   | 480×360 | pixels     |
| target inference     | 15      | Hz         |
| EMA α                | 0.18    | unitless   |
| yaw → cx gain        | 0.0233  | per degree |
| pitch → cy gain      | 0.035   | per degree |
| pitch sign           | flipped | see below  |
| default `(cx, cy)`   | 0.5, 0.5 | unitless  |
| clamp range          | [0, 1]  | unitless   |

**Pitch sign-flip:** MediaPipe yields positive pitch when the head tilts
*up* (chin raises). Visually, looking up moves the feature of interest to
the *top* of the frame, i.e. `cy` should *decrease* (cy=0 is the top
edge). Therefore:

    cx = 0.5 + 0.0233 * yaw_deg
    cy = 0.5 - 0.035  * pitch_deg

## Python compatibility

- **Required:** CPython 3.10, 3.11, or 3.12 on Windows x64.
- **Not supported:** 3.13 (no MediaPipe wheels at sprint start) and 3.9
  (MediaPipe Tasks API requires 3.10+).
- Tested target: 3.11 on Windows 11.

## Setup

```powershell
# From this directory.
py -3.11 -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
```

You also need the MediaPipe `face_landmarker.task` model. Download it
once and drop it next to `head_tracker.py`:

```powershell
# ~3.7 MB. Hosted by Google on storage.googleapis.com.
Invoke-WebRequest `
  -Uri "https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task" `
  -OutFile face_landmarker.task
```

(Or set `$env:SENSEEASE_FACE_LANDMARKER_MODEL` to point elsewhere.)

The model file is **not** checked into the repo — see `.gitignore`.

## Running

### Live webcam

```powershell
python head_tracker.py --input 0
```

Per-frame JSON is written to stdout, one object per line, e.g.:

```json
{"t_ms":123,"cx":0.512,"cy":0.498,"luma":0.387,"face_detected":true,"confidence":1.0,"yaw_deg":-0.81,"pitch_deg":0.04}
```

Pipe to a file with `> out.jsonl` or to another process via pipe.

### Video file (for deterministic testing)

```powershell
python head_tracker.py --input .\fixtures\test_face.mp4
```

### Soak mode (perf early signal — what M2 reports at G1)

```powershell
python head_tracker.py --input .\fixtures\test_face.mp4 --soak 300
```

This runs for 300 seconds (5 minutes), loops the input video if it ends
early, suppresses the per-frame JSON, and emits a single perf-report JSON
on stdout at the end. The result feeds `PERF.md`'s three-layer analysis.

## CLI

```
python head_tracker.py --input <0|path/to/video> [options]

Options:
  --input         (required) webcam index OR path to a video file
  --soak SECONDS  run for SECONDS seconds in perf-report mode
  --model PATH    override the face_landmarker.task location
  --mirror        horizontal-flip frames (selfie-view)
  --loop          loop a finite video file on EOF (implied by --soak)
```

## Output schema (allowlisted by `PRIVACY.md`)

Per-frame JSON line — only these fields:

| field           | type         | range / values |
|-----------------|--------------|----------------|
| `t_ms`          | int          | monotonic ms since start |
| `cx`            | float        | `[0.0, 1.0]`   |
| `cy`            | float        | `[0.0, 1.0]`   |
| `luma`          | float        | `[0.0, 1.0]`   |
| `face_detected` | bool         | true / false   |
| `confidence`    | float        | `0.0` or `1.0` (see PRIVACY.md note) |
| `yaw_deg`       | float / null | raw pre-smoothing, debug only |
| `pitch_deg`     | float / null | raw pre-smoothing, debug only |

## Privacy

See [`PRIVACY.md`](./PRIVACY.md). Hard rules: no raw landmarks emitted,
no frame images emitted, no face data persisted, no telemetry.

## Perf

See [`PERF.md`](./PERF.md) for the three-layer perf report (pure CPU
baseline / prototype usability verdict / NPU outlook).

## Test fixture

`fixtures/` is intentionally empty in git. The recommended test video is
a 5–10 second clip of a head moving naturally (slow yaw/pitch sweeps).
We document the chosen fixture and its license in
`fixtures/FIXTURE_NOTE.md`.

If you cannot obtain a face video, the sidecar still runs on any video
file — it simply records `face_detected=false` for every frame and
emits the default `(cx, cy) = (0.5, 0.5)`. This is the "stationary-face
surrogate" mode discussed in `PERF.md`.
