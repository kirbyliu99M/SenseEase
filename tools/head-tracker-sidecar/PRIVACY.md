# Head-Tracker Sidecar — Privacy Rules

This document captures the **OBS-3 privacy constraints** from the SenseEase
head-tracking sprint plan. These rules are **non-negotiable**: any code
change that violates them must be rejected at review.

## Hard rules

1. **No raw landmark coordinates leave the sidecar process.**
   The MediaPipe `FaceLandmarker` produces 478 3D landmarks per frame. These
   are used internally to derive the 4x4 facial transformation matrix and
   are discarded immediately. They are **never** logged, never written to
   disk, and never emitted on stdout.

2. **No frame image data leaves the sidecar process.**
   The webcam frame (or test-video frame) is decoded in-process, used for
   inference + mean-luma, and then dropped on the next iteration. The
   sidecar **does not** write frames to disk, **does not** stream them over
   sockets, and **does not** display them in a debug window by default.

3. **No face data is persisted to disk.**
   The sidecar writes nothing to disk under normal operation. The only
   stdout output is the per-frame schema (see below) or, in `--soak` mode,
   an aggregate perf report (which contains zero face data — only timing
   and CPU model info).

4. **No analytics / telemetry.**
   The sidecar makes no outbound network calls. The MediaPipe model is
   loaded from a local `.task` file. There is no "phone-home" path.

## Emitted-on-stdout schema (allowlist)

Per-frame JSON line fields. **Only** these fields are permitted (G1-signed):

| field            | type           | emitted when         | purpose                                       |
|------------------|----------------|----------------------|-----------------------------------------------|
| `t_ms`           | int            | always               | monotonic timestamp, ms since sidecar start   |
| `cx`             | float [0, 1]   | always               | smoothed + clamped FOV mask center X          |
| `cy`             | float [0, 1]   | always               | smoothed + clamped FOV mask center Y          |
| `luma`           | float [0, 1]   | always               | frame mean luma (Rec. 601 Y), normalized      |
| `face_detected`  | bool           | always               | true when MediaPipe returned a transformation |
| `face_present`   | float [0, 1]   | always               | 1.0 when face present, 0.0 when not (see note)|
| `yaw_deg`        | float          | `--debug` only       | pre-smoothing raw yaw                         |
| `pitch_deg`      | float          | `--debug` only       | pre-smoothing raw pitch                       |

**Note on `face_present`** (G1 schema decision, renamed from `confidence`):
MediaPipe's FaceLandmarker IMAGE-mode API does not expose a per-detection
scalar score; the `min_face_presence_confidence` threshold gates the
detection internally. We emit `1.0` when a face landmark set is returned
(and the matrix is well-formed), `0.0` otherwise. The field is float (not
bool) so a future model with real scalar confidence can reuse the slot
without breaking C# consumers; the rename from `confidence` to
`face_present` makes the binary-today semantic precise.

**Note on debug fields:** `yaw_deg` / `pitch_deg` are emitted **only**
when the sidecar is launched with `--debug`. In production they are
absent keys (not `null`) — the per-frame JSON literally does not
contain them. Use during M5 UX-iteration gain tuning; do not enable
in shipped builds.

## What is explicitly forbidden

- Adding any field that contains landmark coordinates (e.g. `nose_x`,
  `eye_left`, `landmarks[]`).
- Adding any field that contains an image buffer (base64, JPEG bytes, etc.).
- Enabling `output_face_blendshapes=True` in the MediaPipe options
  (blendshapes are derived facial expressions and we don't need them;
  see `make_face_landmarker` in `head_tracker.py`).
- Writing frames or landmarks to any file under any flag, including debug.
- Adding any HTTP / socket / IPC channel that transmits face-derived data
  outside the sidecar's stdout JSON schema above.

## Diagnostic mode (--diag) — SEC-12

The sidecar accepts a `--diag` flag intended for developer debugging only.
When enabled, the sidecar writes **derived pose scalars only** (the same
`(cx, cy, yaw_deg, pitch_deg, luma, face_detected)` values already present
in the stdout JSON schema) to a local diagnostic file. It **never** writes
raw frames, raw landmark coordinates, or any other data not already in the
allowlist above.

**`--diag` is never passed by the C# host.** The enforcement point is
`BuildSidecarArgs` in `SidecarHeadPoseReader.cs`, which constructs the
sidecar's command-line arguments. That method does not include `--diag`
under any code path. A user or an attacker who manually launches
`head_tracker.exe --diag` cannot cause the C# host to produce frame or
landmark data through the IPC channel; the C# side only consumes the
stdout JSON schema, which is already the allowlist above regardless of any
sidecar flags.

## Process-lifecycle guarantee

When the sidecar exits, all face data is gone:
- MediaPipe state is destroyed when `landmarker.close()` runs in the
  `finally` block of `run()`.
- The OS reclaims any image buffers held by OpenCV.
- No file artifacts are left behind.

If you find a violation of any of the above in code review or runtime
inspection, treat it as a release blocker.
