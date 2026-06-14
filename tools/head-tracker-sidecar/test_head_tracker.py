"""Unit tests for head_tracker.py.

M5.1-C close follow-up per advisor (2026-05-30): AdaptiveEma2D's
velocity-driven alpha curve was the load-bearing M5.1-C iter-1 change,
but the original commit only sanity-tested it via Python REPL. S2
polished standard requires regression-protected unit tests.

Run with:
    .\.venv\Scripts\python.exe -m unittest test_head_tracker.py -v
"""

from __future__ import annotations

import importlib.util
import math
import os
import sys
import unittest


def _load_module():
    """Load head_tracker.py via importlib so dataclasses resolve correctly."""
    here = os.path.dirname(os.path.abspath(__file__))
    spec = importlib.util.spec_from_file_location(
        "head_tracker",
        os.path.join(here, "head_tracker.py"),
    )
    mod = importlib.util.module_from_spec(spec)
    sys.modules["head_tracker"] = mod
    spec.loader.exec_module(mod)
    return mod


_HT = _load_module()
AdaptiveEma2D     = _HT.AdaptiveEma2D
JumpFilter        = _HT.JumpFilter
CaptureManager    = _HT.CaptureManager
WebcamOpenError   = _HT.WebcamOpenError
resync_next_tick  = _HT.resync_next_tick
TARGET_DT_S       = _HT.TARGET_DT_S


