---

description: "Task list for Reference Analyst Container on the Python SDK"
---

# Tasks: Reference Analyst Container on the Python SDK

**Input**: Design documents from `specs/20261018-095952-reference-analyst/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires capability tests and Docker-backed end-to-end evidence (FR-016 to FR-018, SC-001 to
SC-005), so test tasks accompany the code they exercise and every landed task keeps the suites green.

**Organization**: Tasks are grouped by user story. The Python Analyst SDK
(`specs/archive/20261017-220709-python-analyst-sdk`), Materialized Video Segments
(`specs/archive/20261017-222433-materialized-segments`), and Fact Indexing (`specs/archive/20261017-175648-fact-indexing-queries`)
must be merged first: the capability uses the SDK's `run`, `AnalystContext.media`, and `SegmentMedia.to_match_ms`, and
the end-to-end test reuses the segment feature's `SyntheticMedia` support, 5-second test policy, and media staging, and
the fact feature's member fact read. The image is foundational because User Stories 1 and 2 both need it.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint
  files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4)
- Every task names its exact file paths

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The locked capability project that the environment's verification recognizes

- [x] T001 Create the independent project `src/analysts/capabilities/reference-media-probe/` (research R1, R6; FR-001,
  FR-002): `pyproject.toml` (`[project].name` `socalytics-reference-media-probe`, version `1.0.0`, `requires-python`
  `>=3.12,<3.13`, dependency `socalytics-analyst-sdk` with `[tool.uv.sources]` `socalytics-analyst-sdk = { path =
  "../../sdk" }`, hatchling build backend with the `src/reference_media_probe` package, locked `dev` group with
  `hatchling`, `pytest`, and `jsonschema` (used directly by the tests), no other runtime dependency, and
  `[tool.pytest.ini_options]` with `testpaths = ["tests"]`
  and `pythonpath = ["tests"]` so later test modules import `support`); `uv.lock` generated with `uv lock` and committed;
  `.gitignore` limited to `.venv/`, `__pycache__/`, `.pytest_cache/`, and `dist/`;
  `src/reference_media_probe/__init__.py` (`CAPABILITY_ID = "reference-media-probe"`, `CAPABILITY_VERSION = "1.0.0"`,
  `FACT_KIND = "frame-second-probe"`); and `tests/test_package.py` that imports the package and `socalytics_analyst`;
  the environment's locked sequence (`uv sync --locked --all-groups --no-install-local --no-build`,
  `uv sync --locked --all-groups --no-build-isolation`, `uv build --wheel --no-build-isolation`,
  `uv run --no-sync pytest`) passes from the project directory

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Test support, the Analyst itself, its declaration, and its image

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Test support and payload schema

- [x] T002 [P] Add the capability test support (research R11): `tests/support/__init__.py`;
  `tests/support/media.py` that creates synthetic MP4 media at test time with the `ffmpeg` on `PATH` (`testsrc2`,
  `64x64`, native `mpeg4`, a given frame rate (default 25), keyframe interval equal to the frame rate, a given duration
  and timestamp offset) inside a pytest temporary directory and fails the test with a message naming the missing tool
  when `ffmpeg` or `ffprobe` is absent; `tests/support/mounts.py` that writes a mount root with
  `job.json` (analyst-job 1.2.0, segment scope, capability `reference-media-probe` 1.0.0, schema version `1.0.0`, a
  given nominal window, and `segmentMedia` `{ "videoPolicy": "required", "manifestChecksum": <sha-256 of the written
  manifest> }`, or without `segmentMedia` on request), `attempt.json` (analyst-attempt 1.0.0), `media/segment.mp4`, and
  `media/segment-manifest.json` (segments/segment-manifest 1.0.0 with the given `nominalWindow`,
  `requestedSourceInterval`, `encodedInterval`, `timelineSpans`, and the media's real digest and size), plus empty
  `result/` and `scratch/`; and `tests/conftest.py` with fixtures for both; `tests/test_support.py` validates each
  written document against the SDK's bundled schemas, so the support itself cannot drift from the contracts (all paths
  under `src/analysts/capabilities/reference-media-probe/`)
- [x] T003 [P] Add the payload schema `src/analysts/capabilities/reference-media-probe/schemas/frame-second-probe.payload.schema.json`
  exactly as contracts/reference-media-probe.md § Fact Payload (JSON Schema 2020-12, `additionalProperties: false`,
  `"decodedFrames": { "type": "integer", "minimum": 0 }`, `meanLuma` with required `min`, `mean`, `max`, each
  `"minimum": 0, "maximum": 255`) and `src/analysts/capabilities/reference-media-probe/tests/test_payload_schema.py`
  with valid payloads (zero frames without `meanLuma`; 25 frames with `meanLuma`) and invalid ones (negative count,
  unknown property, luma above 255, missing `mean`)

### Analyst

- [x] T004 Implement the decoder in `src/analysts/capabilities/reference-media-probe/src/reference_media_probe/probe.py`
  (research R3; FR-008): read the video stream's `time_base` with `ffprobe -v error -select_streams v:0
  -show_entries stream=time_base -of csv=p=0 <path>`; then stream `ffprobe -v error -f lavfi -i
  movie=<escaped path>,signalstats -show_entries frame=best_effort_timestamp:frame_tags=lavfi.signalstats.YAVG
  -of csv=p=0` line by line as an iterator of `(media_ms, y_avg)` where `media_ms` is the floor of the exact rational
  `pts·time_base·1000` and `y_avg` is a `Decimal`; run with an argument list (no shell), working directory
  `/socalytics/scratch` (or the context's scratch path), and bounded standard-error capture; raise the SDK's
  `ResultError` with category `media-decode-failed` on a non-zero exit, unparsable line, or a frame without a
  timestamp; add `src/analysts/capabilities/reference-media-probe/tests/test_probe.py` (25 fps synthetic media yields
  25 frames per second with increasing timestamps and luma in `[0, 255]`; an offset start keeps source timestamps;
  bytes that are not media raise `media-decode-failed`)
- [x] T005 Implement the facts and the entry point (research R2, R4; data-model "Frame-Second Fact"; FR-004 to FR-007):
  `src/analysts/capabilities/reference-media-probe/src/reference_media_probe/facts.py` aggregates the probe's frames
  per match-second while streaming: a frame counts only when its media time lies inside the manifest's
  `requestedSourceInterval`, `SegmentMedia.to_match_ms` returns a match time, and that match time lies inside the
  job's nominal window; it belongs to the second `[k·1000, (k+1)·1000)` containing its match time; one fact per second
  that overlaps both the nominal window and the mapped match time of `timelineSpans`, with range "the match-second
  `[k·1000, (k+1)·1000)` clipped to the nominal window and the second's mapped part; `endMs > startMs`", kind
  `frame-second-probe`, confidence `1.0`, payload `decodedFrames` ("integer ≥ 0") and, "present only when
  `decodedFrames > 0`", `meanLuma` `min`, `mean`, `max` "rounded half-even to 3 decimals" with the mean computed in
  `Decimal` before rounding; facts in start-time order; `src/analysts/capabilities/reference-media-probe/src/reference_media_probe/__main__.py`
  calls the SDK's `run(analyse, output_type="foundational-facts")`, where `analyse` raises the SDK's `InputError`
  category `segment-media-required` when `context.media` is `None` before decoding, adds the facts, and sets the result
  confidence to `1.0` when it added at least one fact; add
  `src/analysts/capabilities/reference-media-probe/tests/test_facts.py` that runs the entry point through the SDK's
  `run` with a T002 mount root for a 5-second, fully mapped, 25 fps segment and asserts exit 0, exactly `facts.json`
  and `manifest.json` in `result/`, five facts `[k·1000, (k+1)·1000)` with `decodedFrames` 25, payloads valid against
  the T003 schema, and `min ≤ mean ≤ max`

### Declaration and image

- [x] T006 Add `src/analysts/capabilities/reference-media-probe/analyst-manifest.json` with exactly the values of
  data-model.md § Capability Declaration (`reference-media-probe` `1.0.0`, `low-level`, `segment`, `upstream: []`,
  `videoAccess: required`, `output.schemaVersion: 1.0.0`, `compatibleProfiles: ["reference-media-probe-dev"]`,
  `model.digest` `sha-256:` followed by 64 zeros, `runtime.constraints: ["linux-amd64"]`, `accelerators: []`,
  `resources` `memory: 512Mi`, `cpu: 1`, `requirements` `minMaxAttempts: 1`, `minLeaseSeconds: 30`,
  `minStaleTimeoutSeconds: 60`, `minExecutionTimeoutSeconds: 300`, `determinism` deterministic and idempotent) and
  `src/analysts/capabilities/reference-media-probe/tests/test_packaging.py` that validates it against
  `contracts/analysis/analyst-manifest/v1/analyst-manifest.schema.json` (with the common schema registered) located by
  walking up to the repository root, and asserts the capability id and version equal the package constants (research
  R8; FR-003, FR-009)
- [x] T007 Add the image (research R5 to R9; contracts/reference-media-probe.md § Build and § Run; FR-009 to FR-012):
  `src/analysts/capabilities/reference-media-probe/docker/fetch_verified.py` (standard library only: download a URL,
  verify its SHA-256 before extraction, extract only named members of a `tar.gz` or `tar.xz` archive without path
  traversal, and exit non-zero with a message naming the archive on any mismatch);
  `src/analysts/capabilities/reference-media-probe/Dockerfile` (two stages on
  `python:3.12-slim-bookworm@sha256:<current digest>`; `ARG` defaults equal to the uv and FFmpeg archive URLs and
  SHA-256 values of `.github/actions/environment-setup/analyst-media-toolchain.json`; the builder verifies the
  manifest's SHA-256 against the literal label value with `sha256sum -c` and fails otherwise, fetches uv and
  `bin/ffmpeg`, `bin/ffprobe`, and `LICENSE.txt` through `fetch_verified.py`, copies `src/analysts/sdk/` and the
  capability project to their repository-relative paths, and runs `uv sync --locked --all-groups --no-install-local
  --no-build` and then `uv sync --locked --no-dev --no-editable --no-build-isolation --compile-bytecode` with
  `UV_PYTHON_DOWNLOADS=never`, `UV_PYTHON=/usr/local/bin/python3.12`, and the environment at
  `/opt/reference-media-probe/venv`; the final stage copies the environment, `ffmpeg`, `ffprobe`, and the FFmpeg
  license notice to `/opt/reference-media-probe/` (root-owned, world-readable and executable), sets
  `PYTHONDONTWRITEBYTECODE=1`, `PYTHONUNBUFFERED=1`, and `PATH`, declares the five `io.socalytics.*` labels as literal
  values, `USER 65532:65532`, `ENTRYPOINT ["/opt/reference-media-probe/venv/bin/python", "-m",
  "reference_media_probe"]`, and ends with `COPY src/analysts/capabilities/reference-media-probe/analyst-manifest.json
  /opt/socalytics/analyst-manifest.json` as its last layer-creating instruction; no `ADD --checksum`, no `RUN --mount`);
  and `src/analysts/capabilities/reference-media-probe/Dockerfile.dockerignore` that excludes everything except the
  SDK and capability sources, `pyproject.toml`, `uv.lock`, `README.md` files, the manifest, `docker/`, and `schemas/`
  (no `.venv`, caches, `dist`, or tests); `docker build -f src/analysts/capabilities/reference-media-probe/Dockerfile
  -t reference-media-probe:local .` succeeds from the repository root

**Checkpoint**: The Analyst passes its tests through the SDK, and the image builds from pinned inputs

---

## Phase 3: User Story 1 - A real SDK-built Analyst turns a finalized recording into member-readable facts (Priority: P1) 🎯 MVP

**Goal**: The reference image runs for real on the Docker runtime against materialized segment media, and its facts
are accepted, indexed, and readable by members

**Independent Test**: Build and register the image, finalize the synthetic recording under the 5-second test policy,
let a Docker-runtime Manager execute the run, and read the reference capability's facts

- [x] T008 [US1] Add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/ReferenceAnalystImage.cs` (research
  R9, R12): find the repository root from `AppContext.BaseDirectory`, copy `src/analysts/sdk/` and
  `src/analysts/capabilities/reference-media-probe/` (without `.venv`, `__pycache__`, `.pytest_cache`, `dist`, and
  `tests`) to the same relative paths in a temporary context directory, build it with Testcontainers'
  `ImageFromDockerfileBuilder` and the Dockerfile path `src/analysts/capabilities/reference-media-probe/Dockerfile`,
  push it with the existing `DockerEngine` exactly as `TestAnalystImage.BuildAndPushAsync` does, delete the temporary
  context, and return a `PushedAnalystImage`; constants `CapabilityId = "reference-media-probe"`,
  `ProfileId = "reference-media-probe-dev"`, and the no-model digest
