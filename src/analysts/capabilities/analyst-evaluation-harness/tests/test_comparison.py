from __future__ import annotations

import json
import re
from collections.abc import Mapping, Sequence
from pathlib import Path
from typing import Any

from analyst_evaluation_harness.__main__ import main
from analyst_evaluation_harness.evaluate import EvaluationResult, evaluate
from analyst_evaluation_harness.gates import (
    BEAT,
    DID_NOT_BEAT,
    FAILED,
    NO_COMPARATOR,
    NOT_EVIDENCED,
    PASSED,
    PRIMARY_UNDEFINED,
)
from analyst_evaluation_harness.report import verify_report
from support.builders import DEFAULT_CLIPS, KIND, build_config, build_manifest, fact, label, write_corpus
from test_evaluate import AGREEING, _scenario, _subject

KIND_B = "test-other"
EVERYTHING = {"kind": "builtin", "rule": "predict-everything"}
NOTHING = {"kind": "builtin", "rule": "predict-nothing"}
PROFILES = ("fixed", "broadcast")


def _evaluate(
    tmp_path: Path,
    scenario: Mapping[str, Any],
    comparator: Mapping[str, Any] | None,
    *,
    manifest: dict[str, Any] | None = None,
    label_kinds: Mapping[str, Mapping[str, Any]] | None = None,
) -> EvaluationResult:
    corpus = write_corpus(tmp_path / "corpus", manifest)
    options: dict[str, Any] = {} if comparator is None else {"comparator": dict(comparator)}
    config_path = tmp_path / "config.json"
    config_path.write_text(json.dumps(build_config(label_kinds, **options)), encoding="utf-8")
    subject = _subject(tmp_path / "subject", scenario)
    return evaluate(corpus.manifest_path, config_path, tmp_path / "output", subject)


def _capability_comparator(tmp_path: Path, scenario: Mapping[str, Any]) -> dict[str, Any]:
    reference = _subject(tmp_path / "comparator", scenario).reference
    return {"kind": "capability", "reference": reference, "version": "1.0.0", "outputType": "foundational-facts"}


def _every_second(clips: Sequence[tuple[str, str, float, int, int]] = DEFAULT_CLIPS) -> dict[str, Any]:
    return {
        clip_id: {"facts": [fact(second, second + 1000) for second in range(start, start + duration, 1000)]}
        for clip_id, _, _, duration, start in clips
    }


def _kind_verdict(result: EvaluationResult, profile: str, kind: str = KIND) -> dict[str, Any]:
    return result.report["profiles"][profile]["labelKindVerdicts"][kind]


def test_no_comparator_is_not_declared_and_not_evidenced(tmp_path: Path) -> None:
    result = _evaluate(tmp_path, _scenario(AGREEING), None)

    report = result.report
    assert verify_report(report) == []
    assert report["comparator"] == {"kind": "not-declared"}
    for profile in PROFILES:
        entry = report["profiles"][profile]
        assert "comparator" not in entry
        assert entry["verdict"] == NOT_EVIDENCED
        assert entry["comparisonGate"] == NOT_EVIDENCED
        assert report["gates"][f"comparison.{profile}"] == NOT_EVIDENCED
        verdict = _kind_verdict(result, profile)
        assert verdict["verdict"] == NOT_EVIDENCED
        assert verdict["reason"] == NO_COMPARATOR
        assert verdict["margin"] is None
    assert report["gates"]["profileCoverage"] == PASSED
    assert all(clip["comparator"]["execution"]["status"] == "not-run" for clip in report["clips"])


def test_verdicts_differ_by_profile(tmp_path: Path) -> None:
    scenario = _scenario(AGREEING)
    scenario["broadcast-a"] = {"facts": []}
    scenario["broadcast-b"] = {"facts": []}

    result = _evaluate(tmp_path, scenario, EVERYTHING)

    report = result.report
    assert verify_report(report) == []
    assert report["comparator"] == {"kind": "builtin", "rule": "predict-everything"}
    assert report["profiles"]["fixed"]["verdict"] == BEAT
    assert report["profiles"]["broadcast"]["verdict"] == DID_NOT_BEAT
    assert report["gates"]["comparison.fixed"] == PASSED
    assert report["gates"]["comparison.broadcast"] == FAILED
    fixed = _kind_verdict(result, "fixed")
    broadcast = _kind_verdict(result, "broadcast")
    assert fixed["margin"] > 0
    assert fixed["subjectValue"] == 1.0
    assert broadcast["margin"] < 0
    assert broadcast["subjectValue"] == 0.0
    assert broadcast["comparatorValue"] > 0


def test_a_failed_subject_clip_makes_that_profile_not_evidenced_although_metrics_favor_the_capability(
    tmp_path: Path,
) -> None:
    scenario = _scenario(AGREEING)
    scenario["fixed-a"] = {"fail": "input-missing"}

    result = _evaluate(tmp_path, scenario, EVERYTHING)

    report = result.report
    assert result.any_failed
    fixed = report["profiles"]["fixed"]
    assert fixed["subject"][KIND]["f1"] > fixed["comparator"][KIND]["f1"]
    assert fixed["verdict"] == NOT_EVIDENCED
    assert report["gates"]["comparison.fixed"] == NOT_EVIDENCED
    assert _kind_verdict(result, "fixed")["verdict"] == NOT_EVIDENCED
    assert report["profiles"]["broadcast"]["verdict"] == BEAT
    assert report["gates"]["comparison.broadcast"] == PASSED


