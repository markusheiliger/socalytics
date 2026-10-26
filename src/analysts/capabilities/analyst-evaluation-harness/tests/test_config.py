from __future__ import annotations

import copy
import json
import stat
import sys
from pathlib import Path
from typing import Any

import pytest

from analyst_evaluation_harness.config import (
    ConfigRefusal,
    capability_reference_mode,
    load_config,
    parse_config,
)

DIGEST = "a" * 64
IMAGE = f"registry.example/analyst@sha256:{DIGEST}"


def minimal() -> dict[str, Any]:
    return {
        "configVersion": "1.0.0",
        "labelKinds": {"goal": {"primaryMetric": "f1"}},
    }


def problems_of(data: Any, corpus_label_kinds: Any = None) -> list[str]:
    with pytest.raises(ConfigRefusal) as refusal:
        parse_config(data, corpus_label_kinds)
    return refusal.value.problems


def test_defaults_are_applied() -> None:
    config = parse_config(minimal())

    assert config.repetitions == 3
    assert config.time_limit_seconds == 120
    assert config.label_kinds["goal"].primary_metric == "f1"
    assert config.label_kinds["goal"].match_tolerance_ms == 0
    assert config.determinism_tolerance is None
    assert config.comparator is None
    assert config.thresholds == []
    assert config.config_version == "1.0.0"


def test_explicit_values_are_kept() -> None:
    data = minimal()
    data.update(
        repetitions=5,
        timeLimitSeconds=60,
        determinismTolerance={"goal": {"positionMs": 40, "numeric": 0.5}},
        thresholds=[{"labelKind": "goal", "profile": "fixed", "metric": "recall", "minimum": 0.8}],
    )
    data["labelKinds"]["goal"]["matchToleranceMs"] = 250

    config = parse_config(data)

    assert config.repetitions == 5
    assert config.time_limit_seconds == 60
    assert config.label_kinds["goal"].match_tolerance_ms == 250
    assert config.determinism_tolerance == {"goal": {"positionMs": 40, "numeric": 0.5}}
    assert config.thresholds[0].metric == "recall"
    assert config.thresholds[0].minimum == 0.8


@pytest.mark.parametrize("value", [1, 10])
def test_repetitions_bounds_are_accepted(value: int) -> None:
    data = minimal()
    data["repetitions"] = value
    assert parse_config(data).repetitions == value


@pytest.mark.parametrize("value", [0, 11, -1, 2.5, "3"])
def test_repetitions_outside_bounds_are_refused(value: Any) -> None:
    data = minimal()
    data["repetitions"] = value
    assert any(p.startswith("repetitions") for p in problems_of(data))


@pytest.mark.parametrize("value", [1, 3600])
def test_time_limit_bounds_are_accepted(value: int) -> None:
    data = minimal()
    data["timeLimitSeconds"] = value
    assert parse_config(data).time_limit_seconds == value


@pytest.mark.parametrize("value", [0, 3601, -5])
def test_time_limit_outside_bounds_are_refused(value: int) -> None:
    data = minimal()
    data["timeLimitSeconds"] = value
    assert any(p.startswith("timeLimitSeconds") for p in problems_of(data))


def test_label_kind_without_primary_metric_is_refused() -> None:
    data = minimal()
    data["labelKinds"]["goal"] = {}
    assert any("goal" in p and "primaryMetric" in p for p in problems_of(data))


def test_corpus_label_kind_missing_from_configuration_is_refused() -> None:
    problems = problems_of(minimal(), ["goal", "corner"])

    assert problems == ["labelKinds.corner: label kind of the corpus has no primaryMetric"]


def test_corpus_label_kind_without_primary_metric_is_refused() -> None:
    data = minimal()
    data["labelKinds"]["corner"] = {"matchToleranceMs": 100}

    problems = problems_of(data, ["goal", "corner"])

    assert any(p.startswith("labelKinds.corner") and "primaryMetric" in p for p in problems)


def test_corpus_label_kinds_all_configured_are_accepted() -> None:
    data = minimal()
    data["labelKinds"]["corner"] = {"primaryMetric": "recall"}

    config = parse_config(data, ["goal", "corner"])

    assert set(config.label_kinds) == {"goal", "corner"}


def test_unknown_primary_metric_is_refused() -> None:
    data = minimal()
    data["labelKinds"]["goal"]["primaryMetric"] = "accuracy"
    assert problems_of(data)


def test_negative_tolerance_is_refused() -> None:
    data = minimal()
    data["labelKinds"]["goal"]["matchToleranceMs"] = -1
    assert any("matchToleranceMs" in p for p in problems_of(data))


def test_negative_determinism_tolerance_is_refused() -> None:
    for field in ("positionMs", "numeric"):
        data = minimal()
        data["determinismTolerance"] = {"goal": {field: -1}}
        assert any(field in p for p in problems_of(data))


@pytest.mark.parametrize("rule", ["predict-nothing", "predict-everything"])
def test_builtin_comparator_rules_are_accepted(rule: str) -> None:
    data = minimal()
    data["comparator"] = {"kind": "builtin", "rule": rule}

    comparator = parse_config(data).comparator

    assert comparator is not None
    assert (comparator.kind, comparator.rule) == ("builtin", rule)


def test_unknown_comparator_rule_is_refused() -> None:
    data = minimal()
    data["comparator"] = {"kind": "builtin", "rule": "predict-randomly"}

    problems = problems_of(data)

    assert any("comparator.rule" in p and "predict-randomly" in p for p in problems)