- [x] T009 [US1] Add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/ReferenceAnalystEndToEndTests.cs` (research
  R12; FR-013, FR-014, FR-018; SC-001, SC-002, SC-004): start the harness with the OCI registry fixture as
  `DockerRuntimeEndToEndTests` does; build and push through `ReferenceAnalystImage`; register the image by digest,
  create and approve the `reference-media-probe-dev` profile (data-model.md § Analyst Profile: `videoPolicy`
  `required`, the no-model digest, `resources` `memory: 512Mi`, `cpu: 1`, attempts, lease, stale, and execution values
  at least the manifest's requirements), define a workflow with one segment node and the materialized-segments
  feature's 5-second test segmentation policy, and make it the default, all through the existing Registry operations;
  start a Docker-runtime Manager as the segment media end-to-end test does; upload and finalize the 10-second
  `SyntheticMedia` recording; wait for the run to complete; then as a member with team read access call
  `GET /api/v1/analysis/runs/{runId}/fact-streams` and `GET /api/v1/analysis/runs/{runId}/facts?capabilityId=
  reference-media-probe&capabilityVersion=1.0.0&schemaVersion=1.0.0` and assert two accepted segment results on
  attempt number 1, ten `frame-second-probe` facts with ranges `[k·1000, (k+1)·1000)` for `k = 0..9` in time order,
  `decodedFrames` equal to the synthetic recording's frame rate, `meanLuma` values within `[0, 255]` with
  `min ≤ mean ≤ max`, full `accepted` coverage, and afterwards no container labelled by the Manager, attempt directory,
  or staged media on the host

**Checkpoint**: User Story 1 is evidenced end to end

---

## Phase 4: User Story 2 - The reference image is built from pinned inputs and runs under the Manager's restrictions (Priority: P1)

**Goal**: The image's pins, labels, last layer, user, and license notice are verified, and the Registry accepts it

**Independent Test**: Run the packaging tests in the capability project and the image tests in the end-to-end project

### Packaging evidence

- [ ] T010 [P] [US2] Add `src/analysts/capabilities/reference-media-probe/tests/test_dockerfile.py` (FR-010, FR-016,
  FR-017): parse the Dockerfile and assert the uv and FFmpeg `ARG` defaults (URL and SHA-256) equal
  `.github/actions/environment-setup/analyst-media-toolchain.json` (located by walking up to the repository root), the
  base images are pinned by `@sha256:` digest, the five `io.socalytics.*` label values equal the manifest's
  `manifestVersion`, the SHA-256 of `analyst-manifest.json`, its capability id and version, and its model digest, the
  user is non-root and numeric, the final stage copies only the Python environment, `ffmpeg`, `ffprobe`, the FFmpeg
  license notice, and the manifest, the last layer-creating instruction copies only the manifest to
  `/opt/socalytics/analyst-manifest.json`, and no `ENV` names a credential, address, or token
- [ ] T011 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/ReferenceAnalystImageTests.cs` (FR-009,
  FR-011, FR-013; SC-004): build and push through `ReferenceAnalystImage`, register it by digest and assert the stored
  capability declaration equals the project's `analyst-manifest.json` and the response states the capability id and
  version; inspect the image through `DockerEngine.GetJsonAsync` and assert the configured user is non-root, the labels
  equal the manifest, and the architecture is `amd64`; run a short-lived container from the image with a user id that
  does not exist in it, a read-only root file system, network mode `none`, and the entry point replaced by a check that
  the FFmpeg license notice and `ffprobe` exist and that `python -m reference_media_probe` with no mounts exits 2
  without writing (a helper that runs a container to exit and returns its status lives in this test file)

