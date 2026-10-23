# Research: Computer-Vision and Inference Verification Environment

All Technical Context unknowns are resolved here. Versions and digests were read from the PyPI JSON API on
2026-10-23 and are provisional until the implementation task re-reads and commits them.

## D1: Library set and exact pins

- **Decision**: Pin six `manylinux` x86_64 binary wheels for CPython 3.12:
  `opencv-python-headless` 5.0.0.93 (`cp37-abi3-manylinux_2_28_x86_64`), `numpy` 2.5.4
  (`cp312-cp312-manylinux_2_27_x86_64.manylinux_2_28_x86_64`), `onnxruntime` 1.31.0
  (`cp312-cp312-manylinux_2_28_x86_64`), `flatbuffers` 25.12.19 (`py2.py3-none-any`), `packaging` 26.3
  (`py3-none-any`), and `protobuf` 7.36.2 (`cp310-abi3-manylinux2014_x86_64`). Each entry carries file name, URL,
  SHA-256, publisher (`pypi.org`), and project page.
- **Rationale**: These are the complete runtime closure of the three named libraries for the supported interpreter
  (OpenCV needs NumPy 2; ONNX Runtime needs `flatbuffers`, `packaging`, `protobuf`, and NumPy). `sympy`,
  `ml_dtypes`, and `onnx` are optional extras and stay out.
- **Alternatives considered**: `opencv-python` (GUI variant, pulls GUI libraries; rejected by FR-004); building OpenCV
  from source (forbidden by FR-003); the `opencv-contrib` variants (extra non-free modules; out of scope).

## D2: How the pins reach the locked sync

- **Decision**: Setup downloads the six wheels into a verified wheelhouse for its own smoke check. A consuming
  project's locked sync is not redirected: `uv sync --locked` already installs only the wheels named by the project's
  `uv.lock` and verifies their lock digests. A new library-pin stage runs first and requires every pinned library in
  the lock to have exactly the pinned version and to list the pinned wheel file with the pinned SHA-256, so the locked
  sync installs the same bytes. The existing `--no-build` flags already forbid building a source distribution; the pin
  stage additionally fails a lock entry that offers only a source distribution for a pinned library.
- **Rationale**: Satisfies FR-007 (resolve from the pinned digests, fail naming library, locked value, pinned value)
  without cache seeding tricks that depend on uv's private cache layout, and keeps the committed locks unchanged.
- **Alternatives considered**: Pointing `UV_FIND_LINKS` or an offline cache at the wheelhouse (uv prefers the lock's
  registry URLs, so it would not prove the pin); a local index service (new infrastructure, forbidden by FR-005);
  rewriting project locks (forbidden by FR-008).

## D3: Smoke check without model weights or `onnx`

- **Decision**: Create an isolated virtual environment in the install root with the pinned CPython, install the six
  wheels from the wheelhouse with `uv pip install --offline --no-index --require-hashes --no-deps` from a generated
  hash-pinned requirements file, then run `analyst-cv-smoke.py` with the pinned `ffmpeg`. It writes a two-second
  64x64 MPEG-4 clip (the codec the existing media smoke check uses), decodes it with `cv2.VideoCapture`, computes
  per-frame means with NumPy, and runs a tiny ONNX graph (`Add` of two float inputs) that it serializes itself with a
  small protobuf byte writer, so no `onnx` package and no model file are needed. Each command is wrapped by `timeout`,
  output is truncated, and only the script's own temporary directory is deleted.
- **Rationale**: Matches FR-006 and keeps the existing `smoke_run` style. The OpenCV wheel bundles its own FFmpeg
  libraries, so decoding proves the wheel's codecs, while the pinned FFmpeg only creates the clip.
- **Alternatives considered**: Generating the clip with OpenCV's `VideoWriter` (depends on the bundled encoder, which
  can be absent in LGPL builds); the `onnx` package (extra wheel and licence surface).

## D4: Licence evidence and the curated table

