# Tasks: Shot-Boundary Media Context Analyst (Baseline)

**Input**: Design documents from `/specs/20261023-022148-shot-boundary-analyst/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/shot-boundary-fact.md](contracts/shot-boundary-fact.md), [quickstart.md](quickstart.md)

**Tests**: Included. The spec requires unit, image, audit, and Docker-backed end-to-end evidence (FR-013, FR-014,
FR-016, SC-001 to SC-006).

**Organization**: Tasks are grouped by user story. `[P]` marks tasks that touch different files; consecutive `[P]` tasks under one heading run in parallel, each from the same commit.

## Format: `- [ ] [ID] [P?] [Story] Description with file path`

## Path Conventions

- Capability project: `src/analysts/capabilities/media-context-classification/` (abbreviated `<cap>/` in descriptions)
- Python package: `<cap>/src/media_context_classification/`
- Docker-backed tests: `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/`
- Mirror the layout and conventions of `src/analysts/capabilities/reference-media-probe/`, which stays unchanged
  (FR-019). Do not change the SDK, the platform, contracts, the Web UI, seeds, or `.github/actions/` (FR-001, FR-015,
  FR-017).

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the locked Python project.

- [x] T001 Create `<cap>/pyproject.toml` (Python `>=3.12,<3.13`, hatchling build backend, `socalytics-analyst-sdk` as the path source `../../sdk`, runtime dependencies `opencv-python-headless` and `numpy`, dev group `pytest` and `jsonschema`, wheels only), `<cap>/.gitignore`, the hash-locked `<cap>/uv.lock` created by `UV_PYTHON_DOWNLOADS=never uv lock` in `<cap>/` (the project verification of every task requires a committed `uv.lock` beside each `pyproject.toml`, so the lock is part of this task), and `<cap>/src/media_context_classification/__init__.py` exporting `CAPABILITY_ID` (`media-context-classification`), `CAPABILITY_VERSION` (`1.0.0`), and `FACT_KIND` (`shot-boundary`), and `<cap>/tests/test_identity.py` asserting those three constants (the project verification runs `pytest`, which exits non-zero when it collects no test, so the first task needs one collectable test), following `reference-media-probe`
- [x] T002 Run, with the `<cap>/uv.lock` that T001 committed, the locked sequence from [quickstart.md](quickstart.md) section 1 (`uv sync --locked --all-groups --no-install-local --no-build`, `uv sync --locked --all-groups --no-build-isolation`, `uv build --wheel --no-build-isolation --python .venv/bin/python`); then create `<cap>/tests/test_environment.py` asserting that `cv2`, `numpy`, and `socalytics_analyst` import from the locked environment and that `cv2.__version__` and `numpy.__version__` equal the versions pinned in `<cap>/uv.lock` (the task must leave a tracked file, because a task that changes nothing is a failed attempt, and the test records the evidence of the sequence); if a locked wheel cannot be installed by this sequence, stop and report that a standalone environment spec must land first (research.md R-02)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Settings, frame acquisition, the measure, and the test support every story needs.

**CRITICAL**: No user story work begins until this phase is complete.

### Foundational modules and test support

- [x] T003 [P] Create `<cap>/src/media_context_classification/settings.py` with the declared constants of [data-model.md](data-model.md) (`DETECTOR_VERSION` `1.0.0`, `FRAME_WIDTH` 64, `FRAME_HEIGHT` 36, `HARD_THRESHOLD` 30.0, `ADAPTIVE_FLOOR` 8.0, `ADAPTIVE_RATIO` 3.0, `ADAPTIVE_WINDOW` 4, `MIN_SHOT_LENGTH_MS` 500, `RULE_ORDER` `hard-cut` then `adaptive-cut`, `CONFIDENCE` 0.5) and a `settings` provenance mapping (`detectorVersion`, `frameWidth`, `frameHeight`, `adaptiveWindow`, `minShotLengthMs`, `ruleOrder`)
- [x] T004 [P] Create `<cap>/tests/support/__init__.py` and `<cap>/tests/support/media.py`: helpers that generate small clips at test time with the pinned `ffmpeg` (known hard cuts, low-contrast cut, static scene with noise, steady pan, closely spaced cuts, single-frame and empty media, a lead-in clip with extra frames before the requested start), no committed binary media (FR-014)
- [x] T005 [P] Create `<cap>/tests/support/mounts.py` and `<cap>/tests/conftest.py`: build a job document, attempt context, and read-only staged media mount (`segment-manifest.json` and `segment.mp4`, non-zero segment offset supported) for the SDK so capability code runs in tests, following the reference capability's `tests/support/mounts.py`
- [x] T006 [P] Create `<cap>/src/media_context_classification/frames.py`: `ffprobe` listing of `best_effort_timestamp` for every frame of the first video stream (`media_ms = floor(pts * time_base * 1000)`, exact time base), then `ffmpeg` decoding of the same stream to `bgr24` rawvideo at 64x36 with `scale=64:36:flags=area` and `-fps_mode passthrough` streamed one fixed-size record per frame; pair the n-th timestamp with the n-th frame; raise named failures `media-decode-failed` (tool failure, undecodable media, count mismatch) and `media-no-video-stream` through the SDK error types (research.md R-01, R-08)
- [x] T007 [P] Create `<cap>/src/media_context_classification/measure.py`: HSV change measure per research.md R-03 (`cv2.COLOR_BGR2HSV` 8-bit, per-pixel absolute difference per channel without hue wrap-around, `(sum|dH| + sum|dS| + sum|dV|) / (3 * pixels)` from integer sums, exact `Fraction`/`Decimal` quantized to three decimals with `ROUND_HALF_EVEN`, range 0 to 255) and `cv2.setNumThreads(1)`

### Foundational tests

- [x] T008 [P] Create `<cap>/tests/test_support.py` verifying the generated clips and mounts (frame counts, cut positions, offsets) and `<cap>/tests/test_frames.py` verifying timestamp and frame pairing, constant-size streaming, and the `media-decode-failed` and `media-no-video-stream` reasons
- [x] T009 [P] Create `<cap>/tests/test_measure.py` verifying the measure for identical frames (0), a full-scale change, a color-only change, three-decimal `ROUND_HALF_EVEN` quantization, and independence of thread count

**Checkpoint**: Frame acquisition and the measure work; stories can start.

---

## Phase 3: User Story 1 - A recorded match yields shot-boundary facts (Priority: P1) MVP

**Goal**: A segment's hard cuts become exactly one `shot-boundary` fact each, at the match time of the first frame of
the new shot, written through the SDK entry point.

**Independent Test**: Run the capability on a generated clip with known hard cuts (and non-zero segment offset) through
the SDK test mounts: every known cut has a fact at its exact match time, no other fact exists, a segment without a cut
yields an accepted empty result, and repeated runs are byte-identical.

### Implementation for User Story 1

- [x] T010 [P] [US1] Create `<cap>/schemas/shot-boundary.payload.schema.json` for the payload of [contracts/shot-boundary-fact.md](contracts/shot-boundary-fact.md) (`rule`, `changeMeasure` 0 to 255, `hardThreshold`, optional `adaptiveFloor`, `adaptiveRatio`, `contextMean` required together for adaptive cuts, `frameIndex` integer `>= 1`, `previousShotLastFrameMs` integer or null, `settings`), `additionalProperties` false
- [x] T011 [P] [US1] Create `<cap>/src/media_context_classification/facts.py`: convert a boundary to a `Fact` (kind `shot-boundary`, range per FR-007, confidence from settings, payload with provenance `settings`, adaptive fields only for `adaptive-cut`)

### Hard-cut detector and entry point

- [x] T012 [US1] Create `<cap>/src/media_context_classification/detector.py`: streaming detector with a bounded context window over frames from `frames.py` and `measure.py`; apply the nominal-window and requested-interval rules (lead-in frames only as predecessor of the first in-window frame; a pair is scored only when its second frame is in window), the hard-cut rule `measure >= HARD_THRESHOLD`, one boundary per pair, the `previous_frame_ms` rule, and fewer than two frames yielding no boundary (research.md R-04, R-05)
- [x] T013 [US1] Create `<cap>/src/media_context_classification/__main__.py`: SDK entry point that reads only the job document and staged segment media (no network, credentials, upstream results, or randomness), fails with `segment-media-required` when the job has no media, runs the detector, writes the `foundational-facts` result `1.0.0` with facts in match-time order, and writes no result on failure (FR-003, FR-009, FR-011)

### Tests for User Story 1

- [x] T014 [P] [US1] Create `<cap>/tests/test_detector.py`: hard-cut detection on generated clips (three cuts, one cut exactly on the second frame of the segment, no cut yields no boundary), fewer than two frames completes with no boundary, lead-in frames (a cut among lead-in frames is not reported, a cut between the last lead-in frame and the first in-window frame is reported), and a cut across the segment edge in two cases (with a lead-in predecessor frame the next segment reports it exactly once and the earlier segment never; without a predecessor no segment reports it)
- [x] T015 [P] [US1] Create `<cap>/tests/test_time_mapping.py`: fact positions derive from frame timestamps through `SegmentMedia.to_match_ms` and `segment_window` with a non-zero segment offset and with variable frame rate, and `timestamp-unmappable` for a frame in the requested interval that cannot be mapped
- [x] T016 [P] [US1] Create `<cap>/tests/test_facts.py`: range from the first frame of the new shot to the next in-window frame or the window end for the last frame, raised to `start + 1` when equal and never beyond the window end, confidence `0.5`, payload fields of [contracts/shot-boundary-fact.md](contracts/shot-boundary-fact.md) for hard-cut facts, facts in `startMs` order, accepted result with zero facts
- [x] T017 [P] [US1] Create `<cap>/tests/test_payload_schema.py`: every produced payload validates against `schemas/shot-boundary.payload.schema.json` with `jsonschema`, and additional or missing properties are rejected
- [x] T018 [P] [US1] Create `<cap>/tests/test_determinism.py`: at least three runs on the same media yield byte-identical results, including runs under different CPU limits (for example `taskset` with one and several CPUs) (SC-003)
- [x] T019 [P] [US1] Create `<cap>/tests/test_failures.py`: every named failure reason (`segment-media-required`, `media-decode-failed`, `media-no-video-stream`, `timestamp-unmappable`) ends the attempt as failed with that reason and writes no result file

**Checkpoint**: User Story 1 passes `uv run --no-sync pytest` for its tests, on hard cuts.

---

## Phase 4: User Story 2 - Fixed-camera and gradual-change footage does not drown members in false boundaries (Priority: P1)

**Goal**: Add the adaptive rule and the minimum shot length so noise, pans, and closely spaced cuts yield no false or
duplicate boundaries, and each fact names its rule.

**Independent Test**: Run the capability on the generated clips for each case and compare the facts with the clips'
known truth.

### Adaptive rule and minimum shot length

- [x] T020 [US2] Extend `<cap>/src/media_context_classification/detector.py` with the adaptive rule (`measure >= ADAPTIVE_FLOOR` and `measure >= ADAPTIVE_RATIO * mean(surrounding measures)`, window of `ADAPTIVE_WINDOW` pairs on each side inside the nominal window, exact arithmetic, hard rule tested first) and the minimum shot length (a boundary is reported only when its start is at least `MIN_SHOT_LENGTH_MS` after the previous reported boundary's start); keep memory bounded by the window (research.md R-04)

### Tests for User Story 2

- [x] T021 [P] [US2] Create `<cap>/tests/test_rules.py`: static scene with sensor-like noise yields no fact, steady pan yields no fact (SC-002), a low-contrast cut below the fixed threshold but large relative to its surroundings yields one `adaptive-cut` fact carrying `adaptiveFloor`, `adaptiveRatio`, and `contextMean`, a very high-contrast cut yields one `hard-cut` fact and never two facts for the same cut, and the adaptive context excludes the pair itself and pairs outside the nominal window
- [x] T022 [P] [US2] Create `<cap>/tests/test_min_shot_length.py`: two cuts closer than `MIN_SHOT_LENGTH_MS` report only the first and leave no trace of the second, a suppressed boundary does not move the reference point, and the first boundary of a segment is never suppressed

**Checkpoint**: User Stories 1 and 2 pass all Python tests.

---

## Phase 5: User Story 3 - A Club Admin registers and activates the capability through the normal Registry path (Priority: P2)

**Goal**: A digest-pinned image with a valid manifest that the Registry imports, a documented registration procedure
without a seed, and Docker-backed end-to-end evidence that facts appear for a real run (the US1 and US2 scenarios,
end to end).

**Independent Test**: Register the built image by digest through the Registry operations; the imported declaration shows
the capability identity, version, segment scope, `required` video policy, no dependencies, and the fact kind, and a run
over a synthetic recording yields exactly the known facts.

### Manifest, packaging, and static tests

- [x] T023 [P] [US3] Create `<cap>/analyst-manifest.json` valid against `contracts/analysis/analyst-manifest/v1`: capability `media-context-classification` 1.0.0, `tier` `low-level`, `executionScope` `segment`, empty `upstream`, `videoAccess` `required`, output `schemaVersion` `1.0.0` with fact kind `shot-boundary`, `compatibleProfiles` `["media-context-classification-dev"]`, no-model digest `sha-256:` followed by 64 zeros, `runtime.constraints` `["linux-amd64"]`, no accelerators, `resources` memory `2Gi` and cpu `2`, `minExecutionTimeoutSeconds` 300, deterministic and idempotent (research.md R-09, contract summary)
- [x] T024 [P] [US3] Create `<cap>/docker/fetch_verified.py` and `<cap>/Dockerfile.dockerignore` by adapting the reference capability's files (hash-verified download helper; ignore everything except the files the image needs)
- [x] T025 [US3] Create `<cap>/tests/test_packaging.py` verifying the manifest against the released manifest schema, its declared identity, scope, video policy, empty upstream, fact kind, and that the schema is not declared in the manifest (research.md R-06), plus `<cap>/tests/test_package.py` verifying the package constants equal the manifest
- [x] T026 [US3] Create `<cap>/Dockerfile`: repository-root build with the reference image's structure (`python:3.12-slim-bookworm` by digest, `uv` and FFmpeg and ffprobe 8.1 fetched with `docker/fetch_verified.py` against SHA-256 pins that equal `.github/actions/environment-setup/analyst-media-toolchain.json`, `MANIFEST_SHA256` check, locked `uv sync --locked --no-build` of runtime dependencies, literal `io.socalytics.*` labels verified against the manifest at build time, manifest copied last, `USER 65532:65532`, `ENTRYPOINT` of the project's venv python, no tag references, no model weights)
- [x] T027 [US3] Create `<cap>/tests/test_dockerfile.py` verifying the Dockerfile's pins (digest base, toolchain hashes equal the environment file, no tag references), literal labels equal to the manifest, manifest copied last, non-root user, and that no model weights are copied

### Registration procedure

- [x] T028 [US3] Create `<cap>/README.md`: purpose and development-evidence status (no production readiness, not published, catalog quality gates not met, no accuracy claim beyond the synthetic fixtures), declared detection settings and limitations (segment-edge cuts, fades, dissolves, flashes, minimum shot length), the fact contract summary linking `schemas/shot-boundary.payload.schema.json`, failure reasons, local test and image commands from [quickstart.md](quickstart.md), and the numbered registration procedure of research.md R-11 using only existing Registry operations (build, push inside the stamp's registry host and prefix, `registerAnalystImage` by digest, create and approve profile `media-context-classification-dev` with the no-model digest, define a workflow with a segment node, set it as default; no seed)

### Docker-backed end-to-end evidence

- [ ] T029 [P] [US3] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/CutMedia.cs`: generate with the pinned FFmpeg a 15-second 25 fps H.264 recording from lavfi sources with known hard cuts, so that three 5-second segments result, with cuts in the first and third segments, one cut exactly on the second frame of its segment, no cut on a segment edge, and the middle segment cut-free; expose the exact expected match-time positions and rules as truth (research.md R-10)
- [x] T030 [P] [US3] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/ShotBoundaryAnalystImage.cs`: build the capability image from its Dockerfile and push it by digest, following `ReferenceAnalystImage.cs` without modifying it
- [ ] T031 [US3] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/ShotBoundaryEndToEndTests.cs` following `ReferenceAnalystEndToEndTests.cs`: register the image by digest and assert the imported declaration (identity, version, segment scope, `required` video policy, no dependencies, fact kind), approve a profile, define and default a workflow, finalize the `CutMedia` recording under a short segmentation policy, execute the run through the Segment Service, Manager, result acceptance, and fact indexing, and compare kinds, match-time starts, rules, and counts with the known truth exactly through `listAnalysisRunFacts` and `listAnalysisRunFactStreams`, including a segment without a cut analyzed with an empty fact stream (SC-001)