class AdaptiveEma2DAlphaCurveTests(unittest.TestCase):
    """The velocity-driven alpha curve is the load-bearing M5.1-C change.

    Curve specification (from AdaptiveEma2D docstring + advisor spec):

      velocity        α       regime
      < 1°/frame      0.08    anti-jitter (head holding still)
      1-5°/frame      0.08 → 0.30   responsive (interp)
      5-10°/frame     0.30 → 0.50   medium-rapid (interp)
      > 10°/frame     0.55    snappy (rapid head turn)

    Velocity is itself EMA-smoothed (α=0.4) so the regime transitions
    don't thrash on single-frame noise — tests use enough frames to
    let the velocity EMA settle before asserting alpha.
    """

    def _drive_at_constant_velocity(self, deg_per_frame: float, n_frames: int = 30):
        """Run the smoother with constant per-frame yaw delta. Returns alpha
        after the velocity EMA has settled."""
        s = AdaptiveEma2D(0.5, 0.5)
        # Prime with a stationary frame.
        s.update(0.5, 0.5, 0.0, 0.0)
        # Drive constant velocity (split between yaw and pitch arbitrarily;
        # we test pure yaw motion here, the L2 norm equals |dyaw| when
        # dpitch = 0).
        for i in range(n_frames):
            yaw = i * deg_per_frame
            s.update(0.5 + i * 0.01, 0.5, yaw, 0.0)
        return s.current_alpha, s.current_velocity

    def test_static_motion_uses_anti_jitter_alpha(self):
        """At velocity ≈ 0, alpha pins to ALPHA_LOW (0.08)."""
        alpha, vel = self._drive_at_constant_velocity(0.0)
        self.assertAlmostEqual(vel, 0.0, places=3)
        self.assertAlmostEqual(alpha, AdaptiveEma2D.ALPHA_LOW, places=3)

    def test_below_low_threshold_uses_anti_jitter_alpha(self):
        """Velocity 0.5°/frame (well below VEL_LOW=1.0) → ALPHA_LOW."""
        alpha, vel = self._drive_at_constant_velocity(0.5)
        # Velocity EMA settles toward 0.5 (under 1.0).
        self.assertLess(vel, AdaptiveEma2D.VEL_LOW)
        self.assertAlmostEqual(alpha, AdaptiveEma2D.ALPHA_LOW, places=3)

    def test_medium_motion_in_low_to_med_interp_band(self):
        """Velocity 3°/frame is mid-way between VEL_LOW (1.0) and VEL_MED (5.0).
        Expected α: 0.08 + (3-1)/(5-1) × (0.30-0.08) = 0.08 + 0.5 × 0.22 = 0.19."""
        alpha, vel = self._drive_at_constant_velocity(3.0)
        self.assertAlmostEqual(vel, 3.0, places=3)
        # Piecewise linear: t = (3-1)/4 = 0.5 → α = 0.08 + 0.5 × (0.30-0.08) = 0.19
        expected = AdaptiveEma2D.ALPHA_LOW + 0.5 * (AdaptiveEma2D.ALPHA_MED_LOW - AdaptiveEma2D.ALPHA_LOW)
        self.assertAlmostEqual(alpha, expected, places=3)

    def test_at_med_threshold_alpha_equals_med_low(self):
        """At velocity 5.0°/frame (=VEL_MED boundary), α = ALPHA_MED_LOW (0.30)."""
        alpha, vel = self._drive_at_constant_velocity(5.0)
        self.assertAlmostEqual(vel, 5.0, places=3)
        # Piecewise: exactly at VEL_MED boundary. The current implementation
        # returns ALPHA_MED_LOW because the next segment's start = previous
        # segment's end at v=VEL_MED.
        self.assertAlmostEqual(alpha, AdaptiveEma2D.ALPHA_MED_LOW, places=3)

    def test_in_med_to_high_band(self):
        """Velocity 7°/frame is mid-way between VEL_MED (5.0) and VEL_HIGH (10.0).
        Expected α: 0.30 + (7-5)/(10-5) × (0.50-0.30) = 0.30 + 0.4 × 0.20 = 0.38."""
        alpha, vel = self._drive_at_constant_velocity(7.0)
        self.assertAlmostEqual(vel, 7.0, places=3)
        expected = AdaptiveEma2D.ALPHA_MED_LOW + 0.4 * (AdaptiveEma2D.ALPHA_MED_HIGH - AdaptiveEma2D.ALPHA_MED_LOW)
        self.assertAlmostEqual(alpha, expected, places=3)

    def test_above_high_threshold_uses_snappy_alpha(self):
        """Velocity > VEL_HIGH (10°/frame) pins to ALPHA_HIGH (0.55)."""
        alpha, vel = self._drive_at_constant_velocity(15.0)
        self.assertAlmostEqual(vel, 15.0, places=3)
        self.assertAlmostEqual(alpha, AdaptiveEma2D.ALPHA_HIGH, places=3)

    def test_extreme_velocity_pins_to_alpha_high(self):
        """Very high velocity (50°/frame) still pins at ALPHA_HIGH, not unbounded."""
        alpha, vel = self._drive_at_constant_velocity(50.0)
        self.assertAlmostEqual(alpha, AdaptiveEma2D.ALPHA_HIGH, places=3)


