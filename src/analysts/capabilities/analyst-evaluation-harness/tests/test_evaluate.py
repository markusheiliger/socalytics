from __future__ import annotations

import json
import shlex
import sys
from collections.abc import Mapping, Sequence
from pathlib import Path
from typing import Any

import pytest

from analyst_evaluation_harness.config import ConfigRefusal
from analyst_evaluation_harness.evaluate import EvaluationResult, Subject, evaluate
from analyst_evaluation_harness.report import verify_report
from support.builders import KIND, build_config, build_manifest, fact, label, write_corpus

# A capability that answers per clip: the scenario is keyed by clip id, found in the staged media bytes.
PER_CLIP_SCRIPT = '''\
import json
import sys
from pathlib import Path

from socalytics_analyst import Fact, InputError, run

mount = Path(sys.argv[1])
scenario = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
media = (mount / "media" / "segment.mp4").read_bytes()
entry = next((value for clip_id, value in scenario.items() if clip_id.encode() in media), {})


def analyse(context):
    if entry.get("fail"):
        raise InputError(entry["fail"], "scripted failure.")
    for item in entry.get("facts", []):
        context.result.add_fact(
            Fact(
                kind=item["kind"],
                start_ms=item["startMs"],
                end_ms=item["endMs"],
                confidence=item["confidence"],
                payload=item["payload"],
            )
        )
    if entry.get("facts"):
        context.result.set_confidence(1.0)


run(analyse, output_type="foundational-facts", root=mount)
'''

AGREEING: dict[str, list[dict[str, Any]]] = {
    "fixed-a": [fact(1000)],
    "fixed-b": [fact(1000)],
    "broadcast-a": [fact(11000)],
    "broadcast-b": [fact(21000)],
}


def _subject(folder: Path, scenario: Mapping[str, Any]) -> Subject:
    folder.mkdir(parents=True, exist_ok=True)
    script = folder / "per_clip.py"
    script.write_text(PER_CLIP_SCRIPT, encoding="utf-8")
    scenario_path = folder / "per_clip.scenario.json"
    scenario_path.write_text(json.dumps(scenario), encoding="utf-8")
    command = " ".join(
        (shlex.quote(sys.executable), shlex.quote(str(script)), "{mount}", shlex.quote(str(scenario_path)))
    )
    return Subject(command, "test-capability", "1.0.0", "foundational-facts")


def _scenario(facts: Mapping[str, Sequence[dict[str, Any]]]) -> dict[str, Any]:
    return {clip_id: {"facts": list(items)} for clip_id, items in facts.items()}


def _run(tmp_path: Path, scenario: Mapping[str, Any], manifest: dict[str, Any] | None = None) -> EvaluationResult:
    corpus = write_corpus(tmp_path / "corpus", manifest)
    config_path = tmp_path / "config.json"
    config_path.write_text(json.dumps(build_config()), encoding="utf-8")
    return evaluate(corpus.manifest_path, config_path, tmp_path / "output", _subject(tmp_path / "subject", scenario))


def _pairing(result: EvaluationResult, clip_id: str) -> dict[str, Any]:
    clip = next(item for item in result.report["clips"] if item["clipId"] == clip_id)
    (pairing,) = clip["subject"]["pairings"]
    assert pairing["labelKind"] == KIND
    return pairing


def test_agreeing_clips_report_no_discrepancy(tmp_path: Path) -> None:
    result = _run(tmp_path, _scenario(AGREEING))

    assert not result.any_failed
    assert verify_report(result.report) == []
    for clip_id in AGREEING:
        pairing = _pairing(result, clip_id)
        assert len(pairing["pairs"]) == 1
        assert pairing["missedLabels"] == []
        assert pairing["falsePositives"] == []
    for profile in ("fixed", "broadcast"):
        counts = result.report["profiles"][profile]["subject"][KIND]
        assert counts["f1"] == 1.0


