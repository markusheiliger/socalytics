# Tasks: Person and Role Detection Analyst (Development Baseline)

**Input**: Design documents from `/specs/20261025-195345-person-role-detection/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/person-role-detection-fact.md](contracts/person-role-detection-fact.md), [quickstart.md](quickstart.md)

**Tests**: Included. The spec requires unit, image, audit, and Docker-backed end-to-end evidence (FR-015, FR-017,
FR-014, SC-001 to SC-006).

**Organization**: Tasks are grouped by user story. `[P]` marks tasks that touch different files; consecutive `[P]` tasks under one heading run in parallel, each from the same commit.

## Format: `- [ ] [ID] [P?] [Story] Description with file path`

## Path Conventions

- Capability project: `src/analysts/capabilities/person-role-detection/` (abbreviated `<cap>/` in descriptions)
- Python package: `<cap>/src/person_role_detection/`
- Docker-backed tests: `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/`
- Mirror the layout and conventions of `src/analysts/capabilities/media-context-classification/` (the shot-boundary
  capability), which stays unchanged together with `reference-media-probe` (FR-021). Do not change the SDK, the platform,
  contracts, the Web UI, seeds, the existing EndToEnd helpers, or `.github/actions/` (FR-001, FR-016, FR-019).
- The OpenCV, NumPy, and ONNX Runtime CPU wheels come from the dependent environment spec
  [CV and Inference Verification Environment](../20261023-085421-cv-inference-environment/spec.md). If a locked wheel cannot be
  installed by the locked sequence, stop and report that the environment spec must land first (research.md R-08, R-10).

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the locked Python project.

- [ ] T001 Create `<cap>/pyproject.toml` (Python `>=3.12,<3.13`, hatchling build backend, `socalytics-analyst-sdk` as the path source `../../sdk`, runtime dependencies `opencv-python-headless`, `numpy`, and `onnxruntime` at the versions the environment spec pins, dev group `hatchling`, `pytest`, and `jsonschema`, wheels only), `<cap>/.gitignore`, the hash-locked `<cap>/uv.lock` created by `UV_PYTHON_DOWNLOADS=never uv lock` in `<cap>/` (the project verification requires a committed `uv.lock` beside each `pyproject.toml`), `<cap>/src/person_role_detection/__init__.py` exporting `CAPABILITY_ID` (`person-role-detection`), `CAPABILITY_VERSION` (`1.0.0`), and `FACT_KIND` (`person-role-detection`), and `<cap>/tests/test_identity.py` asserting those three constants (pytest exits non-zero when it collects no test), following `media-context-classification`
- [ ] T002 Run, with the `<cap>/uv.lock` that T001 committed, the locked sequence from [quickstart.md](quickstart.md) section 1 (`uv sync --locked --all-groups --no-install-local --no-build`, `uv sync --locked --all-groups --no-build-isolation`, `uv build --wheel --no-build-isolation --python .venv/bin/python`); then create `<cap>/tests/test_environment.py` asserting that `cv2`, `numpy`, `onnxruntime`, and `socalytics_analyst` import from the locked environment, that their versions equal the versions in `<cap>/uv.lock`, and that `onnxruntime.get_available_providers()` contains `CPUExecutionProvider` (the task must leave a tracked file); if a locked wheel cannot be installed, stop and report that the environment spec must land first

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The pinned model, settings, frame acquisition, preprocessing, postprocessing, the inference seam, and the test support every story needs.

**CRITICAL**: No user story work begins until this phase is complete.

### Model pin

- [ ] T003 Audit and pin the model per research.md R-01: fetch the official YOLOX-S ONNX file of the upstream Megvii release (Apache-2.0) outside the repository, confirm the upstream licence and file provenance, and create `<cap>/model/model-record.json` (`id` `yolox-s`, `version` upstream release tag, `source` release URL, `licence` `Apache-2.0`, `trainingDataTerms` COCO 2017 terms, `retrievedOn`, `sha256` of the file as the single pin for build, start, label, and audit, `conversion` `none`, `status` `development evidence`) and `<cap>/tests/test_model_record.py` asserting the fields and that a present file at `PERSON_ROLE_MODEL_PATH` has the pinned digest; never commit the weights (FR-013); stop and report for a spec amendment if the licence or provenance cannot be confirmed or the digest cannot be pinned

### Settings

- [ ] T004 Create `<cap>/src/person_role_detection/settings.py` with the declared constants of [data-model.md](data-model.md) (`DETECTOR_VERSION` `1.0.0`, `SAMPLING_RULE` `fixed-2fps-v1` with a 500 ms grid, `INPUT_SIZE` 640, pad value 114, `CONFIDENCE_FLOOR` 0.30, `MERGE_IOU` 0.45, `MAX_DETECTIONS_PER_FRAME` 100, `CLASS_MAPPING_VERSION` `1.0.0`, COCO class id 0 to role `person`, `THREADS` 1, rounding of boxes to 2 and confidence to 4 decimals, the model path `/opt/model/yolox_s.onnx` overridable only through `PERSON_ROLE_MODEL_PATH` for tests); none is read from the job, profile, or Manager

### Foundational modules and test support

- [ ] T005 [P] Create `<cap>/tests/support/__init__.py` and `<cap>/tests/support/media.py`: helpers that generate small clips at test time with the pinned `ffmpeg` (rendered figures at known positions and sizes, no figures, a single frame, variable frame rate, lead-in frames before the nominal window, several source resolutions and aspect ratios); no committed binary media
- [ ] T006 [P] Create `<cap>/tests/support/mounts.py` and `<cap>/tests/conftest.py`: build a job document, attempt context, and read-only staged media mount (`segment-manifest.json` and `segment.mp4`, non-zero segment offset supported) for the SDK, following the shot-boundary capability's test support
- [ ] T007 [P] Create `<cap>/tests/support/detectors.py`: a deterministic scripted `Detector` that returns raw YOLOX head tensors (`x, y, w, h, objectness, 80 class scores` per anchor for strides 8, 16, and 32) for known boxes, so mechanics tests need no model file (research.md R-10)
- [ ] T008 [P] Create `<cap>/src/person_role_detection/frames.py`: `ffprobe` listing of `best_effort_timestamp` for every frame of the first video stream mapped to match time through `SegmentMedia.to_match_ms`; the sampling rule `fixed-2fps-v1` (grid point `t` every 500 ms anchored at the nominal window start, the first in-window frame at or after `t` and before `t + 500`, a grid point without a frame skipped, a frame exactly at the window end outside); batched full-size `bgr24` decode of the selected zero-based frame indices with `ffmpeg` `select` and `-fps_mode passthrough` (at most 256 indices per invocation, one batch in memory); the named reasons `media-decode-failed` (tool cannot start, exits non-zero, undecodable, or index and timestamp count mismatch), `media-no-video-stream`, and `timestamp-unmappable` (research.md R-04, R-05, R-07)
- [ ] T009 [P] Create `<cap>/src/person_role_detection/preprocess.py`: letterbox to 640x640 (scale `min(640/w, 640/h)`, `cv2.INTER_LINEAR`, pad 114, BGR order, float32, no normalization, HWC to CHW), the inverse remap of boxes to source-frame coordinates, and `cv2.setNumThreads(1)` (research.md R-03)
- [ ] T010 [P] Create `<cap>/src/person_role_detection/postprocess.py`: YOLOX head decode (grid and stride transform for strides 8, 16, 32), score `objectness * class score`, class mapping `1.0.0` keeping only COCO class 0 as role `person`, the 0.30 floor, greedy NMS at IoU 0.45 applied once per frame (highest confidence first, ties by smaller top then smaller left), the cap of 100 detections, remap with clipping to the frame, dropping boxes below 1 px after clipping, and rounding after the cap (research.md R-02, R-03)
- [ ] T011 [P] Create `<cap>/src/person_role_detection/model.py`: the `Detector` protocol; the ONNX Runtime CPU session (`intra_op_num_threads=1`, `inter_op_num_threads=1`, `ORT_SEQUENTIAL`, fixed graph optimization level, `CPUExecutionProvider` only); the start check that raises `model-missing` for an absent or unreadable file and `model-digest-mismatch` for a SHA-256 that differs from the pin in `model/model-record.json` (the pin is also available to the package as a constant); `model-inference-failed` when the session cannot be created or run

### Foundational tests

- [ ] T012 [P] Create `<cap>/tests/test_support.py` verifying the generated clips and mounts (frame counts, figure positions, offsets) and `<cap>/tests/test_frames.py` verifying timestamp and frame pairing, the sampling rule with a non-zero segment offset, variable frame rate, a single-frame and an empty clip, lead-in frames never selected, the bounded batch size, and the `media-decode-failed` and `media-no-video-stream` reasons
- [ ] T013 [P] Create `<cap>/tests/test_preprocess.py` verifying the letterbox (scale, padding, value 114, channel order) and the box remap across several source resolutions and aspect ratios, independent of the model input size (FR-015)
- [ ] T014 [P] Create `<cap>/tests/test_postprocess.py` verifying head decode on scripted tensors, class mapping (only COCO `person`, role `person`), the confidence floor, the duplicate merge applied once per frame with no same-role pair above 0.45 IoU remaining, the cap of 100 with deterministic tie-breaking, clipping and dropping of boxes below 1 px, and rounding
- [ ] T015 [P] Create `<cap>/tests/test_model.py` verifying `model-missing`, `model-digest-mismatch`, and `model-inference-failed` with temporary files, and a real-model smoke detection and thread-independent output that run when the model file is present at `PERSON_ROLE_MODEL_PATH` and otherwise skip with an explicit reason (research.md R-10)

**Checkpoint**: Model pinned; frame acquisition, preprocessing, postprocessing, and the inference seam work; stories can start.

---

## Phase 3: User Story 1 - A recorded match yields person-role detection facts (Priority: P1) MVP

**Goal**: A segment's analyzed frames become exactly one `person-role-detection` fact per retained detection, written
through the SDK entry point.

**Independent Test**: Run the capability on a generated clip with rendered figures (and a non-zero segment offset)
through the SDK test mounts: facts exist for the detected figures inside the nominal window, a segment without a
detectable person yields an accepted empty result, and repeated runs are byte-identical.

### Schema and facts

- [ ] T016 [P] [US1] Create `<cap>/schemas/person-role-detection.payload.schema.json` for the payload of [contracts/person-role-detection-fact.md](contracts/person-role-detection-fact.md) in the Registry's restricted self-contained JSON Schema 2020-12 subset (internal `$defs` and `$ref`, no formats, no external references): `role` enum `player`, `goalkeeper`, `referee`, `staff`, `person`; `box` with `left`, `top` from 0 to 16384 and `width`, `height` with `exclusiveMinimum` 0; `frame` `width` and `height` integers of at least 1; `confidence` from 0 to 1; `frameIndex` integer of at least 0; `model`; `settings`; and `additionalProperties: false` at every level
- [ ] T017 [P] [US1] Create `<cap>/src/person_role_detection/facts.py`: convert a detection to a `Fact` (kind `person-role-detection`, `startMs` the match time of the analyzed frame, `endMs` the match time of the next analyzed frame or the window end for the last one and always greater than `startMs`, envelope confidence equal to the payload confidence, payload and provenance per the contract including the model digest, class mapping version, sampling rule, and settings), ordered by `startMs`, then confidence descending, then box top, then box left

### Entry point

- [ ] T018 [US1] Create `<cap>/src/person_role_detection/__main__.py`: SDK entry point that reads only the job document, the staged segment media, and the model file (no network, credentials, upstream results, or run-time download; job fields that look like settings are ignored); fails with `segment-media-required` when the job has no staged media; verifies the model digest, selects frames with `frames.py`, runs one full-frame pass per analyzed frame through `preprocess.py`, `model.py`, and `postprocess.py` with one decoded frame in memory at a time, builds facts with `facts.py`, and writes the result as output type `foundational-facts` schema version `1.0.0` (an empty result for a segment without an analyzable frame or detection) through the SDK, which validates the payload schema before writing

### Tests for User Story 1

- [ ] T019 [P] [US1] Create `<cap>/tests/test_detection.py`: end-to-end through the SDK test mounts with the scripted detector, two segments with non-zero offsets, an empty segment accepted with no facts, a single-frame segment, and, when the model file is present, a real-model run on rendered figures that yields at least one `person` fact
- [ ] T020 [P] [US1] Create `<cap>/tests/test_time_mapping.py`: fact positions derive from frame timestamps through `SegmentMedia.to_match_ms` and `segment_window` with a non-zero segment offset and with variable frame rate, lead-in frames never analyzed, every fact inside the half-open nominal window, the range ending at the next analyzed frame or the window end, and `timestamp-unmappable` for a frame that cannot be mapped
- [ ] T021 [P] [US1] Create `<cap>/tests/test_determinism.py`: at least three runs on the same media yield byte-identical results, including runs under different CPU limits (for example `taskset` with one and several CPUs) and different thread environment settings (SC-003); real-model variant skips without the file
- [ ] T022 [P] [US1] Create `<cap>/tests/test_failures.py`: every named failure reason (`segment-media-required`, `media-decode-failed`, `media-no-video-stream`, `timestamp-unmappable`, `model-missing`, `model-digest-mismatch`, `model-inference-failed`) ends the attempt as failed with that reason and writes no result file

**Checkpoint**: User Story 1 passes `uv run --no-sync pytest` for its tests.

---

## Phase 4: User Story 2 - Every detection fact is validated, attributable, and honest about the model (Priority: P1)

**Goal**: Every fact validates against the declared payload schema and carries the provenance of its model, mapping, settings, and frame.

**Independent Test**: Validate every fact written on generated frames against the schema, and confirm that schema-violating payloads are rejected.

### Tests for User Story 2

- [ ] T023 [P] [US2] Create `<cap>/tests/test_facts.py`: range rules (start at the analyzed frame, end at the next analyzed frame or window end, never beyond the window end, never empty), envelope confidence equal to the payload confidence, ordering by `startMs`, confidence, top, and left, role `person` only, box inside the exact frame, the zero-based `frameIndex` counting lead-in frames, and the provenance of the contract (model id, version, digest, class mapping version, sampling rule, settings)
- [ ] T024 [P] [US2] Create `<cap>/tests/test_payload_schema.py`: every produced payload validates against `schemas/person-role-detection.payload.schema.json` with `jsonschema`, and a missing field, an undeclared role, a confidence outside zero to one, a non-positive box size, a negative or over-bound coordinate, and additional properties at every level are rejected (the documented JSON Schema limit on frame containment is asserted by `test_facts.py`)

**Checkpoint**: User Stories 1 and 2 pass all Python tests.

---

## Phase 5: User Story 3 - Preprocessing is owned by the container and is full-frame (Priority: P2)

**Goal**: Boxes are in source-frame coordinates, independent of the model input size, and nothing but the container decides the settings.

**Independent Test**: Analyze frames of different resolutions and aspect ratios containing a figure at a known position and compare the boxes.

### Tests for User Story 3

- [ ] T025 [US3] Create `<cap>/tests/test_full_frame.py`: the same scene at two source resolutions and aspect ratios yields boxes that agree in relative position within 0.02 of the frame size (research.md R-14) and are expressed in each frame's own pixel coordinates; a job document carrying preprocessing, tiling, or threshold-like fields yields an unchanged result; the package contains no tile, tiled, or hybrid code path and persists nothing but the result

**Checkpoint**: User Story 3 passes.

---

## Phase 6: User Story 4 - A Club Admin registers and activates the capability through the normal Registry path (Priority: P2)

**Goal**: A digest-pinned image with a valid manifest that the Registry imports, a documented registration procedure without a seed, and Docker-backed end-to-end evidence that facts appear for a real run (the US1 and US2 scenarios, end to end).

**Independent Test**: Register the built image by digest through the Registry operations; the imported declaration shows the capability identity, version, segment scope, `required` video policy, optional media-context dependency, model digest, and the fact kind with its payload schema, and a run over a synthetic recording yields facts that meet the recall bound.

### Manifest and packaging

- [ ] T026 [P] [US4] Create `<cap>/analyst-manifest.json` valid against `contracts/analysis/analyst-manifest/v1` at manifest version `1.2.0`: capability `person-role-detection` 1.0.0, `tier` `low-level`, `executionScope` `segment`, `upstream` declaring media context as an optional dependency that this version does not consume, `videoAccess` `required`, `resources.memory` `2Gi`, `cpu` `2`, `minExecutionTimeoutSeconds` `300`, `compatibleProfiles` `["person-role-detection-dev"]`, `model.digest` the pin of `model/model-record.json`, output type `foundational-facts` with the fact kind `person-role-detection` and `output.factPayloadSchemas` carrying the same JSON as `schemas/person-role-detection.payload.schema.json`, and a description that states which specific roles it can emit (none; only `person`), that it is development evidence, and that no quality gate is met
- [ ] T027 [P] [US4] Create `<cap>/docker/fetch_verified.py` and `<cap>/Dockerfile.dockerignore` by adapting the shot-boundary capability's files (hash-verified download helper used for the toolchain and the model; ignore everything except the files the image needs)
- [ ] T028 [US4] Create `<cap>/tests/test_packaging.py` verifying the manifest against the released manifest schema (and the Registry's restricted payload-schema subset), its declared identity, scope, video policy, optional media-context dependency, fact kind, `model.digest` equal to the model record pin, and that `output.factPayloadSchemas` equals `schemas/person-role-detection.payload.schema.json` (research.md R-06)
- [ ] T029 [US4] Create `<cap>/Dockerfile`: repository-root build with the shot-boundary image's structure (`python:3.12-slim-bookworm` by digest, `uv` and FFmpeg and ffprobe 8.1 fetched with `docker/fetch_verified.py` against SHA-256 pins equal to `.github/actions/environment-setup/analyst-media-toolchain.json`, the model fetched by the same helper against `model/model-record.json` into `/opt/model/yolox_s.onnx` with its digest verified, locked dependencies, `MANIFEST_SHA256` checked before the build, literal `io.socalytics.*` labels checked against the manifest at build time including the model digest label, the manifest copied last, `USER 65532:65532`, no network or credentials at run time, no tag references); build it once with `docker build -f src/analysts/capabilities/person-role-detection/Dockerfile -t person-role-detection:local .` when a Docker engine is available
- [ ] T030 [US4] Create `<cap>/tests/test_dockerfile.py` verifying the Dockerfile's pins (digest base, toolchain hashes equal the environment file, model fetched by digest from `model-record.json`, no tag references), literal labels equal to the manifest, the manifest copied last, the non-root user, and that no model weights are committed under `<cap>/`

### Registration procedure and documentation

- [ ] T031 [US4] Create `<cap>/README.md`: purpose and development-evidence status (no production readiness, not published, not a catalog capability, catalog quality gates and threshold calibration not met, no accuracy claim beyond the synthetic fixtures), the declared detection settings and the class mapping `1.0.0` with the roles it can and cannot emit, the limitations (small or distant people, blur, occlusion, crowds, spectators, full-frame only, no tiled or hybrid profile), the failure reasons, the numeric tolerances for cross-host comparison (box coordinates within 1.0 px, confidence within 0.001, research.md R-14; a documented contract, while the tests run on one architecture class), and the numbered Development and Test registration procedure of research.md R-12 using only existing Registry operations (build and push the image to the stamp's registry host and prefix, `registerAnalystImage` by digest, create and approve the profile `person-role-detection-dev` binding capability 1.0.0 to the digest and the model digest, define a workflow with a segment node, set it as default; no seed)

### Docker-backed end-to-end evidence

- [ ] T032 [P] [US4] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/FigureMedia.cs`: generate with the pinned FFmpeg a 15-second 25 fps H.264 recording from lavfi sources with rendered figures (rectangle-composed head, torso, and legs by `drawbox`, distinct colours per nominal role) at known positions and sizes moving on known linear paths over three 5-second segments, the middle segment containing an empty-figure stretch, and the truth (figure appearances with interval and box) as data; fix the figure geometry as committed constants after first running the built image on the recording (research.md R-11 calibration stop rule: if the pinned model cannot reach the recall bound on any drawn geometry, stop and report for a spec amendment; never lower the bound or tune per run)
- [ ] T033 [P] [US4] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/PersonRoleDetectionAnalystImage.cs`: build the capability image from its Dockerfile and push it by digest, following `ShotBoundaryAnalystImage.cs` without modifying it
- [ ] T034 [US4] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/PersonRoleDetectionEndToEndTests.cs` following `ShotBoundaryEndToEndTests.cs`: register the image by digest and assert the imported declaration (identity, version, segment scope, `required` video policy, optional media-context dependency, model digest, fact kind, payload schema); approve the profile, define and default a workflow; finalize the recording of `FigureMedia.cs` under a 5-second test segmentation policy; run it through the Segment Service, Manager execution, acceptance with payload validation, and fact indexing; read the facts through `listAnalysisRunFacts` and `listAnalysisRunFactStreams`; call the existing evidence-media operation (`POST /api/v1/analysis/runs/{runId}/evidence-media`) for one fact of the run and assert that presigned segment media links covering the fact's match time are issued (FR-019, SC-004); assert every fact validates, lies inside its segment's window, and that the recall over all rendered figure appearances at sampled frames is at least 0.6 at IoU at least 0.5 with role `person`, with unmatched facts reported but not failing (SC-001); assert the empty stretch is an accepted segment with an empty fact stream; assert the four schema-violating variants (missing box, negative box coordinate, undeclared role, confidence above one) are refused at acceptance with `result-payload-invalid` and leave no visible fact (SC-002)

