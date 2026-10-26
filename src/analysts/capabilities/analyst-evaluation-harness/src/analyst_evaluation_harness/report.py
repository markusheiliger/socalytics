"""Evaluation report assembly, identity, and verification (FR-010; data-model "Evaluation report")."""

from __future__ import annotations

import json
import platform
import subprocess
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from importlib import metadata
from pathlib import Path
from types import SimpleNamespace
from typing import Any

from jsonschema import Draft202012Validator

from . import __version__
from .canonical import canonical_bytes, identity_of, round_floats
from .corpus import CorpusManifest
from .gates import determinism_gate, evaluate_profile, lineage_gate, profile_coverage_gate, threshold_verdicts
from .matching import Pairing
from .metrics import clip_counts, profile_metrics

REPORT_VERSION = "1.0.0"
SCHEMA_PATH = Path(__file__).resolve().parents[2] / "schemas" / "evaluation-report.schema.json"
PROFILES = ("fixed", "broadcast")
IDENTITY_EXCLUDED = ("identity", "run")

NOT_EVIDENCED = "not-evidenced"
EVIDENCED = "evidenced"
NOT_RUN = "not-run"
NOT_RECORDED = "not recorded"


@dataclass(frozen=True)
class ClipEvaluation:
    """The first-run outcome of the subject on one clip, with its pairing result per label kind."""

    clip_id: str
    profile: str
    status: str
    failure_category: str | None = None
    fact_count: int | None = None
    pairings: Sequence[Pairing] = field(default_factory=tuple)
    comparator: ClipEvaluation | None = None

    @property
    def failed(self) -> bool:
        return self.status != "succeeded"


def _execution(clip: ClipEvaluation) -> dict[str, Any]:
    execution: dict[str, Any] = {"status": clip.status}
    if clip.failure_category is not None:
        execution["failureCategory"] = clip.failure_category
    if clip.fact_count is not None:
        execution["factCount"] = clip.fact_count
    return execution


def _clip_result(clip: ClipEvaluation) -> dict[str, Any]:
    compared = clip.comparator
    return {
        "clipId": clip.clip_id,
        "profile": clip.profile,
        "subject": {
            "execution": _execution(clip),
            "pairings": [pairing.to_dict() for pairing in clip.pairings],
        },
        "comparator": (
            {"execution": _execution(compared), "pairings": [pairing.to_dict() for pairing in compared.pairings]}
            if compared is not None
            else {"execution": {"status": NOT_RUN}, "pairings": []}
        ),
    }


def _metrics_of(profile: str, results: Sequence[ClipEvaluation]) -> dict[str, dict[str, Any]]:
    items = [
        (
            profile,
            pairing.label_kind,
            clip_counts(
                labels=len(pairing.pairs) + len(pairing.missed_labels),
                true_positives=pairing.true_positives,
                false_positives=len(pairing.false_positives),
                failed=result.failed,
            ),
        )
        for result in results
        for pairing in result.pairings
    ]
    return profile_metrics(items).get(profile, {})


def _profile_result(
    profile: str,
    clips: Sequence[ClipEvaluation],
    corpus_clips: int,
    primary_metrics: Mapping[str, str],
    comparator_declared: bool,
) -> tuple[dict[str, Any], dict[str, dict[str, Any]]]:
    """The profile result and its subject metrics (unrounded, for the thresholds)."""
    in_profile = [clip for clip in clips if clip.profile == profile]
    subject = _metrics_of(profile, in_profile)
    compared = [clip.comparator for clip in in_profile if clip.comparator is not None]
    comparator_complete = comparator_declared and len(compared) == len(in_profile) and bool(in_profile)
    comparator = _metrics_of(profile, compared) if comparator_complete else None
    any_failed = any(clip.failed for clip in in_profile) or any(clip.failed for clip in compared)
    outcome = evaluate_profile(
        primary_metrics,
        subject,
        comparator,
        comparator_declared=comparator_declared,
        comparator_incomplete=comparator_declared and not comparator_complete,
        any_clip_failed=any_failed,
    )
    result: dict[str, Any] = {
        "status": EVIDENCED if corpus_clips > 0 else NOT_EVIDENCED,
        "subject": subject,
        **({"comparator": comparator} if comparator is not None else {}),
        "comparisonGate": outcome["comparisonGate"],
        "verdict": outcome["verdict"],
        "labelKindVerdicts": outcome["labelKindVerdicts"],
    }
    return result, subject


def _sdk_version() -> str:
    try:
        return metadata.version("socalytics-analyst-sdk")
    except metadata.PackageNotFoundError:
        return NOT_RECORDED


