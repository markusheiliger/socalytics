from __future__ import annotations

from decimal import Decimal
from fractions import Fraction

import cv2
import numpy as np
import pytest

from media_context_classification.measure import change_measure, quantize

WIDTH = 64
HEIGHT = 36


def _solid(blue: int, green: int, red: int, *, width: int = WIDTH, height: int = HEIGHT) -> np.ndarray:
    frame = np.empty((height, width, 3), dtype=np.uint8)
    frame[:, :] = (blue, green, red)
    return frame


def _noise(seed: int) -> np.ndarray:
    return np.random.default_rng(seed).integers(0, 256, size=(HEIGHT, WIDTH, 3), dtype=np.uint8)


def test_identical_frames_measure_zero() -> None:
    for frame in (_solid(0, 0, 0), _solid(10, 200, 90), _noise(1)):
        result = change_measure(frame, frame.copy())
        assert result == Decimal("0")
        assert str(result) == "0.000"


def test_full_scale_change_between_black_and_white() -> None:
    # Only the value channel changes, by 255, so the mean over three channels is 85.
    result = change_measure(_solid(0, 0, 0), _solid(255, 255, 255))
    assert result == Decimal("85.000")
    assert change_measure(_solid(255, 255, 255), _solid(0, 0, 0)) == result


def test_largest_possible_change_uses_saturation_and_value() -> None:
    # Black to pure red changes saturation and value by 255 each and hue by 0.
    assert change_measure(_solid(0, 0, 0), _solid(0, 0, 255)) == Decimal("170.000")


def test_color_only_change_is_detected() -> None:
    red = _solid(0, 0, 255)
    green = _solid(0, 255, 0)
    # Both colors share saturation and value, so only the hue differs (0 versus 60).
    assert change_measure(red, green) == Decimal("20.000")


def test_hue_is_taken_without_wrap_around() -> None:
    red = _solid(0, 0, 255)
    magenta = _solid(255, 0, 255)
    hue_red = int(cv2.cvtColor(red, cv2.COLOR_BGR2HSV)[0, 0, 0])
    hue_magenta = int(cv2.cvtColor(magenta, cv2.COLOR_BGR2HSV)[0, 0, 0])
    assert (hue_red, hue_magenta) == (0, 150)
    assert change_measure(red, magenta) == Decimal("50.000")


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        (Fraction(0), "0.000"),
        (Fraction(1, 3), "0.333"),
        (Fraction(2, 3), "0.667"),
        (Fraction(5, 2000), "0.002"),
        (Fraction(15, 2000), "0.008"),
        (Fraction(25, 2000), "0.012"),
        (Fraction(35, 2000), "0.018"),
        (Fraction(51, 100000), "0.001"),
        (Fraction(5, 10000), "0.000"),
        (Fraction(15, 10000), "0.002"),
        (Fraction(25, 10000), "0.002"),
        (Fraction(1001, 10000), "0.100"),
        (Fraction(255), "255.000"),
    ],
)
def test_quantization_is_three_decimals_round_half_even(value: Fraction, expected: str) -> None:
    result = quantize(value)
    assert str(result) == expected
    assert result.as_tuple().exponent == -3


def test_measure_is_quantized_from_exact_sums() -> None:
    # One pixel whose value differs by one: 1 / 3 rounds to 0.333, 2 / 3 to 0.667.
    base = _solid(10, 10, 10, width=1, height=1)
    assert change_measure(base, _solid(11, 11, 11, width=1, height=1)) == Decimal("0.333")
    assert change_measure(base, _solid(12, 12, 12, width=1, height=1)) == Decimal("0.667")


def test_measure_is_symmetric_and_in_range() -> None:
    first, second = _noise(2), _noise(3)
    forward = change_measure(first, second)
    assert forward == change_measure(second, first)
    assert Decimal("0") < forward <= Decimal("255")


def test_measure_does_not_depend_on_thread_count() -> None:
    first, second = _noise(4), _noise(5)
    original = cv2.getNumThreads()
    try:
        results = []
        for threads in (1, 2, 4, 8):
            cv2.setNumThreads(threads)
            results.append(change_measure(first, second))
    finally:
        cv2.setNumThreads(original)
    assert len(set(results)) == 1


def test_importing_the_measure_limits_opencv_to_one_thread() -> None:
    import importlib

    import media_context_classification.measure as measure

    original = cv2.getNumThreads()
    try:
        cv2.setNumThreads(4)
        importlib.reload(measure)
        assert cv2.getNumThreads() == 1
    finally:
        cv2.setNumThreads(original)


def test_mismatched_frames_are_rejected() -> None:
    with pytest.raises(ValueError):
        change_measure(_solid(0, 0, 0), _solid(0, 0, 0, width=32))
    with pytest.raises(ValueError):
        change_measure(np.zeros((HEIGHT, WIDTH), dtype=np.uint8), np.zeros((HEIGHT, WIDTH), dtype=np.uint8))
