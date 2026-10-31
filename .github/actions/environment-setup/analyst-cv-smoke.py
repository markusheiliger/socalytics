#!/usr/bin/env python3
"""Synthetic computer-vision smoke check for the Analyst environment.

Uses only the pinned ffmpeg (to write a two-second 64x64 MPEG-4 clip), OpenCV, NumPy, and ONNX Runtime from the
interpreter that runs it. Needs no model file and no `onnx` package. Deletes only its own temporary directory.
"""

import argparse
import os
import shutil
import struct
import subprocess
import sys
import tempfile

COMMAND_TIMEOUT_SECONDS = 60
OUTPUT_LIMIT = 300


class SmokeFailure(Exception):
    pass


def truncated(text):
    text = (text or "").strip()
    return text[:OUTPUT_LIMIT]


def run_bounded(operation, command):
    try:
        completed = subprocess.run(
            command,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=COMMAND_TIMEOUT_SECONDS,
            check=False,
            text=True,
            errors="replace",
        )
    except subprocess.TimeoutExpired:
        raise SmokeFailure(f"{operation} timed out after {COMMAND_TIMEOUT_SECONDS} seconds")
    except OSError as error:
        raise SmokeFailure(f"{operation} could not start: {truncated(str(error))}")
    if completed.returncode != 0:
        raise SmokeFailure(f"{operation} failed: {truncated(completed.stdout)}")


def varint(value):
    out = bytearray()
    while True:
        part = value & 0x7F
        value >>= 7
        if value:
            out.append(part | 0x80)
        else:
            out.append(part)
            return bytes(out)


def field_varint(number, value):
    return varint(number << 3) + varint(value)


def field_bytes(number, payload):
    if isinstance(payload, str):
        payload = payload.encode("utf-8")
    return varint((number << 3) | 2) + varint(len(payload)) + payload


def value_info(name, length):
    dim = field_bytes(1, field_varint(1, length))
    shape = field_bytes(2, dim)
    tensor = field_varint(1, 1) + shape  # elem_type 1 is float
    return field_bytes(1, name) + field_bytes(2, field_bytes(1, tensor))


def add_model_bytes(length):
    node = field_bytes(1, "a") + field_bytes(1, "b") + field_bytes(2, "c") + field_bytes(4, "Add")
    graph = (
        field_bytes(1, node)
        + field_bytes(2, "smoke_add")
        + field_bytes(11, value_info("a", length))
        + field_bytes(11, value_info("b", length))
        + field_bytes(12, value_info("c", length))
    )
    opset = field_bytes(1, "") + field_varint(2, 13)
    return field_varint(1, 8) + field_bytes(8, opset) + field_bytes(7, graph)


def create_clip(ffmpeg, clip):
    run_bounded(
        "creating the synthetic clip",
        [
            ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=25:duration=2",
            "-c:v", "mpeg4", "-g", "5", "-pix_fmt", "yuv420p", "-an", "-y", clip,
        ],
    )
    if not os.path.isfile(clip) or os.path.getsize(clip) == 0:
        raise SmokeFailure("creating the synthetic clip produced no output")


def decode_means(clip):
    try:
        import cv2
        import numpy
    except Exception as error:
        raise SmokeFailure(f"importing OpenCV or NumPy failed: {truncated(str(error))}")
    capture = cv2.VideoCapture(clip)
    try:
        if not capture.isOpened():
            raise SmokeFailure("decoding with OpenCV failed: the clip could not be opened")
        means = []
        while True:
            ok, frame = capture.read()
            if not ok:
                break
            means.append(float(numpy.mean(frame)))
    finally:
        capture.release()
    if not means:
        raise SmokeFailure("decoding with OpenCV failed: no frame was decoded")
    mean = float(numpy.mean(numpy.array(means, dtype=numpy.float64)))
    if not numpy.isfinite(mean):
        raise SmokeFailure("NumPy computed a non-finite mean")
    return len(means), mean


def run_add_graph():
    try:
        import numpy
        import onnxruntime
    except Exception as error:
        raise SmokeFailure(f"importing ONNX Runtime or NumPy failed: {truncated(str(error))}")
    try:
        session = onnxruntime.InferenceSession(add_model_bytes(4), providers=["CPUExecutionProvider"])
        a = numpy.array([1.0, 2.0, 3.0, 4.0], dtype=numpy.float32)
        b = numpy.array([0.5, 0.25, 0.125, 0.0625], dtype=numpy.float32)
        (result,) = session.run(["c"], {"a": a, "b": b})
    except Exception as error:
        raise SmokeFailure(f"running the ONNX Add graph failed: {truncated(str(error))}")
    if not numpy.allclose(result, a + b):
        raise SmokeFailure(f"the ONNX Add graph returned unexpected values: {truncated(str(result.tolist()))}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", default=os.environ.get("ANALYST_CV_FFMPEG"))
    arguments = parser.parse_args()
    ffmpeg = arguments.ffmpeg or shutil.which("ffmpeg")
    if not ffmpeg:
        print("synthetic CV smoke check failed: no ffmpeg is available", file=sys.stderr)
        return 1

    fixture = tempfile.mkdtemp(prefix="analyst-cv-smoke.")
    try:
        clip = os.path.join(fixture, "clip.mp4")
        create_clip(ffmpeg, clip)
        frames, mean = decode_means(clip)
        run_add_graph()
    except SmokeFailure as failure:
        print(f"synthetic CV smoke check failed: {failure}", file=sys.stderr)
        return 1
    finally:
        shutil.rmtree(fixture, ignore_errors=True)

    print(
        f"Verified synthetic CV smoke check: decoded {frames} frames, NumPy mean {mean:.2f}, "
        "ONNX Runtime Add graph executed"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
