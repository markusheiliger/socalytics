# Tasks: Computer-Vision and Inference Verification Environment

**Input**: Design documents from `/specs/20261023-085421-cv-inference-environment/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/environment-interface.md](contracts/environment-interface.md), [quickstart.md](quickstart.md)

**Tests**: Required. FR-013 demands environment-local automated evidence for every failure kind, so test tasks are part
of each story. Tests use fixture wheels and stubbed `curl`, `uv`, and `python` commands (no network, no product
project).

**Organization**: Tasks are grouped by user story. This is an environment-only spec: every task changes only files in
`.github/actions/environment-setup/` or `.github/actions/environment-verify/` (FR-001); the only other change is the
completion markers of this file. Do not change anything under `src/`, `docs/`, `contracts/`, or other `specs/` folders.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files are
  disjoint and none depends on a sibling
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Paths are relative to the repository root. `SETUP` means `.github/actions/environment-setup` and `VERIFY` means
  `.github/actions/environment-verify`.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Establish the single source of the pins.

- [x] T001 Re-read the six wheel entries (`opencv-python-headless` 5.0.0.93, `numpy` 2.5.4, `onnxruntime` 1.31.0, `flatbuffers` 25.12.19, `packaging` 26.3, `protobuf` 7.36.2) from the PyPI JSON API for the `cp312` `manylinux` x86_64 binary wheels and create `.github/actions/environment-setup/analyst-cv-libraries.json` per [data-model.md](data-model.md): `schemaVersion` 1, `supportedHost`, `interpreter` (`3.12`, `cp312`), the ordered `wheels[]` closure (`name`, `version`, `fileName`, `url` ending with `fileName`, 64-hex lowercase `sha256`, `publisher` `pypi.org`, `projectUrl`, `licenseExpression`, `licenseText`, `role`), `deniedPackages` (`opencv-python`, `opencv-contrib-python`, `opencv-contrib-python-headless`, `onnxruntime-gpu`, `onnxruntime-*` other than `onnxruntime`, `nvidia-*`, `tensorrt*`, `cuda-*`, `triton`), `licenseAllowList` (`MIT`, `BSD-2-Clause`, `BSD-3-Clause`, `Apache-2.0`, `ISC`, `Zlib`, `PSF-2.0`, `MPL-2.0`, `0BSD`, `CC0-1.0`), `lgplLicenses` (`LGPL-2.1-only`, `LGPL-2.1-or-later`, `LGPL-3.0-only`, `LGPL-3.0-or-later`), `runtimeExceptionLicenses` (`GPL-3.0-or-later WITH GCC-exception-3.1`, accepted only for a distinct dynamic `.so` like the LGPL rule), `deniedLicenseFamilies` (`GPL`, `AGPL`, `SSPL`), `deniedLibraries` (`libx264`, `libx265`, `libxvid`, `libvidstab`, `libfdk`), and an empty `licenseTable` that T013 fills (rows of kind `library` carry a `version`; see [data-model.md](data-model.md)); every digest MUST come from the live PyPI JSON API read, and the task fails (and the implementation stops for a person) when the index is unreachable, rather than copying values from [research.md](research.md); leave `analyst-media-toolchain.json` untouched

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Nothing blocks all stories beyond the declaration in T001; the shared test fixture helpers are the only
prerequisite of the story tests.

- [ ] T002 Add the shared fixture helpers to the new `.github/actions/environment-setup/cv-libraries.test.mjs`: a builder that writes fixture wheels (zip files with `METADATA`, `*.dist-info/licenses/`, and `*.libs/` members) into a temporary directory, a declaration generator that computes their SHA-256 values, stub `curl`, `uv`, `python3`, and `ffmpeg` commands on a temporary `PATH`, and a runner for `install-analyst-cv.sh` with `ANALYST_CV_LIBRARIES`, `ANALYST_MEDIA_INSTALL_ROOT`, `ANALYST_MEDIA_HOST_OS`, and `ANALYST_MEDIA_HOST_ARCH` set; follow the helper style of `.github/actions/environment-setup/toolchain.test.mjs` without editing it

**Checkpoint**: Declaration and fixture helpers exist; story work can begin.

---

## Phase 3: User Story 1 - Build and verify a capability that needs computer-vision libraries (Priority: P1) 🎯 MVP

**Goal**: Setup provisions the pinned, hash-verified CPU-only wheels beside the existing toolchain and proves them with
a synthetic smoke check; verification rejects a project lock that differs from the pins before the locked sync.

**Independent Test**: Run `install-analyst-cv.sh` against fixture wheels (passes, then each download, digest,
compatibility, version, and smoke failure) and `analyst-library-pins.py` against fixture `uv.lock` files (matching lock
passes; version, platform tag, digest, source-distribution, missing-wheel, and disallowed-package findings fail naming
library, locked value, and pinned value).

### Tests for User Story 1

#### Installer and smoke evidence

- [ ] T003 [US1] Extend `.github/actions/environment-setup/cv-libraries.test.mjs` with installer cases using stubbed `curl`: success asserting only the line prefix `Verified Analyst CV libraries:` (the full form is asserted in T011) with the stage order following the contract (`cv licenses` is inserted by T014; `cv install` and `cv smoke` are covered by T004); failures for a missing declaration, an unsupported host, a failed download, a digest mismatch, a wheel tag incompatible with `cp312` or the host, a source distribution, and a version differing from the declaration; each asserts the line `ERROR: Analyst CV libraries: library=<name|tool> stage=<stage>: <problem>` followed by one `Recommended action:` line and exit 1, and that no unpinned or source-built fallback is attempted
- [ ] T004 [US1] Extend `.github/actions/environment-setup/cv-libraries.test.mjs` with smoke and offline-install cases: the `cv install` stage uses `uv pip install --offline --no-index --require-hashes --no-deps` with a generated hash-pinned requirements file, the `cv smoke` stage passes with the line `Verified synthetic CV smoke check:`, fails (stage `cv smoke`, one recommended action) when the stubbed decode, NumPy, or ONNX Runtime step fails or times out, truncates reported output, and deletes only its own fixtures; also assert that downloads are removed after the check

#### Library pin evidence

- [ ] T005 [P] [US1] Extend `.github/actions/environment-verify/analyst-projects.test.mjs` with `analyst-library-pins.py` cases using fixture `uv.lock` files and the real declaration shape: a lock without pinned libraries passes, a lock with only `packaging` at another version passes (no `role: library` wheel), a lock with `opencv-python-headless` and a different `protobuf` fails, a lock with exactly the pinned versions, wheel files, and digests passes, and each of the kinds `version`, `platform-tag`, `digest`, `source-distribution`, `missing-wheel`, and `disallowed-package` (`opencv-python`, `onnxruntime-gpu`, `nvidia-*`, `tensorrt*`) fails with the line `project <root>: library pin: <library>: <kind>: locked <value>, pinned <value>. Recommended action: <action>`; assert the script never writes and that the first finding stops the project's sequence

### Implementation for User Story 1

#### Scripts

- [ ] T006 [P] [US1] Create `.github/actions/environment-setup/analyst-cv-smoke.py` (Python 3.12 standard library plus the installed wheels): with the pinned `ffmpeg` write a two-second 64x64 MPEG-4 clip into its own temporary directory, decode it with `cv2.VideoCapture`, compute per-frame means with NumPy, serialize and run a tiny ONNX `Add` graph of two float inputs with `onnxruntime` using a small protobuf byte writer (no `onnx` package, no model file), print `Verified synthetic CV smoke check: decoded <frames> frames, NumPy mean <value>, ONNX Runtime Add graph executed`, bound every external command with a timeout and truncated output, and delete only its own temporary directory
- [ ] T007 [P] [US1] Create `.github/actions/environment-verify/analyst-library-pins.py` (standard library only; `--declaration <file> --lock uv.lock --project <root>`): parse the project's `uv.lock`, enforce the whole pinned closure (including `role: dependency` wheels) only when the lock contains at least one `role: library` wheel (`opencv-python-headless`, `numpy`, `onnxruntime`), and then require each pinned library found in the lock to have exactly the pinned version, a pinned wheel file (platform tag), and the pinned SHA-256, report `source-distribution` when only an sdist is offered and `missing-wheel` when the pinned wheel is not listed, reject every package matching `deniedPackages` (normalized names and prefixes) with kind `disallowed-package` in every project regardless of the activation rule, print one finding line in the contract's format with one recommended action, exit 1 on the first finding, exit 0 otherwise, and never write

#### Installer

- [ ] T008 [US1] Create `.github/actions/environment-setup/install-analyst-cv.sh` in the style of `install-analyst-media.sh` (Bash and `jq`): read `ANALYST_CV_LIBRARIES` (default next to the script), `ANALYST_MEDIA_TOOLCHAIN`, `ANALYST_MEDIA_INSTALL_ROOT`, `ANALYST_MEDIA_HOST_OS`, `ANALYST_MEDIA_HOST_ARCH`, and `SOCALYTICS_PYTHON`; run the stages `prerequisites`, `declaration`, `host platform`, `cv download` (bounded `curl` into the downloads folder), `cv integrity` (SHA-256 of every wheel before it is made available), `cv compatibility` (file name tags against `cp312` and the host, binary wheels only, pinned versions), `cv install` (wheels copied to `analyst-cv-wheels/`, generated hash-pinned `requirements.txt`, isolated `analyst-cv-venv/` installed with `uv pip install --offline --no-index --require-hashes --no-deps`), and `cv smoke` (run `analyst-cv-smoke.py` in that venv under `timeout`); print the failure line `ERROR: Analyst CV libraries: library=<name|tool> stage=<stage>: <problem>` and one `Recommended action:` (broken environment: restore access or correct the pin through a standalone environment change, never bypass a check), remove downloads after the check, and change neither `GITHUB_PATH` nor `GITHUB_ENV`; the final success line `Verified Analyst CV libraries:` is completed by T014

#### Action wiring

- [ ] T009 [P] [US1] Add the step `Install the Analyst computer-vision libraries` to `.github/actions/environment-setup/action.yml` directly after `Install the Analyst Python and media toolchain`, with `if: inputs.install-tools == 'true'`, `shell: bash`, and `run: bash "$GITHUB_ACTION_PATH/install-analyst-cv.sh"`; add no input or output and no cache entry
- [ ] T010 [P] [US1] In `.github/actions/environment-verify/action.yml` add the stage `library pin` as the first entry of the `commands` array of `python_project` (command `python3 "$GITHUB_ACTION_PATH/analyst-library-pins.py" --declaration "$GITHUB_ACTION_PATH/../environment-setup/analyst-cv-libraries.json" --lock uv.lock --project "$root"`, run through the existing `SOCALYTICS_PYTHON`-aware step, recommendation distinguishing a consuming-project lock from a broken environment), and extend `ENVIRONMENT_TESTS` with `${{ github.action_path }}/../environment-setup/cv-libraries.test.mjs`; keep the existing failure line `Analyst Python stage failed: ...` and status handling

**Checkpoint**: User Story 1 is complete: pinned wheels are provisioned and smoke-checked, and project locks are
checked against the pins before the locked sync.

---

## Phase 4: User Story 2 - Check licences and the software bill of materials of the added wheels (Priority: P2)

**Goal**: A licence and SBOM check cross-checks shipped wheel evidence against the committed table, enforces the
allow-list and the LGPL rule, and writes a CycloneDX SBOM and provenance notices.

**Independent Test**: Run `analyst-cv-licenses.py` against fixture wheel sets: a conforming set passes and writes the
SBOM; each failure kind of the contract fails with one line naming the wheel or library.

### Tests for User Story 2

- [ ] T011 [US2] Extend `.github/actions/environment-setup/cv-libraries.test.mjs` with licence and SBOM cases using fixture wheels (including a minimal ELF writer for `ET_DYN` objects with `DT_SONAME` and `DT_NEEDED`): a conforming set passes and the CycloneDX 1.6 `sbom.cdx.json` lists every wheel (`purl`, SHA-256, supplier, licence, licence-text location), every nested bundled library (asserting version, SHA-256, publisher, licence, and licence-text location on every nested component), and the pinned FFmpeg build as a separate component; a `table-gap` failure case when a bundled library row lacks a `version`; one case per kind `denied-license`, `unknown-license`, `not-allow-listed`, `evidence-mismatch`, `table-gap`, `evidence-gap`, `denied-library`, `lgpl-not-distinct`, `lgpl-not-dynamic`, `not-in-sbom`, `missing-digest`; `OR` expressions pass with one allowed alternative and `AND` expressions need every term allowed; a case where an exception-licensed (`GPL-3.0-or-later WITH GCC-exception-3.1`) distinct dynamic `.so` passes and a case where the same licence on a statically linked or non-distinct file fails (kind `lgpl-not-distinct` or `lgpl-not-dynamic`); a plain GPL or AGPL library still fails `denied-license`; and the installer reports stage `cv licenses` and the success line `Verified Analyst CV libraries: <n> wheels, <m> bundled libraries, SBOM <path>`

### Implementation for User Story 2

#### Check and table

- [ ] T012 [P] [US2] Create `.github/actions/environment-setup/analyst-cv-licenses.py` (standard library only; `--declaration <file> --wheels <dir> --sbom <file> [--ffmpeg-toolchain <file>]`): derive the bundled-library inventory by listing each wheel archive (`*.libs/` shared objects), read shipped evidence (`METADATA` `License-Expression` or `License` and `Classifier` lines, `*.dist-info/licenses/` and `LICENSE*` files), cross-check it against `licenseTable`, apply the allow-list (an `OR` passes with one allowed alternative, an `AND` needs every term), the `deniedLicenseFamilies`, and `deniedLibraries`, accept LGPL, or a licence on `runtimeExceptionLicenses` (recorded with its licence name, exception name, and licence-text location), only for a row with `linkage: dynamic` whose member is a distinct ELF `ET_DYN` with a `DT_SONAME` that another ELF in the same wheel lists in `DT_NEEDED` (parsed with `struct`), verify every wheel has a `sha256`, compute each bundled library's SHA-256 from its wheel member and take its version from its `licenseTable` row (a `library` row without `version` is a `table-gap`) and its publisher from its wheel's entry, write the CycloneDX 1.6 SBOM (wheels, nested bundled libraries, and the pinned FFmpeg build from the toolchain declaration), copy licence files and a per-wheel `PROVENANCE.txt` into `notices/analyst-cv/` beside the SBOM, print one line per finding `<wheel|library>: <kind>: <problem>` with `kind` from the contract, and exit 1 on any finding
- [ ] T013 [US2] Fill the `licenseTable` of `.github/actions/environment-setup/analyst-cv-libraries.json` by downloading the six pinned wheels into a temporary directory (verifying each against the SHA-256 recorded by T001), listing their `*.libs/` members, and using the shipped evidence: one row per wheel and per bundled shared library in the OpenCV, NumPy, and ONNX Runtime wheels (`component`, `wheel`, `kind`, `licenseExpression`, `licenseText`, `version` for every `library` row, and `linkage: dynamic` for LGPL libraries), each reviewed against its shipped evidence, with no GPL, AGPL, or GPL-only codec library accepted; before the task is done, run `python3 .github/actions/environment-setup/analyst-cv-licenses.py --declaration .github/actions/environment-setup/analyst-cv-libraries.json --wheels <dir> --sbom <tmp>/sbom.cdx.json` against those real wheels and require exit 0 (fix the table or, if the script is wrong, T012's script in the action folders), then delete the temporary directory

#### Installer integration

- [ ] T014 [US2] Add the stage `cv licenses` to `.github/actions/environment-setup/install-analyst-cv.sh` between `cv compatibility` and `cv install`: run `analyst-cv-licenses.py` with the declaration, the verified wheels, `$ANALYST_MEDIA_INSTALL_ROOT/notices/analyst-cv/sbom.cdx.json`, and the toolchain declaration, turn its finding lines into the failure line with `library=<wheel|library>` and one `Recommended action` (broken environment: correct the pin or table through a standalone environment change, never bypass the check), and print `Verified Analyst CV libraries: <n> wheels, <m> bundled libraries, SBOM <path>` on success

**Checkpoint**: User Stories 1 and 2 both work independently; the SBOM and provenance notices sit beside the toolchain
notices.

---

## Phase 5: User Story 3 - Verify every capability project and keep coverage honest (Priority: P3)

**Goal**: Fixture evidence that every project under `src/analysts/capabilities/` is verified once per invocation by the
locked sequence (now starting with the pin stage), covered only while it exists, with the action interface preserved.

**Independent Test**: Run the selection and coverage fixtures with no, one, several, and a deleted capability project
in task, phase, and finalize modes.

### Tests for User Story 3

#### Capability selection, coverage, and interface

- [ ] T015 [P] [US3] Extend `.github/actions/environment-verify/analyst-projects.test.mjs` with fixture capability projects under `src/analysts/capabilities/<name>/` (none, one, several, deleted) in task, phase, and finalize modes: a change to one project checks that project and its dependents in task and phase mode and every project in finalize mode, each project runs the stages `library pin`, `bootstrap sync`, `project sync`, `wheel build`, and `tests` once per invocation, `COVERED` includes `^src/analysts/capabilities/<name>/` only while `pyproject.toml` exists, no other path under `src/analysts/` is covered, a failing stage reports project, stage, and one recommended action through the existing `Analyst Python stage failed` line and `status=1`, a documentation-only change triggers no extra check, and committed declarations stay unchanged
- [ ] T016 [P] [US3] Extend `.github/actions/environment-setup/toolchain.test.mjs` only where the interface assertions need it: the setup `action.yml` keeps its inputs and outputs, the new step exists after the toolchain step with `install-tools == 'true'`, tools-disabled mode downloads nothing, and `analyst-media-toolchain.json` and `cache-keys.mjs` are unchanged; every existing assertion passes unchanged

### Implementation for User Story 3

- [ ] T017 [US3] Run `node --test .github/actions/environment-verify/analyst-projects.test.mjs`; only if a T015 case proves a gap in selection or coverage, correct `.github/actions/environment-verify/analyst-projects.py` minimally and keep every existing case passing unchanged (FR-012)

**Checkpoint**: All three stories are independently functional.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Documentation (FR-014) and final validation.

### Documentation

- [ ] T018 [P] Update `.github/actions/environment-setup/README.md`: the six pinned libraries and where the single declaration lives, download, integrity, and retention behavior (wheelhouse, smoke venv, deleted downloads), the stages and failure line, the licence and SBOM rule and how it is verified (allow-list, LGPL distinct dynamic `.so` rule, evidence-versus-table cross-check, SBOM and notices location), the excluded GPU, CUDA, TensorRT, model, and dataset material, tools-disabled mode, the corrective actions, and the statement that the toolchain is build-environment tooling that approves no library for product use, redistribution, or production and sets no production value
- [ ] T019 [P] Update `.github/actions/environment-verify/README.md`: the `library pin` stage and its finding kinds, the capability project check for every project under `src/analysts/capabilities/` with the selection rules for task, phase, and finalize modes, the coverage rule (covered only while `pyproject.toml` exists), the extended environment test list, and the corrective actions that distinguish a broken environment from a failing consuming project

### Validation

- [ ] T020 Run `node --test .github/actions/environment-setup/*.test.mjs .github/actions/environment-verify/*.test.mjs` and fix any failure in the action folders without weakening a test; also run, read-only, `python3 .github/actions/environment-verify/analyst-library-pins.py --declaration .github/actions/environment-setup/analyst-cv-libraries.json --lock uv.lock --project <root>` in `src/analysts/sdk` and `src/analysts/capabilities/reference-media-probe`, requiring exit 0 and no file change, and fix the pin or the activation rule in the action folders if it fails
- [ ] T021 Run `node .github/scripts/check-markdown.mjs --files .github/actions/environment-setup/README.md .github/actions/environment-verify/README.md specs/20261023-085421-cv-inference-environment/tasks.md` and fix every finding
- [ ] T022 Confirm confinement (SC-006) with `git diff --name-only` against the base branch: only paths under `.github/actions/environment-setup/`, `.github/actions/environment-verify/`, and this spec's `tasks.md` changed, and no committed declaration other than `analyst-cv-libraries.json` was modified by a verification run

---

## Dependencies & Execution Order

- Phase 1 (T001) precedes everything; T002 follows it and precedes the story tests in `cv-libraries.test.mjs`.
- **US1** has no dependency on US2 or US3: tests T003, T004 (same file, in order) and T005 (a different file, parallel) precede the scripts T006 and T007 (parallel), then T008 (needs T006), then T009 and T010 (parallel).
- **US2** builds on the installer and test file of US1: T011 follows T004; T012 runs first (a group of its own), then T013 (needs T001 and the T012 script, because it runs the check against the real wheels); T014 follows T008, T011, T012, and T013.
- **US3** is independent of US2: T015 and T016 run in parallel after T010; T017 follows T015.
- **Polish**: T018 and T019 follow the stories they document, T020 to T022 run last, in order.

### Parallel Opportunities

- T005 is a group of its own (a different test file than T003 and T004) and runs alone after them.
- T006 and T007 (different scripts), T009 and T010 (different `action.yml` files), T015 and T016, T018 and T019 are parallel groups.

## Implementation Strategy

- **MVP**: Phases 1 to 3 (User Story 1): the pinned wheels are provisioned and smoke-checked and project locks are
  checked against the pins.
- **Incremental delivery**: add User Story 2 (licence and SBOM gate in setup), then User Story 3 (capability selection
  and coverage evidence), then documentation and validation.
- A change to any pin, allow-list, or table entry later is a standalone environment change; never edit applied
  declarations in a task that is not an environment spec.