**Checkpoint**: The capability is registrable by digest and produces the known facts end to end on a Linux engine.

---

## Phase 6: User Story 4 - Maintainers can trust the image's provenance (Priority: P2)

**Goal**: Dependency audit and SBOM evidence verified against the lock, and image evidence for labels, isolation,
no weights, and resource bounds.

**Independent Test**: Build the image, inspect labels and manifest, and check the audit against the installed
dependency set; a dependency without a record or with a disallowed license fails verification.

### Audit evidence

- [ ] T032 [US4] Create `<cap>/scripts/generate_audit.py` that regenerates `audit/dependency-audit.json` (`name`, `version`, `license` SPDX expression, `scope` `runtime`, `dev`, `bundled-library`, or `toolchain`, `source`) for every locked package plus the OpenCV-bundled FFmpeg libraries and the pinned FFmpeg build, and `audit/sbom.cdx.json` (CycloneDX 1.5 of the capability layers, with the base image reference by digest and a statement that operating-system packages are identified only by that digest), from `uv.lock` and installed distribution metadata, with no network access (research.md R-07)
- [ ] T033 [US4] Run `<cap>/scripts/generate_audit.py` to produce and commit `<cap>/audit/dependency-audit.json` and `<cap>/audit/sbom.cdx.json`, and resolve any license outside the allowed set (permissive licenses; LGPL for separately packaged shared libraries; and GPL-3.0-with-GCC-exception only for bundled GCC runtime libraries that name their parent wheel; any other GPL or AGPL expression and any unknown license fail) by stopping and reporting it rather than weakening the check
- [ ] T034 [US4] Create `<cap>/tests/test_audit.py`: every locked package has a record whose name and version equal `uv.lock`, every license is in the allowed set (permissive licenses such as MIT, BSD, Apache-2.0, PSF, and ISC; LGPL for separately packaged shared libraries; and `GPL-3.0-with-GCC-exception` only for `bundled-library` entries of the GCC runtime libraries that name their parent wheel), the check fails for a missing record, a differing record, and a GPL, AGPL, or unknown license (use synthetic inputs for the failing cases), the SBOM matches the audit, and no model weight file exists in the project

