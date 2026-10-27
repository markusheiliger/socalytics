from __future__ import annotations

from typing import Any

from socalytics_analyst.models.facts import Fact

from analyst_evaluation_harness.corpus import Label
from analyst_evaluation_harness.matching import (
    CLIP_FAILED,
    FACT_POSITION,
    NO_FACT,
    PAYLOAD_MISMATCH,
    failed_clip_pairing,
    fact_position_distance,
    pair_clip,
    pair_kind,
    rule_for,
)

KIND = "test-event"


def at(position_ms: int, kind: str = KIND, payload: dict[str, Any] | None = None) -> Label:
    return Label(label_kind=kind, position_ms=position_ms, payload=payload)


def during(start_ms: int, end_ms: int, kind: str = KIND, payload: dict[str, Any] | None = None) -> Label:
    return Label(label_kind=kind, range_start_ms=start_ms, range_end_ms=end_ms, payload=payload)


def found(start_ms: int, kind: str = KIND, payload: dict[str, Any] | None = None) -> Fact:
    return Fact(kind=kind, start_ms=start_ms, end_ms=start_ms + 1000, confidence=1.0, payload=payload or {})


def pairs_of(pairing) -> list[tuple[int, int, int]]:
    return [(pair.label_index, pair.fact_index, pair.distance_ms) for pair in pairing.pairs]


def test_the_default_rule_is_the_fact_position_rule() -> None:
    assert rule_for(KIND) is fact_position_distance
    assert FACT_POSITION == "fact-position"


def test_exact_position_pairs_at_distance_zero() -> None:
    pairing = pair_kind(KIND, [at(1000)], [found(1000)])

    assert pairs_of(pairing) == [(0, 0, 0)]
    assert pairing.true_positives == 1
    assert pairing.missed_labels == () and pairing.false_positives == ()


def test_a_fact_off_by_one_millisecond_is_not_exact() -> None:
    pairing = pair_kind(KIND, [at(1000)], [found(1001)])

    assert pairing.pairs == ()
    assert [(missed.label_index, missed.reason) for missed in pairing.missed_labels] == [(0, NO_FACT)]
    assert pairing.false_positives == (0,)


def test_tolerance_accepts_up_to_and_including_its_value() -> None:
    inside = pair_kind(KIND, [at(1000)], [found(1100)], tolerance_ms=100)
    before = pair_kind(KIND, [at(1000)], [found(900)], tolerance_ms=100)
    outside = pair_kind(KIND, [at(1000)], [found(1101)], tolerance_ms=100)

    assert pairs_of(inside) == [(0, 0, 100)]
    assert pairs_of(before) == [(0, 0, 100)]
    assert outside.pairs == ()
    assert outside.false_positives == (0,)


def test_a_range_label_matches_anywhere_inside_at_distance_zero() -> None:
    labels = [during(1000, 2000)]

    assert pairs_of(pair_kind(KIND, labels, [found(1000)])) == [(0, 0, 0)]
    assert pairs_of(pair_kind(KIND, labels, [found(1500)])) == [(0, 0, 0)]
    assert pairs_of(pair_kind(KIND, labels, [found(2000)])) == [(0, 0, 0)]
    assert pair_kind(KIND, labels, [found(2001)]).pairs == ()


def test_a_range_label_is_widened_by_the_tolerance_on_both_ends() -> None:
    labels = [during(1000, 2000)]

    assert pairs_of(pair_kind(KIND, labels, [found(950)], tolerance_ms=50)) == [(0, 0, 50)]
    assert pairs_of(pair_kind(KIND, labels, [found(2050)], tolerance_ms=50)) == [(0, 0, 50)]
    assert pair_kind(KIND, labels, [found(949)], tolerance_ms=50).pairs == ()
    assert pair_kind(KIND, labels, [found(2051)], tolerance_ms=50).pairs == ()


def test_a_label_without_position_or_range_never_matches() -> None:
    assert fact_position_distance(Label(label_kind=KIND), found(0), 10_000) is None


def test_a_payload_expectation_is_a_subset_deep_equality() -> None:
    label = at(1000, payload={"team": "home", "detail": {"a": [1, 2]}})
    extra_keys = found(1000, payload={"team": "home", "detail": {"a": [1, 2]}, "other": True})

    pairing = pair_kind(KIND, [label], [extra_keys])

    assert pairs_of(pairing) == [(0, 0, 0)]


def test_a_payload_mismatch_is_a_miss_and_a_false_positive() -> None:
    wrong_value = pair_kind(KIND, [at(1000, payload={"team": "home"})], [found(1000, payload={"team": "away"})])
    missing_key = pair_kind(KIND, [at(1000, payload={"team": "home"})], [found(1000)])

    for pairing in (wrong_value, missing_key):
        assert pairing.pairs == ()
        assert [(missed.label_index, missed.reason) for missed in pairing.missed_labels] == [(0, PAYLOAD_MISMATCH)]
        assert pairing.false_positives == (0,)


