"""Staged upstream inputs: verified loading and the failure categories (FR-018, FR-019, FR-022)."""

from __future__ import annotations

import copy
import json
import subprocess
import sys
import textwrap
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest

from fixtures.mounts import ANALYSIS, Mount, read_json, sha256_digest, validate_canonical, write_json
from socalytics_analyst import errors
from socalytics_analyst.context import load_job
from socalytics_analyst.inputs import load_inputs
from socalytics_analyst.models.facts import FACTS_MEDIA_TYPE

BARRIER_JOB = "match-job-with-segment-barrier.json"
CAPABILITY = "player-detection"
RESULT_IDS = ("res-0001", "res-0002")
SEGMENTS = ("segment-3", "segment-4")

DRIVER = textwrap.dedent(
    """
    import runpy
    import sys
    from pathlib import Path

    from socalytics_analyst import entrypoint

    entrypoint.run.__kwdefaults__["root"] = Path(sys.argv[2])
    runpy.run_path(sys.argv[1], run_name="__main__")
    """
)

ANALYST = textwrap.dedent(
    """
    from socalytics_analyst import run


    def analyse(context):
        pass


    if __name__ == "__main__":
        run(analyse, output_type="foundational-facts")
    """
)


def _facts_bytes() -> bytes:
    document = read_json(ANALYSIS / "fact-artifact" / "v1" / "examples" / "valid" / "segment-foundational-facts.json")
    return json.dumps(document, indent=2).encode("utf-8")


def _manifest(index: int, facts: bytes) -> dict[str, Any]:
    manifest = read_json(ANALYSIS / "result-manifest" / "v1" / "examples" / "valid" / "segment-result.json")
    manifest["scope"]["segment"]["segmentId"] = SEGMENTS[index]
    manifest["scope"]["logicalJobId"] = f"job-000{index + 1}"
    manifest["artifacts"] = [
        {
            "name": "facts.json",
            "digest": sha256_digest(facts),
            "sizeBytes": len(facts),
            "mediaType": FACTS_MEDIA_TYPE,
        },
        {
            "name": "notes.bin",
            "digest": sha256_digest(b"notes-bytes"),
            "sizeBytes": len(b"notes-bytes"),
            "mediaType": "application/octet-stream",
        },
    ]
    manifest["factCount"] = 2
    return manifest


def _coverage(states: tuple[str, str] = ("accepted", "failed")) -> dict[str, Any]:
    return {
        "upstream": {
            "capability": {"id": CAPABILITY, "version": "1.0.0"},
            "schemaVersion": "1.0.0",
            "edgeKind": "required",
            "coverage": {"mode": "partial", "minimumAcceptedFraction": 0.5},
        },
        "coverage": [{"segmentId": segment, "state": state} for segment, state in zip(SEGMENTS, states)],
    }


class Staged:
    """A match job with one required dependency whose two accepted results are staged on a mount."""

    def __init__(self, mount: Mount) -> None:
        self.mount = mount
        self.facts = _facts_bytes()
        self.manifests = [_manifest(index, self.facts) for index in range(2)]

    def stage(self, mutate: Callable[[int, dict[str, Any]], None] | None = None) -> None:
        job = read_json(self.mount.job_path)
        results = job["inputs"][0]["acceptedResults"]
        for index, accepted_id in enumerate(RESULT_IDS):
            manifest = copy.deepcopy(self.manifests[index])
            if mutate:
                mutate(index, manifest)
            path = self.mount.stage_result(
                CAPABILITY, accepted_id, manifest, {"facts.json": self.facts, "notes.bin": b"notes-bytes"}
            )
            results[index]["acceptedResultId"] = accepted_id
            results[index]["manifestChecksum"] = sha256_digest(path.read_bytes())
        self.mount.write_job(job)
        self.mount.stage_coverage(CAPABILITY, _coverage())

    def artifact(self, index: int, name: str) -> Path:
        return self.mount.inputs / CAPABILITY / RESULT_IDS[index] / name


def _with_workflow(job: dict[str, Any]) -> None:
    job["contractVersion"] = "1.1.0"
    job["workflow"] = {"id": "default-analysis", "version": "1.0.0"}


