"""Evaluation configuration model, validation, and refusals."""

from __future__ import annotations

import json
import os
import re
import shlex
import shutil
from collections.abc import Iterable, Mapping
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

CONFIG_VERSION = "1.0.0"
DEFAULT_REPETITIONS = 3
DEFAULT_TIME_LIMIT_SECONDS = 120
DEFAULT_MATCH_TOLERANCE_MS = 0
BUILTIN_RULES = ("predict-nothing", "predict-everything")
PRIMARY_METRICS = ("precision", "recall", "f1")

SCHEMA_PATH = Path(__file__).resolve().parents[2] / "schemas" / "evaluation-config.schema.json"
IMAGE_REFERENCE = re.compile(r"^[^\s@]+@sha256:[0-9a-f]{64}$")


class ConfigRefusal(Exception):
    """A refused evaluation configuration; every problem is listed."""

    def __init__(self, problems: list[str]) -> None:
        self.problems = list(problems)
        super().__init__("; ".join(self.problems))


@dataclass(frozen=True)
class LabelKindConfig:
    primary_metric: str
    match_tolerance_ms: int = DEFAULT_MATCH_TOLERANCE_MS


@dataclass(frozen=True)
class Comparator:
    kind: str
    rule: str | None = None
    reference: str | None = None
    version: str | None = None
    output_type: str | None = None
    entry: str | None = None


@dataclass(frozen=True)
class Threshold:
    label_kind: str
    profile: str
    metric: str
    minimum: float


@dataclass(frozen=True)
class EvaluationConfig:
    label_kinds: dict[str, LabelKindConfig]
    repetitions: int = DEFAULT_REPETITIONS
    time_limit_seconds: int = DEFAULT_TIME_LIMIT_SECONDS
    determinism_tolerance: dict[str, dict[str, float]] | None = None
    comparator: Comparator | None = None
    thresholds: list[Threshold] = field(default_factory=list)
    config_version: str = CONFIG_VERSION


def _is_executable(token: str) -> bool:
    if os.sep in token or (os.altsep and os.altsep in token):
        return os.path.isfile(token) and os.access(token, os.X_OK)
    return shutil.which(token) is not None


def capability_reference_mode(reference: str) -> str | None:
    """Return ``container`` or ``subprocess``, or ``None`` when the reference is neither."""
    if not isinstance(reference, str) or not reference.strip():
        return None
    if IMAGE_REFERENCE.match(reference):
        return "container"
    try:
        tokens = shlex.split(reference)
    except ValueError:
        return None
    if tokens and _is_executable(tokens[0]):
        return "subprocess"
    return None


def _path(parts: Iterable[Any]) -> str:
    text = "".join(f"[{p}]" if isinstance(p, int) else f".{p}" for p in parts)
    return text.lstrip(".") or "<root>"


def _load_schema() -> dict[str, Any]:
    return json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))


def _validate(
    data: Any,
    corpus_label_kinds: Iterable[str] | None,
) -> list[str]:
    problems: list[str] = []
    if not isinstance(data, Mapping):
        return ["configuration: must be a JSON object"]

    validator = Draft202012Validator(_load_schema())
    for error in sorted(validator.iter_errors(data), key=lambda e: _path(e.absolute_path)):
        # Comparator problems are reported by the dedicated checks below.
        if error.absolute_path and error.absolute_path[0] == "comparator":
            continue
        problems.append(f"{_path(error.absolute_path)}: {error.message}")

    comparator = data.get("comparator")
    if comparator is not None:
        problems.extend(_validate_comparator(comparator))

    label_kinds = data.get("labelKinds")
    if corpus_label_kinds is not None:
        declared = label_kinds if isinstance(label_kinds, Mapping) else {}
        for kind in sorted(set(corpus_label_kinds)):
            entry = declared.get(kind)
            if not isinstance(entry, Mapping) or "primaryMetric" not in entry:
                problems.append(
                    f"labelKinds.{kind}: label kind of the corpus has no primaryMetric"
                )
    return problems