**Checkpoint**: The capability is registrable by digest and produces facts that meet the recall bound end to end on a Linux engine.

---

## Phase 7: User Story 5 - Maintainers can trust the model and image provenance (Priority: P2)

**Goal**: Dependency and model audit evidence and a CycloneDX bill of materials verified against the lock, and image evidence for labels, isolation, the model digest, and resource bounds.

**Independent Test**: Build the image, inspect labels, manifest, model file, and audit evidence; a model digest mismatch, a dependency without a record, or a licence outside the allowed set fails verification.

### Audit evidence

- [ ] T035 [US5] Create `<cap>/scripts/generate_audit.py` that regenerates `audit/dependency-audit.json` (`name`, `version`, `license` SPDX expression, `scope` `runtime`, `dev`, `bundled-library`, or `toolchain`, `source`, for every direct and transitive Python package of `uv.lock` including the ONNX Runtime dependencies, the native libraries bundled in the OpenCV and ONNX Runtime wheels, the FFmpeg build, and the model with source, version, licence, training-data terms, retrieval date, digest, and conversion `none`) and `audit/sbom.cdx.json` (CycloneDX of the capability layers: Python environment, FFmpeg build, model, and base image reference by digest, operating-system packages identified only by the base image digest), following the shot-boundary capability's script
- [ ] T036 [US5] Run `<cap>/scripts/generate_audit.py` to produce and commit `<cap>/audit/dependency-audit.json` and `<cap>/audit/sbom.cdx.json`, and resolve any licence outside the allowed set of the shot-boundary audit (permissive licences, LGPL for separately packaged shared libraries, the GCC runtime library exception); if a dependency's licence cannot be allowed, stop and report instead of widening the set (research.md R-08)
- [ ] T037 [US5] Create `<cap>/tests/test_audit.py`: every locked package has a record whose name and version equal `uv.lock`, every licence is in the allowed set, the model has a record whose digest equals the model record pin, the SBOM is valid CycloneDX and lists the model, FFmpeg, and the base image digest, and the test fails for a missing record, a digest mismatch, or a disallowed licence (SC-006)