class AdaptiveEma2DSmoothingTests(unittest.TestCase):
    """The EMA convergence behavior — what alpha actually does to x/y."""

    def test_first_frame_seeds_without_smoothing(self):
        """The first .update() call returns the input unchanged (priming
        the seed). Velocity is not computed (no prior frame)."""
        s = AdaptiveEma2D(0.5, 0.5)
        x, y = s.update(0.7, 0.3, 5.0, 5.0)
        self.assertAlmostEqual(x, 0.7, places=4)
        self.assertAlmostEqual(y, 0.3, places=4)
        self.assertAlmostEqual(s.current_velocity, 0.0, places=4)

    def test_static_motion_converges_slowly(self):
        """At ALPHA_LOW=0.08, 5 ticks toward a target should still be far
        from converged. Formula: x_n = target - (1-α)^n × (target - x_0)."""
        s = AdaptiveEma2D(0.5, 0.5)
        s.update(0.5, 0.5, 0.0, 0.0)  # prime
        # Drive 5 ticks with static velocity → α stays at ALPHA_LOW.
        for _ in range(5):
            s.update(1.0, 1.0, 0.0, 0.0)
        # After 5 ticks at α=0.08, displaced = 1 - (1-0.08)^5 × (1 - 0.5)
        # = 1 - 0.92^5 × 0.5 = 1 - 0.659 × 0.5 = 1 - 0.330 = 0.670 (approximately)
        # But we updated x=1.0, y=1.0 every tick, so converging toward 1.0.
        # Actual: x_5 ≈ 1 - (0.92)^5 × (1 - 0.5) = 1 - 0.659 × 0.5 = 0.670
        # (Note: first update was the prime; the 5 driving ticks start
        # from x=0.5 toward 1.0.)
        self.assertGreater(s.x, 0.5)
        self.assertLess(s.x, 1.0)
        # Sanity: not too close to target after 5 anti-jitter ticks.
        self.assertLess(s.x, 0.75)

    def test_fast_motion_converges_quickly(self):
        """At ALPHA_HIGH=0.55, 5 ticks toward a target should be much further
        along than at ALPHA_LOW."""
        s = AdaptiveEma2D(0.5, 0.5)
        s.update(0.5, 0.5, 0.0, 0.0)  # prime
        # Drive 5 ticks while ramping yaw rapidly so velocity goes high.
        for i in range(5):
            s.update(1.0, 1.0, (i + 1) * 15.0, 0.0)
        # By the 5th tick alpha should be near ALPHA_HIGH=0.55, and the
        # displaced value should be much closer to the (1.0, 1.0) target
        # than the static case.
        self.assertGreater(s.x, 0.9)
        self.assertGreater(s.current_alpha, 0.4)

    def test_reset_to_clears_velocity_state(self):
        """reset_to clears the smoother including velocity history."""
        s = AdaptiveEma2D(0.5, 0.5)
        s.update(0.5, 0.5, 0.0, 0.0)
        # Build up some velocity.
        for i in range(10):
            s.update(0.5 + i * 0.01, 0.5, i * 15.0, 0.0)
        self.assertGreater(s.current_velocity, 0.0)
        s.reset_to(0.3, 0.7)
        self.assertEqual(s.x, 0.3)
        self.assertEqual(s.y, 0.7)
        self.assertEqual(s.current_velocity, 0.0)
        self.assertEqual(s.current_alpha, AdaptiveEma2D.ALPHA_LOW)


class AdaptiveEma2DRegimeTransitionTests(unittest.TestCase):
    """Boundary behavior between α regimes — guards against off-by-one
    or sign errors in the piecewise interpolation."""

    def test_zero_velocity_strictly_uses_alpha_low(self):
        s = AdaptiveEma2D(0.5, 0.5)
        s.update(0.5, 0.5, 0.0, 0.0)
        s.update(0.5, 0.5, 0.0, 0.0)
        self.assertEqual(s.current_alpha, AdaptiveEma2D.ALPHA_LOW)

    def test_velocity_just_above_low_threshold_is_in_interp_band(self):
        """At v slightly above VEL_LOW, alpha should be slightly above
        ALPHA_LOW — not jump straight to ALPHA_MED_LOW."""
        s = AdaptiveEma2D(0.5, 0.5)
        s.update(0.5, 0.5, 0.0, 0.0)
        # Velocity EMA will converge to ~1.5 with consistent input.
        for i in range(30):
            s.update(0.5 + i * 0.005, 0.5, i * 1.5, 0.0)
        self.assertGreater(s.current_velocity, AdaptiveEma2D.VEL_LOW)
        self.assertLess(s.current_velocity, AdaptiveEma2D.VEL_MED)
        # α should be strictly between LOW and MED_LOW, not at either.
        self.assertGreater(s.current_alpha, AdaptiveEma2D.ALPHA_LOW)
        self.assertLess(s.current_alpha, AdaptiveEma2D.ALPHA_MED_LOW)


