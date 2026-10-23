"""Synthetic MP4 media created at test time with the `ffmpeg` on `PATH`."""

from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

FRAME_SIZE = "64x64"


def require_tools() -> None:
    """Fail the calling test visibly when `ffmpeg` or `ffprobe` is absent."""
    for tool in ("ffmpeg", "ffprobe"):
        if shutil.which(tool) is None:
            raise AssertionError(f"the required tool '{tool}' is not on PATH; the capability tests need ffmpeg and ffprobe.")


def create_media(
    directory: Path,
    *,
    name: str = "segment.mp4",
    duration_seconds: float = 5,
    frame_rate: float = 25,
    offset_seconds: float = 0,
) -> Path:
    """Create a `testsrc2` MPEG-4 clip with a keyframe interval equal to the frame rate and a timestamp offset."""
    require_tools()
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / name
    keyframe_interval = max(1, round(frame_rate))
    command = [
        "ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", f"testsrc2=size={FRAME_SIZE}:rate={frame_rate}:duration={duration_seconds}",
        "-c:v", "mpeg4", "-pix_fmt", "yuv420p", "-g", str(keyframe_interval),
        "-output_ts_offset", str(offset_seconds), "-an", str(path),
    ]
    completed = subprocess.run(command, capture_output=True, text=True, check=False)
    assert completed.returncode == 0, f"ffmpeg failed to create synthetic media: {completed.stderr.strip()}"
    return path