### Image evidence

- [ ] T038 [US5] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/PersonRoleDetectionImageTests.cs` following `ShotBoundaryImageTests.cs`: labels equal the manifest, the model digest label equals the SHA-256 of the model file inside the image, the only data file beyond the manifest is the model, the image completes a job as a non-root user on a read-only root file system without network access (SC-003 repeated runs byte-identical with different CPU limits included)
- [ ] T039 [US5] Extend `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/PersonRoleDetectionImageTests.cs` with the resource tests: a few-second evidence segment completes within 120 seconds, and a generated one-minute 1080p clip run in a restricted container with 2 GiB memory completes within the manifest's `minExecutionTimeoutSeconds` of 300; record the measured numbers in the test output; if either bound is exceeded, stop and report that the sampling rate must change as a version decision before merge (research.md R-04, SC-005)
- [ ] T040 [US5] Add the audit evidence section to `<cap>/README.md`: what `model/` and `audit/` contain, how `scripts/generate_audit.py` regenerates the evidence, the allowed licence set, the model's source, licence, and training-data terms, that the weights are fetched at build by digest and never committed, and that the evidence starts but does not complete the catalog's audit

**Checkpoint**: All five stories are complete and verified.

---

## Phase 8: User Story 6 - The capability is evaluated through the evaluation harness (Priority: P3)

**Goal**: Record the evaluation requirement as deferred while the harness is not implemented, or add it through the harness where it exists.

**Independent Test**: The README states the deferral and no test depends on the harness.

- [ ] T041 [US6] Add an "Evaluation" section to `<cap>/README.md` recording FR-018 as deferred until the Analyst Evaluation Harness exists (research.md R-13), stating that the synthetic corpus supports no accuracy claim on real footage and that the capability is not promotable. If the harness is implemented before merge, a follow-up convergence task adds the evaluation (FR-018); this task carries no such branch

---

## Phase 9: Polish & Cross-Cutting Concerns

**Purpose**: Documentation corrections and final verification (FR-020, FR-021, FR-022, SC-007, SC-008).

### Documentation (re-read each document and fix every statement the merged state makes false)

- [ ] T042 [P] Update `docs/architecture/analyst-capability-catalog.md` per the plan's Architecture Updates so that Low-Level Capabilities (the `person-role-detection` row and the implemented-status paragraph), Catalog Rules (development reference capabilities, the first catalog-named capability, the "only other development capability" wording, and the payload-schema sentence "no catalog capability declares them yet"), and Implementation Waves state the merged outcome: `person-role-detection` exists as a first detection slice with a pretrained development model, role-unknown `person` only, full frame only, passing no gate, and the fine-tuned RT-DETRv2-S, the YOLOX challenger, tiled and hybrid inference, CUDA and TensorRT profiles, per-role AP gates, and calibration are not implemented and no Wave 1 gate is met; the evaluation harness paragraph stays true and notes the deferred evaluation (FR-020, FR-022)
- [ ] T043 [P] Update `docs/architecture/analysts-models-and-hardware.md` (Image Metadata And Packaging: name the third development image, the first with model weights, with ONNX Runtime CPU, the pretrained model fetched at build by digest, and the model audit, keeping "no model weights" true only for the first two images; the result manifest and lineage statement that no catalog capability declares payload schemas; the Docker Desktop adapter evidence sentence's list of capabilities verified end to end; keep registries, publication, the catalog's licence and provenance audit, and production values open)
- [ ] T044 [P] Update `docs/architecture/analyst-runtime-and-recovery.md` so that the list of capabilities verified end to end against a Linux engine includes `person-role-detection` as development evidence, and no statement there contradicts the merged state
- [ ] T045 [P] Update `README.md`, `AGENTS.md`, and `src/analysts/README.md` (source-area structure, executable evidence, the sentences that list `reference-media-probe` and `media-context-classification` as the only capability projects under `capabilities/`, and the sentences that call the shot-boundary baseline the only catalog-named capability), stating that `src/analysts/capabilities/person-role-detection` is executable as development evidence (pretrained YOLOX-S model, role-unknown `person`, full frame, not a catalog capability, passing no gate, never published) and adding its image build and test commands without implying production readiness
- [ ] T046 [P] Update the `src/analysts` bullet of the source-area list in `docs/architecture/platform-implementation.md` so that it names `person-role-detection` under `src/analysts/capabilities/person-role-detection` as a first detection slice with a pretrained development model (development evidence only, no catalog gate met, never in a production workflow) beside the shot-boundary baseline and the reference capability, and that the rest of the catalog's Analyst capabilities remain future (Constitution I)

### Final verification

- [ ] T047 Run the full verification of [quickstart.md](quickstart.md): the locked Python sequence and `uv run --no-sync pytest` in `<cap>/` (with `PERSON_ROLE_MODEL_PATH` set to the pinned model so the real-model tests run), the same in `src/analysts/capabilities/media-context-classification` and `src/analysts/capabilities/reference-media-probe` (unchanged and passing), `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~PersonRoleDetection"`, `--filter "FullyQualifiedName~ShotBoundary"`, and `--filter "FullyQualifiedName~ReferenceAnalyst"` on a Linux engine, and `node .github/scripts/check-markdown.mjs`; fix every finding caused by this feature, and record the outcome by appending a short "Verification" section to `<cap>/README.md` (one line per command run with its result, such as the test counts; it must not be under `specs/`, `.github/`, or `.specify/`, because the land job rejects an agent change to those protected paths), because a task whose agent changes no file fails

