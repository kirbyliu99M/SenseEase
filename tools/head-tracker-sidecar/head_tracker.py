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
    R = np.asarray(mat4[:3, :3], dtype=float)
    # Beta-1 angle fix (B, 2026-06-14): MediaPipe's facial_transformation_matrix is a Procrustes
    # SIMILARITY transform — its 3x3 block can carry SCALE, so the raw entries are s*rotation, not
    # pure-rotation values. The old code did asin(R[0,2]) on the RAW block and clamped to [-1,1]
    # "for numerical safety" — but that clamp was HIDING the scale distortion (|s*sin(yaw)| could
    # exceed 1), giving systematically wrong yaw (Kirby symptom #1: "facing often incorrect").
    # Orthonormalise via SVD (R_rot = U @ Vt) to divide out the (positive) scale before the Euler
    # extraction; force a proper rotation (det +1) to reject a reflection from a degenerate frame.
    # Positive uniform scale preserves entry SIGNS, so the downstream sign conventions are
    # unchanged — only the magnitudes are corrected. Falls back to the raw block on SVD failure.
    try:
        U, _s, Vt = np.linalg.svd(R)
        d = 1.0 if np.linalg.det(U @ Vt) >= 0.0 else -1.0
        R = U @ np.diag([1.0, 1.0, d]) @ Vt
    except np.linalg.LinAlgError:
        pass  # keep the raw block; the clamp below still guards asin
    # asin is now applied to a true rotation entry; the clamp is purely defensive.
    sy = max(-1.0, min(1.0, float(R[0, 2])))
    yaw = math.degrees(math.asin(sy))
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
# THEME-4 (Beta-1, 2026-06-14): monotonic-timestamp guard for VIDEO mode
# ---------------------------------------------------------------------------
def monotonic_video_ts_ms(computed_ts_ms: int, last_ts_ms: int) -> int:
    """Return a STRICTLY-increasing int-ms timestamp for detect_for_video().

    MediaPipe's VIDEO running mode raises if a frame's timestamp is not
    strictly greater than the previous one. The loop clock can stall, resync,
    or (under the SIDE-03 scheduler-resync) snap forward; any of those can
    yield a ``computed_ts_ms`` that is <= the last value we fed. Clamp UP so
    the contract (strict monotonicity) is never violated:

        result = max(last_ts_ms + 1, computed_ts_ms)

    Pure — no side effects; the caller owns ``last_ts_ms`` bookkeeping. The
    FIRST call should pass ``last_ts_ms = -1`` so a computed 0 is accepted
    as-is (max(0, 0) == 0).
    """
    floor = last_ts_ms + 1
    return floor if computed_ts_ms < floor else computed_ts_ms


# ---------------------------------------------------------------------------
# THEME-4 (Beta-1, 2026-06-14): multi-face primary selection (D-20)
# ---------------------------------------------------------------------------
# The sidecar requests num_faces=3 (cheap headroom). In a 2+ person scene
# MediaPipe does NOT guarantee a stable ordering of result.face_landmarks /
# facial_transformation_matrixes frame-to-frame, so blindly taking index 0
# makes the mask hop between people. D-20: "largest + most-central + sticky =
# the seated user wins."
#
# Scoring per face (computed purely from the normalized 2D landmark list — the
# 4x4 transformation matrix has NO 2D extent, so bbox MUST come from landmarks):
#   score = bbox_area * (1 - CENTRALITY_WEIGHT * dist_from_frame_centre)
# where dist_from_frame_centre is the Euclidean distance of the bbox centre
# from (0.5, 0.5), capped, so a large but off-to-the-edge background face is
# penalised relative to a slightly-smaller seated-and-centred user.
#
# Hysteresis / stickiness (anti flip-flop):
#   - If a previously-chosen face is still present and its score is within
#     STICKY_SCORE_MARGIN of the new best score, KEEP it (don't switch for a
#     momentary advantage).
#   - When multiple candidates are within the margin, prefer the one whose
#     bbox centre is NEAREST last frame's chosen centre (spatial continuity).
#   - If the primary is lost for more than STICKY_LOST_FRAMES consecutive
#     frames, clear the stickiness so re-acquisition picks fresh.

CENTRALITY_WEIGHT: float = 0.6   # how hard off-centre faces are penalised
STICKY_SCORE_MARGIN: float = 0.15  # keep prior face if within 15% of new max
STICKY_LOST_FRAMES: int = 5        # clear stickiness after N lost frames


def _face_bbox(landmarks) -> tuple[float, float, float, float]:
    """Return (min_x, min_y, max_x, max_y) of a normalized landmark list.

    ``landmarks`` is one entry of result.face_landmarks — a sequence of
    objects each exposing .x / .y in normalized [0, 1] image coordinates.
    Pure; raises ValueError on an empty list (caller guards)."""
    xs_min = xs_max = None
    ys_min = ys_max = None
    for lm in landmarks:
        x = float(lm.x)
        y = float(lm.y)
        if xs_min is None or x < xs_min:
            xs_min = x
        if xs_max is None or x > xs_max:
            xs_max = x
        if ys_min is None or y < ys_min:
            ys_min = y
        if ys_max is None or y > ys_max:
            ys_max = y
    if xs_min is None:
        raise ValueError("empty landmark list")
    return xs_min, ys_min, xs_max, ys_max


def _bbox_metrics(bbox: tuple[float, float, float, float]) -> tuple[float, float, float]:
    """From (min_x, min_y, max_x, max_y) return (area, centre_x, centre_y)."""
    min_x, min_y, max_x, max_y = bbox
    w = max(0.0, max_x - min_x)
    h = max(0.0, max_y - min_y)
    return w * h, (min_x + max_x) * 0.5, (min_y + max_y) * 0.5


