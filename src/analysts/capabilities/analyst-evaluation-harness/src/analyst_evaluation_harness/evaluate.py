"""Evaluate a subject capability on a corpus (FR-004, FR-005, FR-006, FR-010; data-model "Execution result")."""

from __future__ import annotations

import json
import platform
import re
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from .canonical import canonical_bytes
from .config import ConfigRefusal, EvaluationConfig, capability_reference_mode, parse_config
from .corpus import Clip, CorpusManifest, load_corpus
from .facts import EXECUTION_FAILED
from .matching import failed_clip_pairing, pair_clip
from .report import ClipEvaluation, build_report, report_bytes, verify_report
from .runners import ExecutionOutcome, ensure_image_present, run_container, run_subprocess
from .staging import OUTPUT_TYPES, StagingError, stage_mount

REPORT_FILE_NAME = "report.json"
RUNS_FOLDER_NAME = "runs"
OUTCOME_FILE_NAME = "outcome.json"
IMAGE_EVALUATED = "evaluated as an image"
IMAGE_NOT_EVALUATED = "not evaluated as an image"
_UNSAFE = re.compile(r"[^A-Za-z0-9._-]")


@dataclass(frozen=True)
class Subject:
    """The capability under evaluation."""

    reference: str
    capability_id: str
    capability_version: str
    output_type: str


@dataclass(frozen=True)
class EvaluationResult:
    """The written report and whether any clip failed for the subject."""

    report: dict[str, Any]
    report_path: Path
    any_failed: bool

    @property
    def identity(self) -> str:
        return self.report["identity"]


def _now() -> datetime:
    return datetime.now(UTC)


def _timestamp(moment: datetime) -> str:
    return moment.strftime("%Y-%m-%dT%H:%M:%SZ")


def _subject_record(subject: Subject, mode: str) -> dict[str, Any]:
    is_image = mode == "container"
    return {
        "capabilityId": subject.capability_id,
        "capabilityVersion": subject.capability_version,
        "mode": mode,
        "reference": subject.reference,
        "imageDigest": subject.reference.rpartition("@")[2] if is_image else None,
        "imageDigestStatus": IMAGE_EVALUATED if is_image else IMAGE_NOT_EVALUATED,
        "outputType": subject.output_type,
    }


def _load_config_data(path: Path, corpus_kinds: set[str]) -> tuple[dict[str, Any], EvaluationConfig]:
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError) as error:
        raise ConfigRefusal([f"configuration file {path}: cannot be read ({error})"]) from error
    except json.JSONDecodeError as error:
        raise ConfigRefusal([f"configuration file {path}: not valid JSON ({error})"]) from error
    return data, parse_config(data, corpus_kinds)


def _check_subject(subject: Subject) -> str:
    problems: list[str] = []
    mode = capability_reference_mode(subject.reference)
    if mode is None:
        problems.append("subject: neither an image digest reference '<repository>@sha256:<64 hex>' nor a runnable command.")
    if not subject.capability_id.strip():
        problems.append("subject: the capability id must not be empty.")
    if not subject.capability_version.strip():
        problems.append("subject: the capability version must not be empty.")
    if subject.output_type not in OUTPUT_TYPES:
        problems.append(f"subject: the output type must be one of {', '.join(OUTPUT_TYPES)}.")
    if problems:
        raise ConfigRefusal(problems)
    assert mode is not None
    if mode == "container":
        ensure_image_present(subject.reference)
    return mode


def _safe_name(index: int, clip_id: str) -> str:
    return f"{index:03d}-{_UNSAFE.sub('_', clip_id)}"[:120]


def _execute(subject: Subject, mode: str, clip: Clip, run_folder: Path, time_limit: int) -> ExecutionOutcome:
    try:
        mount = stage_mount(
            run_folder,
            "subject-run-1",
            clip_file=clip.path,
            match_start_ms=clip.match_start_ms,
            duration_ms=clip.duration_ms,
            capability_id=subject.capability_id,
            capability_version=subject.capability_version,
            output_type=subject.output_type,
        )
    except StagingError:
        return ExecutionOutcome(status="failed", failure_category=EXECUTION_FAILED, duration_ms=0)
    if mode == "container":
        return run_container(subject.reference, mount, run_folder, time_limit)
    return run_subprocess(subject.reference, mount, run_folder, time_limit)