### Image evidence

- [ ] T035 [US4] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/ShotBoundaryImageTests.cs` following `ReferenceAnalystImageTests.cs`: labels equal the manifest, the model digest is the no-model digest, no model weight files in the image, the image completes a job as a non-root user on a read-only root file system without network, and the installed Python distributions in the image match `audit/dependency-audit.json` (SC-006)
- [ ] T036 [US4] Extend `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/ShotBoundaryImageTests.cs` with the resource test: generate a one-minute 1080p clip with the pinned `ffmpeg`, run the image on it in a restricted container, read the container's peak memory from its cgroup, and fail above 2 GiB or when its wall time exceeds 300 seconds (the manifest's `minExecutionTimeoutSeconds`); for the few-second evidence segment fail when its wall time exceeds 30 seconds (SC-005)
- [ ] T037 [US4] Add the audit evidence section to `<cap>/README.md`: what `audit/` contains, how `scripts/generate_audit.py` regenerates it, the allowed license set, and that it starts but does not complete the catalog's independent license and provenance audit (FR-013)

**Checkpoint**: All four stories are complete and verified.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Documentation corrections and final verification (FR-018, FR-019, FR-020, SC-007, SC-008).

### Documentation (re-read each document and fix every statement the merged state makes false)

- [ ] T038 [P] Update `docs/architecture/analyst-capability-catalog.md` so that Low-Level Capabilities, Implementation Waves, and Catalog Rules state the merged outcome: `media-context-classification` exists as a shot-boundary baseline only (development evidence, no gate met, no learned classifier, never in a production workflow), shot type, live, replay, graphics, quality, usable intervals, and the challenger are not implemented, and the reference capability remains the only other development capability; correct any planned, future-tense, or unimplemented wording this makes false, outside these sections too
- [ ] T039 [P] Update `docs/architecture/analysts-models-and-hardware.md` (Image Metadata And Packaging: name the second image and its additions, the OpenCV and NumPy wheels, the dependency audit with its allowed-license set and SBOM, no model weights; keep registries, publication, the catalog's independent license and provenance audit, and production values as open) and `docs/architecture/analyst-runtime-and-recovery.md` (reconcile the statement that real Analyst execution evidence is a development reference that is not a catalog capability image with the first catalog-named capability image), correcting any other statement these documents make false and searching `docs/architecture/` for any other statement that no catalog capability exists, correcting it in the files this task or T038 owns
- [ ] T040 [P] Update `README.md`, `AGENTS.md`, and `src/analysts/README.md` (source-area structure, executable evidence, and the sentence that lists `reference-media-probe` as the first project under `capabilities/`), stating the new capability as development evidence with the commands of [quickstart.md](quickstart.md); search the non-architecture repository documentation (everything outside `docs/architecture/`) for any other statement that no catalog capability exists and correct it

### Final verification

- [ ] T041 Run the full verification of [quickstart.md](quickstart.md): the locked Python sequence and `uv run --no-sync pytest` in `<cap>/`, the same in `src/analysts/capabilities/reference-media-probe` (unchanged and passing), `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundary"` and `--filter "FullyQualifiedName~ReferenceAnalyst"` on a Linux engine, and `node .github/scripts/check-markdown.mjs`; fix every finding caused by this feature

