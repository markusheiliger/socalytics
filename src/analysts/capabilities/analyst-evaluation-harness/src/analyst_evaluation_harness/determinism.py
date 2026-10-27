"""Compare repeated executions of one clip with its first execution (FR-009, research R7).

Facts are compared in canonical form: ordered by kind, start, end, and canonical payload. A kind without a tolerance
must be byte-identical in canonical JSON; a kind with a tolerance allows `startMs` and `endMs` to differ by
`positionMs` and `confidence` and numeric payload leaves to differ by `numeric`, while fact counts, kinds, and
non-numeric values must be equal.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from typing import Any

from socalytics_analyst.models.facts import Fact

from .canonical import canonical_text

FACT_COUNT = "fact-count"


def _document(fact: Fact) -> dict[str, Any]:
    return {
        "kind": fact.kind,
        "startMs": fact.start_ms,
        "endMs": fact.end_ms,
        "confidence": fact.confidence,
        "payload": fact.payload,
    }


def canonical_facts(facts: Sequence[Fact]) -> list[dict[str, Any]]:
    """Fact documents in canonical order."""
    documents = [_document(fact) for fact in facts]
    return sorted(
        documents,
        key=lambda d: (d["kind"], d["startMs"], d["endMs"], canonical_text(d["payload"])),
    )


def _is_number(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _leaf_equal(first: Any, other: Any, numeric: float | None) -> bool:
    if numeric is not None and _is_number(first) and _is_number(other):
        return abs(first - other) <= numeric
    return canonical_text(first) == canonical_text(other)


def _payload_differences(first: Any, other: Any, path: str, numeric: float | None) -> list[str]:
    """Dotted paths of the payload leaves that differ."""
    if isinstance(first, Mapping) and isinstance(other, Mapping):
        paths: list[str] = []
        for key in sorted(set(first) | set(other)):
            child = f"{path}.{key}"
            if key not in first or key not in other:
                paths.append(child)
            else:
                paths.extend(_payload_differences(first[key], other[key], child, numeric))
        return paths
    if isinstance(first, list) and isinstance(other, list):
        if len(first) != len(other):
            return [path]
        paths = []
        for index, (a, b) in enumerate(zip(first, other, strict=True)):
            paths.extend(_payload_differences(a, b, f"{path}[{index}]", numeric))
        return paths
    return [] if _leaf_equal(first, other, numeric) else [path]


def _fact_fields(first: Mapping[str, Any], other: Mapping[str, Any], tolerance: Mapping[str, float] | None) -> list[str]:
    """Names of the differing fields of two facts of one kind."""
    if tolerance is None:
        if canonical_text(first) == canonical_text(other):
            return []
        fields = [
            name
            for name in ("startMs", "endMs", "confidence")
            if canonical_text(first[name]) != canonical_text(other[name])
        ]
        numeric = None
    else:
        position = tolerance.get("positionMs", 0)
        numeric = tolerance.get("numeric", 0)
        fields = [name for name in ("startMs", "endMs") if abs(first[name] - other[name]) > position]
        if abs(first["confidence"] - other["confidence"]) > numeric:
            fields.append("confidence")
    fields.extend(_payload_differences(first["payload"], other["payload"], "payload", numeric))
    return fields


def compare_runs(
    clip_id: str,
    runs: Sequence[Sequence[Fact]],
    tolerance: Mapping[str, Mapping[str, float]] | None = None,
) -> list[dict[str, Any]]:
    """Differences of runs 2..n from run 1 of one clip; `runs[0]` is run 1.

    Each difference has `clipId`, `run` (1-based), `field`, and either `factIndex` (index of the fact in the canonical
    order of run 1) or `category` (`fact-count`, with the kind as `field`).
    """
    if not runs:
        return []
    tolerance = tolerance or {}
    base = canonical_facts(runs[0])
    base_by_kind: dict[str, list[tuple[int, dict[str, Any]]]] = {}
    for index, document in enumerate(base):
        base_by_kind.setdefault(document["kind"], []).append((index, document))

    differences: list[dict[str, Any]] = []
    for offset, facts in enumerate(runs[1:], start=2):
        by_kind: dict[str, list[dict[str, Any]]] = {}
        for document in canonical_facts(facts):
            by_kind.setdefault(document["kind"], []).append(document)
        for kind in sorted(set(base_by_kind) | set(by_kind)):
            expected = base_by_kind.get(kind, [])
            actual = by_kind.get(kind, [])
            if len(expected) != len(actual):
                differences.append({"clipId": clip_id, "run": offset, "category": FACT_COUNT, "field": kind})
                continue
            for (index, first), other in zip(expected, actual, strict=True):
                for name in _fact_fields(first, other, tolerance.get(kind)):
                    differences.append({"clipId": clip_id, "run": offset, "factIndex": index, "field": name})
    return differences