@pytest.fixture
def staged(mount_factory: Callable[..., Mount]) -> Staged:
    return Staged(mount_factory(job_example_name=BARRIER_JOB, adjust_job=_with_workflow))


def _expect(mount: Mount, category: str) -> None:
    with pytest.raises(errors.InputError) as raised:
        load_inputs(mount.root, load_job(mount.root))
    assert raised.value.category == category
    assert raised.value.exit_code == 2


def _run_entry_point(mount: Mount, tmp_path: Path) -> subprocess.CompletedProcess[str]:
    script = tmp_path / "analyst.py"
    script.write_text(ANALYST, encoding="utf-8")
    return subprocess.run(
        [sys.executable, "-c", DRIVER, str(script), str(mount.root)],
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
    )


def _assert_exit_2(mount: Mount, tmp_path: Path, category: str) -> None:
    completed = _run_entry_point(mount, tmp_path)
    assert completed.returncode == 2
    assert completed.stdout == ""
    assert completed.stderr.strip() == f"socalytics-analyst: failed {category}"
    assert mount.result_entries() == []


def test_matching_staged_inputs_load_typed_and_verified(staged: Staged) -> None:
    staged.stage()
    mount = staged.mount

    inputs = load_inputs(mount.root, load_job(mount.root))

    assert len(inputs) == 1
    dependency = inputs.get(CAPABILITY)
    assert dependency is not None
    assert inputs.by_node_key("detect-players") is dependency
    assert dependency.edge_kind == "required"
    assert dependency.status == "accepted"
    assert dependency.capability.id == CAPABILITY
    assert [result.accepted_result_id for result in dependency.results] == list(RESULT_IDS)
    assert dependency.coverage is not None
    assert dependency.coverage.upstream.coverage is not None
    assert dependency.coverage.upstream.coverage.mode == "partial"
    assert [(entry.segment_id, entry.state) for entry in dependency.coverage.coverage] == [
        ("segment-3", "accepted"),
        ("segment-4", "failed"),
    ]
    for index, result in enumerate(dependency.results):
        assert result.manifest.scope.segment.segment_id == SEGMENTS[index]
        assert set(result.artifacts) == {"facts.json", "notes.bin"}
        assert result.artifacts["notes.bin"].read_bytes() == b"notes-bytes"
        assert [fact.kind for fact in result.facts] == ["player-detected", "player-detected"]
        assert result.facts[0].start_ms == 120000


def test_staged_manifests_are_valid_against_the_contract(staged: Staged) -> None:
    staged.stage()

    for index in range(2):
        validate_canonical("result-manifest", read_json(staged.mount.inputs / CAPABILITY / RESULT_IDS[index] / "manifest.json"))


def test_optional_and_conditional_entries_load_without_results(mount_factory: Callable[..., Mount]) -> None:
    def adjust(job: dict[str, Any]) -> None:
        _with_workflow(job)
        entry = job["inputs"][0]
        entry.update(edgeKind="optional", status="absent", acceptedResults=[])
        job["inputs"].append(
            {**copy.deepcopy(entry), "upstreamNodeKey": "other-node", "capability": {"id": "ball-tracking", "version": "1.0.0"},
             "edgeKind": "conditional", "status": "not-applicable"}
        )

    mount = mount_factory(job_example_name=BARRIER_JOB, adjust_job=adjust)

    inputs = load_inputs(mount.root, load_job(mount.root))

    assert [(item.edge_kind, item.status, item.results, item.coverage) for item in inputs] == [
        ("optional", "absent", (), None),
        ("conditional", "not-applicable", (), None),
    ]


def test_a_job_without_inputs_needs_no_input_mount(segment_mount: Mount) -> None:
    inputs = load_inputs(segment_mount.root, load_job(segment_mount.root))

    assert not inputs
    assert len(inputs) == 0


def test_a_changed_artifact_byte_fails_as_artifact_mismatch(staged: Staged, tmp_path: Path) -> None:
    staged.stage()
    target = staged.artifact(1, "notes.bin")
    target.write_bytes(b"notes-bytez")

    _expect(staged.mount, errors.INPUT_ARTIFACT_MISMATCH)
    _assert_exit_2(staged.mount, tmp_path, errors.INPUT_ARTIFACT_MISMATCH)