def _face_score(area: float, cx: float, cy: float) -> float:
    """Score = area attenuated by distance of bbox centre from frame centre.

    dist is in normalized image units; the maximum possible centre-to-corner
    distance is ~0.707, so we normalise by that to keep the penalty in a
    sane [0, ~1] range before the weight is applied. A perfectly centred face
    keeps full area; an edge face is reduced by up to CENTRALITY_WEIGHT."""
    dx = cx - 0.5
    dy = cy - 0.5
    dist = math.sqrt(dx * dx + dy * dy)
    norm_dist = min(1.0, dist / 0.70710678)  # /sqrt(0.5) -> [0, 1]
    return area * (1.0 - CENTRALITY_WEIGHT * norm_dist)


class FaceSelector:
    """Owns the cross-frame stickiness for primary-face selection (D-20).

    Pure-ish: ``select`` takes a list of per-face (area, cx, cy, score) tuples
    (already derived from result.face_landmarks by the caller) and returns the
    chosen index, with hysteresis against the previously-chosen face. State is
    only the previous chosen centre + a lost-frame counter, so it is trivially
    unit-testable without MediaPipe."""

    def __init__(
        self,
        *,
        score_margin: float = STICKY_SCORE_MARGIN,
        lost_frames: int = STICKY_LOST_FRAMES,
    ) -> None:
        self._score_margin = score_margin
        self._lost_frames = lost_frames
        self._prev_centre: Optional[tuple[float, float]] = None
        self._lost_count = 0

    def select(self, faces: list[tuple[float, float, float, float]]) -> Optional[int]:
        """Choose the primary face index from ``faces``.

        ``faces[i]`` = (area, centre_x, centre_y, score). Returns the chosen
        index, or None when ``faces`` is empty (primary lost this frame).

        Selection:
          1. Find the max-score face (the candidate to switch to).
          2. If a previous centre exists, gather all faces whose score is
             within ``score_margin`` (relative) of the max score — these are
             "good enough" candidates. Among them, prefer the face nearest the
             previous chosen centre (spatial continuity / stickiness). This
             means a momentarily-larger background face cannot steal focus from
             the seated user as long as the seated user stays within the
             margin.
          3. With no previous centre (fresh acquisition or after a long loss),
             just take the max-score face.
        """
        if not faces:
            # Primary lost this frame. Count toward the clear-stickiness bound.
            self._lost_count += 1
            if self._lost_count > self._lost_frames:
                self._prev_centre = None
            return None

        # A present primary resets the lost counter.
        self._lost_count = 0

        # Candidate to switch to: the global max score.
        best_idx = 0
        best_score = faces[0][3]
        for i in range(1, len(faces)):
            if faces[i][3] > best_score:
                best_score = faces[i][3]
                best_idx = i

        chosen = best_idx
        if self._prev_centre is not None and best_score > 0.0:
            # Faces within the relative margin of the best score are "sticky-
            # eligible"; among them pick the one nearest last chosen centre.
            threshold = best_score * (1.0 - self._score_margin)
            pcx, pcy = self._prev_centre
            nearest_idx = None
            nearest_d2 = None
            for i, (_area, cx, cy, score) in enumerate(faces):
                if score >= threshold:
                    d2 = (cx - pcx) ** 2 + (cy - pcy) ** 2
                    if nearest_d2 is None or d2 < nearest_d2:
                        nearest_d2 = d2
                        nearest_idx = i
            if nearest_idx is not None:
                chosen = nearest_idx

        # Record the chosen centre for next-frame stickiness.
        self._prev_centre = (faces[chosen][1], faces[chosen][2])
        return chosen

    def reset(self) -> None:
        self._prev_centre = None
        self._lost_count = 0


def select_primary_face(result, selector: FaceSelector) -> Optional[int]:
    """Pure-helper bridge: derive per-face metrics from a FaceLandmarker
    result and delegate ranking/stickiness to ``selector``.

    Returns the chosen index into BOTH result.face_landmarks[i] and
    result.facial_transformation_matrixes[i] (they are index-aligned), or
    None when no usable face is present. Faces with an empty/degenerate
    landmark list are skipped (defensive — MediaPipe shouldn't emit them).
    """
    if result is None or not getattr(result, "face_landmarks", None):
        return selector.select([])

    faces: list[tuple[float, float, float, float]] = []
    index_map: list[int] = []  # faces[j] corresponds to original result index
    for orig_i, lms in enumerate(result.face_landmarks):
        if not lms:
            continue
        try:
            bbox = _face_bbox(lms)
        except ValueError:
            continue
        area, cx, cy = _bbox_metrics(bbox)
        if area <= 0.0:
            continue
        faces.append((area, cx, cy, _face_score(area, cx, cy)))
        index_map.append(orig_i)

    chosen = selector.select(faces)
    if chosen is None:
        return None
    return index_map[chosen]


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
    """Construct a FaceLandmarker in VIDEO mode.

    THEME-4 (Beta-1, 2026-06-14): switched from IMAGE to VIDEO running mode.
    In IMAGE mode the sidecar called landmarker.detect() per frame with NO
    temporal tracking, so min_tracking_confidence was inert and the soak
    showed ~41% dropout for a single still user (review findings #11/#12/#13).
    VIDEO mode (detect_for_video with a strictly-monotonic timestamp) carries
    detection state across frames, making min_tracking_confidence live and
    cutting the per-frame re-detection dropout.

    We use VIDEO (not LIVE_STREAM) because we drive the loop at a fixed 15 Hz
    cadence ourselves and want deterministic, synchronous per-frame timing
    suitable for soak measurement — LIVE_STREAM is async/callback-based.

    num_faces=3 (was 1): cheap headroom so select_primary_face / FaceSelector
    can pick the seated user in a 2+ person scene instead of whichever face
    MediaPipe happened to order at index 0 that frame (D-20).

    Confidence tuning (now that VIDEO mode makes tracking live):
      min_face_detection_confidence = 0.4   (lower so a re-acquire under poor
                                             lighting is easier)
      min_tracking_confidence       = 0.5   (baseline; 2026-06-15 reverted from
                                             a 0.6 trial that dropped the carried
                                             track too readily under marginal
                                             light → fewer tracking dropouts)
      min_face_presence_confidence  = 0.5   (unchanged)
    The LOCKED downstream params (gains, jump filter, AdaptiveEma2D) are NOT
    touched — the running-mode + face-selection fix is validated in isolation.

    Privacy: output_face_blendshapes=False, output_facial_transformation
    _matrixes=True. We need ONLY the transformation matrix (+ landmarks for
    2D bbox-based primary-face selection, which are never emitted).
    """
    base_options = mp_python.BaseOptions(model_asset_path=model_path)
    options = mp_vision.FaceLandmarkerOptions(
        base_options=base_options,
        running_mode=mp_vision.RunningMode.VIDEO,
        num_faces=3,
        output_face_blendshapes=False,
        output_facial_transformation_matrixes=True,
        min_face_detection_confidence=0.4,
        min_face_presence_confidence=0.5,
        min_tracking_confidence=0.5,
    )
    return mp_vision.FaceLandmarker.create_from_options(options)


