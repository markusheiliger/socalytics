from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from analyst_evaluation_harness.__main__ import main
from analyst_evaluation_harness.canonical import canonical_bytes
from analyst_evaluation_harness.evaluate import Subject, evaluate
from analyst_evaluation_harness.report import IDENTITY_EXCLUDED, verify_report
from support.builders import build_config

OUTSIDE_IDENTITY = set(IDENTITY_EXCLUDED)


@pytest.fixture
def evaluation(tmp_path, corpus_factory, capability_factory):
    corpus = corpus_factory()
    capability = capability_factory()
    config_path = tmp_path / "config.json"
    config_path.write_text(json.dumps(build_config()), encoding="utf-8")
    subject = Subject(capability.command, "test-capability", "1.0.0", "foundational-facts")

    def run(name: str):
        return evaluate(corpus.manifest_path, config_path, tmp_path / name, subject)

    return run


def _outside_identity(report: dict[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in report.items() if key not in OUTSIDE_IDENTITY}


def test_two_evaluations_of_the_same_inputs_yield_the_same_identity(evaluation) -> None:
    first = evaluation("first")
    second = evaluation("second")

    assert first.report["identity"] == second.report["identity"]
    assert _outside_identity(first.report) == _outside_identity(second.report)
    assert canonical_bytes(_outside_identity(first.report)) == canonical_bytes(_outside_identity(second.report))
    assert verify_report(first.report) == []
    assert verify_report(second.report) == []


def test_a_different_configuration_yields_a_different_identity(tmp_path, corpus_factory, capability_factory) -> None:
    corpus = corpus_factory()
    subject = Subject(capability_factory().command, "test-capability", "1.0.0", "foundational-facts")
    identities = []
    for name, repetitions in (("one", 2), ("two", 3)):
        config_path = tmp_path / f"{name}.json"
        config_path.write_text(json.dumps(build_config(repetitions=repetitions)), encoding="utf-8")
        identities.append(evaluate(corpus.manifest_path, config_path, tmp_path / name, subject).report["identity"])

    assert identities[0] != identities[1]


def test_the_run_section_does_not_change_the_identity(evaluation) -> None:
    report = evaluation("first").report
    report["run"] = {**report["run"], "host": "another-host"}

    assert verify_report(report) == []


def test_changing_one_byte_of_the_report_makes_verify_report_fail(evaluation, capsys) -> None:
    result = evaluation("first")
    original = result.report_path.read_bytes()
    run_bytes = canonical_bytes(result.report["run"])
    run_start = original.index(run_bytes)
    run_span = range(run_start, run_start + len(run_bytes))
    assert main(["verify-report", str(result.report_path)]) == 0

    tested = 0
    for position in range(0, len(original) - 1, 5):
        if position in run_span:
            continue
        changed = bytearray(original)
        changed[position] ^= 0x01
        result.report_path.write_bytes(bytes(changed))
        capsys.readouterr()

        assert main(["verify-report", str(result.report_path)]) == 2, f"byte {position} went undetected"
        tested += 1

    assert tested > 50
    result.report_path.write_bytes(original)
    assert main(["verify-report", str(result.report_path)]) == 0


def test_a_changed_value_of_either_report_fails_the_verification(evaluation) -> None:
    for name in ("first", "second"):
        report = evaluation(name).report
        for mutate in (
            lambda r: r["corpus"].__setitem__("corpusVersion", "9.9.9"),
            lambda r: r["subject"].__setitem__("capabilityVersion", "9.9.9"),
            lambda r: r["determinism"].__setitem__("repetitions", 99),
            lambda r: r["clips"][0].__setitem__("clipId", "other-clip"),
            lambda r: r.__setitem__("identity", r["identity"][:-1] + ("0" if r["identity"][-1] != "0" else "1")),
        ):
            changed = json.loads(json.dumps(report))
            mutate(changed)

            assert verify_report(changed) != []