**Checkpoint**: User Story 2 is evidenced

---

## Phase 5: User Story 3 - The capability's facts are correct and deterministic in SDK-level tests (Priority: P2)

**Goal**: Every fact rule, determinism, and failure exit is proven through the SDK without Docker

**Independent Test**: Run the capability project's tests with the environment's locked verification sequence

- [ ] T012 [US3] Add `src/analysts/capabilities/reference-media-probe/tests/test_time_mapping.py` (FR-005, FR-006; edge
  cases; SC-002) and correct `src/analysts/capabilities/reference-media-probe/src/reference_media_probe/facts.py` where
  a case fails: media with one second of keyframe context before `requestedSourceInterval` yields no fact for it; a
  nominal window only partly covered by the mapping yields facts only for the covered part, the last one clipped; a
  two-span mapping with a gap yields no fact for unmapped seconds and a clipped range for a partly mapped second; a frame
  exactly on a second boundary counts in the later second and a frame at the window's end counts in none; a 0.5 fps
  clip with a mapped second without a decoded frame yields that second with `decodedFrames` 0 and no `meanLuma`; no fact
  lies outside the nominal window in any case
- [ ] T013 [US3] Add `src/analysts/capabilities/reference-media-probe/tests/test_determinism.py` (FR-007; SC-003) and
  correct `src/analysts/capabilities/reference-media-probe/src/reference_media_probe/facts.py` where needed: two runs
  of the entry point over the same job and media in two mount roots produce byte-identical `facts.json`; luma values
  that differ only beyond the third decimal round half-even to the same payload
