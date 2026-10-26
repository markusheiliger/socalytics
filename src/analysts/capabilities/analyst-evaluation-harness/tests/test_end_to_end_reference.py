"""End-to-end proof: the harness evaluates `reference-media-probe` on the generated corpus (FR-017, SC-001)."""

from __future__ import annotations

import json
import shlex
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

import pytest

from analyst_evaluation_harness.__main__ import main

HARNESS_ROOT = Path(__file__).resolve().parents[1]
LABEL_KIND = "frame-second-probe"
SC_001_BOUND_SECONDS = 600
PROFILES = ("fixed", "broadcast")
# (true positives, false positives, false negatives); the fixed profile's clip without labels makes its 6 facts
# false positives by construction.
EXPECTED_COUNTS = {"fixed": (16, 6, 0), "broadcast": (22, 0, 0)}


@pytest.fixture(scope="module")
def corpus(tmp_path_factory: pytest.TempPathFactory) -> Path:
    folder = tmp_path_factory.mktemp("corpus") / "corpus"
    subprocess.run(
        [sys.executable, str(HARNESS_ROOT / "scripts" / "generate_corpus.py"), "--output", str(folder)],
        check=True,
        capture_output=True,
    )
    return folder


def _subject() -> str:
    entry = HARNESS_ROOT / "scripts" / "local_entry.py"
    return " ".join(
        (
            shlex.quote(sys.executable),
            shlex.quote(str(entry)),
            "reference_media_probe.__main__:analyse",
            "--output-type foundational-facts",
            "--root {mount}",
        )
    )


def _configuration(path: Path, rule: str) -> Path:
    document = {
        "configVersion": "1.0.0",
        "repetitions": 2,
        "timeLimitSeconds": 120,
        "labelKinds": {LABEL_KIND: {"primaryMetric": "f1", "matchToleranceMs": 0}},
        "comparator": {"kind": "builtin", "rule": rule},
    }
    path.write_text(json.dumps(document), encoding="utf-8")
    return path


def _evaluate(corpus: Path, folder: Path, rule: str, output_name: str) -> tuple[int, float, Path]:
    folder.mkdir(parents=True, exist_ok=True)
    output = folder / output_name
    started = time.monotonic()
    code = main(
        [
            "evaluate",
            "--corpus", str(corpus),
            "--config", str(_configuration(folder / f"{output_name}.json", rule)),
            "--output", str(output),
            "--subject", _subject(),
            "--capability-id", "reference-media-probe",
            "--capability-version", "1.0.0",
            "--output-type", "foundational-facts",
        ]
    )  # fmt: skip
    return code, time.monotonic() - started, output / "report.json"


def _load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def nothing(corpus: Path, tmp_path_factory: pytest.TempPathFactory) -> tuple[int, float, dict[str, Any]]:
    code, seconds, path = _evaluate(corpus, tmp_path_factory.mktemp("nothing"), "predict-nothing", "first")
    return code, seconds, _load(path)


def test_reference_probe_beats_predict_nothing_with_per_profile_metrics(nothing) -> None:
    code, seconds, report = nothing

    assert code == 0
    assert seconds < SC_001_BOUND_SECONDS
    assert set(report["profiles"]) == set(PROFILES)
    for profile in PROFILES:
        result = report["profiles"][profile]
        assert result["status"] == "evidenced"
        metrics = result["subject"][LABEL_KIND]
        expected = EXPECTED_COUNTS[profile]
        assert (metrics["truePositives"], metrics["falsePositives"], metrics["falseNegatives"]) == expected
        assert metrics["recall"] == 1.0
        assert result["comparator"][LABEL_KIND]["f1"] == 0.0
        assert result["verdict"] == "beat"
        assert result["comparisonGate"] == "passed"
        assert report["gates"][f"comparison.{profile}"] == "passed"
    assert all(not clip.get("failed") for clip in report["clips"])


def test_determinism_passes_and_lineage_is_not_evidenced_for_a_subprocess(nothing) -> None:
    _, _, report = nothing

    assert report["determinism"]["status"] == "passed"
    assert report["determinism"]["differences"] == []
    assert report["gates"]["determinism"] == "passed"
    assert report["gates"]["lineage"] == "not-evidenced"
    assert report["subject"]["imageDigestStatus"] == "not evaluated as an image"


def test_report_verifies_and_a_repeat_has_the_same_identity(
    corpus: Path, nothing, tmp_path: Path
) -> None:
    _, _, report = nothing
    code, _, repeat_path = _evaluate(corpus, tmp_path, "predict-nothing", "second")
    assert code == 0

    assert _load(repeat_path)["identity"] == report["identity"]
    assert main(["verify-report", str(repeat_path)]) == 0


def test_predict_everything_is_evaluated_and_scores_nothing_at_zero_tolerance(corpus: Path, tmp_path: Path) -> None:
    code, _, path = _evaluate(corpus, tmp_path, "predict-everything", "everything")
    report = _load(path)

    assert code == 0
    assert report["comparator"] == {"kind": "builtin", "rule": "predict-everything"}
    for profile in PROFILES:
        comparator = report["profiles"][profile]["comparator"][LABEL_KIND]
        assert comparator["truePositives"] == 0
        assert comparator["falsePositives"] > 0
        assert comparator["f1"] == 0.0
        assert report["profiles"][profile]["verdict"] == "beat"
