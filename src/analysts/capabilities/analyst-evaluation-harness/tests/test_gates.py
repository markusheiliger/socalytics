from __future__ import annotations

import pytest

from analyst_evaluation_harness import gates
from analyst_evaluation_harness.config import Threshold
from analyst_evaluation_harness.gates import (
    BEAT,
    DID_NOT_BEAT,
    EVALUATION_LOCAL,
    FAILED,
    NOT_EVIDENCED,
    PASSED,
    comparison_gate,
    evaluate_profile,
    label_kind_verdict,
    profile_coverage_gate,
    profile_verdict,
    threshold_verdicts,
)


def _kind(verdict: str) -> dict[str, str]:
    return {"verdict": verdict}


def test_higher_primary_metric_beats_and_reports_values_and_margin() -> None:
    result = label_kind_verdict("f1", {"f1": 0.75}, {"f1": 0.5})

    assert result["verdict"] == BEAT
    assert result["primaryMetric"] == "f1"
    assert result["subjectValue"] == 0.75
    assert result["comparatorValue"] == 0.5
    assert result["margin"] == pytest.approx(0.25)


def test_lower_primary_metric_did_not_beat_with_negative_margin() -> None:
    result = label_kind_verdict("f1", {"f1": 0.25}, {"f1": 0.5})

    assert result["verdict"] == DID_NOT_BEAT
    assert result["margin"] == pytest.approx(-0.25)


def test_a_tie_does_not_beat() -> None:
    result = label_kind_verdict("f1", {"f1": 0.5}, {"f1": 0.5})

    assert result["verdict"] == DID_NOT_BEAT
    assert result["margin"] == 0


@pytest.mark.parametrize(
    ("subject", "comparator"),
    [
        ({"f1": None}, {"f1": 0.0}),
        ({"f1": 1.0}, {"f1": None}),
        ({"f1": None}, {"f1": None}),
        ({}, {"f1": 0.5}),
        (None, {"f1": 0.5}),
    ],
)
def test_undefined_primary_metric_never_beats_or_is_beaten(subject, comparator) -> None:
    result = label_kind_verdict("f1", subject, comparator)

    assert result["verdict"] == NOT_EVIDENCED
    assert result["margin"] is None
    assert result["reason"] == gates.PRIMARY_UNDEFINED


def test_zero_is_a_defined_value() -> None:
    assert label_kind_verdict("f1", {"f1": 0.0}, {"f1": 0.0})["verdict"] == DID_NOT_BEAT
    assert label_kind_verdict("f1", {"f1": 0.1}, {"f1": 0.0})["verdict"] == BEAT


def test_missing_comparator_is_not_evidenced() -> None:
    result = label_kind_verdict("f1", {"f1": 1.0}, None)

    assert result["verdict"] == NOT_EVIDENCED
    assert result["reason"] == gates.NO_COMPARATOR
    assert result["comparatorValue"] is None


def test_incomplete_comparator_or_subject_is_not_evidenced_even_when_values_favor_the_capability() -> None:
    comparator = label_kind_verdict("f1", {"f1": 1.0}, {"f1": 0.1}, comparator_incomplete=True)
    subject = label_kind_verdict("f1", {"f1": 1.0}, {"f1": 0.1}, subject_incomplete=True)

    assert comparator["verdict"] == NOT_EVIDENCED
    assert comparator["reason"] == gates.COMPARATOR_INCOMPLETE
    assert comparator["margin"] is None
    assert subject["verdict"] == NOT_EVIDENCED
    assert subject["reason"] == gates.SUBJECT_INCOMPLETE
    assert subject["margin"] is None


def test_profile_verdict_beat_only_when_every_kind_beat() -> None:
    verdict = profile_verdict(
        {"a": _kind(BEAT), "b": _kind(BEAT)}, comparator_declared=True, any_clip_failed=False
    )

    assert verdict == BEAT


def test_profile_verdict_did_not_beat_when_any_kind_did_not() -> None:
    verdict = profile_verdict(
        {"a": _kind(BEAT), "b": _kind(DID_NOT_BEAT)}, comparator_declared=True, any_clip_failed=False
    )

    assert verdict == DID_NOT_BEAT


