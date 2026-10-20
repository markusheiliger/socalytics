from __future__ import annotations

import pytest

from fixtures.mounts import MATCH_JOB_EXAMPLE, SEGMENT_JOB_EXAMPLE
from socalytics_analyst import errors
from socalytics_analyst.context import load_attempt, load_documents, load_job


def test_valid_segment_job_and_attempt_load(mount_factory):
    mount = mount_factory(job_example_name=SEGMENT_JOB_EXAMPLE)
    job, attempt = load_documents(mount.root)
    assert job.contract_version == "1.1.0"
    assert job.execution_scope == "segment"
    assert job.segment_window is not None and job.segment_window.segment_id == "seg-0001"
    assert job.workflow is not None
    assert attempt.attempt_number >= 1


def test_valid_match_job_loads_without_segment_window(mount_factory):
    mount = mount_factory(job_example_name=MATCH_JOB_EXAMPLE)
    job = load_job(mount.root)
    assert job.execution_scope == "match"
    assert job.segment_window is None
    assert job.workflow is not None


def test_missing_job_file(segment_mount):
    segment_mount.job_path.unlink()
    with pytest.raises(errors.InputError) as raised:
        load_job(segment_mount.root)
    assert raised.value.category == errors.INPUT_MISSING
    assert raised.value.exit_code == errors.INPUT_EXIT_CODE


def test_missing_attempt_file(segment_mount):
    segment_mount.attempt_path.unlink()
    with pytest.raises(errors.InputError) as raised:
        load_attempt(segment_mount.root)
    assert raised.value.category == errors.INPUT_MISSING


def test_missing_mount_root(tmp_path):
    with pytest.raises(errors.InputError) as raised:
        load_documents(tmp_path / "absent")
    assert raised.value.category == errors.INPUT_MISSING


@pytest.mark.parametrize("loader, path_name", [(load_job, "job_path"), (load_attempt, "attempt_path")])
def test_malformed_json_is_invalid(segment_mount, loader, path_name):
    getattr(segment_mount, path_name).write_text("{ not json", encoding="utf-8")
    with pytest.raises(errors.InputError) as raised:
        loader(segment_mount.root)
    assert raised.value.category == errors.INPUT_INVALID


def test_job_violating_its_schema_is_invalid(mount_factory):
    mount = mount_factory(adjust_job=lambda job: job.pop("runId"))
    with pytest.raises(errors.InputError) as raised:
        load_job(mount.root)
    assert raised.value.category == errors.INPUT_INVALID
    assert "analyst-job violates its schema" in str(raised.value)


def test_job_that_is_not_an_object_is_invalid(segment_mount):
    segment_mount.write_job([])
    with pytest.raises(errors.InputError) as raised:
        load_job(segment_mount.root)
    assert raised.value.category == errors.INPUT_INVALID


def test_attempt_violating_its_schema_is_invalid(mount_factory):
    mount = mount_factory(adjust_attempt=lambda attempt: attempt.pop("attemptNumber"))
    with pytest.raises(errors.InputError) as raised:
        load_attempt(mount.root)
    assert raised.value.category == errors.INPUT_INVALID


@pytest.mark.parametrize("version", ["1.2.0", "2.0.0", "0.9.0"])
def test_unsupported_job_version(mount_factory, version):
    mount = mount_factory(adjust_job=lambda job: job.update(contractVersion=version))
    with pytest.raises(errors.InputError) as raised:
        load_job(mount.root)
    assert raised.value.category == errors.UNSUPPORTED_CONTRACT_VERSION


@pytest.mark.parametrize("version", ["1.1.0", "2.0.0"])
def test_unsupported_attempt_version(mount_factory, version):
    mount = mount_factory(adjust_attempt=lambda attempt: attempt.update(contractVersion=version))
    with pytest.raises(errors.InputError) as raised:
        load_attempt(mount.root)
    assert raised.value.category == errors.UNSUPPORTED_CONTRACT_VERSION


def test_version_1_0_0_job_without_workflow_is_incomplete(mount_factory):
    def adjust(job):
        job["contractVersion"] = "1.0.0"
        job.pop("workflow", None)
        job.pop("segmentWindow", None)

    mount = mount_factory(adjust_job=adjust)
    with pytest.raises(errors.InputError) as raised:
        load_job(mount.root)
    assert raised.value.category == errors.JOB_CONTEXT_INCOMPLETE


def test_segment_job_without_segment_window_is_incomplete(mount_factory):
    mount = mount_factory(job_example_name=SEGMENT_JOB_EXAMPLE, adjust_job=lambda job: job.pop("segmentWindow"))
    with pytest.raises(errors.InputError) as raised:
        load_job(mount.root)
    assert raised.value.category == errors.JOB_CONTEXT_INCOMPLETE
    assert "segment window" in raised.value.detail


def test_errors_do_not_echo_document_values(mount_factory):
    mount = mount_factory(adjust_job=lambda job: job.update(runId=12345678))
    with pytest.raises(errors.InputError) as raised:
        load_job(mount.root)
    assert "12345678" not in str(raised.value)
