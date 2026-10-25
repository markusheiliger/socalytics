"""Streaming frame source: yields `(media_ms, frame)` per decoded frame of the first video stream.

Timestamps come from `ffprobe` (`best_effort_timestamp`, exact time base) and pixels from `ffmpeg` (64x36 `bgr24`
rawvideo, one fixed-size record per frame); the n-th timestamp is paired with the n-th frame.
"""

from __future__ import annotations

import subprocess
import threading
from collections.abc import Iterator
from fractions import Fraction
from pathlib import Path

import numpy as np
from socalytics_analyst import InputError, ResultError

MEDIA_DECODE_FAILED = "media-decode-failed"
MEDIA_NO_VIDEO_STREAM = "media-no-video-stream"
FRAME_WIDTH = 64
FRAME_HEIGHT = 36
FRAME_BYTES = FRAME_WIDTH * FRAME_HEIGHT * 3
DEFAULT_SCRATCH = Path("/socalytics/scratch")
STDERR_LIMIT_BYTES = 4096


def _fail(message: str) -> ResultError:
    return ResultError(MEDIA_DECODE_FAILED, message)


class _BoundedStderr:
    """Drains a pipe on a thread and keeps only the first bytes, so a chatty process can never block or grow memory."""

    def __init__(self, stream: object) -> None:
        self._stream = stream
        self._buffer = bytearray()
        self._thread = threading.Thread(target=self._drain, daemon=True)
        self._thread.start()

    def _drain(self) -> None:
        while chunk := self._stream.read(1024):  # type: ignore[attr-defined]
            if len(self._buffer) < STDERR_LIMIT_BYTES:
                self._buffer.extend(chunk[: STDERR_LIMIT_BYTES - len(self._buffer)])

    def text(self) -> str:
        self._thread.join()
        return self._buffer.decode("utf-8", errors="replace").strip()


def _start(arguments: list[str], scratch: Path | None) -> subprocess.Popen[bytes]:
    try:
        return subprocess.Popen(
            arguments,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=scratch if scratch is not None and scratch.is_dir() else None,
            shell=False,
        )
    except OSError as error:
        raise _fail(f"{arguments[0]} could not be started ({error.__class__.__name__}).") from None


def _stop(process: subprocess.Popen[bytes]) -> None:
    if process.poll() is None:
        process.kill()
    process.wait()
    process.stdout.close()  # type: ignore[union-attr]
    process.stderr.close()  # type: ignore[union-attr]


def _run_probe(media: Path, entries: str, scratch: Path | None) -> list[str]:
    process = _start(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", entries, "-of", "csv=p=0", str(media)],
        scratch,
    )
    stderr = _BoundedStderr(process.stderr)
    try:
        output = process.stdout.read().decode("utf-8", errors="replace")  # type: ignore[union-attr]
        code = process.wait()
        detail = stderr.text()
    finally:
        _stop(process)
    if code != 0:
        raise _fail(f"ffprobe exited with {code}: {detail}")
    return [line.strip() for line in output.splitlines() if line.strip()]


def read_time_base(media: Path, scratch: Path | None = DEFAULT_SCRATCH) -> Fraction:
    """Read the first video stream's time base as an exact fraction."""
    lines = _run_probe(media, "stream=time_base", scratch)
    if not lines:
        raise InputError(MEDIA_NO_VIDEO_STREAM, "the media has no video stream.")
    try:
        time_base = Fraction(lines[0])
    except (ValueError, ZeroDivisionError):
        raise _fail("the video stream time base is not a rational number.") from None
    if time_base <= 0:
        raise _fail("the video stream time base is not positive.")
    return time_base


def read_timestamps(media: Path, scratch: Path | None = DEFAULT_SCRATCH) -> list[int]:
    """List `media_ms = floor(pts * time_base * 1000)` for every frame of the first video stream, in decode order."""
    time_base = read_time_base(media, scratch)
    timestamps: list[int] = []
    for line in _run_probe(media, "frame=best_effort_timestamp", scratch):
        try:
            pts = int(line.rstrip(","))
        except ValueError:
            raise _fail("a decoded frame has no timestamp.") from None
        timestamps.append((pts * time_base * 1000).__floor__())
    return timestamps


def _read_record(stream: object) -> bytes | None:
    """Read exactly one frame record; `None` at a clean end, a failure on a partial record."""
    data = bytearray()
    while len(data) < FRAME_BYTES:
        chunk = stream.read(FRAME_BYTES - len(data))  # type: ignore[attr-defined]
        if not chunk:
            break
        data.extend(chunk)
    if not data:
        return None
    if len(data) != FRAME_BYTES:
        raise _fail("the decoder ended inside a frame record.")
    return bytes(data)


def decode_frames(media: Path, scratch: Path | None = DEFAULT_SCRATCH) -> Iterator[tuple[int, np.ndarray]]:
    """Stream `(media_ms, frame)` per frame; `frame` is a read-only `(36, 64, 3)` `uint8` BGR array and nothing is stored."""
    media = media.resolve()
    timestamps = read_timestamps(media, scratch)
    process = _start(
        [
            "ffmpeg", "-v", "error", "-nostdin", "-i", str(media), "-map", "0:v:0",
            "-vf", f"scale={FRAME_WIDTH}:{FRAME_HEIGHT}:flags=area", "-fps_mode", "passthrough",
            "-pix_fmt", "bgr24", "-f", "rawvideo", "-",
        ],
        scratch,
    )
    stderr = _BoundedStderr(process.stderr)
    try:
        count = 0
        while (record := _read_record(process.stdout)) is not None:
            if count >= len(timestamps):
                raise _fail("the decoder produced more frames than the stream lists.")
            frame = np.frombuffer(record, dtype=np.uint8).reshape(FRAME_HEIGHT, FRAME_WIDTH, 3)
            yield timestamps[count], frame
            count += 1
        code = process.wait()
        detail = stderr.text()
        if code != 0:
            raise _fail(f"ffmpeg exited with {code} decoding the frames: {detail}")
        if count != len(timestamps):
            raise _fail(f"the decoder produced {count} frames for {len(timestamps)} listed timestamps.")
    finally:
        _stop(process)