- **Decision**: The declaration holds `licenseAllowList` (SPDX identifiers: `MIT`, `BSD-2-Clause`, `BSD-3-Clause`,
  `Apache-2.0`, `ISC`, `Zlib`, `PSF-2.0`, `MPL-2.0`, `0BSD`, `CC0-1.0`), a `lgplLicenses` list (`LGPL-2.1-only`,
  `LGPL-2.1-or-later`, `LGPL-3.0-only`, `LGPL-3.0-or-later`), a `deniedLicenseFamilies` list (`GPL`, `AGPL`, `SSPL`),
  a `deniedLibraries` list of GPL-only codec library name prefixes (`libx264`, `libx265`, `libxvid`, `libvidstab`,
  `libfdk`), and the `licenseTable`: one row per wheel and per bundled shared library with its SPDX expression,
  licence-text path inside the wheel, and, for LGPL libraries, `linkage: dynamic`. The check reads each wheel's
  shipped evidence (`METADATA` `License-Expression` or `License` field and `Classifier` lines, `*.dist-info/licenses/`
  or `LICENSE*` files, and the vendored `*.libs/` shared objects) and fails on any mismatch with the table, a wheel or
  bundled library with no row, a row with no shipped evidence, an expression with an identifier outside the allow-list
  (an `OR` expression passes when at least one alternative is allowed; an `AND` expression needs every term allowed),
  or a bundled library on `deniedLibraries`. The bundled-library inventory comes from listing the wheel archive, so a
  library cannot be left out of the table silently. The table rows for the OpenCV and NumPy bundled libraries are
  filled in during implementation from the downloaded wheels and reviewed with the pins.
- **LGPL rule**: A bundled library under LGPL passes only when the table row declares `linkage: dynamic`, the archive
  member is a distinct ELF shared object (type `ET_DYN` with a `DT_SONAME`), and at least one other ELF in the same
  wheel lists that SONAME in `DT_NEEDED`; the check parses ELF dynamic sections with `struct`. A library that
  is absent as a separate file, has no SONAME, or is not needed by another binary fails as statically linked or not
  distinct.
- **Rationale**: Implements the three clarifications exactly; derives the library inventory from the wheel rather than
  the table, so gaps are detectable.
- **Alternatives considered**: Trusting only wheel metadata (many bundled libraries have none); trusting only a table
  (unverifiable); a third-party scanner (new tool and download).

## D5: Software bill of materials format and location

- **Decision**: CycloneDX 1.6 JSON written by `analyst-cv-licenses.py` as `notices/analyst-cv/sbom.cdx.json` in the
  install root. Components: each wheel (`purl` `pkg:pypi/<name>@<version>`, SHA-256 hash, supplier `pypi.org`,
  licence, licence-text location), each bundled library (nested component with its licence), and the pinned FFmpeg
  build from the toolchain declaration as a separate component, so both FFmpeg builds are recorded and neither
  replaces the other. Licence files and a `PROVENANCE.txt` per wheel are copied next to the existing notices.
- **Rationale**: CycloneDX is a standard, JSON-only exchange format that a standard-library script can emit and
  validate structurally; the check also verifies that every wheel and bundled library appears in it.
- **Alternatives considered**: SPDX 2.3 (heavier relationship model, no gain here).

## D6: Where the checks run and how failures are reported

- **Decision**: Setup performs download, digest, compatibility (file name tags against the declared interpreter tag
  and host), licence/SBOM, and smoke checks in the stages `cv download`, `cv integrity`, `cv compatibility`,
  `cv licenses`, `cv install`, and `cv smoke`. Failures print
  `ERROR: Analyst CV libraries: library=<name> stage=<stage>: <problem>` and
  `Recommended action: <one action>`, the format of `install-analyst-media.sh`. Verification prints
  `project <path>: library pin: <problem>. Recommended action: <action>` and stops that project before the first
  sync stage, through the existing `Analyst Python stage failed` line so output and status handling are unchanged.
- **Rationale**: FR-011 and the existing conventions; the stage names let tests assert on them.

## D7: Capability project verification and coverage

- **Decision**: Keep the existing discovery, selection, and `COVERED` handling, which already verify every project
  under `src/analysts/capabilities/` once and add only existing project roots. Extend the tests with fixtures that
  cover no, one, several, and a deleted capability project in task, phase, and finalize modes, and with the pin stage.
  The pin stage applies to every discovered Analyst Python project, because the disallowed packages must also be
  rejected in the SDK.
- **Rationale**: FR-008 and FR-009 describe behavior the existing helper implements; changing it would risk the
  preserved behavior of FR-012.

## D8: Tools-disabled mode and cache boundaries

- **Decision**: The new step carries `if: inputs.install-tools == 'true'` like its siblings and runs after the
  existing toolchain step, whose `SOCALYTICS_PYTHON`, `uv`, and `ffmpeg` it needs. `cache-keys.mjs` is not changed and
  no cache entry is added.
- **Rationale**: FR-005.
