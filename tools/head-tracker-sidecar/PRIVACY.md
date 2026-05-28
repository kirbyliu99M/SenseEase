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

Per-frame JSON line fields. **Only** these fields are permitted:

| field            | type           | purpose                                          |
|------------------|----------------|--------------------------------------------------|
| `t_ms`           | int            | monotonic timestamp, ms since sidecar start      |
| `cx`             | float [0, 1]   | smoothed + clamped FOV mask center X             |
| `cy`             | float [0, 1]   | smoothed + clamped FOV mask center Y             |
| `luma`           | float [0, 1]   | frame mean luma (Rec. 601 Y), normalized         |
| `face_detected`  | bool           | true when MediaPipe returned a transformation    |
| `confidence`     | float [0, 1]   | 1.0 when face present, 0.0 when not (see note)   |
| `yaw_deg`        | float or null  | pre-smoothing raw yaw, for debugging only        |
| `pitch_deg`      | float or null  | pre-smoothing raw pitch, for debugging only      |

**Note on `confidence`:** MediaPipe's FaceLandmarker IMAGE-mode API does
not expose a per-detection scalar score; the `min_face_presence_confidence`
threshold gates the detection internally. We therefore emit `1.0` when
a face landmark set is returned (and the matrix is well-formed), `0.0`
otherwise. This is documented in `head_tracker.py` next to where it is
assigned.

`yaw_deg` and `pitch_deg` are included **only** as debugging aids and are
considered low-sensitivity pose data (not raw landmarks). They may be
stripped by downstream consumers without changing the contract.

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

## Process-lifecycle guarantee

When the sidecar exits, all face data is gone:
- MediaPipe state is destroyed when `landmarker.close()` runs in the
  `finally` block of `run()`.
- The OS reclaims any image buffers held by OpenCV.
- No file artifacts are left behind.

If you find a violation of any of the above in code review or runtime
inspection, treat it as a release blocker.