# ---------------------------------------------------------------------------
# Smoother
# ---------------------------------------------------------------------------
class Ema2D:
    """Simple per-axis exponential moving average for (cx, cy).

    Retained for tests / legacy; production now uses AdaptiveEma2D.
    """

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


class AdaptiveEma2D:
    """M5.1-C (2026-05-29) — velocity-driven adaptive EMA for (cx, cy).

    Replaces the constant α=0.18 smoother that drove the user-reported lag
    (sidecar EMA converges to 90% in ~770ms regardless of how slow the head
    is moving, so rapid head turns trail by half a second). This class
    measures input velocity from per-frame yaw/pitch deltas and picks an α
    accordingly:

      ===========================================================
      velocity        α       EMA τ at 15 Hz   regime
      ===========================================================
      < 1°/frame    0.08      ~800 ms          anti-jitter
                              (head holding still — heavy smoothing
                               so MediaPipe noise doesn't visibly
                               oscillate the mask)
      1-5°/frame    linear    interp           natural motion
                    0.08→0.30 800ms→200ms      (responsive)
      5-10°/frame   linear    interp           medium-rapid
                    0.30→0.50 200ms→100ms
      > 10°/frame   0.55      ~84 ms           snappy
                              (rapid head turn — minimal smoothing
                               so the mask catches up immediately)
      ===========================================================

    Velocity itself is EMA-smoothed (α=0.4) so that single-frame noise
    doesn't oscillate α — otherwise a borderline-fast frame followed by
    a quiet frame would alternate smoothing regimes and produce visible
    judder.

    The PRECONDITION measurement (2026-05-29) confirmed sidecar full-load
    sample density at ~15 Hz, which means the 80ms fast-motion bound
    covers ~1.2 samples — just enough for the EMA to converge in two
    ticks. Per advisor: if a future change drops sidecar Hz below 12,
    revisit the lower bound.
    """

    # α at each velocity regime (piecewise linear between control points).
    ALPHA_LOW       = 0.08   # < VEL_LOW
    ALPHA_MED_LOW   = 0.30   # at VEL_MED
    ALPHA_MED_HIGH  = 0.50   # at VEL_HIGH
    ALPHA_HIGH      = 0.55   # > VEL_HIGH

    # Velocity thresholds (degrees per frame of combined yaw+pitch motion).
    VEL_LOW         = 1.0
    VEL_MED         = 5.0
    VEL_HIGH        = 10.0

    # α for the velocity-smoothing EMA (smooth the velocity signal itself
    # so single-frame deltas don't cause α-regime thrashing).
    VELOCITY_SMOOTH_ALPHA = 0.4

    def __init__(self, init_x: float, init_y: float) -> None:
        self.x = init_x
        self.y = init_y
        self._primed = False
        self._prev_yaw: Optional[float] = None
        self._prev_pitch: Optional[float] = None
        self._velocity = 0.0
        self._current_alpha = self.ALPHA_LOW  # exposed for diag CSV

    def update(self, x: float, y: float, yaw_deg: float, pitch_deg: float) -> tuple[float, float]:
        if not self._primed:
            self.x = x
            self.y = y
            self._primed = True
            self._prev_yaw = yaw_deg
            self._prev_pitch = pitch_deg
            return self.x, self.y

        # Compute instantaneous velocity (combined yaw + pitch motion).
        instant_v = 0.0
        if self._prev_yaw is not None and self._prev_pitch is not None:
            dyaw   = yaw_deg   - self._prev_yaw
            dpitch = pitch_deg - self._prev_pitch
            instant_v = math.sqrt(dyaw * dyaw + dpitch * dpitch)
        # EMA-smooth velocity itself.
        self._velocity = (self.VELOCITY_SMOOTH_ALPHA * instant_v
                          + (1.0 - self.VELOCITY_SMOOTH_ALPHA) * self._velocity)
        self._prev_yaw = yaw_deg
        self._prev_pitch = pitch_deg

        a = self._velocity_to_alpha(self._velocity)
        self._current_alpha = a

        self.x = a * x + (1.0 - a) * self.x
        self.y = a * y + (1.0 - a) * self.y
        return self.x, self.y

    def _velocity_to_alpha(self, v: float) -> float:
        if v < self.VEL_LOW:
            return self.ALPHA_LOW
        if v < self.VEL_MED:
            t = (v - self.VEL_LOW) / (self.VEL_MED - self.VEL_LOW)
            return self.ALPHA_LOW + t * (self.ALPHA_MED_LOW - self.ALPHA_LOW)
        if v < self.VEL_HIGH:
            t = (v - self.VEL_MED) / (self.VEL_HIGH - self.VEL_MED)
            return self.ALPHA_MED_LOW + t * (self.ALPHA_MED_HIGH - self.ALPHA_MED_LOW)
        return self.ALPHA_HIGH

    @property
    def current_alpha(self) -> float:
        return self._current_alpha

    @property
    def current_velocity(self) -> float:
        return self._velocity

    def reset_to(self, x: float, y: float) -> None:
        self.x = x
        self.y = y
        self._primed = True
        self._prev_yaw = None
        self._prev_pitch = None
        self._velocity = 0.0
        self._current_alpha = self.ALPHA_LOW


