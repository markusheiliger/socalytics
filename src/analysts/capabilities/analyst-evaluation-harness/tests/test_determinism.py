from __future__ import annotations

import json
import shlex
import sys
from collections.abc import Mapping
from pathlib import Path
from typing import Any

from socalytics_analyst import Fact

from analyst_evaluation_harness.__main__ import main
from analyst_evaluation_harness.determinism import FACT_COUNT, canonical_facts, compare_runs
from analyst_evaluation_harness.evaluate import EvaluationResult, Subject, evaluate
from analyst_evaluation_harness.report import report_bytes, verify_report
from support.builders import KIND, build_config, fact, write_corpus


def _fact(
    start_ms: int, end_ms: int | None = None, *, kind: str = "kind-a", confidence: float = 1.0, **payload: Any
) -> Fact:
    return Fact(
        kind=kind,
        start_ms=start_ms,
        end_ms=end_ms if end_ms is not None else start_ms + 1000,
        confidence=confidence,
        payload=payload,
    )


def test_identical_runs_have_no_difference_whatever_the_fact_order() -> None:
    first = [_fact(1000, score=1), _fact(5000, score=2), _fact(2000, kind="kind-b")]
    second = [_fact(2000, kind="kind-b"), _fact(5000, score=2), _fact(1000, score=1)]

    assert compare_runs("clip", [first, second, list(first)]) == []
    assert canonical_facts(first) == canonical_facts(second)


def test_without_tolerance_any_difference_is_named() -> None:
    first = [_fact(1000, confidence=0.9, score=1.0)]
    second = [_fact(1001, confidence=0.9000001, score=1.0000001)]

    differences = compare_runs("clip", [first, second])

    assert differences == [
        {"clipId": "clip", "run": 2, "factIndex": 0, "field": "startMs"},
        {"clipId": "clip", "run": 2, "factIndex": 0, "field": "endMs"},
        {"clipId": "clip", "run": 2, "factIndex": 0, "field": "confidence"},
        {"clipId": "clip", "run": 2, "factIndex": 0, "field": "payload.score"},
    ]


def test_tolerances_apply_per_kind_and_only_to_numbers() -> None:
    tolerance = {"kind-a": {"positionMs": 5, "numeric": 0.01}}
    first = [_fact(1000, confidence=0.9, score=1.0, label="x"), _fact(3000, kind="kind-b", score=1.0)]
    within = [_fact(1004, 2005, confidence=0.905, score=1.005, label="x"), _fact(3000, kind="kind-b", score=1.0)]
    beyond = [_fact(1006, 2000, confidence=0.9, score=1.0, label="x"), _fact(3000, kind="kind-b", score=1.0)]
    non_numeric = [_fact(1000, confidence=0.9, score=1.0, label="y"), _fact(3000, kind="kind-b", score=1.0)]
    other_kind = [_fact(1000, confidence=0.9, score=1.0, label="x"), _fact(3001, 4000, kind="kind-b", score=1.0)]

    assert compare_runs("clip", [first, within], tolerance) == []
    assert compare_runs("clip", [first, beyond], tolerance) == [
        {"clipId": "clip", "run": 2, "factIndex": 0, "field": "startMs"}
    ]
    assert compare_runs("clip", [first, non_numeric], tolerance) == [
        {"clipId": "clip", "run": 2, "factIndex": 0, "field": "payload.label"}
    ]
    # kind-b declares no tolerance: its 1 ms shift is a difference
    assert compare_runs("clip", [first, other_kind], tolerance) == [
        {"clipId": "clip", "run": 2, "factIndex": 1, "field": "startMs"}
    ]


def test_fact_counts_and_kinds_must_be_equal_even_with_a_tolerance() -> None:
    tolerance = {"kind-a": {"positionMs": 1000, "numeric": 100.0}}
    first = [_fact(1000), _fact(2000)]

    assert compare_runs("clip-x", [first, [_fact(1000)]], tolerance) == [
        {"clipId": "clip-x", "run": 2, "category": FACT_COUNT, "field": "kind-a"}
    ]
    assert compare_runs("clip-x", [first, [_fact(1000), _fact(2000, kind="kind-b")]], tolerance) == [
        {"clipId": "clip-x", "run": 2, "category": FACT_COUNT, "field": "kind-a"},
        {"clipId": "clip-x", "run": 2, "category": FACT_COUNT, "field": "kind-b"},
    ]


def test_each_later_run_is_compared_with_the_first() -> None:
    first = [_fact(1000)]

    differences = compare_runs("clip", [first, [_fact(1000)], [_fact(1001)]])

    assert [(d["run"], d["field"]) for d in differences] == [(3, "startMs"), (3, "endMs")]


# A capability that answers per clip (found in the staged media bytes) and counts its executions per clip.
# Scenario per clip: `runs` (facts per execution, the last one repeating) and `failOnRun` (1-based execution number).
PER_CLIP_SCRIPT = '''\
import json
import sys
from pathlib import Path

from socalytics_analyst import Fact, InputError, run

mount = Path(sys.argv[1])
scenario_path = Path(sys.argv[2])
scenario = json.loads(scenario_path.read_text(encoding="utf-8"))
media = (mount / "media" / "segment.mp4").read_bytes()
clip_id, entry = next(((key, value) for key, value in scenario.items() if key.encode() in media), ("", {}))

counter = scenario_path.parent / (clip_id + ".count")
execution = (int(counter.read_text()) if counter.exists() else 0) + 1
counter.write_text(str(execution))
runs = entry.get("runs", [[]])
facts = runs[min(execution, len(runs)) - 1]


def analyse(context):
    if entry.get("failOnRun") == execution:
        raise InputError("input-missing", "scripted failure.")
    for item in facts:
        context.result.add_fact(
            Fact(
                kind=item["kind"],
                start_ms=item["startMs"],
                end_ms=item["endMs"],
                confidence=item["confidence"],
                payload=item["payload"],
            )
        )
    if facts:
        context.result.set_confidence(1.0)


run(analyse, output_type="foundational-facts", root=mount)
'''