class JumpFilterDropoutRecoveryTests(unittest.TestCase):
    """SIDE-01 spec (a): face dropout at extreme yaw -> re-acquire near center
    -> consistency unlatch lets filter accept the new pose within 3 frames.

    Scenario mirrors the real failure: user turns to ~42° yaw (extreme),
    face drops out (face_present=0), then head returns toward ~5° center.
    Without the consistency unlatch, last_valid is stuck at 42° and every
    center frame (delta ~37°) is rejected forever.  With the unlatch,
    3 self-consistent frames at ~5° must cause acceptance.
    """

    def _make_filter(self) -> "JumpFilter":
        return JumpFilter(0.5, 0.5)

    def test_recovery_within_3_frames_after_dropout_at_extreme(self):
        """Latch captures extreme (42°), then face drops out, then returns to
        5° center. The consistency unlatch must accept by frame 3 of
        re-acquisition (i.e., is_valid == True no later than the 3rd
        returned-to-center frame)."""
        f = self._make_filter()
        t = 0.0

        # Phase 1: establish valid latch at extreme yaw=42.
        is_valid, cx, cy = f.process(42.0, 0.0, 0.977, 0.5, 1.0, t)
        self.assertTrue(is_valid)
        self.assertAlmostEqual(f.last_valid_yaw, 42.0, places=3)

        # Phase 2: face dropout for 2 frames (short — no absence reset).
        t += 0.067  # ~1 frame @ 15 Hz
        is_valid, cx, cy = f.process(None, None, 0.5, 0.5, 0.0, t)
        self.assertFalse(is_valid)
        t += 0.067
        is_valid, cx, cy = f.process(None, None, 0.5, 0.5, 0.0, t)
        self.assertFalse(is_valid)
        # Latch still at 42° (absence timer < 1s).
        self.assertAlmostEqual(f.last_valid_yaw, 42.0, places=3)

        # Phase 3: head returns to ~5° — delta from latch=42° is ~37° > 25°.
        # Without unlatch, every frame would be rejected.  With consistency
        # unlatch (N=3), the 3rd self-consistent frame must be accepted.
        recovered_by_frame = 3
        accepted_at: int | None = None
        for frame_idx in range(recovered_by_frame):
            t += 0.067
            is_valid, cx, cy = f.process(5.0, 0.0, 0.617, 0.5, 1.0, t)
            if is_valid and accepted_at is None:
                accepted_at = frame_idx + 1  # 1-based

        self.assertIsNotNone(accepted_at,
            "Filter never accepted re-acquired center pose within 3 frames")
        self.assertLessEqual(accepted_at, recovered_by_frame,
            f"Expected acceptance by frame {recovered_by_frame}, got frame {accepted_at}")
        # After acceptance the latch should be re-seeded at ~5°.
        self.assertAlmostEqual(f.last_valid_yaw, 5.0, places=3)

    def test_no_single_frame_false_accept_during_dropout(self):
        """During a brief face dropout the filter must not accept a frame
        with face_present=0 as valid regardless of the yaw value."""
        f = self._make_filter()
        t = 0.0
        f.process(10.0, 0.0, 0.733, 0.5, 1.0, t)  # seed latch
        t += 0.067
        is_valid, _, _ = f.process(10.0, 0.0, 0.733, 0.5, 0.0, t)
        self.assertFalse(is_valid,
            "Frame with face_present=0 must never be accepted as valid")


