from __future__ import annotations

import json
import math
import os
from collections.abc import Callable

import pytest
from fixtures.mounts import (
    Mount,
    read_json,
    sha256_digest,
    validate_canonical,
    validate_written_json,
)

from socalytics_analyst import errors
from socalytics_analyst.context import load_documents
from socalytics_analyst.models.facts import FACTS_MEDIA_TYPE, Fact
from socalytics_analyst.result import ResultWriter

MountFactory = Callable[..., Mount]


def fact(start: int = 1000, end: int = 2000, confidence: float = 0.9) -> Fact:
    return Fact(kind="player-detected", start_ms=start, end_ms=end, confidence=confidence, payload={"trackId": "t-7"})


def writer_for(mount: Mount, output_type: str = "foundational-facts") -> ResultWriter:
    job, attempt = load_documents(mount.root)
    return ResultWriter(job, attempt, output_type, root=mount.root)


def category(excinfo: pytest.ExceptionInfo[errors.AnalystSdkError]) -> str:
    return excinfo.value.category


def test_segment_result_is_valid_and_equals_the_job(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    writer.add_fact(fact(0, 500))
    writer.add_fact(fact(59000, 60000))
    writer.set_confidence(0.75)
    writer.add_evidence_reference("evidence-1")
    writer.finalize()

    assert segment_mount.result_entries() == ["facts.json", "manifest.json"]
    manifest = validate_written_json("result-manifest", segment_mount.result / "manifest.json")
    facts = validate_written_json("fact-artifact", segment_mount.result / "facts.json")
    job = read_json(segment_mount.job_path)
    attempt = read_json(segment_mount.attempt_path)

    assert manifest["contractVersion"] == "1.1.0"
    assert manifest["scope"] == {
        "teamId": job["teamId"],
        "matchId": job["matchId"],
        "recordingSetVersionId": job["recordingSetVersionId"],
        "runId": job["runId"],
        "nodeId": job["nodeId"],
        "logicalJobId": job["logicalJobId"],
        "segment": {
            "segmentId": job["segmentWindow"]["segmentId"],
            "windowStartMs": job["segmentWindow"]["startMs"],
            "windowEndMs": job["segmentWindow"]["endMs"],
        },
    }
    assert manifest["attempt"] == {"attemptId": attempt["attemptId"], "attemptNumber": attempt["attemptNumber"]}
    pinned = job["pinned"]
    assert manifest["provenance"] == {
        "capability": job["capability"],
        "schemaVersion": job["schemaVersion"],
        "analystProfile": {"id": pinned["analystProfileId"], "digest": pinned["analystProfileDigest"]},
        "imageDigest": pinned["imageDigest"],
        "modelDigest": pinned["modelDigest"],
        "policyVersions": [{"id": p["policyId"], "version": p["version"]} for p in pinned["policies"]],
        "workflow": job["workflow"],
    }
    assert manifest["upstreamLineage"] == []
    assert manifest["validityWindow"] == {"startMs": 0, "endMs": 60000}
    assert manifest["factCount"] == len(facts["facts"]) == 2
    assert manifest["outputType"] == "foundational-facts"
    assert manifest["confidence"] == 0.75
    assert manifest["evidenceReferences"] == ["evidence-1"]
    assert facts["capability"] == job["capability"]
    assert facts["schemaVersion"] == job["schemaVersion"]

    entry = next(a for a in manifest["artifacts"] if a["name"] == "facts.json")
    data = (segment_mount.result / "facts.json").read_bytes()
    assert entry == {
        "name": "facts.json",
        "digest": sha256_digest(data),
        "sizeBytes": len(data),
        "mediaType": FACTS_MEDIA_TYPE,
    }
    assert not (segment_mount.result / ".socalytics-staging").exists()


def test_segment_model_digest_is_omitted_when_the_job_has_none(mount_factory: MountFactory) -> None:
    mount = mount_factory(adjust_job=lambda job: job["pinned"].update(modelDigest=None))
    writer = writer_for(mount)
    writer.finalize()
    manifest = validate_written_json("result-manifest", mount.result / "manifest.json")
    assert "modelDigest" not in manifest["provenance"]


def test_match_result_carries_lineage_in_input_snapshot_order(match_mount: Mount) -> None:
    writer = writer_for(match_mount, "soccer-concepts")
    writer.set_validity_window(0, 5400000)
    writer.add_fact(fact(3000000, 3000100))
    writer.set_confidence(0.5)
    writer.finalize()

    manifest = validate_written_json("result-manifest", match_mount.result / "manifest.json")
    validate_written_json("fact-artifact", match_mount.result / "facts.json")
    job = read_json(match_mount.job_path)

    assert manifest["scope"]["match"] is True
    assert "segment" not in manifest["scope"]
    assert manifest["outputType"] == "soccer-concepts"
    assert manifest["validityWindow"] == {"startMs": 0, "endMs": 5400000}
    assert manifest["factCount"] == 1
    expected = [
        {
            "acceptedResultId": result["acceptedResultId"],
            "capability": result["capability"],
            "schemaVersion": result["schemaVersion"],
            "manifestChecksum": result["manifestChecksum"],
        }
        for entry in job["inputs"]
        for result in entry["acceptedResults"]
    ]
    assert expected and manifest["upstreamLineage"] == expected


def test_match_result_without_a_window_is_refused(match_mount: Mount) -> None:
    writer = writer_for(match_mount)
    with pytest.raises(errors.ResultError) as excinfo:
        writer.finalize()
    assert category(excinfo) == errors.VALIDITY_WINDOW_MISSING
    assert match_mount.result_entries() == []


def test_fact_ending_exactly_at_the_window_end_is_inside(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    writer.add_fact(fact(59999, 60000))
    writer.set_confidence(0.5)
    writer.finalize()
    assert validate_written_json("result-manifest", segment_mount.result / "manifest.json")["factCount"] == 1


@pytest.mark.parametrize("start, end", [(60000, 60001), (59000, 60001), (1000, 1000), (2000, 1000)])
def test_facts_outside_the_window_or_empty_are_refused(segment_mount: Mount, start: int, end: int) -> None:
    writer = writer_for(segment_mount)
    with pytest.raises(errors.ResultError) as excinfo:
        writer.add_fact(fact(start, end))
    assert category(excinfo) == errors.FACT_OUT_OF_WINDOW
    assert writer.fact_count == 0


def test_window_can_be_narrowed_and_applies_to_facts(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    writer.set_validity_window(10000, 20000)
    writer.add_fact(fact(10000, 20000))
    with pytest.raises(errors.ResultError) as excinfo:
        writer.add_fact(fact(9999, 10001))
    assert category(excinfo) == errors.FACT_OUT_OF_WINDOW
    writer.set_confidence(0.5)
    writer.finalize()
    manifest = validate_written_json("result-manifest", segment_mount.result / "manifest.json")
    assert manifest["validityWindow"] == {"startMs": 10000, "endMs": 20000}
    assert manifest["scope"]["segment"]["windowStartMs"] == 0
    assert manifest["scope"]["segment"]["windowEndMs"] == 60000


def test_narrowing_below_an_added_fact_is_refused_at_finalization(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    writer.add_fact(fact(1000, 2000))
    writer.set_validity_window(10000, 20000)
    writer.set_confidence(0.5)
    with pytest.raises(errors.ResultError) as excinfo:
        writer.finalize()
    assert category(excinfo) == errors.FACT_OUT_OF_WINDOW
    assert segment_mount.result_entries() == []


@pytest.mark.parametrize("start, end", [(0, 60001), (-1, 100), (5, 5), (10, 5), (0, 1.5)])
def test_window_cannot_be_extended_or_malformed(segment_mount: Mount, start: int, end: int) -> None:
    writer = writer_for(segment_mount)
    with pytest.raises(errors.ResultError) as excinfo:
        writer.set_validity_window(start, end)
    assert category(excinfo) == errors.VALIDITY_WINDOW_INVALID


def test_empty_fact_list_finalizes_with_zero_facts(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    writer.finalize()
    manifest = validate_written_json("result-manifest", segment_mount.result / "manifest.json")
    facts = validate_written_json("fact-artifact", segment_mount.result / "facts.json")
    assert manifest["factCount"] == 0
    assert facts["facts"] == []


def test_artifacts_are_written_and_described(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    writer.add_artifact("debug.txt", "text/plain", b"hello")
    writer.set_confidence(0.5)
    writer.finalize()
    assert segment_mount.result_entries() == ["debug.txt", "facts.json", "manifest.json"]
    manifest = validate_written_json("result-manifest", segment_mount.result / "manifest.json")
    assert manifest["artifacts"][1] == {
        "name": "debug.txt",
        "digest": sha256_digest(b"hello"),
        "sizeBytes": 5,
        "mediaType": "text/plain",
    }
    assert (segment_mount.result / "debug.txt").read_bytes() == b"hello"


@pytest.mark.parametrize("name", ["", "Upper.txt", "-lead.txt", "a/b.txt", "../x", "a b", "x" * 129])
def test_artifact_names_that_do_not_match_are_refused(segment_mount: Mount, name: str) -> None:
    writer = writer_for(segment_mount)
    with pytest.raises(errors.ResultError) as excinfo:
        writer.add_artifact(name, "text/plain", b"x")
    assert category(excinfo) == errors.ARTIFACT_INVALID
    assert writer.artifact_count == 0


@pytest.mark.parametrize("name", ["manifest.json", "facts.json"])
def test_reserved_artifact_names_are_refused(segment_mount: Mount, name: str) -> None:
    writer = writer_for(segment_mount)
    with pytest.raises(errors.ResultError) as excinfo:
        writer.add_artifact(name, "application/json", b"{}")
    assert category(excinfo) == errors.ARTIFACT_INVALID


def test_duplicate_artifact_names_are_refused(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    writer.add_artifact("a.bin", "application/octet-stream", b"1")
    with pytest.raises(errors.ResultError) as excinfo:
        writer.add_artifact("a.bin", "application/octet-stream", b"2")
    assert category(excinfo) == errors.ARTIFACT_INVALID
    assert writer.artifact_count == 1


def test_empty_artifact_content_is_refused(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    with pytest.raises(errors.ResultError) as excinfo:
        writer.add_artifact("a.bin", "application/octet-stream", b"")
    assert category(excinfo) == errors.ARTIFACT_INVALID


def test_facts_without_confidence_are_refused(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    writer.add_fact(fact())
    with pytest.raises(errors.ResultError) as excinfo:
        writer.finalize()
    assert category(excinfo) == errors.CONFIDENCE_MISSING
    assert segment_mount.result_entries() == []


@pytest.mark.parametrize("value", [-0.1, 1.1, math.nan, math.inf, True, "0.5", None])
def test_invalid_confidence_is_refused(segment_mount: Mount, value: object) -> None:
    writer = writer_for(segment_mount)
    with pytest.raises(errors.ResultError) as excinfo:
        writer.set_confidence(value)  # type: ignore[arg-type]
    assert category(excinfo) == errors.RESULT_VALUE_INVALID


@pytest.mark.parametrize("identifier", ["", "-x", "has space", "x" * 129, 7])
def test_invalid_evidence_reference_is_refused(segment_mount: Mount, identifier: object) -> None:
    writer = writer_for(segment_mount)
    with pytest.raises(errors.ResultError) as excinfo:
        writer.add_evidence_reference(identifier)  # type: ignore[arg-type]
    assert category(excinfo) == errors.RESULT_VALUE_INVALID


def test_unknown_output_type_is_refused(segment_mount: Mount) -> None:
    with pytest.raises(errors.ResultError) as excinfo:
        writer_for(segment_mount, "something-else")
    assert category(excinfo) == errors.RESULT_VALUE_INVALID


def test_unexpected_top_level_file_is_refused(segment_mount: Mount) -> None:
    (segment_mount.result / "stray.txt").write_text("x", encoding="utf-8")
    writer = writer_for(segment_mount)
    with pytest.raises(errors.ResultError) as excinfo:
        writer.finalize()
    assert category(excinfo) == errors.RESULT_UNEXPECTED_FILE
    assert segment_mount.result_entries() == ["stray.txt"]


def test_second_finalization_and_additions_after_it_are_refused(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    writer.finalize()
    before = (segment_mount.result / "manifest.json").read_bytes()
    calls = [
        writer.finalize,
        lambda: writer.add_fact(fact()),
        lambda: writer.add_artifact("a.bin", "application/octet-stream", b"1"),
        lambda: writer.set_validity_window(0, 10),
        lambda: writer.set_confidence(0.5),
        lambda: writer.add_evidence_reference("evidence-1"),
    ]
    for call in calls:
        with pytest.raises(errors.ResultError) as excinfo:
            call()
        assert category(excinfo) == errors.RESULT_ALREADY_FINALIZED
    assert (segment_mount.result / "manifest.json").read_bytes() == before
    assert segment_mount.result_entries() == ["facts.json", "manifest.json"]


def test_no_partial_file_under_a_final_name_when_the_manifest_move_fails(
    segment_mount: Mount, monkeypatch: pytest.MonkeyPatch
) -> None:
    real_replace = os.replace

    def failing_replace(source: object, target: object) -> None:
        if os.path.basename(os.fspath(target)) == "manifest.json":  # type: ignore[arg-type]
            raise OSError("disk full")
        real_replace(source, target)  # type: ignore[arg-type]

    monkeypatch.setattr("socalytics_analyst.result.os.replace", failing_replace)
    writer = writer_for(segment_mount)
    writer.add_artifact("a.bin", "application/octet-stream", b"1")
    writer.set_confidence(0.5)
    with pytest.raises(OSError):
        writer.finalize()
    assert segment_mount.result_entries() == []


def test_no_partial_file_under_a_final_name_when_a_write_fails(
    segment_mount: Mount, monkeypatch: pytest.MonkeyPatch
) -> None:
    def failing_fsync(descriptor: int) -> None:
        raise OSError("io error")

    monkeypatch.setattr("socalytics_analyst.result.os.fsync", failing_fsync)
    writer = writer_for(segment_mount)
    writer.add_fact(fact())
    writer.set_confidence(0.5)
    with pytest.raises(OSError):
        writer.finalize()
    assert segment_mount.result_entries() == []
    with pytest.raises(errors.ResultError) as excinfo:
        writer.finalize()
    assert category(excinfo) == errors.RESULT_ALREADY_FINALIZED


def test_canonical_validation_rejects_a_tampered_manifest(segment_mount: Mount) -> None:
    writer = writer_for(segment_mount)
    writer.finalize()
    manifest = json.loads((segment_mount.result / "manifest.json").read_text(encoding="utf-8"))
    manifest["factCount"] = "many"
    with pytest.raises(AssertionError):
        validate_canonical("result-manifest", manifest)