def _tool_version_line(tool: str) -> str:
    """The first line of `<tool> -version`, or `not recorded` when the tool is absent or fails."""
    try:
        completed = subprocess.run([tool, "-version"], capture_output=True, text=True, timeout=30, check=False)
    except (OSError, subprocess.SubprocessError):
        return NOT_RECORDED
    lines = completed.stdout.splitlines() if completed.returncode == 0 else []
    return lines[0].strip() if lines and lines[0].strip() else NOT_RECORDED


def default_lineage() -> dict[str, str]:
    return {
        "sdkVersion": _sdk_version(),
        "harnessVersion": __version__,
        "ffmpeg": _tool_version_line("ffmpeg"),
        "ffprobe": _tool_version_line("ffprobe"),
        "python": platform.python_version(),
    }


def build_report(
    *,
    corpus: CorpusManifest,
    subject: Mapping[str, Any],
    configuration: Mapping[str, Any],
    clips: Sequence[ClipEvaluation],
    run: Mapping[str, Any],
    comparator: Mapping[str, Any] | None = None,
    lineage: Mapping[str, Any] | None = None,
    executions_per_clip: Sequence[int] = (),
    determinism_differences: Sequence[Mapping[str, Any]] = (),
    any_first_run_failed: bool = False,
) -> dict[str, Any]:
    """Assemble the report and seal it with its identity.

    `subject` carries the subject lineage fields, `configuration` is the evaluation configuration as read, and `run`
    is the non-identity section. The comparator defaults to `not-declared`. The determinism section and gate come from
    the executions per evaluated clip and the recorded differences, the lineage gate from the subject's image digest.
    """
    clip_counts_by_profile = {
        profile: sum(1 for clip in corpus.clips if clip.profile == profile) for profile in PROFILES
    }
    clip_counts_by_profile = {profile: count for profile, count in clip_counts_by_profile.items() if count}
    primary_metrics = {
        kind: entry["primaryMetric"] for kind, entry in configuration.get("labelKinds", {}).items()
    }
    declared = comparator is not None and comparator.get("kind") != "not-declared"
    results = {
        profile: _profile_result(profile, clips, clip_counts_by_profile.get(profile, 0), primary_metrics, declared)
        for profile in PROFILES
    }
    profiles = round_floats({profile: result for profile, (result, _) in results.items()})
    thresholds = threshold_verdicts(
        [
            SimpleNamespace(label_kind=t["labelKind"], profile=t["profile"], metric=t["metric"], minimum=t["minimum"])
            for t in configuration.get("thresholds", [])
        ],
        {profile: metrics for profile, (_, metrics) in results.items()},
    )

    differences = [dict(difference) for difference in determinism_differences]
    determinism_status = determinism_gate(executions_per_clip, differences, any_first_run_failed=any_first_run_failed)
    report: dict[str, Any] = {
        "reportVersion": REPORT_VERSION,
        "corpus": {
            "corpusId": corpus.corpus_id,
            "corpusVersion": corpus.corpus_version,
            "identity": corpus.identity,
            "clipCounts": clip_counts_by_profile,
        },
        "subject": dict(subject),
        "comparator": dict(comparator) if comparator is not None else {"kind": "not-declared"},
        "configuration": json.loads(json.dumps(configuration)),
        "lineage": dict(lineage) if lineage is not None else default_lineage(),
        "clips": [_clip_result(clip) for clip in clips],
        "profiles": profiles,
        "determinism": {
            "status": determinism_status,
            "repetitions": configuration.get("repetitions", 1),
            "differences": differences,
        },
        "gates": {
            "determinism": determinism_status,
            "lineage": lineage_gate(subject.get("imageDigest")),
            "profileCoverage": profile_coverage_gate(clip_counts_by_profile),
            **{f"comparison.{profile}": profiles[profile]["comparisonGate"] for profile in PROFILES},
        },
        "run": dict(run),
    }
    if thresholds:
        report["thresholdVerdicts"] = round_floats(thresholds)
    report["identity"] = identity_of(report, exclude=IDENTITY_EXCLUDED)
    return report


def report_bytes(report: Mapping[str, Any]) -> bytes:
    """The file form of a report: its canonical JSON plus a trailing newline."""
    return canonical_bytes(report) + b"\n"


def verify_report(report: Any) -> list[str]:
    """Problems of a report document: schema violations or an identity that does not match; empty when valid."""
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    problems = [
        f"{'/'.join(str(part) for part in error.absolute_path) or '<root>'}: {error.message}"
        for error in sorted(
            Draft202012Validator(schema).iter_errors(report),
            key=lambda error: [str(part) for part in error.absolute_path],
        )
    ]
    if problems:
        return problems
    expected = identity_of(report, exclude=IDENTITY_EXCLUDED)
    if report["identity"] != expected:
        return [f"identity: {report['identity']} does not match the recomputed identity {expected}"]
    return []
