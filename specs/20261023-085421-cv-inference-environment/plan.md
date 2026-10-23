# Implementation Plan: Computer-Vision and Inference Verification Environment

**Branch**: `20261023-085421-cv-inference-environment` | **Date**: 2026-10-23 |
**Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261023-085421-cv-inference-environment/spec.md`

## Summary

An environment-only change that extends the two composite actions. `environment-setup` gains one committed
declaration, `analyst-cv-libraries.json`, that pins six CPU-only binary wheels (headless OpenCV, NumPy, ONNX Runtime
CPU, and the three wheels ONNX Runtime needs), the fixed SPDX allow-list, the disallowed-package list, and the curated
licence table. A new installer step downloads the wheels into a verified wheelhouse, runs a licence and
software-bill-of-materials check (CycloneDX JSON written beside the toolchain notices), and runs a synthetic smoke
check in an isolated virtual environment built offline from the wheelhouse. `environment-verify` gains a library-pin
stage that runs before the existing locked sync sequence of every Analyst Python project: it reads the project's
`uv.lock` and, when the lock contains at least one `role: library` wheel (headless OpenCV, NumPy, or ONNX Runtime),
fails on a pinned library whose locked version, platform tag, or digest differs from the pin and on a
source-distribution entry; on every project it fails on a disallowed package. The existing discovery, selection, covered-path, and
sync/build/pytest behavior for `src/analysts/capabilities/<name>/` is kept and gains fixture-based evidence for absent,
present, deleted, and several projects. Nothing outside the two action folders and this spec's `tasks.md` changes.

## Technical Context

**Language/Version**: Bash and `jq` (installer, as in `install-analyst-media.sh`), Python 3.12 standard library only
(licence check, smoke check, pin check; the pinned CPython 3.12.15 of the existing toolchain, `python3` in tests),
GitHub composite-action YAML, Node.js `node:test` (environment-local tests)

**Primary Dependencies**: Existing pinned CPython, uv 0.13.1, FFmpeg/ffprobe n8.1.3 LGPL build; wheels (provisional,
see [research.md](research.md)): `opencv-python-headless` 5.0.0.93, `numpy` 2.5.4, `onnxruntime` 1.31.0,
`flatbuffers` 25.12.19, `packaging` 26.3, `protobuf` 7.36.2. No new Actions, packages, or cache infrastructure.

**Storage**: Files under `$RUNNER_TEMP/analyst-media-tools/` (wheelhouse, smoke virtual environment, notices, SBOM); the
committed declaration. No database.

**Testing**: `node --test` files in the two action folders (existing `toolchain.test.mjs` and
`analyst-projects.test.mjs`, extended, plus new `cv-libraries.test.mjs`), using fixture wheels built in a temporary
directory and stubbed `curl`, `uv`, and `python` commands; no network and no product project.

**Target Platform**: Linux x64 GitHub runner with the pinned CPython 3.12 (`manylinux` x86_64 wheels only)

**Project Type**: Build-environment tooling (two composite actions); not a product component

**Performance Goals**: Setup adds one bounded download of about 100 MB of wheels and a smoke check of a few seconds;
every external command has a timeout and bounded reported output (the existing `smoke_run` convention).

**Constraints**: Confined to `.github/actions/environment-setup/` and `.github/actions/environment-verify/` (FR-001);
no change of action inputs or outputs; tools-disabled mode downloads nothing; no GPU, CUDA, TensorRT, model weights, or
datasets; existing environment tests pass unchanged apart from assertions this change extends.

**Scale/Scope**: One new declaration, two new scripts in setup (installer, licence/SBOM check), one in verify (pin
check) plus a smoke script, one new stage in `python_project`, three test files touched or added, two READMEs.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Assessment |
| --- | --- |
| I. Architecture is the design authority | Pass. The change implements the architecture's CPU parts of the Analyst stack and the licence and provenance audit of Model Governance; see Architecture Updates. |
| II. Source-area ownership | Pass. No `src/` change; no new first-level child. |
| III. API-first control plane | Not applicable (no API, no control-plane behavior). |
| IV. Evidence over claims | Pass. Environment-local fixture tests cover every failure kind of FR-013; the quickstart states how they run. Nothing is presented as product or production readiness. |
| V. Focused, minimal changes | Pass. One declaration, no new cache infrastructure, no change of existing tool provisioning. |
| Environment constraint | Pass. This is the standalone environment spec; it changes only the two action folders. Dependent capability specs name it under Dependencies. |

**Post-design re-check**: Pass. The design adds no product dependency, keeps the six pins in one declaration, and
adds no file outside the two folders.

## Project Structure

### Documentation (this feature)

```text
specs/20261023-085421-cv-inference-environment/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── environment-interface.md
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
.github/actions/
├── environment-setup/
│   ├── action.yml                       # + one step "Install the Analyst computer-vision libraries"
│   ├── analyst-cv-libraries.json        # new: pins, allow-list, disallowed packages, licence table
│   ├── install-analyst-cv.sh            # new: wheelhouse download, digest check, check and smoke orchestration
│   ├── analyst-cv-licenses.py           # new: licence evidence cross-check and CycloneDX SBOM
│   ├── analyst-cv-smoke.py              # new: synthetic decode, NumPy, and ONNX Runtime check
│   ├── cv-libraries.test.mjs            # new: installer, licence, SBOM, and smoke evidence
│   ├── toolchain.test.mjs               # extended only where the interface assertions need it
│   └── README.md                        # pinned libraries, integrity, licence rule, exclusions, actions
└── environment-verify/
    ├── action.yml                       # + library-pin stage, test list, nothing else
    ├── analyst-library-pins.py          # new: uv.lock against the pinned declaration
    ├── analyst-projects.test.mjs        # extended: capability projects, coverage, pin stage
    └── README.md                        # capability check, pin stage, coverage, corrective actions
```

**Structure Decision**: The declaration lives in `environment-setup` beside `analyst-media-toolchain.json`, which stays
untouched so its existing tests pass unchanged; `environment-verify` reads it through the existing relative path
convention (`${{ github.action_path }}/../environment-setup/`). The discovery helper `analyst-projects.py` is not
changed unless a test proves a gap, because it already recognizes `src/analysts/capabilities/<name>/pyproject.toml` and
adds only existing project roots to `COVERED`.

## Architecture Updates

None as a design change. The feature implements what the architecture already names (the OpenCV, NumPy, and ONNX
Runtime CPU stack in
[Analyst Technology](../../docs/architecture/platform-implementation.md#analyst-technology) and the licence and
provenance audit in
[Model Governance](../../docs/architecture/analysts-models-and-hardware.md#model-governance)) and applies no design
update. FR-001 forbids documentation changes outside the two action folders, whose READMEs carry the behavior, so the
documentation task is limited to those READMEs. No statement anywhere in `docs/architecture/` claims that this
verification environment is planned or missing; the statements that name the libraries describe Analyst product
design, which this build-environment tooling does not change.

## Complexity Tracking

No constitution violations to justify.