# ---------------------------------------------------------------------------
# Capture helpers
# ---------------------------------------------------------------------------
class WebcamOpenError(RuntimeError):
    """Raised when a webcam *index* could not be opened (busy / denied /
    absent). SIDE-07: distinct from a file-path open failure so the caller
    (and the C# host's stderr tail) gets an unambiguous diagnostic. The
    misleading SIDE-07 fall-through — where a failed webcam index silently
    re-tried the same digit string as a file path and produced a "could not
    open file" error — is gone: once int(spec) parses we NEVER touch the
    file branch."""


def open_capture(spec: str) -> cv2.VideoCapture:
    """Open a webcam index or a video file.

    SIDE-07 (2026-06-11, D-11 capture-loop unlock): when ``spec`` parses as
    an integer it is treated as a webcam index *exclusively*. A failure to
    open that index raises :class:`WebcamOpenError` ("webcam index N could
    not be opened (busy/denied/absent)") instead of falling through to the
    file-path branch — the old fall-through re-interpreted "0" as a filename
    and produced a misleading "could not open capture source: '0'" error.
    Only a genuinely non-integer ``spec`` reaches the file-path branch.
    """
    # Try integer first. A spec that parses as an int is a webcam index and
    # NEVER falls through to the file branch (SIDE-07).
    try:
        idx = int(spec)
    except ValueError:
        idx = None

    if idx is not None:
        cap = cv2.VideoCapture(idx, cv2.CAP_DSHOW if os.name == "nt" else 0)
        if cap.isOpened():
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, CAPTURE_W)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, CAPTURE_H)
            # Exposure is left on AUTO (the reliable baseline). A previous "exposure hardening"
            # pinned CAP_PROP_AUTO_EXPOSURE=0.25 (manual) WITHOUT ever setting CAP_PROP_EXPOSURE,
            # so the frame ran at the camera's cold manual default — markedly underexposed in normal
            # indoor light — and MediaPipe found no face ("幾乎偵測不到頭", 2026-06-15 regression).
            # If lighting-driven dropout needs addressing later, set BOTH manual auto-exposure AND a
            # real CAP_PROP_EXPOSURE value; never pin manual without a value.
            return cap
        cap.release()
        raise WebcamOpenError(
            f"webcam index {idx} could not be opened (busy/denied/absent)"
        )

    # Non-integer spec: treat as a file path.
    cap = cv2.VideoCapture(spec)
    if not cap.isOpened():
        raise RuntimeError(f"Could not open capture source: {spec!r}")
    return cap


# ---------------------------------------------------------------------------
# SIDE-02: camera-loss resilience (D-11 capture-loop unlock, 2026-06-11)
# ---------------------------------------------------------------------------
# A single failed cap.read() previously broke the loop -> clean exit 0 ->
# head-tracking dead for the whole session with no reconnect. CaptureManager
# wraps the VideoCapture and, on sustained read failure, releases + re-opens
# the source on a bounded backoff. While failing it yields None frames so the
# run() loop emits face_detected=false records (the C# host's grace path then
# centers the mask). Only after REOPEN_WINDOW_S of *sustained* failure does it
# give up — run() then exits NON-zero so the host's OnProcessExited fires and
# surfaces the death rather than a silent code-0 exit.
#
# Re-open is NOT attempted for a finite video file in non-loop mode: a real
# EOF there is a legitimate end-of-stream, not a camera loss.

REOPEN_INTERVAL_S: float = 2.0   # re-open attempt cadence while failing
REOPEN_WINDOW_S:   float = 60.0  # sustained-failure bound before giving up


class CaptureManager:
    """Owns the VideoCapture and re-opens it on sustained read failure.

    Usage::

        mgr = CaptureManager(spec, loop=..., reopenable=...)
        mgr.open()                 # initial open (may raise on first open)
        frame = mgr.read(now_mono) # frame, or None while degraded
        if mgr.give_up:            # sustained failure exceeded the bound
            ...                    # caller exits non-zero
        mgr.release()

    The class is deliberately small and side-effect-isolated so it is unit-
    testable with a fake capture/opener injected — no real webcam or model.
    """

    def __init__(
        self,
        spec: str,
        loop: bool,
        reopenable: bool,
        *,
        reopen_interval_s: float = REOPEN_INTERVAL_S,
        reopen_window_s:   float = REOPEN_WINDOW_S,
        opener=open_capture,
        log=None,
    ) -> None:
        self._spec       = spec
        self._loop       = loop
        self._reopenable = reopenable
        self._reopen_interval_s = reopen_interval_s
        self._reopen_window_s   = reopen_window_s
        self._opener     = opener
        self._log        = log

        self._cap = None
        # Monotonic timestamp when the current sustained-failure run began,
        # or None while healthy. Used for the give-up bound.
        self._failing_since: Optional[float] = None
        # Monotonic timestamp of the last re-open *attempt* (throttle).
        self._last_reopen_attempt: Optional[float] = None
        # Set once the sustained-failure window is exceeded: caller exits.
        self.give_up = False
        # THEME-4 (2026-06-14): pending-reopen latch. Set whenever a fresh
        # VideoCapture is successfully created mid-session (camera loss ->
        # reconnect). The run() loop consumes it via consume_reopened() and,
        # when set, recreates the FaceLandmarker — a VIDEO-mode tracker holds
        # temporal state and a backward/seek discontinuity across a device
        # swap is illegal, so the tracker MUST be reset on reconnect.
        self._reopened_pending = False

    def open(self) -> None:
        """Initial open. Propagates the opener's exception on first failure
        (a webcam that is absent at startup is a hard error, distinct from a
        mid-session transient loss)."""
        self._cap = self._opener(self._spec)

    def read(self, now_mono: float) -> Optional[np.ndarray]:
        """Return one frame, or None while the capture is degraded.

        On a successful read the failure timer is cleared. On a failed read:
        if this source is re-openable (a webcam), release the handle and, no
        more than once per ``reopen_interval_s``, attempt to re-open. If the
        source stays unreadable for longer than ``reopen_window_s`` the
        ``give_up`` flag is set and the caller should exit non-zero. A non-
        re-openable finite video simply returns None at EOF (legitimate end
        of stream) without arming the give-up bound."""
        cap = self._cap
        frame = read_frame(cap, loop=self._loop) if cap is not None else None
        if frame is not None:
            # Healthy frame: clear any in-progress failure state.
            self._failing_since = None
            self._last_reopen_attempt = None
            return frame

        # --- Read failed ---
        if not self._reopenable:
            # Finite video, no loop: a real EOF, not a camera loss.
            return None

        if self._failing_since is None:
            self._failing_since = now_mono
            if self._log is not None:
                self._log(
                    f"capture read failed for {self._spec!r}; "
                    "entering bounded re-open (emitting face_detected=false)"
                )

        # Sustained-failure give-up bound.
        if now_mono - self._failing_since > self._reopen_window_s:
            self.give_up = True
            if self._log is not None:
                self._log(
                    f"capture unreadable for >{self._reopen_window_s:.0f}s; "
                    "giving up (will exit non-zero)"
                )
            return None

        # Throttled re-open attempt.
        if (self._last_reopen_attempt is None
                or now_mono - self._last_reopen_attempt >= self._reopen_interval_s):
            self._last_reopen_attempt = now_mono
            self._release_cap()
            try:
                self._cap = self._opener(self._spec)
                # THEME-4: a fresh handle exists. Latch the reopen so the
                # run() loop resets the VIDEO-mode FaceLandmarker before it
                # feeds the new device's frames (stale temporal state +
                # timestamp discontinuity would otherwise corrupt tracking).
                self._reopened_pending = True
            except Exception as ex:  # opener may raise (busy/denied/absent)
                self._cap = None
                if self._log is not None:
                    self._log(f"capture re-open attempt failed: {ex}")
        return None

    def consume_reopened(self) -> bool:
        """Return True exactly once after each successful mid-session re-open.

        THEME-4: the run() loop polls this each iteration; on True it recreates
        the FaceLandmarker (and resets the FaceSelector / video-timestamp
        clock) so the new device starts the VIDEO tracker fresh."""
        if self._reopened_pending:
            self._reopened_pending = False
            return True
        return False

    def _release_cap(self) -> None:
        try:
            if self._cap is not None:
                self._cap.release()
        except Exception:
            pass
        self._cap = None

    def release(self) -> None:
        self._release_cap()


