from __future__ import annotations

import pytest

from analyst_evaluation_harness.metrics import (
    NO_LABELS,
    NO_POSITIVES,
    NO_PREDICTIONS,
    Counts,
    clip_counts,
    compute_metrics,
    profile_metrics,
    sum_counts,
)


def test_counts_and_ratios_by_hand() -> None:
    metrics = compute_metrics(Counts(true_positives=6, false_positives=2, false_negatives=4))

    assert metrics["truePositives"] == 6
    assert metrics["falsePositives"] == 2
    assert metrics["falseNegatives"] == 4
    assert metrics["precision"] == pytest.approx(6 / 8)
    assert metrics["recall"] == pytest.approx(6 / 10)
    assert metrics["f1"] == pytest.approx(12 / 18)
    assert not any(key.endswith("Undefined") for key in metrics)


def test_perfect_agreement_gives_one() -> None:
    metrics = compute_metrics(Counts(5, 0, 0))

    assert metrics["precision"] == 1
    assert metrics["recall"] == 1
    assert metrics["f1"] == 1


def test_no_predictions_leaves_precision_undefined() -> None:
    metrics = compute_metrics(Counts(0, 0, 3))

    assert metrics["precision"] is None
    assert metrics["precisionUndefined"] == NO_PREDICTIONS
    assert metrics["recall"] == 0
    assert metrics["f1"] == 0
    assert "recallUndefined" not in metrics
    assert "f1Undefined" not in metrics


def test_no_labels_leaves_recall_undefined() -> None:
    metrics = compute_metrics(Counts(0, 4, 0))

    assert metrics["recall"] is None
    assert metrics["recallUndefined"] == NO_LABELS
    assert metrics["precision"] == 0
    assert metrics["f1"] == 0


def test_nothing_at_all_leaves_every_ratio_undefined() -> None:
    metrics = compute_metrics(Counts())

    assert metrics["precision"] is None
    assert metrics["recall"] is None
    assert metrics["f1"] is None
    assert metrics["precisionUndefined"] == NO_PREDICTIONS
    assert metrics["recallUndefined"] == NO_LABELS
    assert metrics["f1Undefined"] == NO_POSITIVES


def test_clip_counts_of_a_successful_clip() -> None:
    counts = clip_counts(labels=5, true_positives=3, false_positives=2, failed=False)

    assert counts == Counts(3, 2, 2)


def test_failed_clip_counts_every_label_as_a_miss() -> None:
    counts = clip_counts(labels=5, true_positives=3, false_positives=2, failed=True)

    assert counts == Counts(0, 0, 5)


def test_failed_clip_without_labels_contributes_nothing() -> None:
    assert clip_counts(labels=0, true_positives=0, false_positives=0, failed=True) == Counts()


def test_counts_are_summed_before_ratios_are_formed() -> None:
    first = Counts(1, 0, 0)
    second = Counts(0, 9, 9)
    total = sum_counts([first, second])

    assert total == Counts(1, 9, 9)
    metrics = compute_metrics(total)
    assert metrics["precision"] == pytest.approx(0.1)
    # The mean of the per-clip precisions (1.0 and 0.0) would be 0.5.
    assert metrics["precision"] != pytest.approx(0.5)


def test_sum_of_nothing_is_zero() -> None:
    assert sum_counts([]) == Counts()


def test_failed_clip_lowers_recall_of_the_profile() -> None:
    ok = clip_counts(labels=4, true_positives=4, false_positives=0, failed=False)
    failed = clip_counts(labels=4, true_positives=0, false_positives=0, failed=True)

    metrics = compute_metrics(sum_counts([ok, failed]))

    assert metrics["recall"] == pytest.approx(0.5)
    assert metrics["precision"] == 1
    assert metrics["falseNegatives"] == 4


def test_profiles_and_label_kinds_are_reported_separately_without_aggregate() -> None:
    result = profile_metrics(
        [
            ("profile-b", "shot", Counts(1, 1, 0)),
            ("profile-a", "shot", Counts(2, 0, 2)),
            ("profile-a", "shot", Counts(1, 0, 0)),
            ("profile-a", "scene", Counts(0, 0, 3)),
        ]
    )

    assert list(result) == ["profile-a", "profile-b"]
    assert list(result["profile-a"]) == ["scene", "shot"]
    assert result["profile-a"]["shot"]["truePositives"] == 3
    assert result["profile-a"]["shot"]["falseNegatives"] == 2
    assert result["profile-a"]["scene"]["recall"] == 0
    assert result["profile-b"]["shot"]["precision"] == pytest.approx(0.5)
    # Nothing of profile-b leaks into profile-a and no cross-profile entry exists.
    assert set(result) == {"profile-a", "profile-b"}
    assert set(result["profile-b"]) == {"shot"}


def test_no_items_give_no_profiles() -> None:
    assert profile_metrics([]) == {}