class JumpFilterSpikeRejectionTests(unittest.TestCase):
    """SIDE-01 spec (b): a single-frame 30° yaw spike must still be rejected.

    The consistency unlatch requires N=3 consecutive self-consistent rejected
    frames — one isolated spike never builds enough history to unlock.
    """

    def test_single_frame_30deg_spike_rejected(self):
        """Latch at yaw=5°. Single frame at 35° (delta=30° > 25°) must be
        rejected; subsequent return to 5° must be accepted immediately."""
        f = JumpFilter(0.5, 0.5)
        t = 0.0
        # Seed latch.
        f.process(5.0, 0.0, 0.617, 0.5, 1.0, t)
        self.assertAlmostEqual(f.last_valid_yaw, 5.0, places=3)

        # Single spike frame.
        t += 0.067
        is_valid, cx, cy = f.process(35.0, 0.0, 0.815, 0.5, 1.0, t)
        self.assertFalse(is_valid, "Single 30° spike must be rejected")
        # Latch must not have moved.
        self.assertAlmostEqual(f.last_valid_yaw, 5.0, places=3)

        # Immediate return to normal — must be accepted (rejection history
        # cleared / not enough consecutive rejections at spike value).
        t += 0.067
        is_valid, cx, cy = f.process(6.0, 0.0, 0.640, 0.5, 1.0, t)
        self.assertTrue(is_valid,
            "Frame near original latch after spike must be accepted")

    def test_spike_does_not_accumulate_into_unlatch(self):
        """Two spikes at different yaw values must NOT accumulate into the
        consistency unlatch (they are inconsistent with each other)."""
        f = JumpFilter(0.5, 0.5)
        t = 0.0
        f.process(5.0, 0.0, 0.617, 0.5, 1.0, t)

        # Spike 1 at 35°.
        t += 0.067
        is_valid1, _, _ = f.process(35.0, 0.0, 0.815, 0.5, 1.0, t)
        # Spike 2 at 35° + 30° = 65° (inconsistent with first spike too,
        # but more importantly inconsistent with latch).
        t += 0.067
        is_valid2, _, _ = f.process(65.0, 0.0, 0.99, 0.5, 1.0, t)
        # Spike 3 back at 35° (inconsistent with spike 2 at 65°).
        t += 0.067
        is_valid3, _, _ = f.process(35.0, 0.0, 0.815, 0.5, 1.0, t)
        # None of these should have been accepted; the inconsistent history
        # must have cleared the unlatch accumulation.
        self.assertFalse(is_valid1, "Spike 1 at 35° must be rejected")
        self.assertFalse(is_valid2, "Spike 2 at 65° must be rejected")
        self.assertFalse(is_valid3,
            "Spike 3 at 35° inconsistent with spike 2 at 65° must be rejected")
        # Latch must still be at 5°.
        self.assertAlmostEqual(f.last_valid_yaw, 5.0, places=3)