def resync_next_tick(now: float, next_tick: float, target_dt_s: float = TARGET_DT_S) -> float:
    """SIDE-03 (2026-06-11, D-11 scheduler-resync unlock): drop missed ticks.

    After computing ``now``, if the scheduler has fallen more than two target
    intervals behind (sleep / AV stall / debugger pause), snap ``next_tick``
    forward to ``now`` instead of letting the loop run unthrottled at camera
    rate to "catch up". Missed ticks are DROPPED, not replayed.

    Returns the (possibly snapped) ``next_tick``. Pure — no side effects."""
    if now - next_tick > 2.0 * target_dt_s:
        return now
    return next_tick


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
# Layer-1 jump filter (extracted for testability — D-11 unlock)
# ---------------------------------------------------------------------------
# Locked parameters (do NOT change without sprint sign-off):
#   MAX_JUMP_PER_FRAME_DEG = 25.0
#   CONSISTENCY_UNLATCH_N  = 3        (consecutive rejected frames must agree)
#   ABSENCE_RESET_S        = 1.0      (face absent this long resets latch)
#
# Unlatch rules (SIDE-01 2026-06-11):
#   1. Consistency: if N consecutive REJECTED frames agree with EACH OTHER
#      within MAX_JUMP_PER_FRAME_DEG, the latest is accepted and last_valid_*
#      is re-seeded. A self-consistent sustained pose is real; single-frame
#      spikes are not.
#   2. Absence: when face_present has been 0 for > ABSENCE_RESET_S seconds,
#      last_valid_yaw/pitch are cleared to None so the next re-acquired frame
#      re-seeds without fighting a stale extreme-angle latch.
#
# LOCKED (do NOT modify): AdaptiveEma2D smoothing, dead-zone, face-presence
# decision logic. Only the jump-filter's own memory is touched here.

