"""Regenerate the dependency audit and SBOM evidence of the capability without network access.

Run from the capability folder with its locked environment installed::

    uv run --no-sync python scripts/generate_audit.py

``audit/dependency-audit.json`` lists every package of ``uv.lock`` (scope ``runtime`` or ``dev``), the shared
libraries bundled inside installed runtime wheels (scope ``bundled-library``, naming the parent wheel), and the
pinned FFmpeg build (scope ``toolchain``). ``audit/sbom.cdx.json`` is a CycloneDX 1.5 document of the same
components plus the base image reference by digest. Licenses come from installed distribution metadata; packages
whose metadata names no usable license, or that are not installed on this host, are listed in
``LICENSE_OVERRIDES``. ``--check`` reports stale files and exits non-zero; ``--output-dir`` writes elsewhere.
The output is deterministic (no timestamps).
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import tomllib
import uuid
from importlib import metadata
from pathlib import Path

CAPABILITY_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = CAPABILITY_ROOT.parents[3]
LOCK = CAPABILITY_ROOT / "uv.lock"
DOCKERFILE = CAPABILITY_ROOT / "Dockerfile"
TOOLCHAIN = REPOSITORY_ROOT / ".github" / "actions" / "environment-setup" / "analyst-media-toolchain.json"
AUDIT_DIR = CAPABILITY_ROOT / "audit"
AUDIT_NAME = "dependency-audit.json"
SBOM_NAME = "sbom.cdx.json"

OS_PACKAGES_STATEMENT = (
    "Operating-system packages of the base image are identified only by the base image digest; "
    "they are not enumerated here."
)

CLASSIFIER_LICENSES = {
    "License :: OSI Approved :: MIT License": "MIT",
    "License :: OSI Approved :: Apache Software License": "Apache-2.0",
    "License :: OSI Approved :: Mozilla Public License 2.0 (MPL 2.0)": "MPL-2.0",
}
FIELD_LICENSES = {"MIT": "MIT", "Apache 2.0": "Apache-2.0", "Apache-2.0": "Apache-2.0"}

LICENSE_OVERRIDES = {
    "colorama": "BSD-3-Clause",
    "trove-classifiers": "Apache-2.0",
    "socalytics-analyst-sdk": "MIT",
    "socalytics-media-context-classification": "MIT",
}

# Shared libraries bundled in wheels, by file name prefix: (name, SPDX license expression).
BUNDLED_LIBRARIES = {
    "libaom": ("libaom", "BSD-2-Clause"),
    "libavcodec": ("ffmpeg-libavcodec", "LGPL-2.1-or-later"),
    "libavformat": ("ffmpeg-libavformat", "LGPL-2.1-or-later"),
    "libavutil": ("ffmpeg-libavutil", "LGPL-2.1-or-later"),
    "libswresample": ("ffmpeg-libswresample", "LGPL-2.1-or-later"),
    "libswscale": ("ffmpeg-libswscale", "LGPL-2.1-or-later"),
    "libavif": ("libavif", "BSD-2-Clause"),
    "libcrypto": ("openssl-libcrypto", "OpenSSL"),
    "libssl": ("openssl-libssl", "OpenSSL"),
    "libdrm": ("libdrm", "MIT"),
    "libgfortran": ("gcc-libgfortran", "GPL-3.0-with-GCC-exception"),
    "libquadmath": ("gcc-libquadmath", "LGPL-2.1-or-later"),
    "libopenblasp": ("openblas", "BSD-3-Clause"),
    "libscipy_openblas64_": ("scipy-openblas", "BSD-3-Clause"),
    "libpng16": ("libpng", "Libpng"),
    "libvpx": ("libvpx", "BSD-3-Clause"),
}


def read_lock() -> list[dict]:
    return tomllib.loads(LOCK.read_text(encoding="utf-8"))["package"]


def project_package(packages: list[dict]) -> dict:
    return next(entry for entry in packages if entry["source"].get("editable") == ".")


def runtime_closure(packages: list[dict]) -> set[str]:
    by_name = {entry["name"]: entry for entry in packages}
    pending = [dependency["name"] for dependency in project_package(packages).get("dependencies", [])]
    seen: set[str] = set()
    while pending:
        name = pending.pop()
        if name not in seen:
            seen.add(name)
            pending.extend(dependency["name"] for dependency in by_name[name].get("dependencies", []))
    return seen


def lock_source(entry: dict) -> str:
    source = entry["source"]
    if "directory" in source:
        return f"directory:{source['directory']}"
    if "editable" in source:
        return f"editable:{source['editable']}"
    urls = sorted(wheel["url"] for wheel in entry.get("wheels", []))
    pure = [url for url in urls if url.endswith("py3-none-any.whl")]
    native = [url for url in urls if "manylinux" in url and "x86_64" in url]
    chosen = pure or native
    if chosen:
        return chosen[0]
    if "sdist" in entry:
        return entry["sdist"]["url"]
    raise SystemExit(f"{entry['name']}: no source URL in the lock")


def installed(name: str) -> metadata.Distribution | None:
    try:
        return metadata.distribution(name)
    except metadata.PackageNotFoundError:
        return None


def license_of(distribution: metadata.Distribution | None, name: str) -> str:
    if distribution is not None:
        meta = distribution.metadata
        expression = meta.get("License-Expression")
        if expression:
            return expression.strip()
        for classifier in meta.get_all("Classifier") or []:
            if classifier in CLASSIFIER_LICENSES:
                return CLASSIFIER_LICENSES[classifier]
        field = (meta.get("License") or "").strip()
        if field in FIELD_LICENSES:
            return FIELD_LICENSES[field]
    if name in LICENSE_OVERRIDES:
        return LICENSE_OVERRIDES[name]
    raise SystemExit(f"{name}: no usable license in the installed metadata and no override")


def library_version(file_name: str) -> str:
    openblas = re.search(r"\.(\d+\.\d+)\.so$", file_name)
    if file_name.startswith("libopenblasp") and openblas:
        return f"0.{openblas.group(1)}"
    match = re.search(r"\.so\.(.+)$", file_name)
    return match.group(1) if match else "unversioned"


def bundled_libraries(distribution: metadata.Distribution, parent: str) -> list[dict]:
    records: dict[str, dict] = {}
    for file in distribution.files or []:
        if len(file.parts) < 2 or not file.parts[0].endswith(".libs"):
            continue
        file_name = file.parts[-1]
        prefix = next(
            (prefix for prefix in sorted(BUNDLED_LIBRARIES, key=len, reverse=True) if file_name.startswith(prefix)),
            None,
        )
        if prefix is None:
            raise SystemExit(f"{parent}: bundled library {file_name} has no license mapping")
        name, license_expression = BUNDLED_LIBRARIES[prefix]
        records[name] = {
            "name": name,
            "version": library_version(file_name),
            "license": license_expression,
            "scope": "bundled-library",
            "source": f"bundled in the {parent} wheel as {file_name}",
            "parent": parent,
        }
    return list(records.values())


def toolchain_records() -> list[dict]:
    tools = json.loads(TOOLCHAIN.read_text(encoding="utf-8"))["tools"]
    ffmpeg = next(tool for tool in tools if tool["id"] == "ffmpeg")
    dockerfile = DOCKERFILE.read_text(encoding="utf-8")
    url = re.search(r"^ARG FFMPEG_URL=(\S+)", dockerfile, re.MULTILINE).group(1)
    digest = re.search(r"^ARG FFMPEG_SHA256=(\S+)", dockerfile, re.MULTILINE).group(1)
    if url != ffmpeg["archive"]["url"] or digest != ffmpeg["archive"]["sha256"]:
        raise SystemExit("The Dockerfile FFmpeg pin differs from analyst-media-toolchain.json")
    return [
        {
            "name": name,
            "version": ffmpeg["version"],
            "license": "LGPL-2.1-or-later",
            "scope": "toolchain",
            "source": f"{url}#sha256={digest}",
        }
        for name in ("ffmpeg", "ffprobe")
    ]


def base_image() -> str:
    match = re.search(r"^FROM (\S+@sha256:[0-9a-f]{64})", DOCKERFILE.read_text(encoding="utf-8"), re.MULTILINE)
    if match is None:
        raise SystemExit("The Dockerfile base image is not pinned by digest")
    return match.group(1)


def build_audit() -> list[dict]:
    packages = read_lock()
    runtime = runtime_closure(packages)
    project = project_package(packages)["name"]
    records: list[dict] = []
    for entry in packages:
        name = entry["name"]
        distribution = installed(name)
        if distribution is not None and distribution.version != entry["version"]:
            raise SystemExit(f"{name}: installed {distribution.version} differs from locked {entry['version']}")
        scope = "runtime" if name in runtime or name == project else "dev"
        records.append({
            "name": name,
            "version": entry["version"],
            "license": license_of(distribution, name),
            "scope": scope,
            "source": lock_source(entry),
        })
        if distribution is not None and scope == "runtime":
            records.extend(bundled_libraries(distribution, name))
    records.extend(toolchain_records())
    order = {"runtime": 0, "dev": 1, "bundled-library": 2, "toolchain": 3}
    return sorted(records, key=lambda record: (order[record["scope"]], record["name"], record.get("parent", "")))


def purl(record: dict) -> str:
    kind = "pypi" if record["scope"] in ("runtime", "dev") else "generic"
    return f"pkg:{kind}/{record['name']}@{record['version']}"


def build_sbom(audit: list[dict], image: str) -> dict:
    components = []
    for record in audit:
        parent = record.get("parent")
        properties = [
            {"name": "socalytics:audit-scope", "value": record["scope"]},
            {"name": "socalytics:source", "value": record["source"]},
        ]
        if parent:
            properties.append({"name": "socalytics:parent-wheel", "value": parent})
        components.append({
            "type": "application" if record["scope"] == "toolchain" else "library",
            "bom-ref": f"{purl(record)}#{record['scope']}" + (f"#{parent}" if parent else ""),
            "name": record["name"],
            "version": record["version"],
            "purl": purl(record),
            "scope": "excluded" if record["scope"] == "dev" else "required",
            "licenses": [{"expression": record["license"]}],
            "properties": properties,
        })
    reference, digest = image.split("@", 1)
    components.append({
        "type": "container",
        "bom-ref": f"base-image:{image}",
        "name": reference,
        "version": digest,
        "description": OS_PACKAGES_STATEMENT,
        "hashes": [{"alg": "SHA-256", "content": digest.removeprefix("sha256:")}],
        "properties": [
            {"name": "socalytics:image-reference", "value": image},
            {"name": "socalytics:operating-system-packages", "value": "identified-only-by-image-digest"},
        ],
    })
    body = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "version": 1,
        "metadata": {
            "component": {
                "type": "application",
                "bom-ref": "capability:media-context-classification",
                "name": "media-context-classification",
                "version": "1.0.0",
                "description": "Capability layers: Python environment, FFmpeg build, and base image by digest.",
            },
            "properties": [{"name": "socalytics:statement", "value": OS_PACKAGES_STATEMENT}],
        },
        "components": components,
    }
    serial = uuid.uuid5(uuid.NAMESPACE_URL, json.dumps(body, sort_keys=True))
    return {"bomFormat": body.pop("bomFormat"), "specVersion": body.pop("specVersion"),
            "serialNumber": f"urn:uuid:{serial}", **body}


def render(document: object) -> str:
    return json.dumps(document, indent=2, ensure_ascii=True) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="report stale files instead of writing")
    parser.add_argument("--output-dir", type=Path, default=AUDIT_DIR)
    arguments = parser.parse_args()

    audit = build_audit()
    outputs = {AUDIT_NAME: render(audit), SBOM_NAME: render(build_sbom(audit, base_image()))}
    if arguments.check:
        stale = [
            name for name, text in outputs.items()
            if not (arguments.output_dir / name).is_file()
            or (arguments.output_dir / name).read_text(encoding="utf-8") != text
        ]
        for name in stale:
            print(f"stale: {name}; run: uv run --no-sync python scripts/generate_audit.py", file=sys.stderr)
        return 1 if stale else 0

    arguments.output_dir.mkdir(parents=True, exist_ok=True)
    for name, text in outputs.items():
        (arguments.output_dir / name).write_text(text, encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
