# Tasks: Media Quality and Usable Intervals for Media Context Classification

**Input**: Design documents from `specs/20261025-193130-media-quality-usable-intervals/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required by FR-016, FR-017, and SC-001 to SC-009; test tasks sit next to the code they cover.

**Organization**: Tasks are grouped by user story. Paths are relative to the repository root. `CAP` stands for
`src/analysts/capabilities/media-context-classification`; `E2E` stands for
`src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`. All values are fixed, uncalibrated development values; every
description of the capability stays labelled development evidence (FR-021).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files are
  disjoint and none depends on a sibling
- **[Story]**: the user story the task serves (US1 to US5)

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Move every version, hash, and label to the new capability version at once, so the existing tests stay
green and later tasks build on a consistent baseline.

- [ ] T001 Bump the capability to `1.1.0` (research R1): in `CAP/pyproject.toml` set the project version `1.1.0` and refresh the project's own entry in `CAP/uv.lock` with `uv lock --offline` (no other lock change); in `CAP/src/media_context_classification/__init__.py` set `CAPABILITY_VERSION` to `1.1.0` and add the fact kind constants `media-quality` and `usable-interval` next to the existing `shot-boundary` constant (keep `DETECTOR_VERSION` `1.0.0`); in `CAP/analyst-manifest.json` set manifest version `1.2.0`, capability version `1.1.0`, and output schema version `1.1.0`, keeping resources (`2Gi`, `2`) and `minExecutionTimeoutSeconds` 300; in `CAP/Dockerfile` update `MANIFEST_SHA256` and the labels `io.socalytics.analyst-manifest.version` `1.2.0` and `io.socalytics.capability.version` `1.1.0` (and the output schema label if present); regenerate `CAP/audit/dependency-audit.json` and `CAP/audit/sbom.cdx.json` with `uv run --no-sync python scripts/generate_audit.py`; update the constants of `E2E/Support/ShotBoundaryAnalystImage.cs` to capability version `1.1.0` and output schema version `1.1.0`; update every existing test under `CAP/tests/` that asserts `1.0.0` for the capability, manifest, or output version (find them with a search; for example `test_identity.py`, `test_package.py`, `test_packaging.py`, `test_dockerfile.py`, `test_audit.py`); run the project tests

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Quality settings, the streaming frame-measure analyzer, and synthetic clips with known defects that every
story needs.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T002 Add the quality settings to `CAP/src/media_context_classification/settings.py` exactly as in [data-model.md](data-model.md): `QUALITY_VERSION` `1.0.0`, `BLACK_LUMA_MAX` 16.0, `FROZEN_DIFFERENCE_MAX` 0.5, `LOW_DETAIL_MAX` 2.0, `BLACK_MIN_MS` 300, `FROZEN_MIN_MS` 500, `LOW_DETAIL_MIN_MS` 1000, `QUALITY_RULE_ORDER` (`black-frame`, `frozen-frame`, `low-detail`), the 64 x 36 frame size shared with the cut detector, and a function that returns the closed `settings` payload object (`qualityVersion`, `frameWidth`, `frameHeight`, `ruleOrder`, the three thresholds and three minimum durations by name; no other property, R7) with `CONFIDENCE` 0.5 unchanged; settings provenance stays in the existing detector settings and is not changed

### Parallel group: analyzer and clip support

- [ ] T003 [P] Extend `CAP/tests/support/media.py` with the synthetic shots the defect tests need: `Shot` gains `blur_sigma` and a frozen-repeat option, a flat black shot, a flat near-black shot just under the black threshold, a still blurred shot, and a flat dark-but-above-threshold shot; add clip builders for black, near-black, frozen, duplicated, blurred, low-detail, defect at window start and end, defect shorter than its minimum, a defect that spans a shot boundary, and a combined known-defects-and-cuts clip, each returning the known truth (stretch ranges, rules, interval edges) next to the clip; keep the existing builders and their output unchanged
- [ ] T004 [P] Create `CAP/src/media_context_classification/quality.py`: the streaming analyzer per [data-model.md](data-model.md) and research R3 and R4 — per in-window frame the integer-exact measures on the 64 x 36 BGR frame (`cv2.setNumThreads(1)` as the baseline): mean luma from `cv2.COLOR_BGR2GRAY`, mean absolute luma difference to the predecessor, mean absolute Laplacian (`cv2.Laplacian`, `CV_16S`, `ksize` 1) as integer sum over pixels, converted to exact fractions and quantized to three decimals with the baseline's `quantize` (ROUND_HALF_EVEN); the `FrameObservation` and `Stretch` types (`rule`, `start_ms`, `end_ms`, `measure`, `frame_count`); per-rule state transitions `idle -> running -> closed`; black evaluated first and frozen and low-detail never qualifying on a black frame (a frozen pair needs both frames non-black); a frozen run opening at the preceding in-window frame when a frame pair qualifies, so the stretch starts at the original repeated frame with `frame_count` including it, and a frozen stretch requiring `frame_count` of at least 2 (at least one duplicate) in addition to the minimum duration; lead-in and out-of-window frames used only as predecessors and never counted; stretch end at the first following in-window frame's match time or the nominal window end; minimum duration judged on the clipped in-window range (`end_ms - start_ms >= minDurationMs`, otherwise dropped); reported measure the maximum per-frame measure (`<= threshold`); match times from `media.to_match_ms` with the baseline's `timestamp-unmappable` failure; no network, randomness, or new dependency

---

## Phase 3: User Story 1 - A segment yields usable and unusable intervals with reasons (Priority: P1) 🎯 MVP

**Goal**: Every segment's nominal window is partitioned into usable and not-usable `usable-interval` facts with ordered
reason codes, emitted in one decode pass in a fixed, deterministic order.

**Independent Test**: Run the capability on clips with known black, frozen, blurred, and normal stretches; the
usable-interval facts equal the known truth, cover the window exactly, and are byte-identical across runs.

### Implementation (US1)

- [ ] T005 [P] [US1] Create `CAP/src/media_context_classification/intervals.py`: the `UsableInterval` type and the deterministic derivation of R5 and [contracts/usable-interval-fact.md](contracts/usable-interval-fact.md) — inputs the final stretches, the shot boundaries, and the window; a sweep over the sorted distinct edge points clamped to the window; `usable` true if and only if no stretch covers the sub-range; `reasons` in `QUALITY_RULE_ORDER`, empty when usable; adjacent sub-ranges with equal verdict and reasons merged; the first interval starting at the window start and the last ending at the window end (the span before the first in-window frame takes that frame's verdict); an empty window (no in-window frame) yielding no interval; a shot boundary never changes a verdict by itself
- [ ] T006 [P] [US1] Create `CAP/schemas/usable-interval.payload.schema.json` in the restricted subset (only `type` with one name, `properties`, `required`, `additionalProperties`, `items`, `enum`, `const`, `minimum`/`maximum`, `minLength`/`maxLength`, `minItems`/`maxItems`, `$defs`, `$ref`, `title`, `description`; no `$schema`, `if`, `pattern`, or multi-type; at most 16 KiB, depth 8, 256 nodes): `usable` boolean, `reasons` array (`maxItems` 3) of the enum `black-frame`, `frozen-frame`, `low-detail`, and the closed `settings` object of T002; `additionalProperties` false at every level; all properties required

### Sequential wiring (US1)

- [ ] T007 [US1] In `CAP/src/media_context_classification/facts.py` add `usable_interval_fact` (kind `usable-interval`, confidence 0.5, payload `usable`, `reasons`, `settings`), and a `sort_facts` function ordering facts by `(startMs, kind order, rule order, endMs)` with kind order `shot-boundary`, `media-quality`, `usable-interval` (R6)
- [ ] T008 [US1] In `CAP/src/media_context_classification/__main__.py` decode once with a pass-through generator that feeds each frame to the `quality.py` analyzer and then to the unchanged `detect`, build the usable-interval facts from the finished stretches and the detected boundaries, sort all facts with `sort_facts`, and write them only after every step succeeded (no partial result on any failure; baseline failure reasons unchanged)

### Tests (US1)

- [ ] T009 [P] [US1] Create `CAP/tests/test_intervals.py` covering `intervals.py` without media: full partition (no gap, no overlap, first start and last end at the window), merge of equal neighbours, fixed reason order with overlapping stretches of several rules, no stretch yields one usable interval, an empty window yields none, a single-frame window, a defect starting at the first in-window frame when that frame is later than the window start (the leading span takes its verdict), and a randomized property check that edges lie only at stretch edges
- [ ] T010 [P] [US1] Create `CAP/tests/test_usable_intervals.py` running `analyse` on the synthetic clips of T003 through the existing mount helpers: acceptance scenarios 1 to 5 (defect-free clip gives exactly one usable interval, black in the middle gives usable-black-usable, frozen and blurred reasons, multi-rule reasons in order, byte-identical output on repeated runs), zero gaps and overlaps for every clip (SC-002), non-zero segment offsets, lead-in frames never producing facts, single-frame and empty segments, and every payload valid against `CAP/schemas/usable-interval.payload.schema.json` with `reasons` empty if and only if `usable`; failure behavior (FR-012, FR-016): for every baseline failure reason (missing or undecodable media, no video stream, timestamp-unmappable) the same stable reason and no result file, and a failure injected into the quality analyzer or the interval derivation leaving no partial result

**Checkpoint**: User Story 1 yields correct usable intervals on the synthetic clips.

---

## Phase 4: User Story 2 - Members see the measured evidence behind each quality finding (Priority: P1)

**Goal**: `media-quality` facts carry the rule, measure, threshold, minimum duration, and frame count for each defect
stretch.

**Independent Test**: Run clips with one known defect each; the fact kinds, ranges, measures, and rules equal the known
truth, and normal footage, noise, and pan yield no `media-quality` fact.

### Implementation and unit tests (US2)

- [ ] T011 [P] [US2] Create `CAP/schemas/media-quality.payload.schema.json` in the same restricted subset as T006: `rule` enum (`black-frame`, `frozen-frame`, `low-detail`), `measure` number with `minimum` 0, `threshold` number, `minDurationMs` integer, `frameCount` integer with `minimum` 1, and the closed `settings` object of T002; `additionalProperties` false at every level; all properties required
- [ ] T012 [P] [US2] Create `CAP/tests/test_quality.py` for `quality.py` on in-memory frames: black threshold boundary (`16.0` fires, just above does not), near-black, frozen threshold boundary, low-detail threshold boundary, black precedence (frozen and low-detail never fire on black frames, yet fire together on a still blurred scene), frozen stretch starting at the original frame and counting it, a lone first frame with a missing predecessor never producing a frozen stretch even when a low frame rate makes it satisfy the minimum duration (`frame_count` of at least 2 required), minimum durations just below and at the limit, clipping at the window start and end with lead-in and following frames used only as predecessors, a stretch spanning a shot boundary staying one stretch, variable frame rate timestamps, single-frame and empty windows, measure always `<= threshold`

### Sequential wiring (US2)

- [ ] T013 [US2] In `CAP/src/media_context_classification/facts.py` add `media_quality_fact` (kind `media-quality`, confidence 0.5, `startMs`/`endMs` from the stretch, payload `rule`, `measure`, `threshold`, `minDurationMs`, `frameCount`, `settings`)
- [ ] T014 [US2] In `CAP/src/media_context_classification/__main__.py` add one `media-quality` fact per finished stretch to the fact list before sorting, keeping the result confidence 0.5 when any fact exists

### Fact tests (US2)

- [ ] T015 [P] [US2] Create `CAP/tests/test_media_quality_facts.py` running `analyse` on the clips of T003: one fact per defect with exact kind, range, rule, measure, threshold, minimum duration, and frame count (acceptance scenarios 1 to 3), no fact for noise, steady pan, cuts between normal scenes, and normal footage (SC-003), no fact for a stretch below its minimum, a single black frame whose clipped range reaches the minimum duration yielding one black-frame fact, clipped stretches at window edges reported by one segment only, and every payload valid against `CAP/schemas/media-quality.payload.schema.json`
- [ ] T016 [P] [US2] Extend `CAP/tests/test_facts.py` with builder tests for `media_quality_fact`, `usable_interval_fact`, and `sort_facts` (kind order, rule order, tie-breaking, byte-identical serialization)

**Checkpoint**: User Stories 1 and 2 together write all new facts for the synthetic clips.

---

## Phase 5: User Story 3 - Shot boundaries keep their meaning and shape cuts into intervals (Priority: P1)

**Goal**: `shot-boundary` facts stay byte-identical to the baseline and declare a payload schema in the restricted
subset; cuts shape intervals only where the verdict changes.

**Independent Test**: The new pipeline's `shot-boundary` facts equal the unchanged detector's facts on the baseline's
golden clips, and interval edges coincide with cuts exactly where the verdict changes.

### Schema rewrite (US3)

- [ ] T017 [US3] First copy the current strict `CAP/schemas/shot-boundary.payload.schema.json` unchanged to the test helper `CAP/tests/support/shot-boundary.strict.schema.json`, then rewrite `CAP/schemas/shot-boundary.payload.schema.json` into the restricted subset per research R2: remove `$schema`, make `adaptiveFloor`, `adaptiveRatio`, and `contextMean` optional properties, declare `previousShotLastFrameMs` without `type` and with `minimum` 0, remove `if`/`then` and `dependentRequired`, keep `additionalProperties` false; facts keep emitting `null` unchanged

### Tests (US3)

- [ ] T018 [US3] Update `CAP/tests/test_payload_schema.py`: all three schema files pass a restricted-subset checker (allowed keywords only, one-name `type`, at most 16 KiB, depth 8, 256 nodes, `additionalProperties` false at every level), every emitted `shot-boundary` fact is valid against both the new schema and the strict helper schema, and the conditional requirement (`adaptiveFloor`, `adaptiveRatio`, `contextMean` present for the rules that need them) is checked against `rule` in the project test
- [ ] T019 [P] [US3] Create `CAP/tests/test_shot_boundary_equivalence.py`: for every baseline golden clip (hard cuts, second-frame cut, no cut, low-contrast cut, noise, pan, closely spaced cuts, single frame, lead-in) the `shot-boundary` facts written by `analyse` equal, byte for byte, the facts built directly from the unchanged `detect` and `shot_boundary_fact` pipeline (SC-004, FR-002), including their settings provenance
- [ ] T020 [P] [US3] Create `CAP/tests/test_interval_boundaries.py`: a hard cut from normal to black gives a usable-interval edge at the boundary's match time, a cut inside normal footage gives one usable interval, a cut inside a stretch does not split it or add an edge, black cutting to a different black scene stays one `media-quality` fact, and for every boundary whose two sides differ in verdict or reasons an edge exists at it

**Checkpoint**: Shot boundaries are unchanged and interval edges follow acceptance scenarios 1 to 3 of this story.

---

## Phase 6: User Story 4 - A Club Admin registers the new version through the normal Registry path (Priority: P2)

**Goal**: The manifest declares the payload schemas of all three kinds, the image labels match it, and one documented
procedure registers version `1.1.0` through the existing Registry operations.

**Independent Test**: Register the built image by digest; the cached declaration shows version `1.1.0`, scope, video
policy, no dependencies, and three payload schemas.

### Sequential manifest and image (US4)

- [ ] T021 [US4] In `CAP/analyst-manifest.json` add `output.factPayloadSchemas` for `shot-boundary`, `media-quality`, and `usable-interval`, each a JSON-equal copy of its file in `CAP/schemas/` (FR-009, R2); keep resources, timeout, and dependencies unchanged
- [ ] T022 [US4] Update `MANIFEST_SHA256` in `CAP/Dockerfile` to the hash of the final manifest and verify that the build-time label check still passes (manifest copied last, pinned inputs only)

### Parallel group: tests and documentation (US4)

- [ ] T023 [P] [US4] Create `CAP/tests/test_manifest_schemas.py`: the manifest validates against the SDK's embedded analyst-manifest schema at version `1.2.0`, each `output.factPayloadSchemas` entry is JSON-equal to its file in `CAP/schemas/` (byte-level JSON equality), the declared kinds are exactly the three kinds, and a fact with an undeclared property or a wrong type is rejected by the SDK validation with the failure that fails the write
- [ ] T024 [P] [US4] Update `CAP/tests/test_dockerfile.py` so the hash and label checks cover the final manifest, the version labels `1.2.0` and `1.1.0`, no model weights, and no new dependency or base-image change
- [ ] T025 [P] [US4] Update `CAP/README.md`: version `1.1.0`, the three fact kinds with envelope, payload, behavior that is part of the contract, failure reasons, and manifest declaration summary taken from [contracts/](contracts/) and the unchanged shot-boundary section, the quality and interval rules with every fixed setting, precedence and clipping behavior, documented limitations (night scenes versus black, static camera versus frozen, fades, cuts across segments), the registration procedure for a Development and Test stamp with capability version `1.1.0`, output schema version `1.1.0`, and the note that `1.0.0` stays registrable under its own digest; labelled development evidence with uncalibrated settings and no gate met

**Checkpoint**: Version `1.1.0` can be registered by following the README.

---

## Phase 7: User Story 5 - Maintainers can trust the extended image's provenance and limits (Priority: P2)

**Goal**: Audit, verification notes, and Docker-backed evidence prove the image and the whole path from image to
member-readable facts.

**Independent Test**: Build the image and run the audit, image, and end-to-end tests on a Linux engine.

### Audit

- [ ] T026 [US5] Regenerate `CAP/audit/dependency-audit.json` and `CAP/audit/sbom.cdx.json` and confirm `uv run --no-sync python scripts/generate_audit.py --check` and `CAP/tests/test_audit.py` pass with no new runtime dependency and only allowed licenses (FR-015, SC-008); change the audit script only if the project version bump requires it

### Verification notes and image evidence

- [ ] T027 [P] [US5] Update `CAP/VERIFICATION.md` for `1.1.0`: commands, evidence list, the quality and interval fixed settings, determinism and resource evidence, and the statement that no quality gate, calibration, or corpus exists
- [ ] T028 [P] [US5] Update `E2E/ShotBoundaryImageTests.cs`: labels against the `1.2.0` manifest, the manifest's three `factPayloadSchemas`, the installed distributions against the audit, no model weights, a job on the built image (non-root, read-only root file system, no network) that writes all three kinds, and the SC-007 checks (the evidence segment within 30 seconds on CPU, the built image run at least three times with different Docker CPU limits (for example 1 and 2 CPUs) on the evidence segment with byte-identical result manifests and fact artifacts asserted (SC-005, FR-011), a generated one-minute 1080p segment within the `2Gi` bound and the manifest's execution timeout)

### End-to-end evidence

- [ ] T029 [US5] Update `E2E/ShotBoundaryEndToEndTests.cs`: build and register the image by digest, approve a profile with output schema version `1.1.0`, define and default a workflow, finalize a short synthetic recording with known defects (black, frozen, blurred) and cuts spanning at least two segments under a short segmentation policy, run it through segment materialization, Manager execution, result acceptance, and fact indexing, and compare the kinds, positions, rules, reasons, and counts of all three fact kinds with the known truth exactly through `listAnalysisRunFacts` and `listAnalysisRunFactStreams` (SC-001, SC-002, FR-017), including, through a second test-only simulated Analyst result that declares the `1.2.0` manifest's payload schemas but writes a `usable-interval` payload violating its schema (the real image validates through the SDK and cannot emit one), the rejection with the named reason `result-payload-invalid` and no visible fact (US4 scenario 3); keep the reference capability tests unchanged

**Checkpoint**: Docker-backed evidence passes on a Linux engine.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Make every status statement true for the merged state (constitution Principle I, FR-022) and verify.

### Parallel group: documentation

- [ ] T030 [P] Update `docs/architecture/` so that it describes the merged state, re-reading the documents at implementation time and fixing every statement the feature makes false, including planned, future-tense, or unimplemented wording outside the sections named here, as listed in the plan's Architecture Updates: `docs/architecture/analyst-capability-catalog.md` (Low-Level Capabilities introduction, the `media-context-classification` row and the sentence after the table, and Implementation Waves, Wave 1: the baseline also writes fixed-rule `media-quality` and `usable-interval` facts as version `1.1.0` development evidence, uncalibrated, no gate met, while shot type, live, replay, graphics, and the challenger stay not implemented), `docs/architecture/analysts-models-and-hardware.md` (development image description, the fact payload schemas statement saying that the `1.1.0` development capability declares the payload schemas of all three kinds in its `1.2.0` manifest while no catalog capability does, and the Docker Desktop adapter verification statement; its Result Manifest And Lineage statements are re-read and expected to stay true, because the capability declares no upstream dependency and changes neither the manifest nor the lineage shape), `docs/architecture/analyst-runtime-and-recovery.md` (the verification statement naming the shot-boundary baseline), and `docs/architecture/platform-implementation.md` (which capabilities exist under `src/analysts/capabilities`; quality and usable intervals leave the not-implemented list)
- [ ] T031 [P] Update `README.md`, `AGENTS.md` (Current State and the capability command bullets), and `src/analysts/README.md` for capability `1.1.0`: three fact kinds, fixed uncalibrated development values, no gate met, never published, the previous `1.0.0` still registrable, and the unchanged run and test commands

### Final verification

- [ ] T032 Run the validation of [quickstart.md](quickstart.md): the project's locked `uv` sequence and `pytest`, `generate_audit.py --check`, `node .github/scripts/check-markdown.mjs`, the image build, `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundaryImage"`, `--filter "FullyQualifiedName~ShotBoundaryEndToEnd"`, and `--filter "FullyQualifiedName~ReferenceAnalyst"`; fix every failure the feature caused