class JumpFilter:
    """Layer-1 frame validity filter for yaw/pitch jump detection.

    Accepts or rejects each incoming (yaw, pitch) pose based on whether it
    jumped too far from the last accepted sample. Two unlatch rules prevent
    permanent latch after face dropout at extreme angles:

    Consistency unlatch: once CONSISTENCY_UNLATCH_N consecutive rejected
    frames agree with each other within MAX_JUMP_PER_FRAME_DEG, the latest
    is force-accepted and last_valid_* is re-seeded from it.

    Absence reset: if face_present has been 0 for longer than
    ABSENCE_RESET_S seconds, last_valid_yaw/pitch are cleared so the next
    re-acquired frame re-seeds without a stale latch.
    """

    MAX_JUMP_PER_FRAME_DEG: float = 25.0
    CONSISTENCY_UNLATCH_N:  int   = 3
    ABSENCE_RESET_S:        float = 1.0

    def __init__(self, default_cx: float, default_cy: float) -> None:
        self.last_valid_yaw:   Optional[float] = None
        self.last_valid_pitch: Optional[float] = None
        self.last_valid_cx:    float = default_cx
        self.last_valid_cy:    float = default_cy

        # Consistency-unlatch history: list of (yaw, pitch) for consecutive
        # rejected frames. Reset to [] whenever a frame is accepted.
        self._rejected_history: list[tuple[float, float]] = []

        # Absence-reset bookkeeping: tracks when face_present last turned 1.0.
        # _no_face_since_mono is set to the monotonic timestamp of the first
        # frame with face_present == 0 after a valid-face run (or at init).
        # Set to None while face is present (no absence timer running).
        self._no_face_since_mono: Optional[float] = None

    def process(
        self,
        yaw:        Optional[float],
        pitch:      Optional[float],
        cx_raw:     float,
        cy_raw:     float,
        face_present: float,
        now_mono:   float,
    ) -> tuple[bool, float, float]:
        """Evaluate one frame. Returns (is_valid, cx_out, cy_out).

        is_valid: True if this frame's pose is accepted (caller should update
                  the EMA smoother with cx_out/cy_out as a real measurement).
        cx_out / cy_out: if accepted, the raw values passed in; if rejected,
                  the last-valid values (feed to smoother as glitch bypass).

        Caller must NOT modify last_valid_* state; this method owns it.
        """
        # --- Absence-reset bookkeeping (runs unconditionally) ---
        if face_present <= 0.5:
            if self._no_face_since_mono is None:
                # Start the absence timer.
                self._no_face_since_mono = now_mono
            elif now_mono - self._no_face_since_mono > self.ABSENCE_RESET_S:
                # Face absent long enough: clear the jump-filter's latch so
                # the next re-acquired frame re-seeds cleanly.
                self.last_valid_yaw   = None
                self.last_valid_pitch = None
                self._rejected_history = []
        else:
            # Face present: cancel the absence timer.
            self._no_face_since_mono = None

        # --- Jump check ---
        frame_is_valid = face_present > 0.5
        if frame_is_valid and self.last_valid_yaw is not None:
            yaw_jump   = abs(yaw   - self.last_valid_yaw)   if yaw   is not None else 0.0
            pitch_jump = abs(pitch - self.last_valid_pitch) if pitch is not None else 0.0
            if yaw_jump > self.MAX_JUMP_PER_FRAME_DEG or pitch_jump > self.MAX_JUMP_PER_FRAME_DEG:
                frame_is_valid = False

        # --- Consistency unlatch (only runs when frame was just rejected) ---
        if not frame_is_valid and face_present > 0.5 and yaw is not None and pitch is not None:
            # Candidate rejected frame; check whether it agrees with recent
            # rejected frames. If the history is long enough AND all entries
            # are within MAX_JUMP_PER_FRAME_DEG of each other, accept.
            history = self._rejected_history
            consistent = True
            for (hy, hp) in history:
                if (abs(yaw - hy) > self.MAX_JUMP_PER_FRAME_DEG or
                        abs(pitch - hp) > self.MAX_JUMP_PER_FRAME_DEG):
                    consistent = False
                    break
            if not consistent:
                # "Consecutive" means the window restarts on disagreement —
                # otherwise one outlier stuck in history blocks the unlatch
                # forever and the list grows without bound at frame rate.
                history.clear()
            history.append((yaw, pitch))
            if consistent and len(history) >= self.CONSISTENCY_UNLATCH_N:
                # N consecutive rejected frames all agree — accept this one
                # and re-seed the latch from it.
                frame_is_valid = True

        # --- Update state ---
        if not frame_is_valid:
            # Glitch or dropout: repeat the last-valid (cx, cy) to the smoother.
            return False, self.last_valid_cx, self.last_valid_cy
        else:
            # Accepted: update latch and clear rejection history.
            self.last_valid_yaw   = yaw
            self.last_valid_pitch = pitch
            self.last_valid_cx    = cx_raw
            self.last_valid_cy    = cy_raw
            self._rejected_history = []
            return True, cx_raw, cy_raw


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

    # THEME-4 (2026-06-14): VIDEO-mode primary-face selector + monotonic
    # timestamp clock. The selector carries cross-frame stickiness (D-20);
    # last_video_ts_ms seeds at -1 so the first computed 0 ms is accepted.
    face_selector = FaceSelector()
    last_video_ts_ms = -1

    # M5.1-C: AdaptiveEma2D replaces the constant-α Ema2D. SMOOTH_ALPHA
    # constant kept in module scope for documentation of the old default;
    # AdaptiveEma2D ignores it and computes α per-tick from velocity.
    smoother = AdaptiveEma2D(DEFAULT_CX, DEFAULT_CY)
    stats = SoakStats() if soak_seconds is not None else None
    soak_loop = loop_video or (soak_seconds is not None)

    # SIDE-02 (2026-06-11, D-11 capture-loop unlock): wrap the capture in a
    # CaptureManager so a transient webcam loss triggers a bounded re-open
    # instead of a silent clean exit. Only a webcam *index* is re-openable
    # mid-session; a finite video file's EOF is a legitimate end-of-stream,
    # not a device loss, so it is NOT re-opened (it relies on read_frame's
    # own --loop handling in soak mode).
    try:
        int(input_spec)
        reopenable = True
    except ValueError:
        reopenable = False
    cap_mgr = CaptureManager(
        input_spec, loop=soak_loop, reopenable=reopenable,
        log=lambda m: sys.stderr.write(f"[head_tracker] {m}\n"),
    )
    cap_mgr.open()

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
        # THEME-4 (2026-06-14): two new --diag-only columns make multi-person
        # primary selection CSV-verifiable: selected_face_idx (index chosen by
        # FaceSelector, -1 when no face) and n_faces (faces MediaPipe returned
        # this frame). Production JSON schema is UNCHANGED — these are diag-only.
        diag_file.write("t_ms,yaw_deg,pitch_deg,cx_raw,cy_raw,cx_smoothed,cy_smoothed,face_detected,face_present,mirror,velocity_deg_per_frame,adaptive_alpha,selected_face_idx,n_faces\n")

    # M5.1-A Layer 1 (2026-05-29): frame validity filter. On any frame where
    # face_present == 0 OR yaw / pitch jumps > MAX_JUMP_PER_FRAME_DEG since
    # the last VALID sample, skip emitting and repeat the previously-valid
    # (cx, cy). Diagnose analysis showed max legitimate single-frame jump
    # ≈ 13° during rapid mixed motion; threshold at 25° catches detection
    # glitches without false-positives at observed motion ceilings.
    # SIDE-01 (2026-06-11): extracted to JumpFilter class (D-11 unlock);
    # consistency-unlatch and absence-reset are now encapsulated there.
    jump_filter = JumpFilter(DEFAULT_CX, DEFAULT_CY)
    skipped_frame_count = 0

    # THEME-4 (2026-06-14): per-frame inference try/except bookkeeping. One
    # MediaPipe/cv2 throw must NOT kill the whole sidecar — we log (rate-
    # limited), emit a face_detected=false record like the camera-loss path,
    # and CONTINUE the loop. A separate sustained-exception bound gives up only
    # if the inference path throws continuously for INFER_EXC_WINDOW_S (a real,
    # non-transient fault) so a permanently-wedged model surfaces via a non-
    # zero exit rather than spinning forever emitting centered frames.
    INFER_EXC_LOG_INTERVAL_S = 5.0    # rate-limit identical stderr spam
    INFER_EXC_WINDOW_S       = 30.0   # sustained-failure give-up bound
    infer_exc_failing_since: Optional[float] = None
    infer_exc_last_log: Optional[float] = None
    inference_gave_up = False

    t0_mono = time.monotonic()
    next_tick = t0_mono
    deadline = (t0_mono + soak_seconds) if soak_seconds is not None else None

    # SIDE-02: set when CaptureManager gives up after sustained failure so the
    # run() exit code is NON-zero (host OnProcessExited surfaces the death).
    capture_gave_up = False

    try:
        while True:
            now = time.monotonic()
            if deadline is not None and now >= deadline:
                break
            # SIDE-03 (2026-06-11, D-11 scheduler-resync unlock): if we have
            # fallen >2 ticks behind (sleep / AV stall / debugger pause), drop
            # the missed ticks instead of replaying them — otherwise the loop
            # would run unthrottled at camera rate "catching up". Apply BEFORE
            # the cadence sleep so the snap takes effect this iteration.
            next_tick = resync_next_tick(now, next_tick)
            # Cadence: wait until the next 15 Hz tick.
            if now < next_tick:
                time.sleep(next_tick - now)
            next_tick += TARGET_DT_S

            frame_bgr = cap_mgr.read(now_mono=time.monotonic())
            if frame_bgr is None:
                if cap_mgr.give_up:
                    # SIDE-02: sustained capture failure exceeded the bound.
                    capture_gave_up = True
                    break
                if not reopenable:
                    # Finite video, no loop: legitimate end of stream.
                    break
                # Webcam transiently unreadable: emit a face_detected=false
                # frame so the C# host's grace path centers the mask, then
                # continue the bounded re-open loop. We synthesize a minimal
                # center record rather than running inference on no frame.
                if stats is None:
                    lost_rec = FrameRecord(
                        t_ms=int((time.monotonic() - t0_mono) * 1000.0),
                        cx=round(DEFAULT_CX, 4),
                        cy=round(DEFAULT_CY, 4),
                        luma=0.0,
                        face_detected=False,
                        face_present=0.0,
                    )
                    payload = {k: v for k, v in asdict(lost_rec).items() if v is not None}
                    sys.stdout.write(json.dumps(payload, separators=(",", ":")) + "\n")
                    sys.stdout.flush()
                continue

            # THEME-4 (2026-06-14): a mid-session re-open just produced a fresh
            # VideoCapture (camera loss -> reconnect). The VIDEO-mode tracker
            # holds temporal state and the timestamp clock would jump backward
            # relative to the new device's frames; both are illegal. Recreate
            # the landmarker, reset the selector, and re-seed the timestamp
            # clock so the new device starts a clean track. (No-op for a finite
            # video file, which never re-opens.)
            if cap_mgr.consume_reopened():
                try:
                    landmarker.close()
                except Exception:
                    pass
                landmarker = make_face_landmarker(model_resolved)
                face_selector.reset()
                last_video_ts_ms = -1
                sys.stderr.write(
                    "[head_tracker] capture re-opened; FaceLandmarker reset "
                    "(fresh VIDEO track)\n"
                )

            if mirror:
                frame_bgr = cv2.flip(frame_bgr, 1)

            # Time only the inference + decomposition step (the "hot" path).
            infer_start = time.perf_counter()

            face_detected = False
            face_present = 0.0  # MediaPipe FaceLandmarker doesn't expose a per-detection
                                # score; presence implies >= configured
                                # min_face_presence_confidence (0.5). We report 1.0 when
                                # present, 0.0 when absent. G1-renamed from `confidence`;
                                # schema slot preserved for v1.0 scalar-confidence model.
            raw_yaw: Optional[float] = None
            raw_pitch: Optional[float] = None
            cx_raw = DEFAULT_CX
            cy_raw = DEFAULT_CY
            selected_face_idx = -1   # --diag only; -1 == no face this frame
            n_faces = 0              # --diag only

            # THEME-4: per-frame try/except around the inference + decode block.
            # A single MediaPipe / cv2 throw must NOT kill the sidecar. On
            # exception we log (rate-limited), leave face_present at 0.0 (the
            # block below emits a face_detected=false / last-valid record just
            # like the camera-loss path) and CONTINUE the loop. A sustained-
            # exception bound gives up only after INFER_EXC_WINDOW_S of
            # continuous failure (a real, non-transient fault).
            inference_ok = True
            try:
                # MediaPipe wants an RGB mp.Image.
                rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
                mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
                # THEME-4: VIDEO mode requires a strictly-monotonic int-ms
                # timestamp. Derive it from the loop clock and clamp UP so a
                # clock resync / stall can never feed a non-increasing value
                # (MediaPipe throws on non-monotonic timestamps).
                computed_ts_ms = int((time.monotonic() - t0_mono) * 1000.0)
                video_ts_ms = monotonic_video_ts_ms(computed_ts_ms, last_video_ts_ms)
                last_video_ts_ms = video_ts_ms
                result = landmarker.detect_for_video(mp_image, video_ts_ms)

                if result and getattr(result, "face_landmarks", None):
                    n_faces = len(result.face_landmarks)

                # THEME-4 D-20: choose the seated user among num_faces=3 via
                # largest + most-central + sticky selection. Index the SAME i
                # into facial_transformation_matrixes[i] (index-aligned).
                idx = select_primary_face(result, face_selector)
                if (idx is not None
                        and result is not None
                        and result.facial_transformation_matrixes
                        and 0 <= idx < len(result.facial_transformation_matrixes)):
                    selected_face_idx = idx
                    mat = np.asarray(result.facial_transformation_matrixes[idx],
                                     dtype=np.float64)
                    if mat.shape == (4, 4):
                        yaw, pitch, _roll = decompose_yaw_pitch_roll(mat)
                        # M5.1-A: chin-up gives negative raw pitch (atan2
                        # decomposition); negate unconditionally so downstream
                        # convention is "pitch > 0 = chin up". M5.1-A v2: NO
                        # yaw negation (the "sometimes inverted" symptom was
                        # smoothing lag, not a sign error). Positive raw yaw ->
                        # cx > 0.5 = mask right under the default --mirror=on.
                        pitch = -pitch
                        raw_yaw, raw_pitch = yaw, pitch
                        cx_raw, cy_raw = yaw_pitch_to_cxcy(yaw, pitch)
                        face_detected = True
                        face_present = 1.0
            except Exception as ex:  # MediaPipe / cv2 / decode throw
                inference_ok = False
                face_detected = False
                face_present = 0.0
                raw_yaw = raw_pitch = None
                # Rate-limited stderr log (don't spam at 15 Hz).
                if (infer_exc_last_log is None
                        or now - infer_exc_last_log >= INFER_EXC_LOG_INTERVAL_S):
                    infer_exc_last_log = now
                    sys.stderr.write(
                        f"[head_tracker] inference error (continuing, "
                        f"emitting face_detected=false): {type(ex).__name__}: {ex}\n"
                    )

            # THEME-4: sustained-inference-failure give-up bound. On a healthy
            # frame clear the timer; on a throwing frame arm/advance it and bail
            # out (non-zero exit) only after the window is exhausted.
            if inference_ok:
                infer_exc_failing_since = None
            else:
                if infer_exc_failing_since is None:
                    infer_exc_failing_since = now
                elif now - infer_exc_failing_since > INFER_EXC_WINDOW_S:
                    inference_gave_up = True
                    sys.stderr.write(
                        f"[head_tracker] inference failing for "
                        f">{INFER_EXC_WINDOW_S:.0f}s; giving up (exit non-zero)\n"
                    )

            # M5.1-A Layer 1: validity check via JumpFilter (SIDE-01 2026-06-11).
            # JumpFilter owns last_valid_* state; caller feeds smoother with
            # cx_raw/cy_raw returned by process() regardless of validity so the
            # EMA always gets a value (last-valid on glitch, real on accept).
            # raw_yaw / raw_pitch left as-is for diag CSV so rejections are
            # visible in the trace.
            frame_is_valid, cx_raw, cy_raw = jump_filter.process(
                raw_yaw, raw_pitch, cx_raw, cy_raw, face_present,
                now_mono=time.monotonic(),
            )
            if not frame_is_valid:
                skipped_frame_count += 1

            # M5.1-C adaptive EMA: pass yaw/pitch for velocity-driven α.
            # When face_present == 0 the Layer 1 filter substituted cx_raw/
            # cy_raw with last-valid values; pass raw_yaw / raw_pitch as
            # available (None during dropouts, AdaptiveEma2D handles None
            # by keeping previous velocity unchanged).
            cx, cy = smoother.update(
                cx_raw, cy_raw,
                yaw_deg=  (raw_yaw   if raw_yaw   is not None else (jump_filter.last_valid_yaw   or 0.0)),
                pitch_deg=(raw_pitch if raw_pitch is not None else (jump_filter.last_valid_pitch or 0.0)),
            )
            # Clamp again post-smoothing for safety.
            cx, cy = clamp01(cx), clamp01(cy)
            luma = frame_mean_luma_normalized(frame_bgr)

            infer_end = time.perf_counter()
            latency_ms = (infer_end - infer_start) * 1000.0

            # M5.1-A diagnostic CSV append (only when --diag is set).
            # M5.1-C extension: also record the current adaptive α + smoothed
            # velocity so post-run analysis can see which regime the smoother
            # was in at each instant.
            if diag_file is not None:
                t_ms = int((time.monotonic() - t0_mono) * 1000.0)
                yaw_str   = f"{raw_yaw:.3f}"   if raw_yaw   is not None else ""
                pitch_str = f"{raw_pitch:.3f}" if raw_pitch is not None else ""
                diag_file.write(
                    f"{t_ms},{yaw_str},{pitch_str},"
                    f"{cx_raw:.4f},{cy_raw:.4f},{cx:.4f},{cy:.4f},"
                    f"{int(face_detected)},{face_present:.1f},"
                    f"{int(mirror)},"
                    f"{smoother.current_velocity:.3f},{smoother.current_alpha:.3f},"
                    f"{selected_face_idx},{n_faces}\n"
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

            # THEME-4: sustained inference failure exhausted the bound — exit
            # after this (centered) record so the host's OnProcessExited fires
            # on a real, non-transient fault instead of spinning forever.
            if inference_gave_up:
                break

    finally:
        try:
            landmarker.close()
        except Exception:
            pass
        cap_mgr.release()
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
    # SIDE-02: exit NON-zero only after the bounded re-open window is exhausted,
    # so a transient webcam loss is invisible to the host (frames keep flowing)
    # but a true device loss surfaces via OnProcessExited's non-zero exit code.
    if capture_gave_up:
        return 3
    # THEME-4: sustained inference failure (model wedged) also surfaces as a
    # distinct non-zero exit so the host restarts the sidecar rather than
    # trusting an endless stream of centered records.
    if inference_gave_up:
        return 4
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
             "face_present, mirror, velocity_deg_per_frame, adaptive_alpha, "
             "selected_face_idx, n_faces) to PATH. Used by the M5.1-A "
             "inverted-direction diagnosis and the THEME-4 multi-face "
             "selection check. Off by default.",
    )
    return p


def main(argv: Optional[list[str]] = None) -> int:
    # H12: force UTF-8 on stdout regardless of the inherited environment. The
    # C# host strips PYTHONIOENCODING / PYTHONUTF8 / PYTHONLEGACYWINDOWSSTDIO
    # from the child env so they can't redirect or break our JSON stream, but
    # reconfigure here as belt-and-suspenders for non-host launches (manual
    # runs, soak harness) where those vars may still be set. Best-effort:
    # reconfigure() is unavailable if stdout was replaced with a non-TextIO
    # stream, in which case we leave the existing encoding untouched.
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except (AttributeError, ValueError):
        pass

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
