"""Synthetic MP4 clips created at test time with the `ffmpeg` on `PATH`; no media is committed."""

from __future__ import annotations

import shutil
import subprocess
from collections.abc import Sequence
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

WIDTH = 128
HEIGHT = 72
FRAME_RATE = 25


@dataclass(frozen=True)
class Shot:
    """A run of frames: a flat BGR color with optional sensor-like noise, or a panning pattern."""

    frames: int
    color: tuple[int, int, int] = (128, 128, 128)
    noise: float = 0.0
    pan_pixels_per_frame: int = 0


@dataclass(frozen=True)
class Clip:
    """A generated clip with its expected structure: frame count and the index of the first frame of each new shot."""

    path: Path
    frame_count: int
    frame_rate: float
    cut_indexes: tuple[int, ...]
    lead_in_frames: int = 0
    offset_seconds: float = 0.0
    shots: tuple[Shot, ...] = field(default_factory=tuple)

    def frame_ms(self, index: int) -> int:
        """Media time in milliseconds of the frame at `index`, including the timestamp offset."""
        return int((index * 1000) // self.frame_rate) + round(self.offset_seconds * 1000)


def require_tools() -> None:
    """Fail the calling test visibly when `ffmpeg` or `ffprobe` is absent."""
    for tool in ("ffmpeg", "ffprobe"):
        if shutil.which(tool) is None:
            raise AssertionError(f"the required tool '{tool}' is not on PATH; the capability tests need ffmpeg and ffprobe.")


def _render(shot: Shot, rng: np.random.Generator) -> list[np.ndarray]:
    frames: list[np.ndarray] = []
    if shot.pan_pixels_per_frame:
        pattern = np.linspace(40, 215, WIDTH * 4) + 30 * np.sin(np.arange(WIDTH * 4) / 9.0)
        pattern = pattern.clip(0, 255)
        for index in range(shot.frames):
            start = index * shot.pan_pixels_per_frame
            gray = np.tile(pattern[start : start + WIDTH], (HEIGHT, 1))
            frames.append(np.repeat(gray[:, :, None], 3, axis=2).astype(np.uint8))
        return frames
    base = np.empty((HEIGHT, WIDTH, 3), dtype=np.float64)
    base[:, :] = shot.color
    for _ in range(shot.frames):
        frame = base + rng.normal(0.0, shot.noise, base.shape) if shot.noise else base
        frames.append(frame.clip(0, 255).astype(np.uint8))
    return frames


def _encode(frames: Sequence[np.ndarray], path: Path, frame_rate: float, offset_seconds: float) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    command = [
        "ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{WIDTH}x{HEIGHT}", "-r", str(frame_rate), "-i", "-",
        "-c:v", "mpeg4", "-q:v", "1", "-pix_fmt", "yuv420p", "-g", "1000", "-bf", "0",
        "-output_ts_offset", str(offset_seconds), "-an", str(path),
    ]
    payload = b"".join(frame.tobytes() for frame in frames)
    completed = subprocess.run(command, input=payload, capture_output=True, check=False)
    assert completed.returncode == 0, f"ffmpeg failed to create synthetic media: {completed.stderr.decode().strip()}"


def create_clip(
    directory: Path,
    shots: Sequence[Shot],
    *,
    name: str = "segment.mp4",
    frame_rate: float = FRAME_RATE,
    offset_seconds: float = 0.0,
    lead_in_frames: int = 0,
    seed: int = 7,
) -> Clip:
    """Encode the shots back to back; the first `lead_in_frames` frames of a lead-in clip precede the requested start."""
    require_tools()
    rng = np.random.default_rng(seed)
    frames: list[np.ndarray] = []
    cuts: list[int] = []
    for shot in shots:
        if frames:
            cuts.append(len(frames))
        frames.extend(_render(shot, rng))
    path = directory / name
    _encode(frames, path, frame_rate, offset_seconds)
    return Clip(path, len(frames), frame_rate, tuple(cuts), lead_in_frames, offset_seconds, tuple(shots))


BLACK = (16, 16, 16)
WHITE = (235, 235, 235)
RED = (40, 40, 220)
BLUE = (210, 60, 40)


def hard_cuts_clip(directory: Path, **options: object) -> Clip:
    """Four shots separated by three high-contrast hard cuts at frames 25, 50, and 75."""
    return create_clip(directory, [Shot(25, BLACK), Shot(25, WHITE), Shot(25, RED), Shot(25, BLUE)], **options)  # type: ignore[arg-type]


def second_frame_cut_clip(directory: Path, **options: object) -> Clip:
    """A hard cut exactly between the first and the second frame of the clip."""
    return create_clip(directory, [Shot(1, BLACK), Shot(30, WHITE)], **options)  # type: ignore[arg-type]


def no_cut_clip(directory: Path, **options: object) -> Clip:
    """One uniform shot without any cut."""
    return create_clip(directory, [Shot(50, (90, 120, 150))], **options)  # type: ignore[arg-type]


def low_contrast_cut_clip(directory: Path, **options: object) -> Clip:
    """A cut below the fixed hard threshold in a quiet scene: gray 100 to gray 118 at frame 25."""
    shots = [Shot(25, (100, 100, 100), noise=1.0), Shot(25, (118, 118, 118), noise=1.0)]
    return create_clip(directory, shots, **options)  # type: ignore[arg-type]


def static_noise_clip(directory: Path, **options: object) -> Clip:
    """A static scene with sensor-like noise and no cut."""
    return create_clip(directory, [Shot(75, (110, 110, 110), noise=4.0)], **options)  # type: ignore[arg-type]


def steady_pan_clip(directory: Path, **options: object) -> Clip:
    """A steady horizontal pan of three pixels per frame and no cut."""
    return create_clip(directory, [Shot(75, pan_pixels_per_frame=3)], **options)  # type: ignore[arg-type]


def closely_spaced_cuts_clip(directory: Path, **options: object) -> Clip:
    """Two hard cuts five frames (200 ms at 25 fps) apart, at frames 20 and 25."""
    return create_clip(directory, [Shot(20, BLACK), Shot(5, WHITE), Shot(25, RED)], **options)  # type: ignore[arg-type]


def single_frame_clip(directory: Path, **options: object) -> Clip:
    """A clip with exactly one video frame."""
    return create_clip(directory, [Shot(1, BLACK)], **options)  # type: ignore[arg-type]


def lead_in_clip(directory: Path, *, lead_in_frames: int = 5, cut_in_lead_in: bool = False, **options: object) -> Clip:
    """A clip with `lead_in_frames` frames before the requested start.

    By default the shot changes exactly between the last lead-in frame and the first in-window frame; with
    `cut_in_lead_in` the only cut lies among the lead-in frames.
    """
    if cut_in_lead_in:
        shots = [Shot(2, BLACK), Shot(lead_in_frames - 2 + 25, WHITE)]
    else:
        shots = [Shot(lead_in_frames, BLACK), Shot(25, WHITE)]
    return create_clip(directory, shots, lead_in_frames=lead_in_frames, **options)  # type: ignore[arg-type]


def create_empty_video_clip(directory: Path, *, name: str = "empty.mp4") -> Path:
    """An MP4 whose video stream holds no frames."""
    require_tools()
    path = directory / name
    path.parent.mkdir(parents=True, exist_ok=True)
    command = [
        "ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", f"color=c=black:s={WIDTH}x{HEIGHT}:r={FRAME_RATE}:d=1",
        "-vf", "select=0", "-fps_mode", "passthrough", "-c:v", "mpeg4", "-pix_fmt", "yuv420p", "-an", str(path),
    ]
    completed = subprocess.run(command, capture_output=True, text=True, check=False)
    assert completed.returncode == 0, f"ffmpeg failed to create an empty clip: {completed.stderr.strip()}"
    return path


def create_audio_only_media(directory: Path, *, name: str = "audio-only.mp4") -> Path:
    """An MP4 without a video stream."""
    require_tools()
    path = directory / name
    path.parent.mkdir(parents=True, exist_ok=True)
    command = [
        "ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", "aac", str(path),
    ]
    completed = subprocess.run(command, capture_output=True, text=True, check=False)
    assert completed.returncode == 0, f"ffmpeg failed to create audio-only media: {completed.stderr.strip()}"
    return path
