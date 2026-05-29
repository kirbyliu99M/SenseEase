"""
SenseEase head-tracker sidecar (Path B: Python + MediaPipe FaceLandmarker).

Reads webcam (or a video file) at 480x360, runs MediaPipe FaceLandmarker at a
target 15 Hz, decomposes the 4x4 facial transformation matrix to Tait-Bryan
yaw/pitch/roll, maps yaw/pitch -> Chang-Hain FOV mask center (cx, cy),
computes mean frame luma, and emits one JSON line per processed frame on
stdout.

Locked parameters (do NOT change without sprint sign-off):
    CAPTURE_W, CAPTURE_H        = 480, 360
    TARGET_HZ                   = 15
    SMOOTH_ALPHA                = 0.18   # EMA on cx, cy independently
    YAW_TO_CX_GAIN              = 0.0233 # per degree, positive yaw -> right
    PITCH_TO_CY_GAIN            = 0.035  # per degree, SIGN-FLIPPED (see note)
    DEFAULT_CX, DEFAULT_CY      = 0.5, 0.5
    CX/CY clamped to [0.0, 1.0]

Pitch sign-flip note:
    MediaPipe's facial transformation matrix yields a "positive pitch" when
    the head tilts up (chin raises). Visually, when the user looks up the
    feature of interest in the field of view moves toward the *top* of the
    frame, i.e. cy should DECREASE (cy=0 is top). Therefore we apply a sign
    flip:    cy = 0.5 - PITCH_TO_CY_GAIN * pitch_deg
    while yaw maps "naturally":
            cx = 0.5 + YAW_TO_CX_GAIN * yaw_deg

Privacy (OBS-3, see PRIVACY.md):
    - No raw landmarks emitted.
    - No frame image data emitted.
    - No face data persisted to disk.
    - All face-derived state is discarded between frames except (cx, cy).
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time
from dataclasses import dataclass, asdict
from typing import Optional

import numpy as np

# OpenCV is used for capture, BGR->RGB conversion, and luma computation.
import cv2

# MediaPipe Tasks API (FaceLandmarker).
# We deliberately do NOT enable output_face_blendshapes (privacy + perf).
import mediapipe as mp
from mediapipe.tasks import python as mp_python
from mediapipe.tasks.python import vision as mp_vision


# ---------------------------------------------------------------------------
# Locked parameters
# ---------------------------------------------------------------------------
CAPTURE_W: int = 480
CAPTURE_H: int = 360
TARGET_HZ: float = 15.0
TARGET_DT_S: float = 1.0 / TARGET_HZ

SMOOTH_ALPHA: float = 0.18
YAW_TO_CX_GAIN: float = 0.0233   # per degree
PITCH_TO_CY_GAIN: float = 0.035  # per degree, applied with sign flip (see below)

DEFAULT_CX: float = 0.5
DEFAULT_CY: float = 0.5


# ---------------------------------------------------------------------------
# Per-frame output schema (JSON one-line)
# ---------------------------------------------------------------------------
@dataclass
class FrameRecord:
    """Schema for one stdout JSON line. Only these fields are emitted.

    G1 advisor decision (2026-05-29): the original `confidence` field is
    renamed to `face_present` and kept as float in {0.0, 1.0}. MediaPipe
    FaceLandmarker IMAGE-mode does not expose a per-detection scalar
    score; the binary value reflects "face matrix present this frame".
    The schema slot is preserved so a future model with real scalar
    confidence can reuse it without breaking C# consumers.

    G1 advisor decision (Q2): `yaw_deg` / `pitch_deg` are debug-only —
    emitted only when the sidecar is launched with `--debug`. Production
    consumers see them as absent keys (filtered out before json.dumps),
    not as `null` values.
    """
    t_ms: int
    cx: float
    cy: float
    luma: float
    face_detected: bool
    face_present: float
    yaw_deg: Optional[float] = None   # debug-only (pre-smoothing raw)
    pitch_deg: Optional[float] = None  # debug-only (pre-smoothing raw)


# ---------------------------------------------------------------------------
# Math helpers
# ---------------------------------------------------------------------------
def clamp01(v: float) -> float:
    if v < 0.0:
        return 0.0
    if v > 1.0:
        return 1.0
    return v


def decompose_yaw_pitch_roll(mat4: np.ndarray) -> tuple[float, float, float]:
    """
    Decompose a 4x4 MediaPipe facial_transformation_matrix's rotation block
    to Tait-Bryan yaw / pitch / roll in degrees.

    Convention: extract the 3x3 rotation R from the upper-left of mat4 and
    apply the standard intrinsic XYZ (pitch-yaw-roll) decomposition:
        pitch = atan2(-R[1,2], R[2,2])     # rotation about X
        yaw   = asin( R[0,2] )             # rotation about Y
        roll  = atan2(-R[0,1], R[0,0])     # rotation about Z

    This matches the convention used by most MediaPipe head-pose examples.
    Sign convention here (raw from the decomposition — see M5.1-A fix below
    for the compensated convention used downstream):
        yaw  > 0  -> head turns to the user's RIGHT (un-mirrored frame
                     convention; the --mirror=on default flips the frame
                     before MediaPipe sees it, so the user-facing post-
                     fix convention is set in the run() loop).
        pitch > 0 -> head tilts DOWN (chin drops). NOTE: an earlier comment
                     here claimed "pitch > 0 -> chin UP" — that was the
                     intuitive intent, but the actual atan2(-R[1,2], R[2,2])
                     decomposition with MediaPipe's Y-up world frame yields
                     positive pitch for chin DOWN, not chin UP. Verified by
                     M5.1-A diagnostic CSV analysis (user 2026-05-29: tilted
                     chin UP first, pitch went strongly NEGATIVE in the
                     data). The run() loop compensates by negating pitch
                     so the downstream convention matches "pitch > 0 = up".
        roll  > 0 -> head tilts toward user's RIGHT shoulder (raw; not used
                     by the (cx, cy) mapping).
    """
    R = mat4[:3, :3]
    # Clamp R[0,2] into [-1, 1] for numerical safety before asin.
    sy = float(R[0, 2])
    sy = max(-1.0, min(1.0, sy))
    yaw = math.degrees(math.asin(sy))
    # Standard pitch / roll
    pitch = math.degrees(math.atan2(-float(R[1, 2]), float(R[2, 2])))
    roll = math.degrees(math.atan2(-float(R[0, 1]), float(R[0, 0])))
    return yaw, pitch, roll


def yaw_pitch_to_cxcy(yaw_deg: float, pitch_deg: float) -> tuple[float, float]:
    """Map (yaw_deg, pitch_deg) -> (cx, cy) clamped to [0, 1].

    cx = 0.5 + YAW_TO_CX_GAIN * yaw_deg              (positive yaw -> right)
    cy = 0.5 - PITCH_TO_CY_GAIN * pitch_deg          (positive pitch -> up,
                                                       cy decreases toward 0)
    """
    cx = 0.5 + YAW_TO_CX_GAIN * yaw_deg
    cy = 0.5 - PITCH_TO_CY_GAIN * pitch_deg
    return clamp01(cx), clamp01(cy)


def frame_mean_luma_normalized(frame_bgr: np.ndarray) -> float:
    """Mean luma (Rec. 601 Y) of the frame, normalized to [0, 1].

    Using OpenCV's BGR->GRAY (Rec. 601 weights) is fast and matches the
    perceptual approximation we want for the future WebcamLuxProvider feed.
    """
    if frame_bgr is None or frame_bgr.size == 0:
        return 0.0
    gray = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2GRAY)
    return float(gray.mean()) / 255.0


# ---------------------------------------------------------------------------
# FaceLandmarker setup
# ---------------------------------------------------------------------------
DEFAULT_MODEL_FILENAME = "face_landmarker.task"


def resolve_model_path(explicit: Optional[str]) -> str:
    """Return a path to the FaceLandmarker .task model.

    Resolution order:
        1. --model CLI arg if provided.
        2. $SENSEEASE_FACE_LANDMARKER_MODEL env var.
        3. ./face_landmarker.task next to this script.
    Raises FileNotFoundError if none exist.
    """
    candidates: list[str] = []
    if explicit:
        candidates.append(explicit)
    env = os.environ.get("SENSEEASE_FACE_LANDMARKER_MODEL")
    if env:
        candidates.append(env)
    here = os.path.dirname(os.path.abspath(__file__))
    candidates.append(os.path.join(here, DEFAULT_MODEL_FILENAME))
    for c in candidates:
        if c and os.path.isfile(c):
            return c
    raise FileNotFoundError(
        "Could not find face_landmarker.task model. Tried: "
        + ", ".join(candidates)
        + "\nDownload from: "
        "https://storage.googleapis.com/mediapipe-models/face_landmarker/"
        "face_landmarker/float16/latest/face_landmarker.task"
    )


def make_face_landmarker(model_path: str) -> mp_vision.FaceLandmarker:
    """Construct a FaceLandmarker in IMAGE (synchronous) mode.

    We use IMAGE mode (not LIVE_STREAM) because we drive the loop at a
    fixed 15 Hz cadence ourselves and want deterministic per-frame timing
    suitable for soak measurement.

    Privacy: output_face_blendshapes=False, output_facial_transformation
    _matrixes=True. We need ONLY the transformation matrix.
    """
    base_options = mp_python.BaseOptions(model_asset_path=model_path)
    options = mp_vision.FaceLandmarkerOptions(
        base_options=base_options,
        running_mode=mp_vision.RunningMode.IMAGE,
        num_faces=1,
        output_face_blendshapes=False,
        output_facial_transformation_matrixes=True,
        min_face_detection_confidence=0.5,
        min_face_presence_confidence=0.5,
        min_tracking_confidence=0.5,
    )
    return mp_vision.FaceLandmarker.create_from_options(options)


# ---------------------------------------------------------------------------
# Smoother
# ---------------------------------------------------------------------------
class Ema2D:
    """Simple per-axis exponential moving average for (cx, cy)."""

    def __init__(self, alpha: float, init_x: float, init_y: float) -> None:
        self.alpha = alpha
        self.x = init_x
        self.y = init_y
        self._primed = False

    def update(self, x: float, y: float) -> tuple[float, float]:
        if not self._primed:
            self.x = x
            self.y = y
            self._primed = True
        else:
            a = self.alpha
            self.x = a * x + (1.0 - a) * self.x
            self.y = a * y + (1.0 - a) * self.y
        return self.x, self.y

    def reset_to(self, x: float, y: float) -> None:
        self.x = x
        self.y = y
        self._primed = True


# ---------------------------------------------------------------------------
# Capture helpers
# ---------------------------------------------------------------------------
def open_capture(spec: str) -> cv2.VideoCapture:
    """Open a webcam index or a video file."""
    # Try integer first
    try:
        idx = int(spec)
        cap = cv2.VideoCapture(idx, cv2.CAP_DSHOW if os.name == "nt" else 0)
        if cap.isOpened():
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, CAPTURE_W)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, CAPTURE_H)
            return cap
        cap.release()
    except ValueError:
        pass
    # Treat as file path
    cap = cv2.VideoCapture(spec)
    if not cap.isOpened():
        raise RuntimeError(f"Could not open capture source: {spec!r}")
    return cap


def read_frame(cap: cv2.VideoCapture, loop: bool) -> Optional[np.ndarray]:
    """Read one frame at the target capture resolution. Optionally loop on EOF."""
    ok, frame = cap.read()
    if not ok or frame is None:
        if loop:
            cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
            ok, frame = cap.read()
            if not ok or frame is None:
                return None
        else:
            return None
    # Force exactly 480x360 (so test videos at other resolutions still work).
    h, w = frame.shape[:2]
    if (w, h) != (CAPTURE_W, CAPTURE_H):
        frame = cv2.resize(frame, (CAPTURE_W, CAPTURE_H), interpolation=cv2.INTER_AREA)
    return frame


# ---------------------------------------------------------------------------
# Soak perf accumulator
# ---------------------------------------------------------------------------
class SoakStats:
    def __init__(self) -> None:
        self.latencies_ms: list[float] = []
        self.start_mono_s: float = time.monotonic()
        self.frames_processed: int = 0
        self.frames_with_face: int = 0

    def add(self, lat_ms: float, face_detected: bool) -> None:
        self.latencies_ms.append(lat_ms)
        self.frames_processed += 1
        if face_detected:
            self.frames_with_face += 1

    def report(self, target_hz: float) -> dict:
        elapsed_s = max(1e-6, time.monotonic() - self.start_mono_s)
        achieved_hz = self.frames_processed / elapsed_s
        expected_frames = target_hz * elapsed_s
        dropped = max(0.0, expected_frames - self.frames_processed)
        arr = np.array(self.latencies_ms, dtype=np.float64) if self.latencies_ms else np.zeros(1)

        # Peak RSS in MB. psutil is optional; fall back to resource on POSIX
        # or to 0.0 when unavailable.
        peak_rss_mb = _peak_rss_mb()

        return {
            "elapsed_s": round(elapsed_s, 3),
            "frames_processed": self.frames_processed,
            "frames_with_face": self.frames_with_face,
            "achieved_hz_mean": round(achieved_hz, 3),
            "target_hz": target_hz,
            "dropped_vs_target": int(round(dropped)),
            "latency_ms_mean": round(float(arr.mean()), 3),
            "latency_ms_p50": round(float(np.percentile(arr, 50)), 3),
            "latency_ms_p95": round(float(np.percentile(arr, 95)), 3),
            "latency_ms_p99": round(float(np.percentile(arr, 99)), 3),
            "latency_ms_max": round(float(arr.max()), 3),
            "peak_rss_mb": round(peak_rss_mb, 1),
            "cpu_model": _cpu_model(),
        }


def _peak_rss_mb() -> float:
    try:
        import psutil  # optional; declared in requirements
        return float(psutil.Process().memory_info().peak_wset if os.name == "nt"
                     else psutil.Process().memory_info().rss) / (1024 * 1024)
    except Exception:
        pass
    try:
        import resource  # POSIX
        ru = resource.getrusage(resource.RUSAGE_SELF)
        # ru_maxrss is KB on Linux, bytes on macOS. Be conservative.
        val = ru.ru_maxrss
        # If it looks like KB (typical Linux), convert; otherwise assume bytes.
        return (val / 1024.0) if val > 1024 * 1024 else (val / (1024.0 * 1024.0))
    except Exception:
        return 0.0


def _cpu_model() -> str:
    # Windows: wmic / registry; POSIX: /proc/cpuinfo.
    try:
        if os.name == "nt":
            import subprocess
            out = subprocess.check_output(
                ["wmic", "cpu", "get", "Name"], stderr=subprocess.DEVNULL, timeout=5
            ).decode("utf-8", errors="ignore")
            lines = [ln.strip() for ln in out.splitlines() if ln.strip()]
            # First line is the "Name" header.
            return lines[1] if len(lines) >= 2 else "unknown"
        with open("/proc/cpuinfo", "r", encoding="utf-8") as f:
            for line in f:
                if "model name" in line:
                    return line.split(":", 1)[1].strip()
    except Exception:
        pass
    return "unknown"


# ---------------------------------------------------------------------------
# Main pipeline
# ---------------------------------------------------------------------------
def run(
    input_spec: str,
    soak_seconds: Optional[int],
    model_path: Optional[str],
    mirror: bool,
    loop_video: bool,
    debug: bool = False,
    diag_path: Optional[str] = None,
) -> int:
    model_resolved = resolve_model_path(model_path)
    landmarker = make_face_landmarker(model_resolved)
    cap = open_capture(input_spec)

    smoother = Ema2D(SMOOTH_ALPHA, DEFAULT_CX, DEFAULT_CY)
    stats = SoakStats() if soak_seconds is not None else None
    soak_loop = loop_video or (soak_seconds is not None)

    # M5.1-A PRECONDITION (2026-05-29): per-frame diagnostic CSV for the
    # "sometimes inverted" investigation. When --diag is set, every frame
    # writes a row with raw pose (pre-smoothing yaw/pitch, mirror-corrected)
    # + raw cx/cy + smoothed cx/cy + face_present + face_detected. Allows
    # post-run analysis of:
    #   - whether yaw/pitch monotonic in single-axis tests (mirror sign correct)
    #   - whether mixed motion produces axis jumps (gimbal lock vs noise)
    #   - what frame-to-frame jump magnitudes look like (Layer 1 threshold)
    diag_file = None
    if diag_path is not None:
        diag_file = open(diag_path, "w", encoding="utf-8", buffering=1)
        diag_file.write("t_ms,yaw_deg,pitch_deg,cx_raw,cy_raw,cx_smoothed,cy_smoothed,face_detected,face_present,mirror\n")

    # M5.1-A Layer 1 (2026-05-29): frame validity filter. On any frame where
    # face_present == 0 OR yaw / pitch jumps > MAX_JUMP_PER_FRAME_DEG since
    # the last VALID sample, skip emitting and repeat the previously-valid
    # (cx, cy). Diagnose analysis showed max legitimate single-frame jump
    # ≈ 13° during rapid mixed motion; threshold at 25° catches detection
    # glitches without false-positives at observed motion ceilings.
    MAX_JUMP_PER_FRAME_DEG = 25.0
    last_valid_yaw: Optional[float] = None
    last_valid_pitch: Optional[float] = None
    last_valid_cx_raw: float = DEFAULT_CX
    last_valid_cy_raw: float = DEFAULT_CY
    skipped_frame_count = 0

    t0_mono = time.monotonic()
    next_tick = t0_mono
    deadline = (t0_mono + soak_seconds) if soak_seconds is not None else None

    try:
        while True:
            now = time.monotonic()
            if deadline is not None and now >= deadline:
                break
            # Cadence: wait until the next 15 Hz tick.
            if now < next_tick:
                time.sleep(next_tick - now)
            next_tick += TARGET_DT_S

            frame_bgr = read_frame(cap, loop=soak_loop)
            if frame_bgr is None:
                # End of stream and not looping.
                break
            if mirror:
                frame_bgr = cv2.flip(frame_bgr, 1)

            # Time only the inference + decomposition step (the "hot" path).
            infer_start = time.perf_counter()

            # MediaPipe wants an RGB mp.Image.
            rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
            mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
            result = landmarker.detect(mp_image)

            face_detected = False
            face_present = 0.0  # MediaPipe FaceLandmarker doesn't expose a per-detection
                                # score in IMAGE mode; presence implies >= configured
                                # min_face_presence_confidence (0.5). We report 1.0 when
                                # present, 0.0 when absent. G1-renamed from `confidence`;
                                # schema slot preserved for v1.0 scalar-confidence model.
            raw_yaw: Optional[float] = None
            raw_pitch: Optional[float] = None
            cx_raw = DEFAULT_CX
            cy_raw = DEFAULT_CY

            if result and result.facial_transformation_matrixes:
                mat = np.asarray(result.facial_transformation_matrixes[0],
                                 dtype=np.float64)
                if mat.shape == (4, 4):
                    yaw, pitch, _roll = decompose_yaw_pitch_roll(mat)
                    # M5.1-A (2026-05-29): the documentation in
                    # decompose_yaw_pitch_roll above claimed "pitch > 0 =
                    # chin UP" but the actual atan2(-R[1,2], R[2,2])
                    # decomposition returns the OPPOSITE — chin-up gives
                    # negative pitch. The M2 cy mapping was then computed
                    # against the wrong assumption, so chin-up moved the
                    # mask to the BOTTOM. Diagnose CSV from user (chin-up-
                    # first procedure) showed pitch going to −40° during
                    # chin-up motion, confirming the inversion. Negate
                    # unconditionally (not mirror-conditional — horizontal
                    # flip doesn't affect rotation around the X axis).
                    # M5.1-A v2 (2026-05-29): YAW NEGATION REMOVED. UX-iter 3
                    # added "if mirror: yaw = -yaw" to fix what user reported
                    # as "sometimes inverted". Diagnose CSV + M5.1-A re-test
                    # revealed the negation made yaw GENUINELY inverted. The
                    # "sometimes inverted" symptom was actually smoothing lag
                    # during rapid motion (raw vs smoothed direction opposite
                    # during reversals), not a sign issue. Removing the
                    # negation restores the M2 original convention: positive
                    # raw yaw maps to cx > 0.5 = mask right, which matches
                    # physical head-right under the default --mirror=on flow.
                    pitch = -pitch
                    raw_yaw, raw_pitch = yaw, pitch
                    cx_raw, cy_raw = yaw_pitch_to_cxcy(yaw, pitch)
                    face_detected = True
                    face_present = 1.0
                else:
                    face_present = 0.0
            else:
                face_present = 0.0

            # M5.1-A Layer 1: validity check. If face missing OR pose jumped
            # too far from the last valid sample, treat this frame as a
            # detection glitch and repeat the last valid (cx, cy).
            frame_is_valid = face_present > 0.5
            if frame_is_valid and last_valid_yaw is not None:
                yaw_jump   = abs(raw_yaw   - last_valid_yaw)   if raw_yaw   is not None else 0.0
                pitch_jump = abs(raw_pitch - last_valid_pitch) if raw_pitch is not None else 0.0
                if yaw_jump > MAX_JUMP_PER_FRAME_DEG or pitch_jump > MAX_JUMP_PER_FRAME_DEG:
                    frame_is_valid = False

            if not frame_is_valid:
                # Skip glitch / dropout: feed the smoother the last valid
                # raw (so EMA continues converging) instead of injecting
                # noise. raw_yaw / raw_pitch left as-is for diag CSV so the
                # rejection is visible in the trace.
                cx_raw = last_valid_cx_raw
                cy_raw = last_valid_cy_raw
                skipped_frame_count += 1
            else:
                last_valid_yaw   = raw_yaw
                last_valid_pitch = raw_pitch
                last_valid_cx_raw = cx_raw
                last_valid_cy_raw = cy_raw

            cx, cy = smoother.update(cx_raw, cy_raw)
            # Clamp again post-smoothing for safety.
            cx, cy = clamp01(cx), clamp01(cy)
            luma = frame_mean_luma_normalized(frame_bgr)

            infer_end = time.perf_counter()
            latency_ms = (infer_end - infer_start) * 1000.0

            # M5.1-A diagnostic CSV append (only when --diag is set).
            if diag_file is not None:
                t_ms = int((time.monotonic() - t0_mono) * 1000.0)
                yaw_str   = f"{raw_yaw:.3f}"   if raw_yaw   is not None else ""
                pitch_str = f"{raw_pitch:.3f}" if raw_pitch is not None else ""
                diag_file.write(
                    f"{t_ms},{yaw_str},{pitch_str},"
                    f"{cx_raw:.4f},{cy_raw:.4f},{cx:.4f},{cy:.4f},"
                    f"{int(face_detected)},{face_present:.1f},"
                    f"{int(mirror)}\n"
                )

            rec = FrameRecord(
                t_ms=int((time.monotonic() - t0_mono) * 1000.0),
                cx=round(cx, 4),
                cy=round(cy, 4),
                luma=round(luma, 4),
                face_detected=face_detected,
                face_present=round(face_present, 3),
                yaw_deg=(round(raw_yaw, 2) if (debug and raw_yaw is not None) else None),
                pitch_deg=(round(raw_pitch, 2) if (debug and raw_pitch is not None) else None),
            )

            if stats is not None:
                stats.add(latency_ms, face_detected)
                # In soak mode we suppress per-frame JSON output.
            else:
                # Filter out None debug fields so production schema is exactly
                # the production fields — no null keys for absent debug data.
                payload = {k: v for k, v in asdict(rec).items() if v is not None}
                sys.stdout.write(json.dumps(payload, separators=(",", ":")) + "\n")
                sys.stdout.flush()

    finally:
        try:
            landmarker.close()
        except Exception:
            pass
        cap.release()
        if diag_file is not None:
            try:
                diag_file.close()
            except Exception:
                pass

    if stats is not None:
        report = stats.report(TARGET_HZ)
        # In soak mode we emit ONE JSON object (the report) on stdout.
        sys.stdout.write(json.dumps(report, indent=2) + "\n")
        sys.stdout.flush()
    return 0


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def build_arg_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="SenseEase head-tracker sidecar (Path B). "
                    "Emits one JSON line per processed frame at ~15 Hz, "
                    "or a single soak-report JSON when --soak is set.",
    )
    p.add_argument(
        "--input",
        required=True,
        help="Webcam index (e.g. 0) OR path to a video file.",
    )
    p.add_argument(
        "--soak",
        type=int,
        default=None,
        metavar="SECONDS",
        help="Soak mode: run for N seconds, suppress per-frame JSON, "
             "emit a single perf-report JSON at the end. The input is "
             "auto-looped if it's a finite video.",
    )
    p.add_argument(
        "--model",
        default=None,
        help="Path to face_landmarker.task. Falls back to "
             "$SENSEEASE_FACE_LANDMARKER_MODEL then ./face_landmarker.task.",
    )
    # G1 advisor decision (Q4): default ON. Selfie-view feels natural to
    # most users (head right → mask center right). Use --no-mirror to
    # disable when the camera driver already mirrors or the user prefers
    # raw-frame yaw mapping.
    p.add_argument(
        "--mirror",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="Horizontal-flip incoming frames (selfie-view). Default: ON. "
             "Use --no-mirror to disable.",
    )
    p.add_argument(
        "--loop",
        action="store_true",
        help="Loop a finite video file on EOF (implied by --soak).",
    )
    # G1 advisor decision (Q2): yaw_deg / pitch_deg are emitted only when
    # --debug is set. Production omits them entirely (filtered before
    # json.dumps) to keep the schema minimal.
    p.add_argument(
        "--debug",
        action="store_true",
        help="Emit pre-smoothing yaw_deg / pitch_deg in per-frame JSON. "
             "Off by default; useful during M5 UX-iteration gain tuning.",
    )
    # M5.1-A PRECONDITION (2026-05-29): per-frame diagnostic CSV for the
    # "sometimes inverted" investigation. Stays orthogonal to --debug:
    # --debug controls stdout JSON schema; --diag dumps a raw analysis CSV.
    p.add_argument(
        "--diag",
        type=str,
        default=None,
        metavar="PATH",
        help="Write per-frame diagnostic CSV (t_ms, yaw_deg, pitch_deg, "
             "cx_raw, cy_raw, cx_smoothed, cy_smoothed, face_detected, "
             "face_present, mirror) to PATH. Used by the M5.1-A inverted-"
             "direction diagnosis. Off by default.",
    )
    return p


def main(argv: Optional[list[str]] = None) -> int:
    args = build_arg_parser().parse_args(argv)
    return run(
        input_spec=args.input,
        soak_seconds=args.soak,
        model_path=args.model,
        mirror=args.mirror,
        loop_video=args.loop,
        debug=args.debug,
        diag_path=args.diag,
    )


if __name__ == "__main__":
    sys.exit(main())
