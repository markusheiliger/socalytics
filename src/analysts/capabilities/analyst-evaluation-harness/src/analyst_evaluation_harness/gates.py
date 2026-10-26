"""Verdicts and gate statuses (research R6; FR-007, FR-008, FR-014, FR-015).

This is the only module that decides a verdict or a gate status. A status is `passed` or `failed` only from recorded
results; everything the harness cannot evidence is `not-evidenced`. There is no promotion field.
"""

from __future__ import annotations

from collections.abc import Iterable, Mapping
from typing import Any

BEAT = "beat"
DID_NOT_BEAT = "did-not-beat"
NOT_EVIDENCED = "not-evidenced"
PASSED = "passed"
FAILED = "failed"
EVALUATION_LOCAL = "evaluation-local"

NO_COMPARATOR = "no comparator declared"
COMPARATOR_INCOMPLETE = "the comparator result is incomplete"
SUBJECT_INCOMPLETE = "the capability result is incomplete"
CLIP_FAILED = "a clip of the profile failed for the capability or the comparator"
NO_LABEL_KINDS = "no label kind is evaluated"
PRIMARY_UNDEFINED = "the primary metric is undefined for the capability or the comparator"


def _value(metrics: Mapping[str, Any] | None, metric: str) -> float | None:
    if metrics is None:
        return None
    value = metrics.get(metric)
    return None if value is None else float(value)


def label_kind_verdict(
    primary_metric: str,
    subject_metrics: Mapping[str, Any] | None,
    comparator_metrics: Mapping[str, Any] | None,
    *,
    subject_incomplete: bool = False,
    comparator_incomplete: bool = False,
) -> dict[str, Any]:
    """Verdict of one label kind in one profile, with both values and the margin (capability minus comparator)."""
    subject_value = _value(subject_metrics, primary_metric)
    comparator_value = _value(comparator_metrics, primary_metric)
    result: dict[str, Any] = {
        "verdict": NOT_EVIDENCED,
        "primaryMetric": primary_metric,
        "subjectValue": subject_value,
        "comparatorValue": comparator_value,
        "margin": None,
    }
    if comparator_metrics is None and not comparator_incomplete:
        result["reason"] = NO_COMPARATOR
    elif comparator_incomplete:
        result["reason"] = COMPARATOR_INCOMPLETE
    elif subject_incomplete:
        result["reason"] = SUBJECT_INCOMPLETE
    elif subject_value is None or comparator_value is None:
        result["reason"] = PRIMARY_UNDEFINED
    else:
        result["margin"] = subject_value - comparator_value
        result["verdict"] = BEAT if subject_value > comparator_value else DID_NOT_BEAT
    return result


def profile_verdict(
    kind_verdicts: Mapping[str, Mapping[str, Any]],
    *,
    comparator_declared: bool,
    any_clip_failed: bool,
) -> str:
    """`not-evidenced` if any kind is, any clip failed, or no comparator is declared; else `did-not-beat` if any kind
    is; else `beat`."""
    verdicts = [entry["verdict"] for entry in kind_verdicts.values()]
    if not comparator_declared or any_clip_failed or not verdicts or NOT_EVIDENCED in verdicts:
        return NOT_EVIDENCED
    return DID_NOT_BEAT if DID_NOT_BEAT in verdicts else BEAT


def comparison_gate(verdict: str) -> str:
    """Gate status `comparison.<profile>` from the profile verdict."""
    return {BEAT: PASSED, DID_NOT_BEAT: FAILED}.get(verdict, NOT_EVIDENCED)


def evaluate_profile(
    primary_metrics: Mapping[str, str],
    subject_metrics: Mapping[str, Mapping[str, Any]],
    comparator_metrics: Mapping[str, Mapping[str, Any]] | None,
    *,
    comparator_declared: bool,
    comparator_incomplete: bool = False,
    any_clip_failed: bool = False,
) -> dict[str, Any]:
    """Label-kind verdicts, profile verdict, and comparison gate of one profile.

    A failed clip of the profile makes every label-kind verdict `not-evidenced`, so no verdict is presented as
    evidence from a partial run.
    """
    incomplete = comparator_incomplete or (comparator_declared and comparator_metrics is None)
    kinds: dict[str, dict[str, Any]] = {}
    for kind, metric in sorted(primary_metrics.items()):
        entry = label_kind_verdict(
            metric,
            subject_metrics.get(kind),
            (comparator_metrics or {}).get(kind) if comparator_metrics is not None else None,
            subject_incomplete=any_clip_failed,
            comparator_incomplete=comparator_declared and incomplete,
        )
        if not comparator_declared:
            entry = {**entry, "verdict": NOT_EVIDENCED, "margin": None, "reason": NO_COMPARATOR}
        elif any_clip_failed and entry["verdict"] != NOT_EVIDENCED:
            entry = {**entry, "verdict": NOT_EVIDENCED, "margin": None, "reason": CLIP_FAILED}
        kinds[kind] = entry
    verdict = profile_verdict(kinds, comparator_declared=comparator_declared, any_clip_failed=any_clip_failed)
    return {"labelKindVerdicts": kinds, "verdict": verdict, "comparisonGate": comparison_gate(verdict)}


def profile_coverage_gate(profiles_present: Iterable[str], required: Iterable[str] = ("fixed", "broadcast")) -> str:
    """`passed` only when every required profile has clips in the corpus."""
    return PASSED if set(required) <= set(profiles_present) else NOT_EVIDENCED


def threshold_verdicts(
    thresholds: Iterable[Any],
    subject_metrics_by_profile: Mapping[str, Mapping[str, Mapping[str, Any]]],
) -> list[dict[str, Any]]:
    """Evaluation-local verdicts of the supplied thresholds (items with `label_kind`, `profile`, `metric`,
    `minimum`); empty when none are supplied. An undefined value is neither met nor missed (`met` is `None`)."""
    verdicts: list[dict[str, Any]] = []
    for threshold in thresholds:
        metrics = subject_metrics_by_profile.get(threshold.profile, {}).get(threshold.label_kind)
        value = _value(metrics, threshold.metric)
        verdicts.append(
            {
                "scope": EVALUATION_LOCAL,
                "labelKind": threshold.label_kind,
                "profile": threshold.profile,
                "metric": threshold.metric,
                "minimum": threshold.minimum,
                "value": value,
                "met": None if value is None else value >= threshold.minimum,
            }
        )
    return verdicts