STABLE: dict[str, Any] = {
    "fixed-a": {"runs": [[fact(1000)]]},
    "fixed-b": {"runs": [[fact(1000)]]},
    "broadcast-a": {"runs": [[fact(11000)]]},
    "broadcast-b": {"runs": [[fact(21000)]]},
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


def _executions(tmp_path: Path, clip_id: str) -> int:
    counter = tmp_path / "subject" / f"{clip_id}.count"
    return int(counter.read_text()) if counter.exists() else 0


def _write_config(tmp_path: Path, **options: Any) -> Path:
    config_path = tmp_path / "config.json"
    config_path.write_text(json.dumps(build_config(**options)), encoding="utf-8")
    return config_path


def _run(tmp_path: Path, scenario: Mapping[str, Any], **options: Any) -> EvaluationResult:
    corpus = write_corpus(tmp_path / "corpus")
    config_path = _write_config(tmp_path, **options)
    return evaluate(corpus.manifest_path, config_path, tmp_path / "output", _subject(tmp_path / "subject", scenario))


def _clip(result: EvaluationResult, clip_id: str) -> dict[str, Any]:
    return next(item for item in result.report["clips"] if item["clipId"] == clip_id)


def test_a_stable_capability_passes_the_determinism_gate(tmp_path: Path) -> None:
    result = _run(tmp_path, STABLE)

    assert verify_report(result.report) == []
    assert result.report["determinism"]["differences"] == []
    assert result.report["determinism"]["status"] == "passed"
    assert result.report["gates"]["determinism"] == "passed"
    assert all(_executions(tmp_path, clip_id) == 2 for clip_id in STABLE)


def test_a_varying_capability_fails_for_exactly_that_clip(tmp_path: Path) -> None:
    scenario = {**STABLE, "broadcast-a": {"runs": [[fact(11000)], [fact(11000, confidence=0.5)]]}}

    result = _run(tmp_path, scenario)

    assert not result.any_failed
    assert result.report["gates"]["determinism"] == "failed"
    differences = result.report["determinism"]["differences"]
    assert differences == [{"clipId": "broadcast-a", "run": 2, "factIndex": 0, "field": "confidence"}]
    assert _clip(result, "broadcast-a")["subject"]["execution"]["status"] == "succeeded"


def test_a_tolerance_accepts_the_variation_it_declares(tmp_path: Path) -> None:
    scenario = {**STABLE, "fixed-a": {"runs": [[fact(1000)], [fact(1003)]]}}

    result = _run(tmp_path, scenario, determinismTolerance={KIND: {"positionMs": 5}})

    assert result.report["determinism"]["differences"] == []
    assert result.report["gates"]["determinism"] == "passed"


def test_a_failed_repetition_keeps_the_clip_evaluated_and_exits_zero(
    tmp_path: Path, corpus_factory, capsys
) -> None:
    corpus = corpus_factory()
    config_path = _write_config(tmp_path)
    subject = _subject(tmp_path / "subject", {**STABLE, "fixed-b": {"runs": [[fact(1000)]], "failOnRun": 2}})
    output = tmp_path / "out"

    code = main(
        [
            "evaluate",
            "--corpus", str(corpus.manifest_path),
            "--config", str(config_path),
            "--output", str(output),
            "--subject", subject.reference,
            "--capability-id", "test-capability",
            "--capability-version", "1.0.0",
            "--output-type", "foundational-facts",
        ]  # fmt: skip
    )

    report = json.loads((output / "report.json").read_text(encoding="utf-8"))
    clip = next(item for item in report["clips"] if item["clipId"] == "fixed-b")
    assert code == 0
    assert "determinism: failed" in capsys.readouterr().out
    assert clip["subject"]["execution"]["status"] == "succeeded"
    assert len(clip["subject"]["pairings"][0]["pairs"]) == 1
    (difference,) = report["determinism"]["differences"]
    assert difference["clipId"] == "fixed-b"
    assert difference["run"] == 2
    assert difference["category"] == "input-missing"
    assert report["gates"]["determinism"] == "failed"


def test_a_clip_whose_first_run_failed_is_not_repeated(tmp_path: Path) -> None:
    scenario = {**STABLE, "fixed-a": {"runs": [[fact(1000)]], "failOnRun": 1}}

    result = _run(tmp_path, scenario)

    assert result.any_failed
    assert _executions(tmp_path, "fixed-a") == 1
    assert _executions(tmp_path, "fixed-b") == 2
    assert _clip(result, "fixed-a")["subject"]["execution"]["status"] == "failed"
    assert result.report["determinism"]["differences"] == []
    assert result.report["gates"]["determinism"] == "not-evidenced"
    assert result.report["profiles"]["fixed"]["comparisonGate"] == "not-evidenced"
    assert verify_report(result.report) == []
    assert report_bytes(result.report) == result.report_path.read_bytes()