- [ ] T014 [US3] Add `src/analysts/capabilities/reference-media-probe/tests/test_failures.py` (FR-002, FR-004, FR-008;
  edge cases) and correct `src/analysts/capabilities/reference-media-probe/src/reference_media_probe/__main__.py` or
  `probe.py` where needed: a job without `segmentMedia` and without a media directory exits 2 with the category
  `segment-media-required`; staged media whose digest differs from its manifest exits 2 through the SDK; media with a
  correct digest that cannot be decoded exits 1 with `media-decode-failed`; a match-scoped job exits 2; every failure
  leaves no `manifest.json` in `result/` and writes one bounded line to standard error without media content; a
  successful run with Python socket connections patched to raise still exits 0, and no module of
  `reference_media_probe` reads `os.environ` or `os.getenv`

**Checkpoint**: User Story 3 is evidenced

---

## Phase 6: User Story 4 - A developer can run the reference Analyst locally (Priority: P3)

**Goal**: The capability's documentation explains its behavior, commands, and the local run procedure

**Independent Test**: Follow the documented procedure against a local development stamp

- [ ] T015 [US4] Write `src/analysts/capabilities/reference-media-probe/README.md` (research R13; FR-014, FR-015, FR-020):
  what the capability is (development reference, not a catalog capability, never published, convention-only exclusion
  from production), its facts (kind, ranges, payload with a link to the payload schema), its test commands (the
  environment's locked sequence), the image build command from the repository root and its pinned inputs (including
  the open item that byte-identical rebuilds are not claimed and that the FFmpeg publisher's release is not
  immutable), and the local procedure: start a local OCI registry container, set the API's `Registry:Images` options
  (registry host, repository prefix, HTTP), build and push the image, register it by digest, create and approve the
  `reference-media-probe-dev` profile with `videoPolicy` `required`, define and select a workflow with the reference
  segment node and a short segmentation policy, run a Manager with `Runtime:Profile` `docker-desktop`, upload and
  finalize a short recording, and read its facts; it states that the configured Registry seed stays unchanged