def _write_outcome(run_folder: Path, clip: Clip, outcome: ExecutionOutcome) -> None:
    facts = outcome.facts
    document: dict[str, Any] = {
        "clipId": clip.clip_id,
        "run": 1,
        "status": outcome.status,
        "durationMs": outcome.duration_ms,
    }
    if outcome.failure_category is not None:
        document["failureCategory"] = outcome.failure_category
    if outcome.exit_code is not None:
        document["exitCode"] = outcome.exit_code
    if facts is not None and facts.reason is not None:
        document["reason"] = facts.reason
    (run_folder / OUTCOME_FILE_NAME).write_bytes(canonical_bytes(document) + b"\n")


def _evaluate_clip(
    subject: Subject,
    mode: str,
    clip: Clip,
    index: int,
    runs_folder: Path,
    config: EvaluationConfig,
) -> ClipEvaluation:
    run_folder = runs_folder / _safe_name(index, clip.clip_id)
    run_folder.mkdir()
    kinds = list(config.label_kinds)
    outcome = _execute(subject, mode, clip, run_folder, config.time_limit_seconds)
    _write_outcome(run_folder, clip, outcome)
    if outcome.status != "succeeded" or outcome.facts is None or outcome.facts.facts is None:
        return ClipEvaluation(
            clip_id=clip.clip_id,
            profile=clip.profile,
            status="failed",
            failure_category=outcome.failure_category or EXECUTION_FAILED,
            pairings=failed_clip_pairing(clip.labels, kinds),
        )
    facts = list(outcome.facts.facts)
    tolerances = {kind: entry.match_tolerance_ms for kind, entry in config.label_kinds.items()}
    return ClipEvaluation(
        clip_id=clip.clip_id,
        profile=clip.profile,
        status="succeeded",
        fact_count=len(facts),
        pairings=pair_clip(clip.labels, facts, tolerances, kinds),
    )


def _corpus_kinds(corpus: CorpusManifest) -> set[str]:
    return {label.label_kind for clip in corpus.clips for label in clip.labels}


def evaluate(
    corpus_directory: Path | str,
    config_path: Path | str,
    output_directory: Path | str,
    subject: Subject,
) -> EvaluationResult:
    """Validate the inputs, run the subject on every clip, and write the report and the per-run evidence.

    Raises `CorpusValidationError` or `ConfigRefusal` before any clip runs. A failed clip is recorded and never stops
    the others. Everything is written inside `output_directory`; the result says whether any clip failed.
    """
    corpus_path = Path(corpus_directory)
    corpus = load_corpus(corpus_path if corpus_path.is_file() else corpus_path / "corpus-manifest.json")
    configuration, config = _load_config_data(Path(config_path), _corpus_kinds(corpus))
    mode = _check_subject(subject)

    output = Path(output_directory)
    runs_folder = output / RUNS_FOLDER_NAME
    if runs_folder.exists():
        raise ConfigRefusal([f"the output directory {output} already holds evidence under {RUNS_FOLDER_NAME}/."])
    runs_folder.mkdir(parents=True)

    started = _now()
    clips = [
        _evaluate_clip(subject, mode, clip, index, runs_folder, config)
        for index, clip in enumerate(corpus.clips, start=1)
    ]
    finished = _now()

    report = build_report(
        corpus=corpus,
        subject=_subject_record(subject, mode),
        configuration=configuration,
        clips=clips,
        run={
            "startedAt": _timestamp(started),
            "finishedAt": _timestamp(finished),
            "durationSeconds": round((finished - started).total_seconds(), 3),
            "host": platform.node() or "unknown",
        },
    )
    problems = verify_report(report)
    if problems:
        raise RuntimeError("the assembled report is invalid: " + "; ".join(problems))
    report_path = output / REPORT_FILE_NAME
    report_path.write_bytes(report_bytes(report))
    return EvaluationResult(report, report_path, any(clip.failed for clip in clips))


__all__ = ["EvaluationResult", "Subject", "evaluate"]
