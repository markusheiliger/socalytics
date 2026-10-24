import pytest
from pydantic import ValidationError

import socalytics_analyst.models as models
from socalytics_analyst.models.upstream import StagedCoverage

VALID = {
    "upstream": {
        "capability": {"id": "ball-tracking", "version": "1.0.0"},
        "schemaVersion": "1.0.0",
        "edgeKind": "optional",
        "coverage": {"mode": "partial", "minimumAcceptedFraction": 0.5},
    },
    "coverage": [{"segmentId": "s1", "state": "accepted"}, {"match": True, "state": "missing"}],
}


def test_valid_staged_coverage_round_trips():
    staged = StagedCoverage.model_validate(VALID)
    assert staged.upstream.coverage.minimum_accepted_fraction == 0.5
    assert staged.model_dump(by_alias=True, exclude_none=True) == VALID


def test_coverage_policy_is_optional():
    data = {**VALID, "upstream": {k: v for k, v in VALID["upstream"].items() if k != "coverage"}}
    assert StagedCoverage.model_validate(data).upstream.coverage is None


@pytest.mark.parametrize("fraction", [0, -0.1, 1.1])
def test_minimum_accepted_fraction_bounds(fraction):
    data = {**VALID, "upstream": {**VALID["upstream"], "coverage": {"mode": "partial", "minimumAcceptedFraction": fraction}}}
    with pytest.raises(ValidationError):
        StagedCoverage.model_validate(data)


def test_fraction_one_is_accepted():
    data = {**VALID, "upstream": {**VALID["upstream"], "coverage": {"mode": "partial", "minimumAcceptedFraction": 1}}}
    StagedCoverage.model_validate(data)


@pytest.mark.parametrize(
    "bad",
    [
        {"upstream": {**VALID["upstream"], "edgeKind": "other"}},
        {"upstream": {**VALID["upstream"], "coverage": {"mode": "some"}}},
        {"coverage": [{"state": "accepted"}]},
        {"coverage": [{"segmentId": "s1", "state": "unknown"}]},
        {"extra": 1},
    ],
)
def test_invalid_staged_coverage(bad):
    with pytest.raises(ValidationError):
        StagedCoverage.model_validate({**VALID, **bad})


def test_package_exports_every_model():
    for name in ("LogicalJob", "AttemptContext", "ResultManifest", "FactArtifact", "StagedCoverage", "CoverageEntry"):
        assert name in models.__all__
        assert hasattr(models, name)
    assert all(hasattr(models, n) for n in models.__all__)