---

## Dependencies & Execution Order

- Phase 1 then Phase 2 then the stories. T002 needs T001 and adds the tracked `tests/test_environment.py`, because a task whose agent changes no file fails (T001 creates `uv.lock` and `tests/test_identity.py` because the project verification of each task fails on a `pyproject.toml` without a committed lock and on a project in which `pytest` collects no test); T008 needs T004 to T006; T009 needs T007.
- US1 (T010 to T019) needs Phase 2; T012 needs T003, T006, and T007; T013 needs T011 and T012; the tests T014 to T019 need T010 and T013.
- US2 (T020 to T022) needs US1, because T020 extends `detector.py`; its tests T021 and T022 need T020.
- US3 needs US1 and US2 for a complete capability: T026 needs T023 and T024; T027 needs T026; T031 needs T026, T029, and T030; T028 may start after T023.
- US4 needs US3: T033 needs T032; T034 needs T033; T035 needs T026 and T033; T036 needs T035; T037 needs T028 and T033.
- Polish needs all stories; T041 runs last.

## Parallel Execution Examples

- Phase 2: T003 to T007 in parallel (distinct files), then T008 and T009 in parallel.
- US1: T010 and T011 in parallel, then T012 and T013 in order, then T014 to T019 in parallel.
- US2: T020, then T021 and T022 in parallel.
- US3: T023 and T024 in parallel, then T029 and T030 in parallel.
- Polish: T038, T039, and T040 in parallel.

## Implementation Strategy

- **MVP**: Phases 1 to 3 (US1) deliver hard-cut facts through the SDK entry point, verified by the Python tests.
- **Increment**: US2 makes the detector trustworthy on noise, pans, and low-contrast cuts; US3 makes it registrable and
  proves it end to end; US4 adds the provenance evidence; Polish corrects the documentation.
- Stop and report instead of working around an environment gap (a locked wheel that cannot be installed, or a disallowed
  license); never change the environment actions, the SDK, or the platform in this feature.