class JumpFilterAbsenceResetTests(unittest.TestCase):
    """SIDE-01 spec (c): after >1.0s of face_present==0, the latch is
    cleared so the first re-acquired frame seeds cleanly.

    This ensures that a user who physically walks away and returns does not
    fight a stale extreme-angle latch from before the interruption.
    """

    def test_long_absence_resets_latch(self):
        """Seed latch at yaw=42°. Then drive face_present=0 for >1.0 s.
        The latch must be cleared; the first re-acquired frame at 5° must
        be accepted without requiring consistency unlatch."""
        f = JumpFilter(0.5, 0.5)
        t = 0.0
        # Seed latch at extreme yaw.
        f.process(42.0, 0.0, 0.977, 0.5, 1.0, t)
        self.assertAlmostEqual(f.last_valid_yaw, 42.0, places=3)

        # Drive no-face frames for just over 1.0 s.
        t += 0.067
        while t < 1.15:
            f.process(None, None, 0.5, 0.5, 0.0, t)
            t += 0.067

        # Latch must now be None (absence reset fired).
        self.assertIsNone(f.last_valid_yaw,
            "last_valid_yaw must be None after >1s absence")
        self.assertIsNone(f.last_valid_pitch,
            "last_valid_pitch must be None after >1s absence")

        # Re-acquire at 5° — since latch is None, must be accepted on
        # the very first frame (no last_valid to compare against).
        is_valid, cx, cy = f.process(5.0, 0.0, 0.617, 0.5, 1.0, t)
        self.assertTrue(is_valid,
            "First re-acquired frame after absence reset must be accepted")
        self.assertAlmostEqual(f.last_valid_yaw, 5.0, places=3)

    def test_short_absence_does_not_reset_latch(self):
        """Face dropout shorter than 1.0 s must NOT reset the latch.
        The old spike-rejection behavior must remain intact after brief
        dropout (user just blinked or turned head quickly)."""
        f = JumpFilter(0.5, 0.5)
        t = 0.0
        f.process(10.0, 0.0, 0.733, 0.5, 1.0, t)
        self.assertAlmostEqual(f.last_valid_yaw, 10.0, places=3)

        # Dropout for 0.5 s (shorter than ABSENCE_RESET_S=1.0).
        t += 0.067
        while t < 0.55:
            f.process(None, None, 0.5, 0.5, 0.0, t)
            t += 0.067

        # Latch must still be intact.
        self.assertIsNotNone(f.last_valid_yaw,
            "Latch must NOT reset after <1s absence")
        self.assertAlmostEqual(f.last_valid_yaw, 10.0, places=3)

    def test_absence_timer_resets_on_face_return(self):
        """If face returns and then drops out again, the 1.0 s timer must
        restart from the new dropout, not from the original one."""
        f = JumpFilter(0.5, 0.5)
        t = 0.0
        f.process(10.0, 0.0, 0.733, 0.5, 1.0, t)

        # First dropout: 0.8 s (not enough to reset).
        t += 0.067
        while t < 0.85:
            f.process(None, None, 0.5, 0.5, 0.0, t)
            t += 0.067

        # Face comes back briefly.
        t += 0.067
        f.process(10.0, 0.0, 0.733, 0.5, 1.0, t)
        self.assertAlmostEqual(f.last_valid_yaw, 10.0, places=3)

        # Second dropout: 0.8 s more (total from start = 1.7 s, but timer
        # restarted, so must NOT reset after only 0.8 s again).
        t += 0.067
        t_second_dropout_start = t
        while t - t_second_dropout_start < 0.85:
            f.process(None, None, 0.5, 0.5, 0.0, t)
            t += 0.067

        # Latch must still be intact (timer restarted, only 0.8 s elapsed
        # since the second dropout began).
        self.assertIsNotNone(f.last_valid_yaw,
            "Latch must NOT reset: timer restarted after face returned")
        self.assertAlmostEqual(f.last_valid_yaw, 10.0, places=3)


import numpy as np


# A 480x360 BGR frame so read_frame() passes it through without resizing.
def _good_frame() -> np.ndarray:
    return np.zeros((360, 480, 3), dtype=np.uint8)


class _FakeCap:
    """Minimal cv2.VideoCapture stand-in for CaptureManager tests.

    ``reads`` is a list of bool 'ok' flags consumed one per read(); once
    exhausted it keeps returning the last value. A True returns a good frame,
    a False returns (False, None) — exactly what read_frame() treats as a
    failed read in non-loop mode."""

    def __init__(self, reads):
        self._reads = list(reads)
        self.released = False

    def read(self):
        ok = self._reads.pop(0) if len(self._reads) > 1 else (self._reads[0] if self._reads else False)
        if ok:
            return True, _good_frame()
        return False, None

    def set(self, *_a, **_k):
        return True

    def release(self):
        self.released = True