---

## Dependencies & Execution Order

- Phase 1 then Phase 2 then the stories. T002 needs T001 and adds the tracked `tests/test_environment.py`, because a task whose agent changes no file fails.
- T003 needs T002 and precedes T011 (the pin), T026, T029, and T036.
- Phase 2: T004 needs T003 and runs alone, because T008 to T011 use its constants; T005 to T011 need T004 and are independent files; the tests T012 to T015 need the modules and support they exercise (T012 needs T005, T006, T008; T013 needs T009; T014 needs T007 and T010; T015 needs T007 and T011).
- US1: T016 and T017 need Phase 2; T018 needs T016, T017, and Phase 2; the tests T019 to T022 need T018.
- US2 needs US1: T023 and T024 need T017, T016, and T018.
- US3 needs US1: T025 needs T018.
- US4 needs US1 and US2 for a complete capability: T026 needs T003 and T016; T028 needs T026; T029 needs T026, T027, and the Phase 2 and US1 modules; T030 needs T029; T031 may start after T026; T032 needs T029; T033 needs T029; T034 needs T032 and T033 and the schema variants need T026.
- US5 needs US4: T036 needs T035; T037 needs T036; T038 needs T029 and T036; T039 needs T038; T040 needs T031 and T036.
- US6: T041 needs T031 and T040, because it edits the same README.
- Polish needs all stories; T047 runs last and appends a "Verification" section to `<cap>/README.md`, because a task whose agent changes no file fails and a task that changes a file under `specs/` fails as a protected-path change.

## Parallel Execution Examples

- Phase 2: T004 alone, then T005 to T011 in parallel (distinct files), then T012 to T015 in parallel.
- US1: T016 and T017 in parallel, then T018, then T019 to T022 in parallel.
- US2: T023 and T024 in parallel.
- US4: T026 and T027 in parallel, then T032 and T033 in parallel.
- Polish: T042 to T046 in parallel.

## Implementation Strategy

- **MVP**: Phases 1 to 3 (US1) deliver `person-role-detection` facts through the SDK entry point, verified by the Python tests.
- **Increment**: US2 proves schema conformance and provenance; US3 proves full-frame container-owned preprocessing; US4 makes
  the capability registrable and proves it end to end; US5 adds the model and dependency provenance evidence and the
  image bounds; US6 records the evaluation deferral; Polish corrects the documentation.
- Stop and report instead of working around an environment gap (a locked wheel that cannot be installed, an unconfirmed
  model licence or digest, a disallowed licence, or a missed recall or resource bound); never change the environment
  actions, the SDK, or the platform in this feature, and never lower a bound.
