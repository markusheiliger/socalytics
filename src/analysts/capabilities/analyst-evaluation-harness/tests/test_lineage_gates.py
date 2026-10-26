from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from analyst_evaluation_harness.corpus import load_corpus
from analyst_evaluation_harness.evaluate import IMAGE_NOT_EVALUATED, EvaluationResult, evaluate
from analyst_evaluation_harness.gates import FAILED, NOT_EVIDENCED, PASSED, determinism_gate, lineage_gate
from analyst_evaluation_harness.report import build_report
from support.builders import build_config, write_corpus
from test_evaluate import AGREEING, _scenario, _subject

DIGEST = "sha256:" + "a" * 64
GATE_NAMES = {"determinism", "lineage", "profileCoverage", "comparison.fixed", "comparison.broadcast"}


def _evaluate(tmp_path: Path, **options: Any) -> EvaluationResult:
    corpus = write_corpus(tmp_path / "corpus")
    config_path = tmp_path / "config.json"
    config_path.write_text(json.dumps(build_config(**options)), encoding="utf-8")
    subject = _subject(tmp_path / "subject", _scenario(AGREEING))
    return evaluate(corpus.manifest_path, config_path, tmp_path / "output", subject)


def test_a_subprocess_run_has_no_image_digest_and_the_lineage_gate_is_not_evidenced(tmp_path: Path) -> None:
    report = _evaluate(tmp_path).report

    assert report["subject"]["imageDigest"] is None
    assert report["subject"]["imageDigestStatus"] == IMAGE_NOT_EVALUATED
    assert report["subject"]["mode"] == "subprocess"
    assert report["gates"]["lineage"] == NOT_EVIDENCED


@pytest.mark.parametrize("reference", [DIGEST, f"registry.example/analyst@{DIGEST}"])
def test_an_oci_image_digest_passes_the_lineage_gate(reference: str) -> None:
    assert lineage_gate(reference) == PASSED


@pytest.mark.parametrize(
    "reference",
    [None, "", "latest", "sha256:abc", "sha256:" + "A" * 64, "registry.example/analyst:1.0", "sha256:" + "a" * 65],
)
def test_a_missing_or_malformed_image_digest_is_not_evidenced(reference: str | None) -> None:
    assert lineage_gate(reference) == NOT_EVIDENCED


def test_the_report_lineage_gate_follows_the_subject_image_digest(tmp_path: Path) -> None:
    result = _evaluate(tmp_path)
    corpus = load_corpus(tmp_path / "corpus" / "manifest.json")
    subject = {
        **result.report["subject"],
        "mode": "container",
        "reference": f"registry.example/analyst@{DIGEST}",
        "imageDigest": DIGEST,
        "imageDigestStatus": "evaluated as an image",
    }

    report = build_report(
        corpus=corpus,
        subject=subject,
        configuration=result.report["configuration"],
        clips=[],
        run=result.report["run"],
    )

    assert report["gates"]["lineage"] == PASSED
    assert report["subject"]["imageDigest"] == DIGEST


def test_one_repetition_leaves_the_determinism_gate_not_evidenced(tmp_path: Path) -> None:
    report = _evaluate(tmp_path, repetitions=1).report

    assert report["determinism"]["repetitions"] == 1
    assert report["determinism"]["differences"] == []
    assert report["gates"]["determinism"] == NOT_EVIDENCED


def test_two_repetitions_without_differences_pass_the_determinism_gate(tmp_path: Path) -> None:
    report = _evaluate(tmp_path, repetitions=2).report

    assert report["gates"]["determinism"] == PASSED


@pytest.mark.parametrize(
    ("counts", "first_failed", "expected"),
    [
        ((), False, NOT_EVIDENCED),
        ((1, 1), False, NOT_EVIDENCED),
        ((2, 1), False, NOT_EVIDENCED),
        ((2, 2), True, NOT_EVIDENCED),
        ((2, 3), False, PASSED),
    ],
)
def test_determinism_needs_every_clip_executed_at_least_twice(
    counts: tuple[int, ...], first_failed: bool, expected: str
) -> None:
    assert determinism_gate(counts, [], any_first_run_failed=first_failed) == expected


def test_a_recorded_difference_fails_the_determinism_gate_even_with_one_execution() -> None:
    assert determinism_gate((1,), [{"clipId": "fixed-a"}]) == FAILED


def test_no_gate_is_passed_without_its_evidence(tmp_path: Path) -> None:
    report = _evaluate(tmp_path, repetitions=1).report

    assert set(report["gates"]) == GATE_NAMES
    assert report["gates"]["determinism"] == NOT_EVIDENCED
    assert report["gates"]["lineage"] == NOT_EVIDENCED
    assert report["gates"]["comparison.fixed"] == NOT_EVIDENCED
    assert report["gates"]["comparison.broadcast"] == NOT_EVIDENCED
    assert report["gates"]["profileCoverage"] == PASSED
    assert set(report["gates"].values()) <= {PASSED, FAILED, NOT_EVIDENCED}
    assert [name for name, status in report["gates"].items() if status == PASSED] == ["profileCoverage"]
