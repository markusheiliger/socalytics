"""A new label kind with its matching rule is evaluated with the unchanged report structure (FR-018)."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from analyst_evaluation_harness import matching
from analyst_evaluation_harness.corpus import Label
from analyst_evaluation_harness.evaluate import EvaluationResult, evaluate
from analyst_evaluation_harness.report import verify_report
from socalytics_analyst.models.facts import Fact
from support.builders import DEFAULT_CLIPS, KIND, build_config, build_manifest, fact, label, write_corpus
from test_evaluate import AGREEING, _scenario, _subject

EXTENSION_KIND = "test-extension-event"
EXTENSION_RULE = "fact-end-position"


def fact_end_distance(label: Label, fact: Fact, tolerance_ms: int) -> int | None:
    """Test-only rule: a fact matches a label by where it ends, not where it starts."""
    if label.position_ms is None:
        return None
    distance = abs(fact.end_ms - label.position_ms)
    return distance if distance <= tolerance_ms else None


def _evaluate(tmp_path: Path, kind: str, facts: dict[str, list[dict[str, Any]]]) -> EvaluationResult:
    labels = {clip_id: [label(kind, match_start_ms + 1000)] for clip_id, _, _, _, match_start_ms in DEFAULT_CLIPS}
    corpus = write_corpus(tmp_path / "corpus", build_manifest(labels=labels))
    config_path = tmp_path / "config.json"
    config_path.write_text(
        json.dumps(build_config({kind: {"primaryMetric": "f1", "matchToleranceMs": 0}})), encoding="utf-8"
    )
    subject = _subject(tmp_path / "subject", _scenario(facts))
    return evaluate(corpus.manifest_path, config_path, tmp_path / "output", subject)


def test_a_registered_label_kind_is_evaluated_with_the_same_per_profile_structure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setitem(matching.MATCHING_RULES, EXTENSION_RULE, fact_end_distance)
    monkeypatch.setitem(matching.LABEL_KIND_RULES, EXTENSION_KIND, EXTENSION_RULE)

    baseline = _evaluate(tmp_path / "baseline", KIND, AGREEING)
    # Each fact ends exactly at its label and starts 500 ms earlier, so only the registered rule pairs it.
    ending_at_label = {
        clip_id: [fact(items[0]["startMs"] - 500, items[0]["startMs"], kind=EXTENSION_KIND)]
        for clip_id, items in AGREEING.items()
    }
    extension = _evaluate(tmp_path / "extension", EXTENSION_KIND, ending_at_label)

    assert not extension.any_failed
    assert verify_report(extension.report) == []
    assert verify_report(baseline.report) == []
    assert set(extension.report) == set(baseline.report)
    for profile in ("fixed", "broadcast"):
        extended = extension.report["profiles"][profile]
        reference = baseline.report["profiles"][profile]
        assert extended["status"] == "evidenced"
        assert set(extended) == set(reference)
        assert set(extended["subject"]) == {EXTENSION_KIND}
        assert set(extended["subject"][EXTENSION_KIND]) == set(reference["subject"][KIND])
        assert extended["subject"][EXTENSION_KIND]["f1"] == 1.0
    for clip in extension.report["clips"]:
        (pairing,) = clip["subject"]["pairings"]
        assert pairing["labelKind"] == EXTENSION_KIND
        assert len(pairing["pairs"]) == 1
        assert pairing["missedLabels"] == []
        assert pairing["falsePositives"] == []


def test_an_unregistered_label_kind_uses_the_default_rule(tmp_path: Path) -> None:
    ending_at_label = {
        clip_id: [fact(items[0]["startMs"] - 500, items[0]["startMs"], kind=EXTENSION_KIND)]
        for clip_id, items in AGREEING.items()
    }

    result = _evaluate(tmp_path, EXTENSION_KIND, ending_at_label)

    assert verify_report(result.report) == []
    assert result.report["profiles"]["fixed"]["subject"][EXTENSION_KIND]["truePositives"] == 0
