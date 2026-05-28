# Test fixtures

This directory holds local-only test videos used by `head_tracker.py`'s
soak mode. The actual video files (`*.mp4`, `*.webm`, etc.) are
**gitignored** — see `../.gitignore`.

## Recommended fixture: short Creative Commons face video

A 5–10 second clip of a person doing slow yaw / pitch sweeps is ideal.
Suggested source (public, well-known CC license):

- **Pexels** — search "face talking" / "person nodding", filter by free /
  CC0 / Pexels License. Save as `fixtures/test_face.mp4`.
- **Pixabay** — similar; filter by Pixabay License.

When you add a fixture, append a row to this table:

| filename                  | source URL              | license            | duration | notes |
|---------------------------|-------------------------|--------------------|----------|-------|
| _(none committed)_        | _(none)_                | _(n/a)_            | _(n/a)_  | per .gitignore, videos stay local |

## Stationary-face surrogate (no-internet fallback)

If you cannot obtain a face video on this machine (offline, sandbox,
etc.), the soak run is still useful as a **stationary-face surrogate**:

1. Take any short MP4 or even an image-derived MP4 (one frame looped).
2. Run `python head_tracker.py --input <surrogate.mp4> --soak 300`.
3. The sidecar will report `face_detected=false` for every frame and
   emit `(cx, cy) = (0.5, 0.5)`.
4. The perf report you get measures the **MediaPipe failure-path
   latency** (frame in -> no face out), which is the *upper bound* of
   the inference cost on this CPU — the success path is typically
   the same or slightly higher because the matrix decomposition adds
   ~tens of microseconds. So:
   - If the surrogate test shows ≥ 15 Hz comfortably, the success path
     is very likely also ≥ 15 Hz (verify on real face video before G1).
   - If the surrogate test shows < 15 Hz, the success path is almost
     certainly worse and we should plan accordingly.

Document this clearly in `PERF.md` if you use the surrogate, so the
advisor knows what was actually measured.