def test_profile_verdict_not_evidenced_when_any_kind_is() -> None:
    verdict = profile_verdict(
        {"a": _kind(BEAT), "b": _kind(NOT_EVIDENCED)}, comparator_declared=True, any_clip_failed=False
    )

    assert verdict == NOT_EVIDENCED


def test_one_did_not_beat_and_one_undefined_kind_gives_not_evidenced_profile() -> None:
    result = evaluate_profile(
        {"a": "f1", "b": "f1"},
        {"a": {"f1": 0.1}, "b": {"f1": None}},
        {"a": {"f1": 0.9}, "b": {"f1": 0.5}},
        comparator_declared=True,
    )

    assert result["labelKindVerdicts"]["a"]["verdict"] == DID_NOT_BEAT
    assert result["labelKindVerdicts"]["b"]["verdict"] == NOT_EVIDENCED
    assert result["verdict"] == NOT_EVIDENCED
    assert result["comparisonGate"] == NOT_EVIDENCED


def test_profile_verdict_not_evidenced_without_comparator_failed_clip_or_kinds() -> None:
    kinds = {"a": _kind(BEAT)}

    assert profile_verdict(kinds, comparator_declared=False, any_clip_failed=False) == NOT_EVIDENCED
    assert profile_verdict(kinds, comparator_declared=True, any_clip_failed=True) == NOT_EVIDENCED
    assert profile_verdict({}, comparator_declared=True, any_clip_failed=False) == NOT_EVIDENCED


def test_comparison_gate_mapping() -> None:
    assert comparison_gate(BEAT) == PASSED
    assert comparison_gate(DID_NOT_BEAT) == FAILED
    assert comparison_gate(NOT_EVIDENCED) == NOT_EVIDENCED


def test_evaluate_profile_beat_passes() -> None:
    result = evaluate_profile(
        {"a": "f1"}, {"a": {"f1": 0.8}}, {"a": {"f1": 0.2}}, comparator_declared=True
    )

    assert result["verdict"] == BEAT
    assert result["comparisonGate"] == PASSED
    assert result["labelKindVerdicts"]["a"]["margin"] == pytest.approx(0.6)


def test_evaluate_profile_did_not_beat_fails() -> None:
    result = evaluate_profile(
        {"a": "f1"}, {"a": {"f1": 0.2}}, {"a": {"f1": 0.8}}, comparator_declared=True
    )

    assert result["verdict"] == DID_NOT_BEAT
    assert result["comparisonGate"] == FAILED


def test_evaluate_profile_without_comparator_is_not_evidenced() -> None:
    result = evaluate_profile({"a": "f1"}, {"a": {"f1": 0.8}}, None, comparator_declared=False)

    assert result["verdict"] == NOT_EVIDENCED
    assert result["comparisonGate"] == NOT_EVIDENCED
    entry = result["labelKindVerdicts"]["a"]
    assert entry["reason"] == gates.NO_COMPARATOR
    assert entry["margin"] is None


def test_evaluate_profile_with_incomplete_comparator_is_not_evidenced() -> None:
    declared_without_metrics = evaluate_profile(
        {"a": "f1"}, {"a": {"f1": 0.8}}, None, comparator_declared=True
    )
    flagged = evaluate_profile(
        {"a": "f1"}, {"a": {"f1": 0.8}}, {"a": {"f1": 0.1}}, comparator_declared=True, comparator_incomplete=True
    )

    for result in (declared_without_metrics, flagged):
        assert result["verdict"] == NOT_EVIDENCED
        assert result["comparisonGate"] == NOT_EVIDENCED
        assert result["labelKindVerdicts"]["a"]["reason"] == gates.COMPARATOR_INCOMPLETE


def test_failed_clip_makes_profile_not_evidenced_even_when_metrics_favor_the_capability() -> None:
    result = evaluate_profile(
        {"a": "f1", "b": "f1"},
        {"a": {"f1": 0.9}, "b": {"f1": 0.9}},
        {"a": {"f1": 0.1}, "b": {"f1": 0.1}},
        comparator_declared=True,
        any_clip_failed=True,
    )

    assert result["verdict"] == NOT_EVIDENCED
    assert result["comparisonGate"] == NOT_EVIDENCED
    for entry in result["labelKindVerdicts"].values():
        assert entry["verdict"] == NOT_EVIDENCED
        assert entry["margin"] is None


