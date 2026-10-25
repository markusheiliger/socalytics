import json
import tomllib
from pathlib import Path

import pytest

PROJECT_ROOT = Path(__file__).resolve().parent.parent
LOCK = PROJECT_ROOT / "uv.lock"
AUDIT = PROJECT_ROOT / "audit" / "dependency-audit.json"
SBOM = PROJECT_ROOT / "audit" / "sbom.cdx.json"

SCOPES = {"runtime", "dev", "bundled-library", "toolchain"}
PERMISSIVE = {
    "MIT", "ISC", "0BSD", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0", "PSF-2.0", "Zlib", "CC0-1.0",
    "Libpng", "OpenSSL",
}
LGPL = {"LGPL-2.1-only", "LGPL-2.1-or-later", "LGPL-3.0-only", "LGPL-3.0-or-later"}
GCC_EXCEPTION = "GPL-3.0-with-GCC-exception"
# A development-only tool that is never shipped in the image; file-level copyleft stays out of the runtime.
DEV_ONLY = {"MPL-2.0"}
WEIGHT_SUFFIXES = {
    ".onnx", ".pt", ".pth", ".ckpt", ".safetensors", ".gguf", ".ggml", ".h5", ".hdf5", ".pb", ".tflite",
    ".npz", ".joblib", ".pkl", ".pickle",
}
IGNORED_DIRECTORIES = {".venv", ".git", "__pycache__", ".pytest_cache", "node_modules", "dist", "build"}


def _identifier_allowed(identifier: str, record: dict, names: set[str]) -> bool:
    scope = record.get("scope")
    if identifier in PERMISSIVE:
        return True
    if identifier in LGPL:
        return scope in ("bundled-library", "toolchain")
    if identifier == GCC_EXCEPTION:
        return (
            scope == "bundled-library"
            and record.get("name", "").startswith("gcc-")
            and record.get("parent") in names
        )
    if identifier in DEV_ONLY:
        return scope == "dev"
    return False


def license_problems(records: list[dict]) -> list[str]:
    """Return a problem per record whose SPDX expression is not covered by the allowed set (AND needs all, OR any)."""
    names = {record["name"] for record in records}
    problems = []
    for record in records:
        expression = record.get("license")
        label = f"{record.get('name')} {record.get('version')}"
        if not isinstance(expression, str) or not expression.strip():
            problems.append(f"{label}: license is missing")
            continue
        if record.get("scope") not in SCOPES:
            problems.append(f"{label}: scope {record.get('scope')!r} is unknown")
            continue
        allowed = all(
            any(_identifier_allowed(alternative.strip(), record, names) for alternative in part.split(" OR "))
            for part in expression.split(" AND ")
        )
        if not allowed:
            problems.append(f"{label}: license {expression!r} is not allowed for scope {record['scope']}")
    return problems


def coverage_problems(locked: list[dict], records: list[dict]) -> list[str]:
    """Return a problem per locked package without a record of the same name and version."""
    problems = []
    for package in locked:
        matches = [record for record in records if record.get("name") == package["name"]]
        if not matches:
            problems.append(f"{package['name']}: no audit record")
        elif not any(record.get("version") == package["version"] for record in matches):
            problems.append(
                f"{package['name']}: audit records {[record.get('version') for record in matches]}, "
                f"lock has {package['version']}"
            )
    return problems


def sbom_problems(records: list[dict], sbom: dict) -> list[str]:
    problems = []
    components = [c for c in sbom.get("components", []) if c.get("type") != "container"]
    expected = sorted(
        (r["name"], r["version"], r["license"], r["scope"], r.get("parent", "")) for r in records
    )
    actual = sorted(
        (
            c["name"],
            c["version"],
            c["licenses"][0]["expression"],
            next(p["value"] for p in c["properties"] if p["name"] == "socalytics:audit-scope"),
            next((p["value"] for p in c["properties"] if p["name"] == "socalytics:parent-wheel"), ""),
        )
        for c in components
    )
    if expected != actual:
        problems.append("the SBOM components differ from the audit records")
    return problems


def _lock_packages() -> list[dict]:
    return tomllib.loads(LOCK.read_text(encoding="utf-8"))["package"]


def _audit() -> list[dict]:
    return json.loads(AUDIT.read_text(encoding="utf-8"))


def test_every_locked_package_has_a_record_with_the_locked_version():
    assert coverage_problems(_lock_packages(), _audit()) == []


def test_audit_records_are_complete_and_unique():
    records = _audit()
    assert records
    for record in records:
        assert {"name", "version", "license", "scope", "source"} <= record.keys()
        assert record["scope"] in SCOPES
        assert all(isinstance(record[key], str) and record[key] for key in ("name", "version", "license", "source"))
    keys = [(r["name"], r["scope"], r.get("parent", "")) for r in records]
    assert len(keys) == len(set(keys))


