"""Pair the facts of one clip with its labels (FR-005, FR-012, FR-018; data-model "Pairing result")."""

from __future__ import annotations

from collections.abc import Callable, Iterable, Mapping, Sequence
from dataclasses import dataclass, field
from typing import Any

from socalytics_analyst.models.facts import Fact

from .corpus import Label

FACT_POSITION = "fact-position"

NO_FACT = "no-fact"
PAYLOAD_MISMATCH = "payload-mismatch"
CLIP_FAILED = "clip-failed"

DEFAULT_RULE = FACT_POSITION

# A rule returns the distance in milliseconds of a fact from a label, or None when they do not match positionally.
MatchingRule = Callable[[Label, Fact, int], int | None]


def fact_position_distance(label: Label, fact: Fact, tolerance_ms: int) -> int | None:
    """Distance of `fact.start_ms` from the label, or None outside the label's position or widened range.

    The distance is 0 inside the unwidened range, otherwise the distance to the nearest range end (to the position for
    a label without a range).
    """
    start = fact.start_ms
    if label.position_ms is not None:
        distance = abs(start - label.position_ms)
    elif label.range_start_ms is not None and label.range_end_ms is not None:
        if start < label.range_start_ms:
            distance = label.range_start_ms - start
        elif start > label.range_end_ms:
            distance = start - label.range_end_ms
        else:
            distance = 0
    else:
        return None
    return distance if distance <= tolerance_ms else None


MATCHING_RULES: dict[str, MatchingRule] = {FACT_POSITION: fact_position_distance}

# The label-kind registry `{kind: matching rule name}`; a kind that is not listed uses `DEFAULT_RULE`.
LABEL_KIND_RULES: dict[str, str] = {}


def rule_for(label_kind: str) -> MatchingRule:
    return MATCHING_RULES[LABEL_KIND_RULES.get(label_kind, DEFAULT_RULE)]


def payload_matches(label: Label, fact: Fact) -> bool:
    """Deep equality of the labelled payload keys; always true for a label without `payload`."""
    if label.payload is None:
        return True
    return all(key in fact.payload and fact.payload[key] == value for key, value in label.payload.items())


@dataclass(frozen=True)
class Pair:
    label_index: int
    fact_index: int
    distance_ms: int

    def to_dict(self) -> dict[str, int]:
        return {"labelIndex": self.label_index, "factIndex": self.fact_index, "distanceMs": self.distance_ms}


@dataclass(frozen=True)
class MissedLabel:
    label_index: int
    reason: str

    def to_dict(self) -> dict[str, Any]:
        return {"labelIndex": self.label_index, "reason": self.reason}


@dataclass(frozen=True)
class Pairing:
    """Pairing result of one label kind; `pairs` holds only the true positives."""

    label_kind: str
    pairs: tuple[Pair, ...] = ()
    missed_labels: tuple[MissedLabel, ...] = ()
    false_positives: tuple[int, ...] = ()
    unlabelled: tuple[int, ...] = field(default=())

    @property
    def true_positives(self) -> int:
        return len(self.pairs)

    def to_dict(self) -> dict[str, Any]:
        return {
            "labelKind": self.label_kind,
            "pairs": [pair.to_dict() for pair in self.pairs],
            "missedLabels": [missed.to_dict() for missed in self.missed_labels],
            "falsePositives": list(self.false_positives),
            "unlabelled": list(self.unlabelled),
        }


def _kinds(labels: Sequence[Label], label_kinds: Iterable[str] | None) -> list[str]:
    if label_kinds is None:
        label_kinds = (label.label_kind for label in labels)
    return sorted(set(label_kinds))


def _unlabelled(facts: Sequence[Fact], kinds: Iterable[str]) -> tuple[int, ...]:
    known = set(kinds)
    return tuple(index for index, fact in enumerate(facts) if fact.kind not in known)


def pair_kind(
    label_kind: str,
    labels: Sequence[Label],
    facts: Sequence[Fact],
    tolerance_ms: int = 0,
) -> Pairing:
    """Pair the labels and facts of one kind one to one, greedily by increasing distance.

    Ties are broken by label order, then fact order. Indexes refer to `labels` and `facts` as given. A pair whose
    payload expectation is not met is a `payload-mismatch` miss plus a false positive.
    """
    rule = rule_for(label_kind)
    label_indexes = [i for i, label in enumerate(labels) if label.label_kind == label_kind]
    fact_indexes = [i for i, fact in enumerate(facts) if fact.kind == label_kind]

    candidates: list[tuple[int, int, int]] = []
    for label_index in label_indexes:
        for fact_index in fact_indexes:
            distance = rule(labels[label_index], facts[fact_index], tolerance_ms)
            if distance is not None:
                candidates.append((distance, label_index, fact_index))
    candidates.sort()

    used_labels: set[int] = set()
    used_facts: set[int] = set()
    pairs: list[Pair] = []
    missed: dict[int, str] = {}
    false_positives: set[int] = set()
    for distance, label_index, fact_index in candidates:
        if label_index in used_labels or fact_index in used_facts:
            continue
        used_labels.add(label_index)
        used_facts.add(fact_index)
        if payload_matches(labels[label_index], facts[fact_index]):
            pairs.append(Pair(label_index, fact_index, distance))
        else:
            missed[label_index] = PAYLOAD_MISMATCH
            false_positives.add(fact_index)

    for label_index in label_indexes:
        if label_index not in used_labels:
            missed[label_index] = NO_FACT
    false_positives.update(i for i in fact_indexes if i not in used_facts)

    return Pairing(
        label_kind=label_kind,
        pairs=tuple(sorted(pairs, key=lambda pair: pair.label_index)),
        missed_labels=tuple(MissedLabel(i, reason) for i, reason in sorted(missed.items())),
        false_positives=tuple(sorted(false_positives)),
    )


def pair_clip(
    labels: Sequence[Label],
    facts: Sequence[Fact],
    tolerances_ms: Mapping[str, int] | None = None,
    label_kinds: Iterable[str] | None = None,
) -> list[Pairing]:
    """Pairing results of one clip, one per label kind, sorted by kind.

    `label_kinds` are the evaluated kinds (default: the kinds of the clip's labels), so a clip without labels still
    counts the facts of an evaluated kind as false positives. Facts of other kinds are `unlabelled`, reported on the
    first pairing only.
    """
    tolerances = tolerances_ms or {}
    kinds = _kinds(labels, label_kinds)
    results = [pair_kind(kind, labels, facts, tolerances.get(kind, 0)) for kind in kinds]
    unlabelled = _unlabelled(facts, kinds)
    if results:
        first = results[0]
        results[0] = Pairing(
            first.label_kind, first.pairs, first.missed_labels, first.false_positives, unlabelled
        )
    return results


def failed_clip_pairing(
    labels: Sequence[Label],
    label_kinds: Iterable[str] | None = None,
) -> list[Pairing]:
    """Pairing results of a failed clip: every label missed with `clip-failed`, and no facts."""
    kinds = _kinds(labels, label_kinds)
    return [
        Pairing(
            label_kind=kind,
            missed_labels=tuple(
                MissedLabel(i, CLIP_FAILED) for i, label in enumerate(labels) if label.label_kind == kind
            ),
        )
        for kind in kinds
    ]