class OpenCaptureSide07Tests(unittest.TestCase):
    """SIDE-07: a webcam *index* that fails to open must raise WebcamOpenError
    (never fall through to the file-path branch). Only a genuinely non-integer
    spec reaches the file branch."""

    def test_failed_webcam_index_raises_webcam_error_not_file_error(self):
        original = _HT.cv2.VideoCapture
        try:
            # Force every VideoCapture (index OR path) to report not-opened so
            # we can prove which branch the failure surfaced from.
            class _NeverOpens:
                def __init__(self, *_a, **_k):
                    pass
                def isOpened(self):
                    return False
                def set(self, *_a, **_k):
                    return True
                def release(self):
                    pass
            _HT.cv2.VideoCapture = _NeverOpens
            with self.assertRaises(WebcamOpenError) as ctx:
                _HT.open_capture("0")
            # The message names the index and the busy/denied/absent reason,
            # NOT a file path — proving no fall-through.
            msg = str(ctx.exception)
            self.assertIn("webcam index 0", msg)
            self.assertIn("busy/denied/absent", msg)
            self.assertNotIn("capture source", msg)
        finally:
            _HT.cv2.VideoCapture = original

    def test_failed_file_path_raises_generic_runtime_error(self):
        original = _HT.cv2.VideoCapture
        try:
            class _NeverOpens:
                def __init__(self, *_a, **_k):
                    pass
                def isOpened(self):
                    return False
                def set(self, *_a, **_k):
                    return True
                def release(self):
                    pass
            _HT.cv2.VideoCapture = _NeverOpens
            # A non-integer spec must reach the file branch and raise the
            # generic RuntimeError, NOT WebcamOpenError.
            with self.assertRaises(RuntimeError) as ctx:
                _HT.open_capture("nonexistent_video.mp4")
            self.assertNotIsInstance(ctx.exception, WebcamOpenError)
            self.assertIn("nonexistent_video.mp4", str(ctx.exception))
        finally:
            _HT.cv2.VideoCapture = original


class CaptureManagerSide02Tests(unittest.TestCase):
    """SIDE-02: bounded camera-loss re-open. A transient read failure must
    yield None (so run() emits face_detected=false) and trigger a throttled
    re-open; only sustained failure past the bound sets give_up."""

    def test_healthy_read_passes_frame_through(self):
        cap = _FakeCap([True])
        mgr = CaptureManager("0", loop=False, reopenable=True,
                             opener=lambda spec: cap)
        mgr.open()
        frame = mgr.read(now_mono=0.0)
        self.assertIsNotNone(frame)
        self.assertFalse(mgr.give_up)

    def test_single_failure_returns_none_without_giving_up(self):
        cap = _FakeCap([False])
        mgr = CaptureManager("0", loop=False, reopenable=True,
                             opener=lambda spec: cap)
        mgr.open()
        frame = mgr.read(now_mono=0.0)
        self.assertIsNone(frame)
        self.assertFalse(mgr.give_up,
            "a single failed read must NOT give up (head-tracking stays alive)")

    def test_reopen_is_throttled_to_interval(self):
        """Within one reopen_interval_s only ONE re-open attempt is made even
        though read() is called every frame (~15 Hz)."""
        opens = {"count": 0}

        def opener(spec):
            opens["count"] += 1
            return _FakeCap([False])  # stays broken

        mgr = CaptureManager("0", loop=False, reopenable=True,
                             reopen_interval_s=2.0, reopen_window_s=60.0,
                             opener=opener)
        mgr.open()
        self.assertEqual(opens["count"], 1, "initial open")
        # First failure starts the failing timer + a re-open attempt.
        mgr.read(now_mono=0.0)
        self.assertEqual(opens["count"], 2, "first failure triggers a re-open")
        # Many frames within the same 2 s window: no further re-opens.
        t = 0.0
        for _ in range(20):
            t += 1.0 / 15.0  # ~15 Hz
            mgr.read(now_mono=t)
        self.assertEqual(opens["count"], 2,
            "re-open must be throttled to once per interval, not per frame")
        # Past the interval: exactly one more re-open.
        mgr.read(now_mono=2.5)
        self.assertEqual(opens["count"], 3)

    def test_recovery_after_transient_loss_clears_failure_state(self):
        """A capture that fails then recovers must clear give_up bookkeeping so
        a later short glitch doesn't trip the window prematurely."""
        # Opener returns a cap that fails once then succeeds forever.
        recovered = _FakeCap([False, True])

        def opener(spec):
            return recovered

        mgr = CaptureManager("0", loop=False, reopenable=True,
                             reopen_interval_s=0.0, reopen_window_s=60.0,
                             opener=opener)
        mgr.open()
        f1 = mgr.read(now_mono=0.0)   # fail -> None, re-open attempt
        self.assertIsNone(f1)
        f2 = mgr.read(now_mono=0.1)   # recovered cap now yields a frame
        self.assertIsNotNone(f2)
        self.assertFalse(mgr.give_up)
        # Internal failing timer cleared on recovery.
        self.assertIsNone(mgr._failing_since)

    def test_sustained_failure_past_window_sets_give_up(self):
        """After reopen_window_s of continuous failure, give_up is set so the
        caller exits non-zero."""
        mgr = CaptureManager("0", loop=False, reopenable=True,
                             reopen_interval_s=2.0, reopen_window_s=60.0,
                             opener=lambda spec: _FakeCap([False]))
        mgr.open()
        mgr.read(now_mono=0.0)        # start failing timer
        self.assertFalse(mgr.give_up)
        mgr.read(now_mono=59.0)       # still within the 60 s window
        self.assertFalse(mgr.give_up)
        mgr.read(now_mono=61.0)       # past the bound
        self.assertTrue(mgr.give_up,
            "give_up must be set after sustained failure exceeds the window")

    def test_non_reopenable_eof_returns_none_without_giving_up(self):
        """A finite video (non-reopenable) at EOF returns None as a legitimate
        end-of-stream — it must NOT arm the give-up bound or attempt re-open."""
        opens = {"count": 0}

        def opener(spec):
            opens["count"] += 1
            return _FakeCap([False])

        mgr = CaptureManager("video.mp4", loop=False, reopenable=False,
                             opener=opener)
        mgr.open()
        frame = mgr.read(now_mono=1000.0)
        self.assertIsNone(frame)
        self.assertFalse(mgr.give_up)
        self.assertEqual(opens["count"], 1,
            "non-reopenable source must not attempt a re-open")


