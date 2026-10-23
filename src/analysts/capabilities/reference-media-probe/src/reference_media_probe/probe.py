"""Streaming frame decoder: yields `(media_ms, y_avg)` per decoded video frame using the image's `ffprobe`."""

from __future__ import annotations

import subprocess
import threading
from collections.abc import Iterator
from decimal import Decimal, InvalidOperation
from fractions import Fraction
from pathlib import Path

from socalytics_analyst import ResultError

MEDIA_DECODE_FAILED = "media-decode-failed"
DEFAULT_SCRATCH = Path("/socalytics/scratch")
STDERR_LIMIT_BYTES = 4096


def _fail(message: str) -> ResultError:
    return ResultError(MEDIA_DECODE_FAILED, message)


def _escape(value: str, characters: str) -> str:
    return "".join(f"\\{c}" if c in characters else c for c in value)


def _movie_source(media: Path) -> str:
    """Escape a path for the `movie` source twice: as a filter option value, then as filtergraph text."""
    option_value = _escape(str(media), "\\':")
    return "movie=filename=" + _escape(option_value, "\\'[],;")


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


def _start(arguments: list[str], scratch: Path) -> subprocess.Popen[bytes]:
    try:
        return subprocess.Popen(
            arguments,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=scratch,
            shell=False,
        )
    except OSError as error:
        raise _fail(f"{arguments[0]} could not be started ({error.__class__.__name__}).") from None


def read_time_base(media: Path, scratch: Path = DEFAULT_SCRATCH) -> Fraction:
    """Read the first video stream's time base as an exact fraction."""
    process = _start(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=time_base", "-of", "csv=p=0", str(media)],
        scratch,
    )
    stderr = _BoundedStderr(process.stderr)
    output = process.stdout.read().decode("utf-8", errors="replace")  # type: ignore[union-attr]
    code = process.wait()
    detail = stderr.text()
    if code != 0:
        raise _fail(f"ffprobe exited with {code} reading the time base: {detail}")
    lines = [line.strip() for line in output.splitlines() if line.strip()]
    if len(lines) != 1:
        raise _fail("the media has no single video stream time base.")
    try:
        time_base = Fraction(lines[0])
    except (ValueError, ZeroDivisionError):
        raise _fail("the video stream time base is not a rational number.") from None
    if time_base <= 0:
        raise _fail("the video stream time base is not positive.")
    return time_base


def _parse_line(line: str, time_base: Fraction) -> tuple[int, Decimal]:
    fields = line.split(",")
    if len(fields) != 2:
        raise _fail("a frame line has an unexpected shape.")
    timestamp, luma = fields[0].strip(), fields[1].strip()
    try:
        pts = int(timestamp)
    except ValueError:
        raise _fail("a decoded frame has no timestamp.") from None
    try:
        y_avg = Decimal(luma)
    except InvalidOperation:
        raise _fail("a decoded frame has no luma value.") from None
    if not y_avg.is_finite():
        raise _fail("a decoded frame has no luma value.")
    media_ms = (pts * time_base * 1000).__floor__()
    return media_ms, y_avg


def decode_frames(media: Path, scratch: Path = DEFAULT_SCRATCH) -> Iterator[tuple[int, Decimal]]:
    """Stream `(media_ms, y_avg)` per frame; `media_ms` is `floor(pts * time_base * 1000)` and nothing is stored."""
    media = media.resolve()
    time_base = read_time_base(media, scratch)
    process = _start(
        [
            "ffprobe", "-v", "error", "-f", "lavfi", "-i", f"{_movie_source(media)},signalstats",
            "-show_entries", "frame=best_effort_timestamp:frame_tags=lavfi.signalstats.YAVG",
            "-of", "csv=p=0",
        ],
        scratch,
    )
    stderr = _BoundedStderr(process.stderr)
    try:
        for raw in process.stdout:  # type: ignore[union-attr]
            line = raw.decode("utf-8", errors="replace").strip()
            if line:
                yield _parse_line(line, time_base)
        code = process.wait()
        detail = stderr.text()
        if code != 0:
            raise _fail(f"ffprobe exited with {code} decoding the frames: {detail}")
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        process.stdout.close()  # type: ignore[union-attr]
        process.stderr.close()  # type: ignore[union-attr]