def test_a_label_without_payload_ignores_the_fact_payload() -> None:
    assert pairs_of(pair_kind(KIND, [at(1000)], [found(1000, payload={"x": 1})])) == [(0, 0, 0)]


def test_a_fact_pairs_with_at_most_one_label() -> None:
    pairing = pair_kind(KIND, [at(1000), at(1000)], [found(1000)])

    assert pairs_of(pairing) == [(0, 0, 0)]
    assert [(missed.label_index, missed.reason) for missed in pairing.missed_labels] == [(1, NO_FACT)]
    assert pairing.false_positives == ()


def test_a_label_pairs_with_at_most_one_fact() -> None:
    pairing = pair_kind(KIND, [at(1000)], [found(1000), found(1000)])

    assert pairs_of(pairing) == [(0, 0, 0)]
    assert pairing.false_positives == (1,)


def test_ties_are_broken_by_label_order_then_fact_order() -> None:
    labels = [at(1000), at(1100)]
    facts = [found(1050), found(1050)]

    pairing = pair_kind(KIND, labels, facts, tolerance_ms=100)

    assert pairs_of(pairing) == [(0, 0, 50), (1, 1, 50)]


def test_pairing_is_greedy_by_increasing_distance() -> None:
    labels = [at(1000), at(1100)]
    facts = [found(1090)]

    pairing = pair_kind(KIND, labels, facts, tolerance_ms=200)

    assert pairs_of(pairing) == [(1, 0, 10)]
    assert [(missed.label_index, missed.reason) for missed in pairing.missed_labels] == [(0, NO_FACT)]


def test_pairs_are_sorted_by_label_index() -> None:
    pairing = pair_kind(KIND, [at(1000), at(2000)], [found(2000), found(1000)])

    assert pairs_of(pairing) == [(0, 1, 0), (1, 0, 0)]


def test_facts_of_unlabelled_kinds_are_reported_once_and_never_paired() -> None:
    labels = [at(1000, kind="b-kind"), at(2000, kind="a-kind")]
    facts = [found(1000, kind="b-kind"), found(5000, kind="other"), found(2000, kind="a-kind")]

    results = pair_clip(labels, facts)

    assert [result.label_kind for result in results] == ["a-kind", "b-kind"]
    assert results[0].unlabelled == (1,)
    assert results[1].unlabelled == ()
    assert [result.true_positives for result in results] == [1, 1]
    assert all(result.false_positives == () for result in results)


def test_an_empty_label_list_measures_false_positives_of_the_evaluated_kinds() -> None:
    facts = [found(1000), found(2000), found(3000, kind="other")]

    results = pair_clip([], facts, label_kinds=[KIND])

    assert len(results) == 1
    assert results[0].label_kind == KIND
    assert results[0].pairs == () and results[0].missed_labels == ()
    assert results[0].false_positives == (0, 1)
    assert results[0].unlabelled == (2,)


def test_an_empty_label_list_without_evaluated_kinds_has_no_pairings() -> None:
    assert pair_clip([], [found(1000)]) == []


def test_an_evaluated_kind_without_facts_misses_every_label() -> None:
    results = pair_clip([at(1000), at(2000)], [], label_kinds=[KIND])

    assert [(missed.label_index, missed.reason) for missed in results[0].missed_labels] == [
        (0, NO_FACT),
        (1, NO_FACT),
    ]
    assert results[0].false_positives == ()


def test_tolerances_apply_per_label_kind() -> None:
    labels = [at(1000, kind="a-kind"), at(1000, kind="b-kind")]
    facts = [found(1050, kind="a-kind"), found(1050, kind="b-kind")]

    results = pair_clip(labels, facts, tolerances_ms={"a-kind": 100})

    assert [result.true_positives for result in results] == [1, 0]


def test_a_failed_clip_misses_every_label_with_the_clip_failed_reason() -> None:
    labels = [at(1000), at(2000, kind="other"), at(3000)]

    results = failed_clip_pairing(labels)

    assert [result.label_kind for result in results] == ["other", KIND]
    assert [(missed.label_index, missed.reason) for missed in results[0].missed_labels] == [(1, CLIP_FAILED)]
    assert [(missed.label_index, missed.reason) for missed in results[1].missed_labels] == [
        (0, CLIP_FAILED),
        (2, CLIP_FAILED),
    ]
    assert all(result.pairs == () and result.false_positives == () for result in results)


def test_a_pairing_serializes_to_the_report_shape() -> None:
    pairing = pair_clip([at(1000), at(5000)], [found(1000), found(9000)], label_kinds=[KIND])[0]

    assert pairing.to_dict() == {
        "labelKind": KIND,
        "pairs": [{"labelIndex": 0, "factIndex": 0, "distanceMs": 0}],
        "missedLabels": [{"labelIndex": 1, "reason": NO_FACT}],
        "falsePositives": [1],
        "unlabelled": [],
    }
