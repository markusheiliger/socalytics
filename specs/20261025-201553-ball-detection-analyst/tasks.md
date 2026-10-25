# Tasks: Ball Detection Analyst (Development Baseline)

**Input**: Design documents from `/specs/20261025-201553-ball-detection-analyst/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/ball-observation-fact.md](contracts/ball-observation-fact.md), [quickstart.md](quickstart.md)

**Tests**: Included. The spec requires unit, image, audit, and Docker-backed end-to-end evidence (FR-016, FR-018,
FR-015, SC-001 to SC-006).

**Organization**: Tasks are grouped by user story. `[P]` marks tasks that touch different files; consecutive `[P]` tasks under one heading run in parallel, each from the same commit.

## Format: `- [ ] [ID] [P?] [Story] Description with file path`

## Path Conventions

- Capability project: `src/analysts/capabilities/ball-detection/` (abbreviated `<cap>/` in descriptions)
- Python package: `<cap>/src/ball_detection/`
- Docker-backed tests: `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/`
- Mirror the layout and conventions of `src/analysts/capabilities/media-context-classification/` (the shot-boundary
  capability), which stays unchanged together with `reference-media-probe` (FR-022). Do not change the SDK, the platform,
  contracts, the Web UI, seeds, the existing EndToEnd helpers, or `.github/actions/` (FR-001, FR-017, FR-020). Share no
  code, file, or image layer with `person-role-detection` (FR-022).
- The OpenCV, NumPy, and ONNX Runtime CPU wheels come from the dependent environment spec
  [CV and Inference Verification Environment](../20261023-085421-cv-inference-environment/spec.md). If a locked wheel cannot be
  installed by the locked sequence, stop and report that the environment spec must land first (research.md R-09, R-10).

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the locked Python project.

- [ ] T001 Create `<cap>/pyproject.toml` (Python `>=3.12,<3.13`, hatchling build backend, `socalytics-analyst-sdk` as the path source `../../sdk`, runtime dependencies `opencv-python-headless`, `numpy`, and `onnxruntime` at the versions the environment spec pins, dev group `hatchling`, `pytest`, and `jsonschema`, wheels only), `<cap>/.gitignore`, the hash-locked `<cap>/uv.lock` created by `UV_PYTHON_DOWNLOADS=never uv lock` in `<cap>/` (the project verification requires a committed `uv.lock` beside each `pyproject.toml`), `<cap>/src/ball_detection/__init__.py` exporting `CAPABILITY_ID` (`ball-detection`), `CAPABILITY_VERSION` (`1.0.0`), and `FACT_KIND` (`ball-observation`), and `<cap>/tests/test_identity.py` asserting those three constants (pytest exits non-zero when it collects no test), following `media-context-classification`
- [ ] T002 Run, with the `<cap>/uv.lock` that T001 committed, the locked sequence from [quickstart.md](quickstart.md) section 1 (`uv sync --locked --all-groups --no-install-local --no-build`, `uv sync --locked --all-groups --no-build-isolation`, `uv build --wheel --no-build-isolation --python .venv/bin/python`); then create `<cap>/tests/test_environment.py` asserting that `cv2`, `numpy`, `onnxruntime`, and `socalytics_analyst` import from the locked environment, that their versions equal the versions in `<cap>/uv.lock`, and that `onnxruntime.get_available_providers()` contains `CPUExecutionProvider` (the task must leave a tracked file); if a locked wheel cannot be installed, stop and report that the environment spec must land first

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The pinned model, settings, frame acquisition, preprocessing, postprocessing, visibility rule, the inference seam, and the test support every story needs.

**CRITICAL**: No user story work begins until this phase is complete.

### Model pin

- [ ] T003 Audit and pin the model per research.md R-01: fetch the official YOLOX-S ONNX file of the upstream Megvii release (Apache-2.0) with `<cap>/docker/fetch_verified.py` (or an equivalent hash-checked download) into the untracked directory `<cap>/.cache/` (added to the capability's `.gitignore`), confirm the upstream licence, the training-data terms (COCO 2017), and file provenance, and create `<cap>/model/model-record.json` (`id` `yolox-s`, `version` upstream release tag, `source` release URL, `licence` `Apache-2.0`, `trainingDataTerms` COCO 2017 terms, `retrievedOn`, `sha256` of the file as the single pin for build, start, label, and audit, `conversion` `none`, `status` `development evidence`) and `<cap>/tests/test_model_record.py` asserting the fields and that a present file at `BALL_DETECTION_MODEL_PATH` has the pinned digest; never commit the weights (FR-014); stop and report for a spec amendment if the licence or provenance cannot be confirmed or the digest cannot be pinned (research.md R-01 stop rule)

### Settings

- [ ] T004 Create `<cap>/src/ball_detection/settings.py` with the declared constants of [data-model.md](data-model.md) (`DETECTOR_VERSION` `1.0.0`, `SAMPLING_RULE` `fixed-5fps-v1` with a 200 ms grid, `INPUT_SIZE` 640, pad value 114, `SOURCE_CLASS` `sports ball` with COCO id 32, `CONFIDENCE_FLOOR` 0.20, `MERGE_IOU` 0.45, `MAX_OBSERVATIONS_PER_FRAME` 5, `VISIBILITY_RULE_VERSION` `1.0.0` with threshold 0.50, `DECODE_BATCH` 32, `THREADS` 1, rounding of box and centre values to 2 and confidence to 4 decimals, the model path `/opt/model/yolox_s.onnx` overridable only through `BALL_DETECTION_MODEL_PATH` for tests) and a `settings` provenance mapping for the facts with exactly the eleven keys of the contract (`samplingRule`, `inputSize`, `confidenceFloor`, `mergeRule`, `mergeIou`, `maxObservationsPerFrame`, `profile`, `detectorVersion`, `padValue`, `interpolation`, and `visibilityThreshold`; pad value 114, interpolation `linear`, visibility threshold 0.50); none is read from the job, profile, or Manager

### Foundational modules and test support

- [ ] T005 [P] Create `<cap>/tests/support/__init__.py` and `<cap>/tests/support/media.py`: helpers that generate small clips at test time with the pinned `ffmpeg` (a rendered ball at known positions and diameters, no ball, a single frame, variable frame rate, lead-in frames before the nominal window, several source resolutions and aspect ratios); no committed binary media
- [ ] T006 [P] Create `<cap>/tests/support/mounts.py` and `<cap>/tests/conftest.py`: build a job document, attempt context, and read-only staged media mount (`segment-manifest.json` and `segment.mp4`, non-zero segment offset supported) for the SDK, following the shot-boundary capability's test support
- [ ] T007 [P] Create `<cap>/tests/support/detectors.py`: a deterministic scripted `Detector` that returns raw YOLOX head tensors (`x, y, w, h, objectness, 80 class scores` per anchor for strides 8, 16, and 32) for known boxes and classes, so mechanics tests need no model file (research.md R-11)
- [ ] T008 [P] Create `<cap>/src/ball_detection/frames.py`: `ffprobe` listing of `best_effort_timestamp` for every frame of the first video stream mapped to match time through `SegmentMedia.to_match_ms`; the sampling rule `fixed-5fps-v1` (grid point `t` every 200 ms anchored at the nominal window start, the first in-window frame at or after `t` and before `t + 200`, a grid point without a frame skipped, a frame exactly at the window end outside); batched full-size `bgr24` decode of the selected zero-based frame indices with `ffmpeg` `select` and `-fps_mode passthrough` (at most 32 indices per invocation, one batch in memory); the named reasons `media-decode-failed` (tool cannot start, exits non-zero, undecodable, or index and timestamp count mismatch or a missing frame in a batch), `media-no-video-stream`, and `timestamp-unmappable` (research.md R-04, R-05, R-08)
- [ ] T009 [P] Create `<cap>/src/ball_detection/preprocess.py`: letterbox to 640x640 (scale `min(640/w, 640/h)`, `cv2.INTER_LINEAR`, pad 114, BGR order, float32, no normalization, HWC to CHW), the inverse remap of boxes to source-frame coordinates, and `cv2.setNumThreads(1)` (research.md R-03)
- [ ] T010 [P] Create `<cap>/src/ball_detection/postprocess.py`: YOLOX head decode (grid and stride transform for strides 8, 16, 32), score `objectness * class score`, selection of COCO class 32 (`sports ball`) only, the 0.20 floor, greedy NMS at IoU 0.45 applied once per frame (highest confidence first, ties by smaller top then smaller left), the cap of 5 observations, remap with clipping to the frame, dropping boxes with width or height below 1 px after clipping, the centre of the clipped box, and rounding after the cap (research.md R-02, R-03)
- [ ] T011 [P] Create `<cap>/src/ball_detection/model.py`: the `Detector` protocol; the ONNX Runtime CPU session (`intra_op_num_threads=1`, `inter_op_num_threads=1`, `ORT_SEQUENTIAL`, fixed graph optimization level, `CPUExecutionProvider` only); the start check that raises `model-missing` for an absent or unreadable file and `model-digest-mismatch` for a SHA-256 that differs from the pin in `model/model-record.json` (the pin is also available to the package as a constant); `model-inference-failed` when the session cannot be created or run
- [ ] T012 [P] Create `<cap>/src/ball_detection/visibility.py`: the single-frame visibility rule `1.0.0` (`confidently-visible` when the confidence is at least 0.50, else `not-confidently-visible`), using the frame's own score only and no state for occlusion, off-frame, or out of play (research.md R-06)

### Foundational tests

- [ ] T013 [P] Create `<cap>/tests/test_support.py` verifying the generated clips and mounts (frame counts, ball positions, offsets) and `<cap>/tests/test_frames.py` verifying timestamp and frame pairing, the sampling rule with a non-zero segment offset, variable frame rate, a single-frame and an empty clip, lead-in frames never selected, a frame exactly at the window end excluded, the batch size of at most 32, and the `media-decode-failed` and `media-no-video-stream` reasons
- [ ] T014 [P] Create `<cap>/tests/test_preprocess.py` verifying the letterbox (scale, padding, value 114, channel order) and the box remap across several source resolutions and aspect ratios, independent of the model input size (FR-016)
- [ ] T015 [P] Create `<cap>/tests/test_postprocess.py` verifying head decode on scripted tensors, class selection (only COCO `sports ball`, other classes never emitted), the 0.20 floor, the duplicate merge applied once per frame with no pair above 0.45 IoU remaining, the cap of 5 with deterministic tie-breaking, clipping and dropping of boxes below 1 px, the centre of the clipped box, and rounding
- [ ] T016 [P] Create `<cap>/tests/test_model.py` verifying `model-missing`, `model-digest-mismatch`, and `model-inference-failed` with temporary files, and a real-model smoke detection and thread-independent output that run when the model file is present at `BALL_DETECTION_MODEL_PATH` and otherwise skip with an explicit reason (research.md R-11)
- [ ] T017 [P] Create `<cap>/tests/test_visibility.py` verifying the visibility rule at the boundaries (0.20, 0.4999, 0.50, 1.0), that the set is exactly `confidently-visible` and `not-confidently-visible`, and that the rule reads nothing but the single observation's confidence

**Checkpoint**: Model pinned; frame acquisition, preprocessing, postprocessing, visibility, and the inference seam work; stories can start.

---

## Phase 3: User Story 1 - A recorded match yields ball observation facts (Priority: P1) MVP

**Goal**: A segment's analyzed frames become exactly one `ball-observation` fact per retained observation, written through the SDK entry point.

**Independent Test**: Run the capability on a generated clip with a rendered ball (and a non-zero segment offset) through the SDK test mounts: facts exist for the detected ball inside the nominal window, a segment without a detectable ball yields an accepted empty result, and repeated runs are byte-identical.

### Schema and facts

- [ ] T018 [P] [US1] Create `<cap>/schemas/ball-observation.payload.schema.json` for the payload of [contracts/ball-observation-fact.md](contracts/ball-observation-fact.md) in the Registry's restricted self-contained JSON Schema 2020-12 subset (internal `$defs` and `$ref`, no formats, no external references): `box` with `left`, `top` from 0 to 16384 and `width`, `height` with `exclusiveMinimum` 0 (and at most 16384); `centre` `x`, `y` from 0 to 16384; `frame` `width` and `height` integers of at least 1; `confidence` from 0 to 1; `visibility` with `state` enum `confidently-visible`, `not-confidently-visible` and `ruleVersion`; `frameIndex` integer of at least 0; `model` with `id`, `version`, `digest` (`sha-256:` plus 64 hex), `sourceClass`, `sourceClassId`; `settings` with the eleven required properties of the contract, `samplingRule`, `inputSize`, `confidenceFloor`, `mergeRule`, `mergeIou`, `maxObservationsPerFrame`, `profile`, `detectorVersion`, `padValue`, `interpolation`, and `visibilityThreshold`, each typed as the contract states and restricted to a single-value `enum` of its version's constant; and `additionalProperties: false` at every level
- [ ] T019 [P] [US1] Create `<cap>/src/ball_detection/facts.py`: convert an observation to a `Fact` (kind `ball-observation`, `startMs` the match time of the analyzed frame, `endMs` the match time of the next analyzed frame or the window end for the last one and always greater than `startMs`, envelope confidence equal to the payload confidence, payload and provenance per the contract including the visibility state and rule version from `visibility.py`, the model digest, sampling rule, and settings), ordered by `startMs`, then confidence descending, then box top, then box left

### Entry point

- [ ] T020 [US1] Create `<cap>/src/ball_detection/__main__.py`: SDK entry point that reads only the job document, the staged segment media, and the model file (no network, credentials, upstream results, or run-time download; job fields that look like settings are ignored; media context is not consumed); fails with `segment-media-required` when the job has no staged media; verifies the model digest, selects frames with `frames.py`, runs one full-frame pass per analyzed frame through `preprocess.py`, `model.py`, and `postprocess.py` with one decoded frame in memory at a time and no state carried between frames, builds facts with `facts.py`, and writes the result as output type `foundational-facts` schema version `1.0.0` (an empty result for a segment without an analyzable frame or observation) through the SDK, which validates the payload schema before writing; a failure writes no result

### Tests for User Story 1

- [ ] T021 [P] [US1] Create `<cap>/tests/test_detection.py`: end-to-end through the SDK test mounts with the scripted detector, two segments with non-zero offsets, an empty segment accepted with no facts, a single-frame segment, and, when the model file is present, a real-model run on a rendered ball that yields at least one fact
- [ ] T022 [P] [US1] Create `<cap>/tests/test_time_mapping.py`: fact positions derive from frame timestamps through `SegmentMedia.to_match_ms` and `segment_window` with a non-zero segment offset and with variable frame rate, lead-in frames never analyzed, every fact inside the half-open nominal window, the range ending at the next analyzed frame or the window end, and `timestamp-unmappable` for a frame that cannot be mapped
- [ ] T023 [P] [US1] Create `<cap>/tests/test_determinism.py`: at least three runs on the same media yield byte-identical results, including runs under different CPU limits (for example `taskset` with one and several CPUs) and different thread environment settings (SC-003); the real-model variant skips without the file
- [ ] T024 [P] [US1] Create `<cap>/tests/test_failures.py`: every named failure reason (`segment-media-required`, `media-decode-failed`, `media-no-video-stream`, `timestamp-unmappable`, `model-missing`, `model-digest-mismatch`, `model-inference-failed`) ends the attempt as failed with that reason and writes no result file

**Checkpoint**: User Story 1 passes `uv run --no-sync pytest` for its tests.

---

## Phase 4: User Story 2 - Every observation is validated, attributable, and honest about a single frame (Priority: P1)

**Goal**: Every fact validates against the declared payload schema, carries the provenance of its model, settings, and frame, and claims no more than one frame shows.

**Independent Test**: Validate every fact written on generated frames against the schema, confirm that schema-violating payloads are rejected, and confirm that no state crosses frames.

### Tests for User Story 2

- [ ] T025 [P] [US2] Create `<cap>/tests/test_facts.py`: range rules (start at the analyzed frame, end at the next analyzed frame or window end, never beyond the window end, never empty), envelope confidence equal to the payload confidence, ordering by `startMs`, confidence, top, and left, the centre equal to the centre of the box, the box inside the exact frame, the zero-based `frameIndex` counting lead-in frames, the visibility state and rule version, and the provenance of the contract (model id, version, digest equal to the manifest's, source class, sampling rule, settings whose keys equal the payload schema's required `settings` set)
- [ ] T026 [P] [US2] Create `<cap>/tests/test_payload_schema.py`: every produced payload validates against `schemas/ball-observation.payload.schema.json` with `jsonschema`, and a missing box, a missing field, a negative or over-bound coordinate, an undeclared visibility state, a confidence outside zero to one, a non-positive box size, a missing frame index, and additional properties at every level are rejected (the documented JSON Schema limit on frame containment is asserted by `test_facts.py`)
- [ ] T027 [P] [US2] Create `<cap>/tests/test_single_frame.py`: observations of a frame are identical whether the frame is analyzed alone or inside a longer clip (no cross-frame state, FR-007), no track, ball identifier, velocity, or pitch coordinate appears in any payload, a frame with a weak observation above the floor is written as `not-confidently-visible`, and a frame without a detection after a detection writes no fact (nothing is invented from neighbouring frames)

**Checkpoint**: User Stories 1 and 2 pass all Python tests.

---

## Phase 5: User Story 3 - Preprocessing is owned by the container, full-frame, and honest about small objects (Priority: P2)

**Goal**: Boxes are in source-frame coordinates, independent of the model input size, and nothing but the container decides the settings.

**Independent Test**: Analyze frames of different resolutions and aspect ratios containing a ball at a known position and compare the geometry.

### Tests for User Story 3

- [ ] T028 [US3] Create `<cap>/tests/test_full_frame.py`: the same scene at two source resolutions and aspect ratios yields geometry that agrees in relative centre position within 0.01 of the frame size (research.md R-15) and is expressed in each frame's own pixel coordinates; a job document carrying preprocessing, tiling, or threshold-like fields yields an unchanged result; the package contains no tile, tiled, or hybrid code path and persists nothing but the result

**Checkpoint**: User Story 3 passes.

---

## Phase 6: User Story 4 - A Club Admin registers and activates the capability through the normal Registry path (Priority: P2)

**Goal**: A digest-pinned image with a valid manifest that the Registry imports, a documented registration procedure without a seed, and Docker-backed end-to-end evidence that facts appear for a real run (the US1 and US2 scenarios, end to end).

**Independent Test**: Register the built image by digest through the Registry operations; the imported declaration shows the capability identity, version, segment scope, `required` video policy, optional media-context dependency, model digest, and the fact kind with its payload schema, and a run over a synthetic recording yields facts that meet the recall bound.

### Manifest and packaging

- [ ] T029 [P] [US4] Create `<cap>/analyst-manifest.json` valid against `contracts/analysis/analyst-manifest/v1` at manifest version `1.2.0`: capability `ball-detection` 1.0.0, `tier` `low-level`, `executionScope` `segment`, `upstream` with exactly one entry `capabilityId` `media-context-classification`, `capabilityVersion` `1.0.0`, `schemaVersion` `1.0.0`, `kind` `optional` (no coverage block) that this version does not consume, `videoAccess` `required`, `resources.memory` `2Gi`, `cpu` `2`, `minExecutionTimeoutSeconds` `300`, `compatibleProfiles` `["ball-detection-dev"]`, `model.digest` the pin of `model/model-record.json`, `runtime.constraints` `["linux-amd64"]` with no accelerators, deterministic and idempotent, output type `foundational-facts` with the fact kind `ball-observation` and `output.factPayloadSchemas` carrying the same JSON as `schemas/ball-observation.payload.schema.json`, and a description that states it is development evidence with a pretrained generic model, full frame only, and that no quality gate is met
- [ ] T030 [P] [US4] Create `<cap>/docker/fetch_verified.py` and `<cap>/Dockerfile.dockerignore` by adapting the shot-boundary capability's files (hash-verified download helper used for the toolchain and the model; ignore everything except the files the image needs)
- [ ] T031 [US4] Create `<cap>/tests/test_packaging.py` verifying the manifest against the released manifest schema (and the Registry's restricted payload-schema subset), its declared identity, scope, video policy, optional media-context dependency, fact kind, `model.digest` equal to the model record pin, `output.factPayloadSchemas` equal to `schemas/ball-observation.payload.schema.json`, and package constants equal to the manifest (research.md R-07); that the package's pinned-digest constant equals `sha256` in `model/model-record.json` and the manifest's `model.digest` (with the `sha-256:` prefix); that the manifest `description` and `README.md` contain the development-evidence, not-catalog, no-gate-met, and no-accuracy-claim statements and neither contains forbidden phrases such as `production-ready` or `trajectory` (SC-008); and, with `SOCALYTICS_ANALYST_MANIFEST` pointing at `<cap>/analyst-manifest.json`, that the SDK write refuses each of the five violating variants (missing box, negative coordinate, undeclared visibility state, confidence above one, missing `frameIndex`) without writing a result file and writes valid facts (FR-009, SC-002)
- [ ] T032 [US4] Create `<cap>/Dockerfile`: repository-root build with the shot-boundary image's structure (`python:3.12-slim-bookworm` by digest, `uv` and FFmpeg and ffprobe 8.1 fetched with `docker/fetch_verified.py` against SHA-256 pins equal to `.github/actions/environment-setup/analyst-media-toolchain.json`, the model fetched by the same helper against `model/model-record.json` into `/opt/model/yolox_s.onnx` with its digest verified, locked dependencies, `MANIFEST_SHA256` checked before the build, literal `io.socalytics.*` labels checked against the manifest at build time including the model digest label, the manifest copied last, `USER 65532:65532`, no network or credentials at run time, no tag references); build it once with `docker build -f src/analysts/capabilities/ball-detection/Dockerfile -t ball-detection:local .` when a Docker engine is available
- [ ] T033 [US4] Create `<cap>/tests/test_dockerfile.py` verifying the Dockerfile's pins (digest base, toolchain hashes equal the environment file, model fetched by digest from `model-record.json`, no tag references), literal labels equal to the manifest, the manifest copied last, the non-root user, and that no model weights are committed under `<cap>/`

### Registration procedure and documentation

- [ ] T034 [US4] Create `<cap>/README.md`: purpose and development-evidence status (no production readiness, not published, not a catalog capability, catalog quality gates and threshold calibration not met, no accuracy claim beyond the synthetic fixtures, no trajectory or tracking), the declared detection settings, the visibility set and rule `1.0.0`, the limitations (a 640x640 full-frame pass loses distant small balls, a missing observation means "not detected in this frame" and never "no ball", no tiled or hybrid profile, small-ball recall unmeasured), the failure reasons, the numeric tolerances for cross-host comparison (coordinates within 1.0 px, confidence within 0.001, relative centre position across resolutions within 0.01, research.md R-15; a documented contract, while the tests run on one architecture class), and the numbered Development and Test registration procedure of research.md R-13 using only existing Registry operations (build and push the image to the stamp's registry host and prefix, `registerAnalystImage` by digest, create and approve the profile `ball-detection-dev` binding capability 1.0.0 to the digest and the model digest with a `required` video policy, define a workflow with a segment node, set it as default; no seed)

### Docker-backed end-to-end evidence

- [ ] T035 [P] [US4] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/BallMedia.cs`: generate with the pinned FFmpeg a 15-second 25 fps 1280x720 H.264 recording from lavfi sources (green pitch-like background, one rendered ball of diameter 48 px as a white disk with dark patches moving along a known piecewise-linear path) over three 5-second segments, the middle segment containing a stretch with no ball, and the truth (ball appearances per frame with centre and diameter) as data; fix the ball appearance and path as committed constants after first running the built image on the recording (research.md R-12 calibration stop rule: if the pinned model cannot reach the recall bound on any reasonable drawn ball, stop and report for a spec amendment; never lower the bound or tune per run)
- [ ] T036 [P] [US4] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/BallDetectionAnalystImage.cs`: build the capability image from its Dockerfile and push it by digest, following `ShotBoundaryAnalystImage.cs` without modifying it
- [ ] T037 [US4] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/BallDetectionEndToEndTests.cs` following `ShotBoundaryEndToEndTests.cs`: register the image by digest and assert the imported declaration (identity, version, segment scope, `required` video policy, optional media-context dependency, model digest, fact kind, payload schema); approve the profile, define and default a workflow with only the `ball-detection` segment node (no media-context node) and assert that the run is created and the node becomes ready without one; if registration or run creation refuses the optional upstream without a matching node, stop and report for a spec amendment rather than adding a node or changing the manifest; finalize the recording of `BallMedia.cs` under a 5-second test segmentation policy; run it through the Segment Service, Manager execution, acceptance with payload validation, and fact indexing; read the facts through `listAnalysisRunFacts` and `listAnalysisRunFactStreams`; call the existing evidence-media operation (`POST /api/v1/analysis/runs/{runId}/evidence-media`) for one fact and assert that presigned segment media links covering its match time are issued (FR-020); assert every fact validates, lies inside its segment's window, and carries a frame identity; assert the recall over all rendered ball appearances at analyzed frames is at least 0.5, deriving the analyzed frames by applying the sampling rule `fixed-5fps-v1` to each segment's nominal window over the recording's known 25 fps timeline (grid points every 200 ms from each window start, that is every 5th recording frame), counting a truth appearance for each such frame in which the ball is present, and matching a fact when its `startMs` equals that frame's match time and its centre lies within the truth diameter of the truth centre (`frameIndex` is only asserted to be present, an integer >= 0, and consistent with the segment file, and is not compared with the recording's truth index), with unmatched facts reported but not failing (SC-001); assert the ball-free stretch is an accepted segment with an empty fact stream; assert the five schema-violating variants (missing box, a negative coordinate, an undeclared visibility state, a confidence above one, a missing frame index) are refused at acceptance with `result-payload-invalid` and leave no visible fact (SC-002)

**Checkpoint**: The capability is registrable by digest and produces facts that meet the recall bound end to end on a Linux engine.

---

## Phase 7: User Story 5 - Maintainers can trust the model and image provenance (Priority: P2)

**Goal**: Dependency and model audit evidence and a CycloneDX bill of materials verified against the lock, and image evidence for labels, isolation, the model digest, and resource bounds.

**Independent Test**: Build the image, inspect labels, manifest, model file, and audit evidence; a model digest mismatch, a dependency without a record, or a licence outside the allowed set fails verification.

### Audit evidence

- [ ] T038 [US5] Create `<cap>/scripts/generate_audit.py` that regenerates `audit/dependency-audit.json` (`name`, `version`, `license` SPDX expression, `scope` `runtime`, `dev`, `bundled-library`, or `toolchain`, `source`, for every direct and transitive Python package of `uv.lock` including the ONNX Runtime dependencies, the native libraries bundled in the OpenCV and ONNX Runtime wheels, the FFmpeg build, and the model with source, version, licence, training-data terms, retrieval date, digest, and conversion `none`) and `audit/sbom.cdx.json` (CycloneDX of the capability layers: Python environment, FFmpeg build, model, and base image reference by digest, operating-system packages identified only by the base image digest), following the shot-boundary capability's script
- [ ] T039 [US5] Run `<cap>/scripts/generate_audit.py` to produce and commit `<cap>/audit/dependency-audit.json` and `<cap>/audit/sbom.cdx.json`, and resolve any licence outside the allowed set of the shot-boundary audit (permissive licences, LGPL for separately packaged shared libraries, the GCC runtime library exception); if a dependency's licence cannot be allowed, stop and report instead of widening the set (research.md R-09)
- [ ] T040 [US5] Create `<cap>/tests/test_audit.py`: every locked package has a record whose name and version equal `uv.lock`, every licence is in the allowed set, the model has a record whose digest equals the model record pin, the SBOM is valid CycloneDX and lists the model, FFmpeg, and the base image digest, and the test fails for a missing record, a digest mismatch, or a disallowed licence, using synthetic inputs for the failing cases (SC-006)

### Image evidence

- [ ] T041 [US5] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/BallDetectionImageTests.cs` following `ShotBoundaryImageTests.cs`: labels equal the manifest, the model digest label equals the SHA-256 of the model file inside the image, the only data file beyond the manifest is the model, the image completes a job as a non-root user on a read-only root file system without network access, repeated runs with different CPU limits are byte-identical (SC-003), and the installed Python distributions in the image match `audit/dependency-audit.json`
- [ ] T042 [US5] Extend `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/BallDetectionImageTests.cs` with the resource tests: a few-second evidence segment completes within 120 seconds, and a generated one-minute 1080p clip run in a restricted container with 2 GiB memory completes within the manifest's `minExecutionTimeoutSeconds` of 300 with peak memory read from the container's cgroup below 2 GiB; record the measured numbers in the test output; if either bound is exceeded, stop and report that the sampling rate must change as a version decision before merge (research.md R-04, SC-005)
- [ ] T043 [US5] Add the audit evidence section to `<cap>/README.md`: what `model/` and `audit/` contain, how `scripts/generate_audit.py` regenerates the evidence, the allowed licence set, the model's source, licence, and training-data terms, why the chosen model was preferred over copyleft candidates (research.md R-01), that the weights are fetched at build by digest and never committed, and that the evidence starts but does not complete the catalog's audit

**Checkpoint**: All five stories are complete and verified.

---

## Phase 8: User Story 6 - The capability is evaluated through the evaluation harness (Priority: P3)

**Goal**: Record the evaluation requirement as deferred while the harness is not implemented, or add it through the harness where it exists.

**Independent Test**: The README states the deferral and no test depends on the harness.

- [ ] T044 [US6] Add an "Evaluation" section to `<cap>/README.md` recording FR-019 as deferred until the Analyst Evaluation Harness exists (research.md R-14), stating that the synthetic corpus supports no accuracy claim on real footage and that the capability is not promotable. If the harness is implemented before merge, a follow-up convergence task adds the evaluation (FR-019); this task carries no such branch

---

## Phase 9: Polish & Cross-Cutting Concerns

**Purpose**: Documentation corrections and final verification (FR-021, FR-022, FR-023, SC-007, SC-008).

### Documentation (re-read each document and fix every statement the merged state makes false)

- [ ] T045 [P] Update `docs/architecture/analyst-capability-catalog.md` per the plan's Architecture Updates so that Low-Level Capabilities (the `ball-detection` row and the implemented-status paragraph), Catalog Rules (development reference capabilities, the first catalog-named capability, the wording about how many development or catalog-named capabilities exist, and the payload-schema sentence "no catalog capability declares them yet" wherever it would become false), and Implementation Waves state the merged outcome: `ball-detection` exists as a first single-frame slice with a pretrained development model, full frame only, passing no gate, and the separately fine-tuned RT-DETRv2-S, the YOLOX-S challenger, SAHI-style tiled and hybrid inference, CUDA and TensorRT profiles, ball and small-object AP gates, trajectory, and calibration are not implemented and no Wave 1 gate is met; the container-owned preprocessing section where it refers to implementation status stays true; the evaluation harness paragraph stays true and notes the deferred evaluation (FR-023)
- [ ] T046 [P] Update `docs/architecture/analysts-models-and-hardware.md` (Image Metadata And Packaging: name the development image with model weights fetched at build by digest, ONNX Runtime CPU, and the model audit, keeping "no model weights" true only for the images that have none; the result manifest and lineage statement about which capabilities declare payload schemas; the Docker Desktop adapter evidence sentence's list of capabilities verified end to end; keep registries, publication, the catalog's licence and provenance audit, and production values open)
- [ ] T047 [P] Update `docs/architecture/analyst-runtime-and-recovery.md` so that the list of capabilities verified end to end against a Linux engine includes `ball-detection` as development evidence, and no statement there contradicts the merged state
- [ ] T048 [P] Update the `src/analysts` bullet of the source-area list in `docs/architecture/platform-implementation.md` so that it names `ball-detection` under `src/analysts/capabilities/ball-detection` as a first single-frame ball slice with a pretrained development model (development evidence only, no catalog gate met, never in a production workflow) beside the other development capabilities, and that the rest of the catalog's Analyst capabilities remain future (Constitution I)
- [ ] T049 [P] Update `README.md`, `AGENTS.md`, and `src/analysts/README.md` (source-area structure, executable evidence, and the sentences that list the capability projects under `capabilities/` or call any capability the only catalog-named one), stating that `src/analysts/capabilities/ball-detection` is executable as development evidence (pretrained YOLOX-S model, `sports ball` class, tracking-free single-frame observations, full frame, not a catalog capability, passing no gate, never published) and adding its image build and test commands without implying production readiness; search the documentation outside `docs/architecture/` for any other statement this makes false

### Final verification

- [ ] T050 Run the full verification of [quickstart.md](quickstart.md): the locked Python sequence and `uv run --no-sync pytest` in `<cap>/` (with `BALL_DETECTION_MODEL_PATH` exported as the absolute path of the pinned model in `<cap>/.cache/` so the real-model tests run instead of skipping), the same in `src/analysts/capabilities/media-context-classification` and `src/analysts/capabilities/reference-media-probe` (unchanged and passing), `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~BallDetection"`, `--filter "FullyQualifiedName~ShotBoundary"`, and `--filter "FullyQualifiedName~ReferenceAnalyst"` on a Linux engine, and `node .github/scripts/check-markdown.mjs`; fix every finding caused by this feature, and record the outcome by appending a short "Verification" section to `<cap>/README.md` (one line per command run with its result, such as the test counts; it must not be under `specs/`, `.github/`, or `.specify/`, because the land job rejects an agent change to those protected paths), because a task whose agent changes no file fails; the same section records that the real-model Python tests skip when the file is absent and that the Docker-backed tests are the authoritative real-model evidence

---

## Dependencies & Execution Order

- Phase 1 then Phase 2 then the stories. T002 needs T001 and adds the tracked `tests/test_environment.py`, because a task whose agent changes no file fails.
- T003 needs T002 and precedes T011 (the pin), T029, T032, and T039.
- Phase 2: T004 needs T003 and runs alone, because T008 to T012 use its constants; T005 to T012 need T004 and are independent files; the tests T013 to T017 need the modules and support they exercise (T013 needs T005, T006, T008; T014 needs T009; T015 needs T007 and T010; T016 needs T007 and T011; T017 needs T012).
- US1: T018 and T019 need Phase 2 (T019 needs T018 only for the field names); T020 needs T018, T019, and Phase 2; the tests T021 to T024 need T020.
- US2 needs US1: T025 to T027 need T018, T019, and T020.
- US3 needs US1: T028 needs T020.
- US4 needs US1 and US2 for a complete capability: T029 needs T003 and T018; T031 needs T020 and T029 (its SDK enforcement check runs the SDK write with the ball manifest); T032 needs T029, T030, and the Phase 2 and US1 modules; T033 needs T032; T034 may start after T029; T035 needs T032; T036 needs T032; T037 needs T035 and T036 and the schema variants need T029.
- US5 needs US4: T039 needs T038; T040 needs T039; T041 needs T032 and T039; T042 needs T041; T043 needs T034 and T039.
- US6: T044 needs T034 and T043, because it edits the same README.
- Polish needs all stories; T050 runs last and appends a "Verification" section to `<cap>/README.md`, because a task whose agent changes no file fails and a task that changes a file under `specs/` fails as a protected-path change.

## Parallel Execution Examples

- Phase 2: T004 alone, then T005 to T012 in parallel (distinct files), then T013 to T017 in parallel.
- US1: T018 and T019 in parallel, then T020, then T021 to T024 in parallel.
- US2: T025 to T027 in parallel.
- US4: T029 and T030 in parallel, then T035 and T036 in parallel.
- Polish: T045 to T049 in parallel.

## Implementation Strategy

- **MVP**: Phases 1 to 3 (US1) deliver `ball-observation` facts through the SDK entry point, verified by the Python tests.
- **Increment**: US2 proves schema conformance, provenance, and single-frame honesty; US3 proves full-frame container-owned
  preprocessing; US4 makes the capability registrable and proves it end to end; US5 adds the model and dependency
  provenance evidence and the image bounds; US6 records the evaluation deferral; Polish corrects the documentation.
- Stop and report instead of working around an environment gap (a locked wheel that cannot be installed, an unconfirmed
  model licence or digest, a disallowed licence, or a missed recall or resource bound); never change the environment
  actions, the SDK, or the platform in this feature, and never lower a bound.
