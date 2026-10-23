# Data Model: Computer-Vision and Inference Verification Environment

The model is file-based: one committed declaration and the files the environment writes in the install root.

## Pinned library declaration (`analyst-cv-libraries.json`)

| Field | Meaning and validation |
| --- | --- |
| `schemaVersion` | Integer `1`. |
| `supportedHost` | `os` `Linux` and `architecture` `x86_64` (same as the toolchain declaration). |
| `interpreter` | `python` `3.12` and the wheel tag `cp312`; wheel file names must be compatible with it. |
| `wheels[]` | Ordered closure. Each entry: `name` (normalized), `version`, `fileName`, `url`, `sha256` (64 lowercase hex), `publisher`, `projectUrl`, `licenseExpression`, `licenseText` (member path in the wheel), `role` (`library` or `dependency`). |
| `deniedPackages` | Normalized names and prefixes rejected in any project lock (`opencv-python`, `opencv-contrib-python`, `opencv-contrib-python-headless`, `onnxruntime-gpu`, `onnxruntime-*` other than `onnxruntime`, `nvidia-*`, `tensorrt*`, `cuda-*`, `triton`). |
| `licenseAllowList` | Fixed list of permissive SPDX identifiers. |
| `lgplLicenses`, `runtimeExceptionLicenses`, `deniedLicenseFamilies`, `deniedLibraries` | LGPL identifiers accepted only for distinct dynamic libraries; GPL-with-runtime-exception expressions (for example `GPL-3.0-or-later WITH GCC-exception-3.1`) accepted only for distinct dynamic libraries whose row records the licence name, exception name, and licence-text location, exactly like the LGPL rule; denied families (every other GPL or AGPL licence still fails); denied bundled codec name prefixes. |
| `licenseTable[]` | One row per wheel and per bundled shared library: `component`, `wheel`, `kind` (`wheel` or `library`), `licenseExpression`, `licenseText`, optional `linkage` (`dynamic`), and optional `version` (kind `library` only; the check reports a `table-gap` for a library row without it, so the SBOM records every bundled library's version). The check derives each bundled library's SHA-256 from the wheel member and takes its publisher from the entry of its `wheel`. |

Rules: the declaration is the only source of these versions; every wheel appears in `licenseTable`; every `url` ends
with its `fileName`; changing any value is a standalone environment change.

## Verified wheelhouse (install root)

`analyst-cv-wheels/<fileName>` for each wheel after its digest was verified, the generated hash-pinned
`requirements.txt`, and the smoke virtual environment `analyst-cv-venv/`. Downloads are removed after the check, like
the toolchain's `downloads/` folder; the smoke fixtures are deleted by their own script.

## Software bill of materials (`notices/analyst-cv/`)

`sbom.cdx.json` (CycloneDX 1.6): one `library` component per wheel with `purl`, SHA-256, supplier, licence, and
licence-text location; one nested component per bundled shared library with name, version (from its table row), SHA-256
(computed from the wheel member), supplier (its wheel's publisher), licence, and licence-text location; one component
for the pinned FFmpeg build.
Per wheel `PROVENANCE.txt` and the copied licence files.

## Library pin finding (verification)

Fields: project path, library, locked value (version, wheel file, digest, or package name), pinned value, kind
(`version`, `platform-tag`, `digest`, `source-distribution`, `missing-wheel`, `disallowed-package`). Activation: the
whole closure (including `role: dependency` wheels) is enforced only when the project's lock contains at least one
`role: library` wheel; `disallowed-package` applies to every project. Each finding is one
line and one recommended action; the first finding of a project stops its sequence.

## Verified capability project (existing concept)

Recognized by `src/analysts/capabilities/<name>/pyproject.toml`; state transitions: discovered, selected (by mode and
changed paths), pin-checked, synced, built, tested, covered. A deleted project leaves no covered path.
