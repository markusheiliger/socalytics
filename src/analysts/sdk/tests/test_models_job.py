import copy
import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from socalytics_analyst.models.job import LogicalJob

EXAMPLES = Path(__file__).resolve().parents[4] / "contracts" / "analysis" / "analyst-job" / "v1" / "examples"


def load(kind: str, name: str) -> dict:
    return json.loads((EXAMPLES / kind / f"{name}.json").read_text(encoding="utf-8"))


@pytest.mark.parametrize("path", sorted((EXAMPLES / "valid").glob("*.json")), ids=lambda p: p.name)
def test_every_valid_example_parses(path):
    job = LogicalJob.model_validate_json(path.read_text(encoding="utf-8"))
    assert job.logical_job_id


def test_segment_job_with_window_exposes_typed_fields():
    job = LogicalJob.model_validate(load("valid", "segment-job-with-window"))
    assert job.execution_scope == "segment"
    assert job.segment is not None and job.segment.segment_number == 1
    assert (job.segment_window.segment_id, job.segment_window.start_ms, job.segment_window.end_ms) == (
        "seg-0001",
        0,
        60000,
    )
    assert job.workflow.id == "default-analysis"
    assert job.pinned.model_digest is not None


def test_match_job_allows_null_model_digest_and_lists_results():
    job = LogicalJob.model_validate(load("valid", "match-job-with-workflow"))
    assert job.pinned.model_digest is None
    assert job.segment is None and job.segment_window is None
    assert len(job.inputs[0].accepted_results) == 2


def test_version_1_0_0_job_without_workflow_or_window_is_valid():
    data = load("valid", "segment-job")
    data["contractVersion"] = "1.0.0"
    data.pop("workflow", None)
    data.pop("segmentWindow", None)
    job = LogicalJob.model_validate(data)
    assert job.workflow is None and job.segment_window is None


def test_models_are_frozen_and_closed():
    data = load("valid", "segment-job")
    job = LogicalJob.model_validate(data)
    with pytest.raises(ValidationError):
        job.run_id = "other"
    with pytest.raises(ValidationError):
        LogicalJob.model_validate({**data, "extra": 1})


def test_segment_window_on_match_job_is_rejected():
    data = load("valid", "match-job-with-workflow")
    data["segmentWindow"] = {"segmentId": "seg-1", "startMs": 0, "endMs": 10}
    with pytest.raises(ValidationError):
        LogicalJob.model_validate(data)


def test_segment_job_requires_segment_and_match_job_forbids_it():
    segment_job = load("valid", "segment-job")
    del segment_job["segment"]
    with pytest.raises(ValidationError):
        LogicalJob.model_validate(segment_job)
    match_job = load("valid", "match-job-with-workflow")
    match_job["segment"] = load("valid", "segment-job")["segment"]
    with pytest.raises(ValidationError):
        LogicalJob.model_validate(match_job)


def test_segment_window_bounds_and_unknown_property():
    base = load("valid", "segment-job-with-window")
    for window in (
        {"segmentId": "s", "startMs": -1, "endMs": 5},
        {"segmentId": "s", "startMs": 0, "endMs": 0},
        {"segmentId": "s", "startMs": 0, "endMs": 5, "extra": 1},
        {"segmentId": "s", "startMs": 0},
    ):
        data = copy.deepcopy(base)
        data["segmentWindow"] = window
        with pytest.raises(ValidationError):
            LogicalJob.model_validate(data)


def test_input_status_rules():
    base = load("valid", "match-job-with-workflow")

    def with_input(**changes):
        data = copy.deepcopy(base)
        data["inputs"][0].update(changes)
        return data

    with pytest.raises(ValidationError):
        LogicalJob.model_validate(with_input(acceptedResults=[]))
    with pytest.raises(ValidationError):
        LogicalJob.model_validate(with_input(edgeKind="required", status="absent", acceptedResults=[]))
    with pytest.raises(ValidationError):
        LogicalJob.model_validate(with_input(edgeKind="optional", status="absent"))
    ok = LogicalJob.model_validate(with_input(edgeKind="optional", status="absent", acceptedResults=[]))
    assert ok.inputs[0].status == "absent"
    with pytest.raises(ValidationError):
        LogicalJob.model_validate(with_input(status="unknown"))


def test_workflow_and_resource_limits():
    base = load("valid", "match-job-with-workflow")
    for path, value in (
        (("workflow", "version"), "1.0"),
        (("resources", "cpuMillicores"), 0),
        (("attemptBudget", "maxAttempts"), 0),
        (("timeouts", "leaseSeconds"), 0),
        (("createdAt",), "2026-10-05T13:05:00"),
    ):
        data = copy.deepcopy(base)
        target = data
        for key in path[:-1]:
            target = target[key]
        target[path[-1]] = value
        with pytest.raises(ValidationError):
            LogicalJob.model_validate(data)