def test_toolchain_records_cover_the_pinned_ffmpeg_build():
    toolchain = {record["name"] for record in _audit() if record["scope"] == "toolchain"}
    assert {"ffmpeg", "ffprobe"} <= toolchain


def test_bundled_libraries_name_a_runtime_parent_wheel():
    records = _audit()
    runtime = {record["name"] for record in records if record["scope"] == "runtime"}
    bundled = [record for record in records if record["scope"] == "bundled-library"]
    assert bundled
    for record in bundled:
        assert record.get("parent") in runtime, record["name"]


def test_every_license_is_in_the_allowed_set():
    assert license_problems(_audit()) == []


def test_gcc_runtime_exception_is_limited_to_bundled_gcc_libraries():
    gcc = [record for record in _audit() if record["license"] == GCC_EXCEPTION]
    assert gcc
    assert all(r["scope"] == "bundled-library" and r["name"].startswith("gcc-") and r["parent"] for r in gcc)


def test_sbom_matches_the_audit():
    sbom = json.loads(SBOM.read_text(encoding="utf-8"))
    assert sbom["bomFormat"] == "CycloneDX"
    assert sbom["specVersion"] == "1.5"
    assert sbom_problems(_audit(), sbom) == []
    containers = [c for c in sbom["components"] if c["type"] == "container"]
    assert len(containers) == 1
    assert "@sha256:" in containers[0]["bom-ref"]


def test_no_model_weight_file_exists_in_the_project():
    found = [
        path.relative_to(PROJECT_ROOT).as_posix()
        for path in PROJECT_ROOT.rglob("*")
        if path.is_file()
        and path.suffix.lower() in WEIGHT_SUFFIXES
        and not IGNORED_DIRECTORIES.intersection(path.relative_to(PROJECT_ROOT).parts)
    ]
    assert found == []


LOCKED = [{"name": "alpha", "version": "1.0.0"}, {"name": "beta", "version": "2.0.0"}]


def _record(name, version, license_expression="MIT", scope="runtime", **extra):
    return {"name": name, "version": version, "license": license_expression, "scope": scope, "source": "x", **extra}


def test_check_accepts_matching_synthetic_records():
    records = [_record("alpha", "1.0.0"), _record("beta", "2.0.0", "BSD-3-Clause OR MIT")]
    assert coverage_problems(LOCKED, records) == []
    assert license_problems(records) == []


def test_check_fails_for_a_missing_record():
    problems = coverage_problems(LOCKED, [_record("alpha", "1.0.0")])
    assert problems == ["beta: no audit record"]


def test_check_fails_for_a_differing_record():
    problems = coverage_problems(LOCKED, [_record("alpha", "1.0.0"), _record("beta", "2.0.1")])
    assert len(problems) == 1 and problems[0].startswith("beta:")


@pytest.mark.parametrize(
    "expression",
    ["GPL-2.0-only", "GPL-3.0-or-later", "AGPL-3.0-only", "AGPL-3.0-or-later", "LicenseRef-Unknown", "NOASSERTION", "", "MIT AND GPL-3.0-only"],
)
def test_check_fails_for_gpl_agpl_and_unknown_licenses(expression):
    assert license_problems([_record("alpha", "1.0.0", expression)])


def test_check_allows_an_alternative_but_not_a_missing_license():
    assert license_problems([_record("alpha", "1.0.0", "GPL-3.0-only OR MIT")]) == []
    record = _record("alpha", "1.0.0")
    del record["license"]
    assert license_problems([record])


def test_lgpl_is_limited_to_separately_packaged_libraries():
    lib = _record("libx", "1", "LGPL-2.1-or-later", "bundled-library", parent="alpha")
    assert license_problems([_record("alpha", "1.0.0"), lib]) == []
    assert license_problems([_record("alpha", "1.0.0", "LGPL-2.1-or-later")])


def test_gcc_exception_needs_a_bundled_gcc_library_with_a_parent_wheel():
    parent = _record("alpha", "1.0.0")
    ok = _record("gcc-libgfortran", "5", GCC_EXCEPTION, "bundled-library", parent="alpha")
    assert license_problems([parent, ok]) == []
    assert license_problems([parent, {**ok, "scope": "runtime"}])
    assert license_problems([parent, {k: v for k, v in ok.items() if k != "parent"}])
    assert license_problems([parent, {**ok, "parent": "missing"}])
    assert license_problems([parent, {**ok, "name": "other-lib"}])


def test_sbom_check_fails_for_a_differing_component():
    records = [_record("alpha", "1.0.0")]
    sbom = {
        "components": [
            {
                "type": "library",
                "name": "alpha",
                "version": "1.0.1",
                "licenses": [{"expression": "MIT"}],
                "properties": [{"name": "socalytics:audit-scope", "value": "runtime"}],
            }
        ]
    }
    assert sbom_problems(records, sbom)
    sbom["components"][0]["version"] = "1.0.0"
    assert sbom_problems(records, sbom) == []
