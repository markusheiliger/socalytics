"""Per-profile, per-label-kind metrics from summed counts (research R5; FR-006).

Counts are summed per profile and label kind before ratios are formed. There is never an aggregate across profiles.
"""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass
from typing import Any

NO_PREDICTIONS = "no true or false positives"
NO_LABELS = "no true positives or false negatives"
NO_POSITIVES = "no true positives, false positives, or false negatives"


@dataclass(frozen=True)
class Counts:
    """True positives, false positives, and false negatives of one subject."""

    true_positives: int = 0
    false_positives: int = 0
    false_negatives: int = 0

    def __add__(self, other: Counts) -> Counts:
        return Counts(
            self.true_positives + other.true_positives,
            self.false_positives + other.false_positives,
            self.false_negatives + other.false_negatives,
        )


def clip_counts(*, labels: int, true_positives: int, false_positives: int, failed: bool) -> Counts:
    """Counts of one clip and label kind.

    A failed clip contributes all of its labels as false negatives and no facts, so it never counts as agreement.
    """
    if failed:
        return Counts(0, 0, labels)
    return Counts(true_positives, false_positives, labels - true_positives)


def sum_counts(counts: Iterable[Counts]) -> Counts:
    total = Counts()
    for item in counts:
        total = total + item
    return total


def compute_metrics(counts: Counts) -> dict[str, Any]:
    """Report metrics object; an undefined ratio is `None` with its `*Undefined` reason."""
    tp, fp, fn = counts.true_positives, counts.false_positives, counts.false_negatives
    result: dict[str, Any] = {
        "truePositives": tp,
        "falsePositives": fp,
        "falseNegatives": fn,
        "precision": None,
        "recall": None,
        "f1": None,
    }
    if tp + fp > 0:
        result["precision"] = tp / (tp + fp)
    else:
        result["precisionUndefined"] = NO_PREDICTIONS
    if tp + fn > 0:
        result["recall"] = tp / (tp + fn)
    else:
        result["recallUndefined"] = NO_LABELS
    if 2 * tp + fp + fn > 0:
        result["f1"] = 2 * tp / (2 * tp + fp + fn)
    else:
        result["f1Undefined"] = NO_POSITIVES
    return result


def profile_metrics(items: Iterable[tuple[str, str, Counts]]) -> dict[str, dict[str, dict[str, Any]]]:
    """Metrics keyed by profile and then label kind from `(profile, label kind, counts)` items."""
    summed: dict[str, dict[str, Counts]] = {}
    for profile, kind, counts in items:
        by_kind = summed.setdefault(profile, {})
        by_kind[kind] = by_kind.get(kind, Counts()) + counts
    return {
        profile: {kind: compute_metrics(counts) for kind, counts in sorted(by_kind.items())}
        for profile, by_kind in sorted(summed.items())
    }