---

## Dependencies & Execution Order

- Phase 1 (T001) first, then Phase 2 (T002, then the group T003 and T004); both block every story.
- US1 (T005 to T010) needs Phase 2. US2 (T011 to T016) needs Phase 2 and T007/T008 (shared `facts.py` and `__main__.py`).
- US3 (T017 to T020) needs US1 and US2 (the facts it compares) and T006 and T011 for the schema test.
- US4 (T021 to T025) needs all three schema files (T006, T011, T017); T022 follows T021.
- US5 (T026 to T029) needs US4; T027 and T028 follow T026, and T029 follows them.
- Polish (T030, T031) needs the implemented behavior; T032 is last.

## Parallel Opportunities

- After T002: T003 and T004.
- US1: T005 with T006; later T009 with T010.
- US2: T011 with T012; later T015 with T016.
- US3: T019 with T020.
- US4: T023, T024, and T025.
- US5: T027 with T028.
- Polish: T030 with T031.

## Implementation Strategy

- **MVP**: Phases 1 to 3 give usable intervals with reasons (User Story 1); add User Story 2 to expose the evidence,
  User Story 3 to lock the baseline equivalence, then registration (User Story 4) and the Docker-backed evidence
  (User Story 5).
- Keep the change inside `CAP`, `E2E`, and documentation: no SDK, platform, Web UI, or OpenAPI change.
