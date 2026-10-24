"""Loading of the Analyst's own manifest from `SOCALYTICS_ANALYST_MANIFEST` (FR-014)."""

from __future__ import annotations

from pathlib import Path

import pytest

from socalytics_analyst import context, errors

EXAMPLES = Path(__file__).resolve().parents[4] / "contracts" / "analysis" / "analyst-manifest" / "v1" / "examples"
VALID = EXAMPLES / "valid" / "segment-with-payload-schemas.json"


def test_manifest_is_loaded_from_the_environment_variable(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv(context.MANIFEST_ENVIRONMENT_VARIABLE, str(VALID))

    manifest = context.load_manifest()

    assert manifest is not None
    assert manifest.output.fact_payload_schemas


def test_missing_file_means_no_manifest(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setenv(context.MANIFEST_ENVIRONMENT_VARIABLE, str(tmp_path / "absent.json"))

    assert context.load_manifest() is None


def test_default_location_is_used_without_the_variable(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.delenv(context.MANIFEST_ENVIRONMENT_VARIABLE, raising=False)
    monkeypatch.setattr(context, "DEFAULT_MANIFEST_PATH", VALID)

    assert context.load_manifest() is not None


@pytest.mark.parametrize("content", ["{not json", "{}"])
def test_invalid_file_is_an_input_error(monkeypatch: pytest.MonkeyPatch, tmp_path: Path, content: str) -> None:
    path = tmp_path / "analyst-manifest.json"
    path.write_text(content, encoding="utf-8")
    monkeypatch.setenv(context.MANIFEST_ENVIRONMENT_VARIABLE, str(path))

    with pytest.raises(errors.InputError) as raised:
        context.load_manifest()

    assert raised.value.category == errors.INPUT_INVALID


def test_unreadable_file_is_an_input_error(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setenv(context.MANIFEST_ENVIRONMENT_VARIABLE, str(tmp_path))

    with pytest.raises(errors.InputError):
        context.load_manifest()