def test_failed_clip_hides_a_did_not_beat_verdict() -> None:
    result = evaluate_profile(
        {"a": "f1"}, {"a": {"f1": 0.1}}, {"a": {"f1": 0.9}}, comparator_declared=True, any_clip_failed=True
    )

    assert result["labelKindVerdicts"]["a"]["verdict"] == NOT_EVIDENCED
    assert result["comparisonGate"] == NOT_EVIDENCED


def test_each_label_kind_uses_its_own_primary_metric() -> None:
    result = evaluate_profile(
        {"a": "f1", "b": "recall"},
        {"a": {"f1": 0.9, "recall": 0.1}, "b": {"f1": 0.1, "recall": 0.9}},
        {"a": {"f1": 0.5, "recall": 0.5}, "b": {"f1": 0.5, "recall": 0.5}},
        comparator_declared=True,
    )

    assert result["labelKindVerdicts"]["a"]["primaryMetric"] == "f1"
    assert result["labelKindVerdicts"]["b"]["primaryMetric"] == "recall"
    assert result["verdict"] == BEAT


def test_label_kind_missing_from_metrics_is_not_evidenced() -> None:
    result = evaluate_profile({"a": "f1"}, {}, {"a": {"f1": 0.5}}, comparator_declared=True)

    assert result["labelKindVerdicts"]["a"]["verdict"] == NOT_EVIDENCED
    assert result["verdict"] == NOT_EVIDENCED


def test_profile_coverage_gate() -> None:
    assert profile_coverage_gate(["fixed", "broadcast"]) == PASSED
    assert profile_coverage_gate(["fixed", "broadcast", "extra"]) == PASSED
    assert profile_coverage_gate(["fixed"]) == NOT_EVIDENCED
    assert profile_coverage_gate([]) == NOT_EVIDENCED
    assert profile_coverage_gate(["fixed"], required=["fixed"]) == PASSED


def test_no_thresholds_give_no_threshold_verdicts() -> None:
    assert threshold_verdicts([], {"fixed": {"a": {"f1": 0.9}}}) == []


def test_threshold_verdicts_are_labelled_evaluation_local() -> None:
    thresholds = [
        Threshold(label_kind="a", profile="fixed", metric="f1", minimum=0.5),
        Threshold(label_kind="a", profile="broadcast", metric="f1", minimum=0.5),
        Threshold(label_kind="a", profile="fixed", metric="recall", minimum=0.5),
    ]
    metrics = {
        "fixed": {"a": {"f1": 0.5, "recall": None}},
        "broadcast": {"a": {"f1": 0.25}},
    }

    verdicts = threshold_verdicts(thresholds, metrics)

    assert [v["scope"] for v in verdicts] == [EVALUATION_LOCAL] * 3
    assert EVALUATION_LOCAL == "evaluation-local"
    assert verdicts[0] == {
        "scope": "evaluation-local",
        "labelKind": "a",
        "profile": "fixed",
        "metric": "f1",
        "minimum": 0.5,
        "value": 0.5,
        "met": True,
    }
    assert verdicts[1]["met"] is False
    assert verdicts[2]["value"] is None
    assert verdicts[2]["met"] is None


def test_threshold_for_unknown_profile_or_kind_is_neither_met_nor_missed() -> None:
    verdicts = threshold_verdicts([Threshold("zzz", "nowhere", "f1", 0.1)], {})

    assert verdicts[0]["value"] is None
    assert verdicts[0]["met"] is None


def test_verdicts_carry_no_promotion_field() -> None:
    profile = evaluate_profile({"a": "f1"}, {"a": {"f1": 0.8}}, {"a": {"f1": 0.2}}, comparator_declared=True)
    verdicts = threshold_verdicts([Threshold("a", "fixed", "f1", 0.1)], {"fixed": {"a": {"f1": 0.8}}})

    assert not any("promot" in key.lower() for key in profile)
    assert not any("promot" in key.lower() for key in profile["labelKindVerdicts"]["a"])
    assert not any("promot" in key.lower() for verdict in verdicts for key in verdict)