def _validate_comparator(comparator: Any) -> list[str]:
    if not isinstance(comparator, Mapping):
        return ["comparator: must be an object"]
    kind = comparator.get("kind")
    if kind == "builtin":
        problems = []
        rule = comparator.get("rule")
        if rule not in BUILTIN_RULES:
            problems.append(
                f"comparator.rule: unknown comparator rule {rule!r}; "
                f"expected one of {', '.join(BUILTIN_RULES)}"
            )
        problems.extend(_unexpected(comparator, {"kind", "rule"}))
        return problems
    if kind == "capability":
        problems = []
        for name in ("reference", "version", "outputType"):
            if name not in comparator:
                problems.append(f"comparator.{name}: is required")
        reference = comparator.get("reference")
        if reference is not None and capability_reference_mode(reference) is None:
            problems.append(
                "comparator.reference: neither an image digest reference "
                "'<repository>@sha256:<64 hex>' nor a runnable local command"
            )
        version = comparator.get("version")
        if version is not None and not (
            isinstance(version, str) and re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", version)
        ):
            problems.append("comparator.version: must be a semantic version MAJOR.MINOR.PATCH")
        output_type = comparator.get("outputType")
        if output_type is not None and output_type not in ("foundational-facts", "soccer-concepts"):
            problems.append(
                "comparator.outputType: must be 'foundational-facts' or 'soccer-concepts'"
            )
        problems.extend(_unexpected(comparator, {"kind", "reference", "version", "outputType", "entry"}))
        return problems
    return [f"comparator.kind: unknown comparator kind {kind!r}; expected 'builtin' or 'capability'"]


def _unexpected(obj: Mapping[str, Any], allowed: set[str]) -> list[str]:
    return [f"comparator.{key}: unexpected property" for key in sorted(set(obj) - allowed)]


def parse_config(
    data: Any,
    corpus_label_kinds: Iterable[str] | None = None,
) -> EvaluationConfig:
    """Validate a configuration object and return the model with defaults applied."""
    problems = _validate(data, corpus_label_kinds)
    if problems:
        raise ConfigRefusal(problems)

    label_kinds = {
        kind: LabelKindConfig(
            primary_metric=entry["primaryMetric"],
            match_tolerance_ms=entry.get("matchToleranceMs", DEFAULT_MATCH_TOLERANCE_MS),
        )
        for kind, entry in data["labelKinds"].items()
    }
    comparator = None
    raw = data.get("comparator")
    if raw is not None:
        comparator = Comparator(
            kind=raw["kind"],
            rule=raw.get("rule"),
            reference=raw.get("reference"),
            version=raw.get("version"),
            output_type=raw.get("outputType"),
            entry=raw.get("entry"),
        )
    tolerance = data.get("determinismTolerance")
    return EvaluationConfig(
        label_kinds=label_kinds,
        repetitions=data.get("repetitions", DEFAULT_REPETITIONS),
        time_limit_seconds=data.get("timeLimitSeconds", DEFAULT_TIME_LIMIT_SECONDS),
        determinism_tolerance={k: dict(v) for k, v in tolerance.items()} if tolerance is not None else None,
        comparator=comparator,
        thresholds=[
            Threshold(t["labelKind"], t["profile"], t["metric"], t["minimum"])
            for t in data.get("thresholds", [])
        ],
        config_version=data["configVersion"],
    )


def load_config(
    path: str | Path,
    corpus_label_kinds: Iterable[str] | None = None,
) -> EvaluationConfig:
    """Read a configuration file; an unreadable or malformed file is a refusal."""
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError) as error:
        raise ConfigRefusal([f"configuration file {path}: cannot be read ({error})"]) from error
    except json.JSONDecodeError as error:
        raise ConfigRefusal([f"configuration file {path}: not valid JSON ({error})"]) from error
    return parse_config(data, corpus_label_kinds)
