import tomllib
from pathlib import Path

import pytest

PROJECT_ROOT = Path(__file__).resolve().parents[1]


@pytest.fixture(scope="module")
def pyproject() -> dict:
    with (PROJECT_ROOT / "pyproject.toml").open("rb") as handle:
        return tomllib.load(handle)


def test_lock_file_is_committed() -> None:
    assert (PROJECT_ROOT / "uv.lock").is_file()


def test_requires_python_is_pinned_to_3_12(pyproject: dict) -> None:
    assert pyproject["project"]["requires-python"] == ">=3.12,<3.13"


def test_runtime_dependencies(pyproject: dict) -> None:
    dependencies = pyproject["project"]["dependencies"]
    assert "socalytics-analyst-sdk" in dependencies
    assert "jsonschema" in dependencies


def test_sdk_source_path(pyproject: dict) -> None:
    sources = pyproject["tool"]["uv"]["sources"]
    assert sources["socalytics-analyst-sdk"]["path"] == "../../sdk"


def test_dev_group(pyproject: dict) -> None:
    dev = pyproject["dependency-groups"]["dev"]
    for name in ("hatchling", "pytest", "socalytics-reference-media-probe"):
        assert name in dev


def test_hatchling_package(pyproject: dict) -> None:
    assert pyproject["build-system"]["build-backend"] == "hatchling.build"
    wheel = pyproject["tool"]["hatch"]["build"]["targets"]["wheel"]
    assert wheel["packages"] == ["src/analyst_evaluation_harness"]
    package = PROJECT_ROOT / "src" / "analyst_evaluation_harness"
    assert (package / "__init__.py").is_file()
