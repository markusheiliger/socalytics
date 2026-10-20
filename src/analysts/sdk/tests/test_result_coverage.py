from __future__ import annotations

import json
from collections.abc import Callable
from typing import Any

import pytest
from fixtures.mounts import (
    ANALYSIS,
    Mount,
    read_json,
    sha256_digest,
    validate_written_json,
    write_json,
)

from socalytics_analyst import errors
from socalytics_analyst.context import AnalystContext, load_documents
from socalytics_analyst.entrypoint import run
from socalytics_analyst.inputs import load_inputs
from socalytics_analyst.models.facts import FACTS_MEDIA_TYPE, Fact
from socalytics_analyst.result import ResultWriter

MountFactory = Callable[..., Mount]

SEGMENT_MS = 60000
ACCEPTED = "accepted"


def _capability(capability_id: str) -> dict[str, str]:
    return {"id": capability_id, "version": "1.0.0"}


def _facts_bytes(capability_id: str) -> bytes:
    document = {
        "contractVersion": "1.0.0",
        "capability": _capability(capability_id),
        "schemaVersion": "1.0.0",
        "outputType": "foundational-facts",
        "facts": [
            {"kind": "player-detected", "startMs": 0, "endMs": 10, "confidence": 0.9, "payload": {"trackId": "t-1"}}
        ],
    }
    return json.dumps(document).encode("utf-8")


def _segment_manifest(capability_id: str, number: int, facts: bytes) -> dict[str, Any]:
    start, end = (number - 1) * SEGMENT_MS, number * SEGMENT_MS
    manifest = read_json(ANALYSIS / "result-manifest" / "v1" / "examples" / "valid" / "segment-result.json")
    manifest["scope"]["nodeId"] = f"node-{capability_id}"
    manifest["scope"]["logicalJobId"] = f"job-{capability_id}-{number}"
    manifest["scope"]["segment"] = {"segmentId": f"seg-{number}", "windowStartMs": start, "windowEndMs": end}
    manifest["provenance"]["capability"] = _capability(capability_id)
    manifest["provenance"].pop("modelDigest", None)
    manifest["artifacts"] = [
        {
            "name": "facts.json",
            "digest": sha256_digest(facts),
            "sizeBytes": len(facts),
            "mediaType": FACTS_MEDIA_TYPE,
        }
    ]
    manifest["validityWindow"] = {"startMs": start, "endMs": end}
    manifest["factCount"] = 1
    manifest["upstreamLineage"] = []
    return manifest


def _stage(
    mount: Mount, dependencies: list[tuple[str, str | None, dict[int, str]]]
) -> list[dict[str, Any]]:
    """Stage `(capability, coverage mode, {segment number: state})` dependencies and rewrite the job's inputs."""
    job = read_json(mount.job_path)
    entries: list[dict[str, Any]] = []
    for capability_id, mode, segments in dependencies:
        upstream: dict[str, Any] = {
            "capability": _capability(capability_id),
            "schemaVersion": "1.0.0",
            "edgeKind": "required",
        }
        if mode is not None:
            upstream["coverage"] = {"mode": mode, "minimumAcceptedFraction": 0.5}
        mount.stage_coverage(
            capability_id,
            {
                "upstream": upstream,
                "coverage": [{"segmentId": f"seg-{n}", "state": state} for n, state in sorted(segments.items())],
            },
        )
        facts = _facts_bytes(capability_id)
        accepted: list[dict[str, Any]] = []
        for number, state in sorted(segments.items()):
            if state != ACCEPTED:
                continue
            result_id = f"res-{capability_id}-{number}"
            path = mount.stage_result(
                capability_id,
                result_id,
                _segment_manifest(capability_id, number, facts),
                {"facts.json": facts},
            )
            accepted.append(
                {
                    "acceptedResultId": result_id,
                    "logicalJobId": f"job-{capability_id}-{number}",
                    "capability": _capability(capability_id),
                    "schemaVersion": "1.0.0",
                    "manifestReference": f"results/run-0001/job-{capability_id}-{number}/manifest.json",
                    "manifestChecksum": sha256_digest(path.read_bytes()),
                }
            )
        entries.append(
            {
                "upstreamNodeKey": f"node-{capability_id}",
                "capability": _capability(capability_id),
                "edgeKind": "required",
                "status": ACCEPTED,
                "acceptedResults": accepted,
            }
        )
    job["inputs"] = entries
    mount.write_job(job)
    return entries


def _lineage(entries: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        {key: result[key] for key in ("acceptedResultId", "capability", "schemaVersion", "manifestChecksum")}
        for entry in entries
        for result in entry["acceptedResults"]
    ]


def _finalize(mount: Mount, *, window: tuple[int, int] | None = None) -> dict[str, Any]:
    job, attempt = load_documents(mount.root)
    writer = ResultWriter(job, attempt, "soccer-concepts", mount.root, load_inputs(mount.root, job))
    if window is not None:
        writer.set_validity_window(*window)
    writer.finalize()
    return validate_written_json("result-manifest", mount.result / "manifest.json")


def _analyst_does_nothing(context: AnalystContext) -> None:
    return None


def _analyst_writes_facts(context: AnalystContext) -> None:
    context.result.set_validity_window(0, 600000)
    context.result.add_fact(
        Fact(kind="pass", start_ms=10, end_ms=20, confidence=0.8, payload={"from": "t-1"})
    )
    context.result.set_confidence(0.8)
    context.result.add_evidence_reference("evidence-9")


