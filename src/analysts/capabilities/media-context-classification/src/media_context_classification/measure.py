"""HSV content-change measure between two consecutive downscaled frames."""

from __future__ import annotations

from decimal import ROUND_HALF_EVEN, Decimal
from fractions import Fraction

import cv2
import numpy as np

cv2.setNumThreads(1)

_QUANTUM = Decimal("0.001")


def _to_hsv(frame: np.ndarray) -> np.ndarray:
    return cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)


def quantize(value: Fraction) -> Decimal:
    """Round an exact fraction to three decimals with ROUND_HALF_EVEN."""
    # Decimal division is limited to the context precision, so the thousandths are
    # rounded on exact integers and only then turned into a Decimal.
    scaled = value * 1000
    whole, remainder = divmod(scaled.numerator, scaled.denominator)
    twice = remainder * 2
    if twice > scaled.denominator or (twice == scaled.denominator and whole % 2 == 1):
        whole += 1
    return Decimal(whole).scaleb(-3).quantize(_QUANTUM, rounding=ROUND_HALF_EVEN)


def change_measure(previous: np.ndarray, current: np.ndarray) -> Decimal:
    """Return the HSV change between two BGR frames, from 0 to 255 with three decimals.

    The measure is ``(sum|dH| + sum|dS| + sum|dV|) / (3 * pixels)`` over the 8-bit
    HSV conversions, with hue taken without wrap-around. The sums are integers, so
    the result does not depend on the host or the number of threads.
    """
    if previous.shape != current.shape or previous.ndim != 3 or previous.shape[2] != 3:
        raise ValueError("frames must be two BGR images of the same shape")
    difference = cv2.absdiff(_to_hsv(previous), _to_hsv(current))
    total = int(difference.sum(dtype=np.int64))
    pixels = previous.shape[0] * previous.shape[1]
    return quantize(Fraction(total, 3 * pixels))