**Checkpoint**: All user stories are complete

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Repository documentation, the architecture, and final validation

### Documentation

- [ ] T016 [P] Update `src/analysts/README.md` (the reference capability as the first project under `capabilities/`,
  development evidence only), `README.md` (the capability's test and image build commands), and `AGENTS.md` (Current
  State: the reference Analyst and its end-to-end evidence; the capability's commands) to describe the merged state
  (FR-020)
- [ ] T017 [P] Make `docs/architecture/` describe the merged state (plan.md § Architecture Updates; FR-020): re-read
  `docs/architecture/` at implementation time and make every listed narrative and every status statement the feature
  affects true, including planned, future-tense, or unimplemented wording outside the listed sections; at least turn
  the "decided … and not yet implemented" paragraph on `reference-media-probe` in
  `docs/architecture/analysts-models-and-hardware.md` (Image Metadata And Packaging) and the catalog-rules paragraph in
  `docs/architecture/analyst-capability-catalog.md` into present tense, add the real-container evidence with staged
  segment media to Implementation Validation and to `docs/architecture/analyst-runtime-and-recovery.md` (Built-In
  Runtime Adapter Profiles), and update `docs/architecture/platform-implementation.md` (Source And Runtime Baseline,
  Acceptance Evidence, Architecture Reassessment) so that pinned-input builds are evidenced while byte-identical
  rebuilds, production Analyst images, and catalog capabilities remain open; the rewrites in the plan are examples, not
  the whole job; `node .github/scripts/check-markdown.mjs` passes

### Validation

- [ ] T018 Run [quickstart.md](quickstart.md) steps 1 to 3 (capability tests with the locked sequence, image build and
  inspection, and `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build --filter
  ReferenceAnalyst`), plus `dotnet test src/platform/SocAlytics.Platform.slnx --no-build` and the SDK project's tests,
  and fix any failure in the files of this feature

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies; requires the SDK, segment, and fact-indexing features merged
- **Foundational (Phase 2)**: Depends on Setup; blocks all user stories
- **User Story 1 (Phase 3)**: Depends on Foundational
- **User Story 2 (Phase 4)**: Depends on Foundational and on T008 (`ReferenceAnalystImage`)
- **User Story 3 (Phase 5)**: Depends on Foundational only
- **User Story 4 (Phase 6)**: Depends on Foundational; documents what User Stories 1 and 2 prove
- **Polish (Phase 7)**: Depends on all user stories

### Within Phases

- T002 and T003 run in parallel; T004 needs T002; T005 needs T002 to T004; T006 needs T005's constants; T007 needs
  T006 (the literal manifest checksum)
- T009 needs T008; T010 and T011 run in parallel
- T012 to T014 run in order, because each may correct the Analyst's code
- T016 and T017 run in parallel; T018 runs last

### Parallel Opportunities

- Phase 2: T002 ∥ T003
- Phase 4: T010 ∥ T011
- Phase 7: T016 ∥ T017

---

## Parallel Example: User Story 2

```text
Task: "T010 Dockerfile pins, labels, user, and last layer in tests/test_dockerfile.py"
Task: "T011 Registry acceptance and image run restrictions in ReferenceAnalystImageTests.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Complete Phase 1 and Phase 2
2. Complete Phase 3 (T008, T009)
3. **STOP and VALIDATE**: the end-to-end test proves the real path

### Incremental Delivery

1. Setup and Foundational: the Analyst and its image exist and pass their tests
2. User Story 1: end-to-end evidence (MVP)
3. User Story 2: packaging and restriction evidence
4. User Story 3: full fact-rule and failure coverage
5. User Story 4 and Polish: documentation and architecture

---

## Notes

- No contract, API, persistence, Registry, Manager, SDK, seed, AppHost, or `.github/` change (FR-019)
- [P] tasks change disjoint files; a story's tests accompany the code they exercise