def test_a_failed_comparator_clip_makes_that_profile_not_evidenced(tmp_path: Path) -> None:
    comparator_scenario = _every_second()
    comparator_scenario["fixed-a"] = {"fail": "input-missing"}
    comparator = _capability_comparator(tmp_path, comparator_scenario)

    result = _evaluate(tmp_path, _scenario(AGREEING), comparator)

    report = result.report
    assert not result.any_failed
    clips = {clip["clipId"]: clip for clip in report["clips"]}
    assert clips["fixed-a"]["comparator"]["execution"]["status"] == "failed"
    assert clips["fixed-a"]["subject"]["execution"]["status"] == "succeeded"
    assert report["profiles"]["fixed"]["verdict"] == NOT_EVIDENCED
    assert report["gates"]["comparison.fixed"] == NOT_EVIDENCED
    assert report["profiles"]["broadcast"]["verdict"] == BEAT
    assert report["gates"]["comparison.broadcast"] == PASSED
    assert verify_report(report) == []


def test_one_did_not_beat_kind_and_one_undefined_kind_is_not_evidenced(tmp_path: Path) -> None:
    clips = {
        clip_id: [label(KIND, position_ms=start + 1000), label(KIND_B, position_ms=start + 1000)]
        for clip_id, _, _, _, start in DEFAULT_CLIPS
    }
    manifest = build_manifest(labels=clips)
    label_kinds = {KIND: {"primaryMetric": "f1"}, KIND_B: {"primaryMetric": "precision"}}

    result = _evaluate(tmp_path, {}, EVERYTHING, manifest=manifest, label_kinds=label_kinds)

    assert verify_report(result.report) == []
    for profile in PROFILES:
        assert _kind_verdict(result, profile, KIND)["verdict"] == DID_NOT_BEAT
        undefined = _kind_verdict(result, profile, KIND_B)
        assert undefined["verdict"] == NOT_EVIDENCED
        assert undefined["subjectValue"] is None
        assert undefined["reason"] == PRIMARY_UNDEFINED
        assert result.report["profiles"][profile]["verdict"] == NOT_EVIDENCED
        assert result.report["gates"][f"comparison.{profile}"] == NOT_EVIDENCED


def test_an_incomplete_comparator_never_yields_a_verdict(tmp_path: Path) -> None:
    comparator_scenario = {clip_id: {"fail": "input-missing"} for clip_id in AGREEING}
    comparator = _capability_comparator(tmp_path, comparator_scenario)

    result = _evaluate(tmp_path, _scenario(AGREEING), comparator)

    for profile in PROFILES:
        assert result.report["profiles"][profile]["verdict"] == NOT_EVIDENCED
        assert _kind_verdict(result, profile)["verdict"] == NOT_EVIDENCED


def test_builtin_and_capability_comparators_are_scored_identically(tmp_path: Path) -> None:
    scenario = _scenario(AGREEING)
    scenario["broadcast-a"] = {"facts": []}

    cases = {
        "predict-everything": (EVERYTHING, _every_second()),
        "predict-nothing": (NOTHING, {clip_id: {"facts": []} for clip_id in AGREEING}),
    }
    for name, (builtin, equivalent) in cases.items():
        folder = tmp_path / name
        built_in = _evaluate(folder / "builtin", scenario, builtin)
        capability = _evaluate(folder / "capability", scenario, _capability_comparator(folder, equivalent))

        for profile in PROFILES:
            assert built_in.report["profiles"][profile] == capability.report["profiles"][profile], name
        for left, right in zip(built_in.report["clips"], capability.report["clips"], strict=True):
            assert left["comparator"] == right["comparator"], name
        assert built_in.report["gates"] == capability.report["gates"], name
        assert capability.report["comparator"]["kind"] == "capability"


def _gate_lines(output: str) -> list[str]:
    return [line for line in output.splitlines() if re.fullmatch(r"[\w.]+: (passed|failed|not-evidenced)", line)]


def _evaluate_cli(tmp_path: Path, capability, corpus, **options: Any) -> int:
    config = tmp_path / "config.json"
    config.write_text(json.dumps(build_config(**options)), encoding="utf-8")
    return main(
        [
            "evaluate",
            "--corpus", str(corpus.manifest_path),
            "--config", str(config),
            "--output", str(tmp_path / "out"),
            "--subject", capability.command,
            "--capability-id", "test-capability",
            "--capability-version", "1.0.0",
            "--output-type", "foundational-facts",
        ]  # fmt: skip
    )


def test_the_command_prints_one_line_per_gate_and_exits_zero_when_a_gate_failed(
    tmp_path, corpus_factory, capability_factory, capsys
) -> None:
    corpus = corpus_factory()

    code = _evaluate_cli(tmp_path, capability_factory(), corpus, comparator=EVERYTHING)

    report = json.loads((tmp_path / "out" / "report.json").read_text(encoding="utf-8"))
    lines = _gate_lines(capsys.readouterr().out)
    assert code == 0
    assert report["gates"]["comparison.fixed"] == FAILED
    assert report["gates"]["comparison.broadcast"] == FAILED
    assert sorted(lines) == sorted(f"{gate}: {status}" for gate, status in report["gates"].items())
    assert len(lines) == len(report["gates"])


def test_the_command_exits_zero_when_a_gate_is_not_evidenced(
    tmp_path, corpus_factory, capability_factory, capsys
) -> None:
    corpus = corpus_factory()

    code = _evaluate_cli(tmp_path, capability_factory(), corpus)

    report = json.loads((tmp_path / "out" / "report.json").read_text(encoding="utf-8"))
    lines = _gate_lines(capsys.readouterr().out)
    assert code == 0
    assert "comparison.fixed: not-evidenced" in lines
    assert "comparison.broadcast: not-evidenced" in lines
    assert sorted(lines) == sorted(f"{gate}: {status}" for gate, status in report["gates"].items())