def test_planted_discrepancies_are_reported_against_the_violated_label(tmp_path: Path) -> None:
    facts = {
        "fixed-a": [],
        "fixed-b": [fact(1000), fact(4000)],
        "broadcast-a": [fact(13000)],
        "broadcast-b": [fact(21000)],
    }

    result = _run(tmp_path, _scenario(facts))

    missing = _pairing(result, "fixed-a")
    assert missing["pairs"] == []
    assert missing["missedLabels"] == [{"labelIndex": 0, "reason": "no-fact"}]
    assert missing["falsePositives"] == []

    extra = _pairing(result, "fixed-b")
    assert len(extra["pairs"]) == 1
    assert extra["missedLabels"] == []
    assert extra["falsePositives"] == [1]

    misplaced = _pairing(result, "broadcast-a")
    assert misplaced["pairs"] == []
    assert misplaced["missedLabels"] == [{"labelIndex": 0, "reason": "no-fact"}]
    assert misplaced["falsePositives"] == [0]

    agreeing = _pairing(result, "broadcast-b")
    assert len(agreeing["pairs"]) == 1
    assert agreeing["missedLabels"] == []
    assert agreeing["falsePositives"] == []


def test_a_failed_clip_does_not_stop_the_others_and_counts_as_misses(tmp_path: Path) -> None:
    scenario = _scenario(AGREEING)
    scenario["fixed-a"] = {"fail": "input-missing"}

    result = _run(tmp_path, scenario)

    assert result.any_failed
    clips = {item["clipId"]: item for item in result.report["clips"]}
    assert clips["fixed-a"]["subject"]["execution"]["status"] == "failed"
    assert clips["fixed-a"]["subject"]["execution"]["failureCategory"]
    failed = _pairing(result, "fixed-a")
    assert failed["pairs"] == []
    assert failed["missedLabels"] == [{"labelIndex": 0, "reason": "clip-failed"}]
    for clip_id in ("fixed-b", "broadcast-a", "broadcast-b"):
        assert clips[clip_id]["subject"]["execution"]["status"] == "succeeded"
        assert len(_pairing(result, clip_id)["pairs"]) == 1
    fixed = result.report["profiles"]["fixed"]["subject"][KIND]
    assert fixed["recall"] == 0.5
    assert result.report_path.is_file()


def test_a_one_profile_corpus_reports_the_missing_profile_as_not_evidenced(tmp_path: Path) -> None:
    clips = (
        ("fixed-a", "fixed", 25.0, 5000, 0),
        ("fixed-b", "fixed", 25.0, 6000, 0),
    )
    manifest = build_manifest(clips, {"fixed-a": [label(position_ms=1000)], "fixed-b": [label(position_ms=1000)]})

    result = _run(tmp_path, _scenario({"fixed-a": [fact(1000)], "fixed-b": [fact(1000)]}), manifest)

    assert verify_report(result.report) == []
    assert result.report["profiles"]["fixed"]["status"] == "evidenced"
    assert result.report["profiles"]["broadcast"]["status"] == "not-evidenced"


def test_nothing_is_written_outside_the_output_directory(tmp_path: Path) -> None:
    corpus = write_corpus(tmp_path / "corpus")
    config_path = tmp_path / "config.json"
    config_path.write_text(json.dumps(build_config()), encoding="utf-8")
    subject = _subject(tmp_path / "subject", _scenario(AGREEING))
    output = tmp_path / "output"

    def snapshot() -> dict[Path, bytes]:
        return {
            path: path.read_bytes()
            for path in tmp_path.rglob("*")
            if path.is_file() and output not in path.parents and "__pycache__" not in path.parts
        }

    before = snapshot()
    result = evaluate(corpus.manifest_path, config_path, output, subject)

    assert snapshot() == before
    assert result.report_path.parent == output
    assert (output / "runs").is_dir()
    assert not list(tmp_path.glob("*.json.tmp"))


def test_an_output_directory_with_evidence_is_refused(tmp_path: Path) -> None:
    _run(tmp_path, _scenario(AGREEING))

    with pytest.raises(ConfigRefusal):
        _run(tmp_path, _scenario(AGREEING))