class ResyncNextTickSide03Tests(unittest.TestCase):
    """SIDE-03: scheduler resync. When the loop falls >2 ticks behind, next_tick
    snaps to now (missed ticks dropped, not replayed). Otherwise it is left
    unchanged so normal cadence is preserved."""

    def test_on_time_leaves_next_tick_unchanged(self):
        next_tick = 100.0
        now = 100.0  # exactly on time
        self.assertEqual(resync_next_tick(now, next_tick), next_tick)

    def test_slightly_behind_does_not_resync(self):
        """One tick behind (< 2 * TARGET_DT_S) must NOT snap — normal jitter is
        absorbed by the existing cadence sleep, not by dropping ticks."""
        next_tick = 100.0
        now = next_tick + 1.0 * TARGET_DT_S
        self.assertEqual(resync_next_tick(now, next_tick), next_tick)

    def test_just_under_two_ticks_does_not_resync(self):
        """Just under 2 * TARGET_DT_S behind must NOT snap — only a stall
        STRICTLY greater than two ticks drops missed ticks. (Exact-boundary
        equality is float-dependent and not a meaningful contract; we assert
        just inside the boundary so the behaviour is deterministic.)"""
        next_tick = 100.0
        now = next_tick + 1.99 * TARGET_DT_S
        self.assertEqual(resync_next_tick(now, next_tick), next_tick)

    def test_far_behind_snaps_to_now(self):
        """A long stall (>2 ticks behind) snaps next_tick to now so the loop
        does NOT run unthrottled at camera rate to catch up."""
        next_tick = 100.0
        now = next_tick + 30.0  # 30 s stall (sleep / AV) — way past 2 ticks
        self.assertEqual(resync_next_tick(now, next_tick), now)


if __name__ == "__main__":
    unittest.main(verbosity=2)
