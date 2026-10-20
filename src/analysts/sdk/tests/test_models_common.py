import pytest
from pydantic import ValidationError

from socalytics_analyst.models.common import (
    AcceptedResultLineage,
    AnalysisSegmentReference,
    AttemptNumber,
    CapabilityRef,
    ContractModel,
    CoverageEntry,
    Correlation,
    Digest,
    Identifier,
    Milliseconds,
    ObjectName,
    SemanticVersion,
    ContractVersionV1,
    Timestamp,
)
from pydantic import TypeAdapter

DIGEST = "sha-256:" + "a" * 64


def accepts(annotated, value):
    TypeAdapter(annotated).validate_python(value)


def rejects(annotated, value):
    with pytest.raises(ValidationError):
        TypeAdapter(annotated).validate_python(value)


def test_scalar_constraints():
    accepts(Identifier, "A1._-x")
    rejects(Identifier, "-a")
    rejects(Identifier, "a" * 129)
    accepts(ContractVersionV1, "1.1.0")
    rejects(ContractVersionV1, "2.0.0")
    rejects(SemanticVersion, "01.0.0")
    accepts(Timestamp, "2026-01-01T00:00:00Z")
    rejects(Timestamp, "2026-01-01T00:00:00+00:00")
    accepts(Digest, DIGEST)
    rejects(Digest, "sha-256:ABC")
    rejects(AttemptNumber, 0)
    rejects(Milliseconds, -1)
    accepts(ObjectName, "facts.json")
    rejects(ObjectName, "A.json")


def test_models_are_closed_frozen_and_camel_case():
    ref = CapabilityRef.model_validate({"id": "tracker", "version": "1.0.0"})
    assert ref.model_dump(by_alias=True) == {"id": "tracker", "version": "1.0.0"}
    with pytest.raises(ValidationError):
        CapabilityRef.model_validate({"id": "tracker", "version": "1.0.0", "extra": 1})
    with pytest.raises(ValidationError):
        ref.id = "other"

    corr = Correlation.model_validate({"correlationId": "c1"})
    assert Correlation(correlation_id="c1") == corr
    assert issubclass(Correlation, ContractModel)


def test_segment_reference_and_lineage():
    AnalysisSegmentReference.model_validate(
        {
            "recordingSetVersionId": "rs1",
            "recordingVersionId": "rv1",
            "timelineMappingDigest": DIGEST,
            "segmentationPolicyDigest": DIGEST,
            "segmentNumber": 1,
        }
    )
    lineage = {
        "acceptedResultId": "r1",
        "logicalJobId": "j1",
        "capability": {"id": "tracker", "version": "1.0.0"},
        "schemaVersion": "1.0.0",
        "manifestReference": "results/r1/manifest.json",
        "manifestChecksum": DIGEST,
    }
    AcceptedResultLineage.model_validate(lineage)
    with pytest.raises(ValidationError):
        AcceptedResultLineage.model_validate({**lineage, "manifestReference": "https://x/y"})


@pytest.mark.parametrize("state", ["accepted", "missing", "failed", "blocked", "skipped"])
def test_coverage_entry_states(state):
    assert CoverageEntry.model_validate({"segmentId": "s1", "state": state}).state == state


def test_coverage_entry_requires_exactly_one_subject():
    CoverageEntry.model_validate({"match": True, "state": "accepted"})
    for bad in (
        {"state": "accepted"},
        {"segmentId": "s1", "match": True, "state": "accepted"},
        {"match": False, "state": "accepted"},
        {"segmentId": "s1", "state": "unknown"},
    ):
        with pytest.raises(ValidationError):
            CoverageEntry.model_validate(bad)