def _analyst_adds_artifact(context: AnalystContext) -> None:
    context.result.set_validity_window(60000, 120000)
    context.result.add_artifact("notes.json", "application/json", b"{}")
    context.result.set_confidence(1)


@pytest.mark.parametrize("analyst", [_analyst_does_nothing, _analyst_writes_facts, _analyst_adds_artifact])
def test_lineage_equals_the_input_snapshot_whatever_the_analyst_does(
    match_mount: Mount, analyst: Callable[[AnalystContext], None]
) -> None:
    entries = _stage(
        match_mount,
        [("player-detection", "partial", {1: ACCEPTED, 2: ACCEPTED}), ("ball-detection", None, {1: ACCEPTED})],
    )

    with pytest.raises(SystemExit) as exit_info:
        run(analyst, output_type="soccer-concepts", root=match_mount.root)

    assert exit_info.value.code == 0
    manifest = validate_written_json("result-manifest", match_mount.result / "manifest.json")
    assert manifest["upstreamLineage"] == _lineage(entries)
    assert [item["acceptedResultId"] for item in manifest["upstreamLineage"]] == [
        "res-player-detection-1",
        "res-player-detection-2",
        "res-ball-detection-1",
    ]


def test_segment_lineage_equals_the_job_inputs(segment_mount: Mount) -> None:
    job, attempt = load_documents(segment_mount.root)
    writer = ResultWriter(job, attempt, "foundational-facts", segment_mount.root)
    writer.set_confidence(0.5)
    writer.finalize()

    manifest = validate_written_json("result-manifest", segment_mount.result / "manifest.json")
    expected = [
        {
            "acceptedResultId": result["acceptedResultId"],
            "capability": result["capability"],
            "schemaVersion": result["schemaVersion"],
            "manifestChecksum": result["manifestChecksum"],
        }
        for entry in read_json(segment_mount.job_path)["inputs"]
        for result in entry["acceptedResults"]
    ]
    assert manifest["upstreamLineage"] == expected


def test_partial_coverage_with_a_failed_segment_reports_the_staged_coverage(match_mount: Mount) -> None:
    _stage(match_mount, [("player-detection", "partial", {1: "failed", 2: ACCEPTED, 3: ACCEPTED})])
    staged = read_json(match_mount.inputs / "player-detection" / "coverage.json")

    manifest = _finalize(match_mount)

    assert manifest["contractVersion"] == "1.1.0"
    assert manifest["inputCoverage"] == [
        {
            "capability": staged["upstream"]["capability"],
            "schemaVersion": staged["upstream"]["schemaVersion"],
            "units": staged["coverage"],
        }
    ]
    assert {"segmentId": "seg-1", "state": "failed"} in manifest["inputCoverage"][0]["units"]
    assert len(manifest["upstreamLineage"]) == 2


@pytest.mark.parametrize("mode", [None, "complete"])
def test_complete_coverage_dependencies_get_no_report(match_mount: Mount, mode: str | None) -> None:
    _stage(match_mount, [("player-detection", mode, {1: ACCEPTED, 2: ACCEPTED})])

    manifest = _finalize(match_mount)

    assert "inputCoverage" not in manifest


def test_only_partial_dependencies_are_reported(match_mount: Mount) -> None:
    _stage(
        match_mount,
        [
            ("ball-detection", "complete", {1: ACCEPTED}),
            ("player-detection", "partial", {1: ACCEPTED, 2: "missing"}),
        ],
    )

    manifest = _finalize(match_mount)

    assert [item["capability"]["id"] for item in manifest["inputCoverage"]] == ["player-detection"]
    assert manifest["inputCoverage"][0]["units"] == [
        {"segmentId": "seg-1", "state": ACCEPTED},
        {"segmentId": "seg-2", "state": "missing"},
    ]


def test_match_window_is_derived_from_the_staged_results(match_mount: Mount) -> None:
    _stage(match_mount, [("player-detection", "partial", {1: "failed", 2: ACCEPTED, 4: ACCEPTED})])

    manifest = _finalize(match_mount)

    assert manifest["validityWindow"] == {"startMs": SEGMENT_MS, "endMs": 4 * SEGMENT_MS}


def test_a_stated_match_window_wins_over_the_derived_one(match_mount: Mount) -> None:
    _stage(match_mount, [("player-detection", "complete", {1: ACCEPTED, 2: ACCEPTED})])

    manifest = _finalize(match_mount, window=(0, 5400000))

    assert manifest["validityWindow"] == {"startMs": 0, "endMs": 5400000}


def test_a_match_job_without_staged_results_or_a_window_fails(match_mount: Mount) -> None:
    job = read_json(match_mount.job_path)
    job["inputs"] = []
    write_json(match_mount.job_path, job)

    with pytest.raises(errors.ResultError) as excinfo:
        _finalize(match_mount)

    assert excinfo.value.category == errors.VALIDITY_WINDOW_MISSING
    assert match_mount.result_entries() == []


def test_validity_window_missing_exits_1_without_a_manifest(match_mount: Mount) -> None:
    job = read_json(match_mount.job_path)
    job["inputs"] = []
    write_json(match_mount.job_path, job)

    with pytest.raises(SystemExit) as exit_info:
        run(_analyst_does_nothing, output_type="soccer-concepts", root=match_mount.root)

    assert exit_info.value.code == 1
    assert match_mount.result_entries() == []
