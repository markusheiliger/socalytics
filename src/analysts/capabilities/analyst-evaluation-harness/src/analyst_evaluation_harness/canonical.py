"""Canonical JSON, metric rounding, and sha-256 identities (research R4)."""

from __future__ import annotations

import hashlib
import json
import math
from collections.abc import Iterable, Mapping
from decimal import ROUND_HALF_EVEN, Decimal
from typing import Any

METRIC_DECIMALS = 6
IDENTITY_PREFIX = "sha-256:"

_QUANTUM = Decimal(1).scaleb(-METRIC_DECIMALS)


def round_metric(value: float) -> float:
    """Round a ratio to six decimals, half to even; refuses NaN and infinity."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError(f"metric must be a number, not {type(value).__name__}")
    if not math.isfinite(value):
        raise ValueError("metric must be finite")
    rounded = Decimal(repr(float(value))).quantize(_QUANTUM, rounding=ROUND_HALF_EVEN)
    # Normalize negative zero so equal metrics serialize identically.
    return float(rounded) + 0.0


def round_floats(document: Any) -> Any:
    """Return a copy of the document with every float rounded as a metric."""
    if isinstance(document, bool):
        return document
    if isinstance(document, float):
        return round_metric(document)
    if isinstance(document, Mapping):
        return {key: round_floats(item) for key, item in document.items()}
    if isinstance(document, (list, tuple)):
        return [round_floats(item) for item in document]
    return document


def canonical_bytes(document: Any) -> bytes:
    """UTF-8 JSON with sorted keys and compact separators; NaN and infinity are refused."""
    return json.dumps(
        document,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
        allow_nan=False,
    ).encode("utf-8")


def canonical_text(document: Any) -> str:
    return canonical_bytes(document).decode("utf-8")


def identity_of_bytes(data: bytes) -> str:
    return IDENTITY_PREFIX + hashlib.sha256(data).hexdigest()


def identity_of(document: Any, exclude: Iterable[str] = ()) -> str:
    """Identity of the canonical form of a document without the excluded top-level fields."""
    excluded = set(exclude)
    if excluded and isinstance(document, Mapping):
        document = {key: value for key, value in document.items() if key not in excluded}
    return identity_of_bytes(canonical_bytes(document))