def test_a_wrong_declared_size_fails_as_artifact_mismatch(staged: Staged, tmp_path: Path) -> None:
    def mutate(index: int, manifest: dict[str, Any]) -> None:
        if index == 0:
            manifest["artifacts"][1]["sizeBytes"] += 1

    staged.stage(mutate)

    _expect(staged.mount, errors.INPUT_ARTIFACT_MISMATCH)
    _assert_exit_2(staged.mount, tmp_path, errors.INPUT_ARTIFACT_MISMATCH)


def test_a_missing_artifact_fails_as_artifact_mismatch(staged: Staged) -> None:
    staged.stage()
    staged.artifact(0, "notes.bin").unlink()

    _expect(staged.mount, errors.INPUT_ARTIFACT_MISMATCH)


def test_a_missing_staged_result_fails_as_staging_mismatch(staged: Staged, tmp_path: Path) -> None:
    staged.stage()
    folder = staged.mount.inputs / CAPABILITY / RESULT_IDS[1]
    for child in folder.iterdir():
        child.unlink()
    folder.rmdir()

    _expect(staged.mount, errors.INPUT_STAGING_MISMATCH)
    _assert_exit_2(staged.mount, tmp_path, errors.INPUT_STAGING_MISMATCH)


def test_an_extra_staged_result_fails_as_staging_mismatch(staged: Staged, tmp_path: Path) -> None:
    staged.stage()
    staged.mount.stage_result(CAPABILITY, "res-0099", staged.manifests[0], {"facts.json": staged.facts})

    _expect(staged.mount, errors.INPUT_STAGING_MISMATCH)
    _assert_exit_2(staged.mount, tmp_path, errors.INPUT_STAGING_MISMATCH)


def test_an_undeclared_dependency_fails_as_staging_mismatch(staged: Staged) -> None:
    staged.stage()
    staged.mount.stage_result("ball-tracking", "res-0100", staged.manifests[0], {"facts.json": staged.facts})

    _expect(staged.mount, errors.INPUT_STAGING_MISMATCH)


def test_a_manifest_checksum_mismatch_fails_as_staging_mismatch(staged: Staged, tmp_path: Path) -> None:
    staged.stage()
    job = read_json(staged.mount.job_path)
    job["inputs"][0]["acceptedResults"][0]["manifestChecksum"] = "sha-256:" + "0" * 64
    write_json(staged.mount.job_path, job)

    _expect(staged.mount, errors.INPUT_STAGING_MISMATCH)
    _assert_exit_2(staged.mount, tmp_path, errors.INPUT_STAGING_MISMATCH)


def test_a_missing_input_mount_with_accepted_results_fails_as_staging_mismatch(staged: Staged) -> None:
    _expect(staged.mount, errors.INPUT_STAGING_MISMATCH)


def test_staged_results_for_an_absent_entry_fail_as_staging_mismatch(
    mount_factory: Callable[..., Mount], tmp_path: Path
) -> None:
    def adjust(job: dict[str, Any]) -> None:
        _with_workflow(job)
        job["inputs"][0].update(edgeKind="optional", status="absent", acceptedResults=[])

    mount = mount_factory(job_example_name=BARRIER_JOB, adjust_job=adjust)
    mount.stage_result(CAPABILITY, "res-0001", _manifest(0, _facts_bytes()), {"facts.json": _facts_bytes()})

    _expect(mount, errors.INPUT_STAGING_MISMATCH)
    _assert_exit_2(mount, tmp_path, errors.INPUT_STAGING_MISMATCH)


def test_coverage_describing_another_dependency_fails(staged: Staged) -> None:
    staged.stage()
    coverage = _coverage()
    coverage["upstream"]["edgeKind"] = "optional"
    staged.mount.stage_coverage(CAPABILITY, coverage)

    _expect(staged.mount, errors.INPUT_STAGING_MISMATCH)


def test_an_invalid_coverage_part_fails_as_invalid_input(staged: Staged) -> None:
    staged.stage()
    coverage = _coverage()
    coverage["coverage"][0]["state"] = "unknown"
    staged.mount.stage_coverage(CAPABILITY, coverage)

    _expect(staged.mount, errors.INPUT_INVALID)
