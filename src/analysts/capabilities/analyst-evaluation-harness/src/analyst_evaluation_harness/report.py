"""Evaluation report assembly, identity, and verification (FR-010; data-model "Evaluation report")."""

from __future__ import annotations

import json
import platform
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from importlib import metadata
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

from . import __version__
from .canonical import canonical_bytes, identity_of, round_floats
from .corpus import CorpusManifest
from .matching import Pairing
from .metrics import clip_counts, profile_metrics

REPORT_VERSION = "1.0.0"
SCHEMA_PATH = Path(__file__).resolve().parents[2] / "schemas" / "evaluation-report.schema.json"
PROFILES = ("fixed", "broadcast")
IDENTITY_EXCLUDED = ("identity", "run")

NOT_EVIDENCED = "not-evidenced"
EVIDENCED = "evidenced"
NOT_RUN = "not-run"
NO_COMPARATOR_REASON = "no comparator declared"
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
    return {
        "clipId": clip.clip_id,
        "profile": clip.profile,
        "subject": {
            "execution": _execution(clip),
            "pairings": [pairing.to_dict() for pairing in clip.pairings],
        },
        "comparator": {"execution": {"status": NOT_RUN}, "pairings": []},
    }


def _profile_result(
    profile: str,
    clips: Sequence[ClipEvaluation],
    corpus_clips: int,
    primary_metrics: Mapping[str, str],
) -> dict[str, Any]:
    items = [
        (
            profile,
            pairing.label_kind,
            clip_counts(
                labels=len(pairing.pairs) + len(pairing.missed_labels),
                true_positives=pairing.true_positives,
                false_positives=len(pairing.false_positives),
                failed=clip.failed,
            ),
        )
        for clip in clips
        if clip.profile == profile
        for pairing in clip.pairings
    ]
    metrics = profile_metrics(items).get(profile, {})
    kinds = sorted(set(metrics) | set(primary_metrics))
    return {
        "status": EVIDENCED if corpus_clips > 0 else NOT_EVIDENCED,
        "subject": metrics,
        "comparisonGate": NOT_EVIDENCED,
        "verdict": NOT_EVIDENCED,
        "labelKindVerdicts": {
            kind: {
                "verdict": NOT_EVIDENCED,
                "primaryMetric": primary_metrics[kind],
                "reason": NO_COMPARATOR_REASON,
            }
            for kind in kinds
            if kind in primary_metrics
        },
    }


def _sdk_version() -> str:
    try:
        return metadata.version("socalytics-analyst-sdk")
    except metadata.PackageNotFoundError:
        return NOT_RECORDED


def default_lineage() -> dict[str, str]:
    return {
        "sdkVersion": _sdk_version(),
        "harnessVersion": __version__,
        "ffmpeg": NOT_RECORDED,
        "ffprobe": NOT_RECORDED,
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
) -> dict[str, Any]:
    """Assemble the report and seal it with its identity.

    `subject` carries the subject lineage fields, `configuration` is the evaluation configuration as read, and `run`
    is the non-identity section. The comparator defaults to `not-declared`.
    """
    clip_counts_by_profile = {
        profile: sum(1 for clip in corpus.clips if clip.profile == profile) for profile in PROFILES
    }
    clip_counts_by_profile = {profile: count for profile, count in clip_counts_by_profile.items() if count}
    primary_metrics = {
        kind: entry["primaryMetric"] for kind, entry in configuration.get("labelKinds", {}).items()
    }
    profiles = round_floats(
        {
            profile: _profile_result(profile, clips, clip_counts_by_profile.get(profile, 0), primary_metrics)
            for profile in PROFILES
        }
    )
    all_profiles = all(profile in clip_counts_by_profile for profile in PROFILES)

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
            "status": NOT_EVIDENCED,
            "repetitions": configuration.get("repetitions", 1),
            "differences": [],
        },
        "gates": {
            "determinism": NOT_EVIDENCED,
            "lineage": NOT_EVIDENCED,
            "profileCoverage": "passed" if all_profiles else NOT_EVIDENCED,
        },
        "run": dict(run),
    }
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