def test_missing_comparator_rule_is_refused() -> None:
    data = minimal()
    data["comparator"] = {"kind": "builtin"}
    assert any("comparator.rule" in p for p in problems_of(data))


def test_unknown_comparator_kind_is_refused() -> None:
    data = minimal()
    data["comparator"] = {"kind": "oracle"}
    assert any("comparator.kind" in p for p in problems_of(data))


def test_comparator_must_be_an_object() -> None:
    data = minimal()
    data["comparator"] = "predict-nothing"
    assert problems_of(data) == ["comparator: must be an object"]


def test_capability_comparator_with_image_digest_is_accepted() -> None:
    data = minimal()
    data["comparator"] = {
        "kind": "capability",
        "reference": IMAGE,
        "version": "1.2.3",
        "outputType": "foundational-facts",
    }

    comparator = parse_config(data).comparator

    assert comparator is not None
    assert comparator.reference == IMAGE
    assert comparator.version == "1.2.3"
    assert comparator.output_type == "foundational-facts"


def test_capability_comparator_missing_fields_are_all_reported() -> None:
    data = minimal()
    data["comparator"] = {"kind": "capability"}

    problems = problems_of(data)

    for name in ("reference", "version", "outputType"):
        assert f"comparator.{name}: is required" in problems


@pytest.mark.parametrize("reference", ["registry.example/analyst:latest", f"analyst@sha256:{'a' * 63}", "no-such-executable-xyz --mount {mount}"])
def test_capability_reference_that_is_neither_image_nor_command_is_refused(reference: str) -> None:
    data = minimal()
    data["comparator"] = {
        "kind": "capability",
        "reference": reference,
        "version": "1.0.0",
        "outputType": "soccer-concepts",
    }
    assert any("comparator.reference" in p for p in problems_of(data))


def test_capability_version_and_output_type_are_checked() -> None:
    data = minimal()
    data["comparator"] = {
        "kind": "capability",
        "reference": IMAGE,
        "version": "1.0",
        "outputType": "raw",
    }

    problems = problems_of(data)

    assert any(p.startswith("comparator.version") for p in problems)
    assert any(p.startswith("comparator.outputType") for p in problems)


def test_unexpected_comparator_property_is_refused() -> None:
    data = minimal()
    data["comparator"] = {"kind": "builtin", "rule": "predict-nothing", "extra": 1}
    assert "comparator.extra: unexpected property" in problems_of(data)


def test_all_problems_are_reported_at_once() -> None:
    data = minimal()
    data["repetitions"] = 0
    data["timeLimitSeconds"] = 0
    data["comparator"] = {"kind": "builtin", "rule": "nope"}

    problems = problems_of(data, ["goal", "corner"])

    assert len(problems) >= 4
    assert any(p.startswith("repetitions") for p in problems)
    assert any(p.startswith("timeLimitSeconds") for p in problems)
    assert any(p.startswith("comparator.rule") for p in problems)
    assert any(p.startswith("labelKinds.corner") for p in problems)


def test_unknown_top_level_property_and_wrong_version_are_refused() -> None:
    data = minimal()
    data["configVersion"] = "2.0.0"
    data["surprise"] = True
    assert len(problems_of(data)) >= 2


def test_configuration_must_be_an_object() -> None:
    assert problems_of([]) == ["configuration: must be a JSON object"]


def test_empty_label_kinds_are_refused() -> None:
    data = minimal()
    data["labelKinds"] = {}
    assert problems_of(data)


def test_threshold_with_unknown_profile_or_out_of_range_minimum_is_refused() -> None:
    data = minimal()
    data["thresholds"] = [
        {"labelKind": "goal", "profile": "studio", "metric": "f1", "minimum": 1.5},
    ]
    problems = problems_of(data)
    assert any("profile" in p for p in problems)
    assert any("minimum" in p for p in problems)


def test_parse_does_not_modify_the_input() -> None:
    data = minimal()
    before = copy.deepcopy(data)
    parse_config(data)
    assert data == before


def test_capability_reference_mode(tmp_path: Path) -> None:
    script = tmp_path / "run.sh"
    script.write_text("#!/bin/sh\n", encoding="utf-8")
    script.chmod(script.stat().st_mode | stat.S_IXUSR)
    plain = tmp_path / "plain.txt"
    plain.write_text("x", encoding="utf-8")

    assert capability_reference_mode(IMAGE) == "container"
    assert capability_reference_mode(f"{script} {{mount}}") == "subprocess"
    assert capability_reference_mode(f"{sys.executable} -m something {{mount}}") == "subprocess"
    assert capability_reference_mode(str(plain)) is None
    assert capability_reference_mode("") is None
    assert capability_reference_mode("'unterminated") is None


def test_load_config_reads_a_file(tmp_path: Path) -> None:
    path = tmp_path / "config.json"
    path.write_text(json.dumps(minimal()), encoding="utf-8")

    assert load_config(path, ["goal"]).label_kinds["goal"].primary_metric == "f1"


def test_load_config_refuses_missing_and_malformed_files(tmp_path: Path) -> None:
    with pytest.raises(ConfigRefusal) as missing:
        load_config(tmp_path / "absent.json")
    assert "cannot be read" in missing.value.problems[0]

    broken = tmp_path / "broken.json"
    broken.write_text("{", encoding="utf-8")
    with pytest.raises(ConfigRefusal) as malformed:
        load_config(broken)
    assert "not valid JSON" in malformed.value.problems[0]
