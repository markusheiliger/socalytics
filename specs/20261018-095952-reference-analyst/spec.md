# Feature Specification: Reference Analyst Container on the Python SDK

**Feature Branch**: `20261018-095952-reference-analyst`

**Created**: 2026-10-18

**Status**: Draft

**Input**: User description: "Reference Analyst Container on the Python SDK: everything an Analyst needs now exists or is specified — the SDK, the container interface, staged segment media, the Docker Desktop runtime adapter, the Registry, result acceptance, and fact indexing — but no Analyst has ever been built on them; the simulated Analyst and the Docker adapter's minimal test image bypass the SDK and real media. This feature adds one development reference Analyst capability as an independent Python project under `src/analysts/capabilities/<name>/` (the plan picks a name that cannot be mistaken for a catalog capability, e.g. `reference-media-probe`): a segment-scoped, low-level capability whose video policy requires video, which reads its staged segment media and writes deterministic foundational facts about it (for example per-second decoded-frame counts and simple frame statistics within the segment's nominal half-open window) through the SDK; it contains no model, no network access, and no credentials. Its `analyst-manifest.json` sits beside its Dockerfile and supplies the `io.socalytics.*` labels per docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging; its OCI image is built reproducibly with locked dependencies, a pinned base image by digest, the pinned decoding tools it needs, non-root user, and read-only root compatibility. A development and test workflow (seed) can include it so a run executes it for real. End-to-end evidence: build the image, make it available to the Docker adapter the way its tests provide images, register it in the Registry by digest, create a run from a short synthetic recording, let a Manager with the Docker runtime execute it against materialized segment media, and observe accepted results whose indexed facts are readable through the member fact read; plus SDK-level unit tests of the capability. The architecture currently names only catalog capabilities; the plan records this reference capability as development evidence, never part of the production catalog. Out of scope: any catalog capability, models and weights, accelerators, publishing to GHCR and release workflows (implementation automation cannot change `.github/workflows`), production promotion gates, Windows Docker Desktop manual evidence beyond what the runtime spec already requires. Depends on `specs/20261017-220709-python-analyst-sdk` (#160), `specs/20261017-222433-materialized-segments` (#161), `specs/20261017-175648-fact-indexing-queries` (#144), Docker Desktop Runtime Adapter, Analyst Registry, Analyst Manager Work Execution, and the merged environment spec. Implements docs/architecture/analysts-models-and-hardware.md#analyst-containers and #image-metadata-and-packaging (first SDK-based Analyst image), and the implementation-validation evidence in docs/architecture/analyst-runtime-and-recovery.md for a real container path."

## Clarifications

### Session 2026-10-18

- Q: How should a development and test workflow include the reference capability so that a run executes it for real,
  given that the configured Registry seed has no image-registry access and only stores placeholder image digests? →
  A: Through the existing Registry operations: the end-to-end tests and the documented local procedure register the
  pushed image by digest, approve a profile with a `required` video policy, define a workflow with the reference
  segment node, and set it as the default workflow; the configured seed stays unchanged.
- Q: What does "built reproducibly" have to guarantee for the reference image? → A: Every build input is pinned and
  verified (base image by digest, locked dependencies installed without resolution, decoding tools checked against
  their pinned hash), so rebuilding yields the same software; byte-identical image digests across builds are not
  required and stay open evidence.
- Q: What keeps the reference capability out of production stamps? → A: Convention and documentation only: its id
  marks it as a development reference, it is registered only by tests and the documented local procedure, and the
  architecture records it as development evidence outside the catalog; the Registry gains no refusal rule.
- Q: Which fact values must the end-to-end test compare exactly? → A: The fact ranges and decoded-frame counts must
  equal fixed values derived from the synthetic recording's frame rate and the test segmentation policy; the frame
  statistics must be present and within their valid range, while their exact values are proven by the capability's
  own tests.

## User Scenarios & Testing *(mandatory)*

The users are the maintainers of the platform, the Analyst Manager, and the Analyst SDK, who need evidence that a real
Analyst built the documented way passes every boundary from image registration to member-readable facts; and future
Analyst authors, who need one complete, working example of an Analyst project, its image, and its tests to copy from.

### User Story 1 - A real SDK-built Analyst turns a finalized recording into member-readable facts (Priority: P1)

A Coach finalizes a short match recording. The run's workflow contains the reference capability as a segment-scoped
node whose profile requires video. For every segment, the platform materializes the segment media, an Analyst Manager
with the Docker runtime pulls the reference Analyst image by its digest, stages the segment media read-only, and runs
the container. The Analyst reads the media through the SDK, decodes it, and writes facts about the decoded frames of
each match-second of the segment's nominal window. The platform accepts every result, indexes its facts, and a member
with read access reads them per capability through the member fact read.

**Why this priority**: It is the first evidence that the documented Analyst path works end to end with a real
container, real media, the SDK, acceptance, and fact indexing together; every later catalog capability depends on it.

**Independent Test**: Run the end-to-end test: build and register the reference image, finalize a short synthetic
recording under a short test segmentation policy, let a Docker-runtime Manager execute the run, and read the run's
facts for the reference capability; every segment has an accepted result, the facts lie inside their segments'
nominal windows, and their ranges and decoded-frame counts equal what the synthetic recording implies.

**Acceptance Scenarios**:

1. **Given** a registered reference image and a default workflow whose segment node pins its approved profile,
   **When** a Coach finalizes a synthetic recording that covers two segments of the test policy, **Then** both
   segment jobs wait for their segment media, become ready with it, and run the reference Analyst in the Docker
   runtime.
2. **Given** a running reference Analyst attempt, **When** the container ends, **Then** it exited successfully, the
   result mount held exactly the result manifest and the fact artifact, and the platform accepted the result for that
   attempt.
3. **Given** accepted results for every segment, **When** a member with read access lists the run's fact streams and
   reads the reference capability's facts, **Then** the stream reports every segment as accepted, and the facts cover
   each match-second of the recorded match time once, in time order, with the decoded-frame counts the synthetic
   recording's frame rate implies and frame statistics within their valid range.
4. **Given** the end-to-end run has finished, **When** the host is inspected, **Then** no container, mount, or staged
   media of the reference attempts remains.

---

### User Story 2 - The reference image is built from pinned inputs and runs under the Manager's restrictions (Priority: P1)

A maintainer builds the reference image from the repository root with the documented command. The build uses only
pinned, verified inputs: a base image pinned by digest, the project's locked dependencies, and the decoding tools of
the environment's pinned toolchain verified against their published hash. The image carries the packaging
convention's manifest and labels, so the Registry imports it by digest without refusal, and it runs under any
non-root user, on a read-only root file system, without network, environment configuration, or credentials.

**Why this priority**: An image the Registry refuses or the Manager's restrictions break can never run; this is also
the packaging example later Analyst authors copy.

**Independent Test**: Build the image, register it by digest in a test Registry, and run it under the Manager's launch
restrictions; the import is accepted with the declared capability, and the container produces a result as an
arbitrary non-root user on a read-only root file system without network.

**Acceptance Scenarios**:

1. **Given** the repository checkout, **When** the image is built with the documented command, **Then** the build
   uses the base image by digest, installs only the locked dependencies, verifies the decoding tools against their
   pinned hash, and fails with a named reason when any pinned input differs.
2. **Given** the built image pushed to the stamp's configured registry, **When** a Club Admin registers it by digest,
   **Then** the Registry accepts it, its stored capability declaration equals the project's `analyst-manifest.json`,
   and its labels equal the manifest's version, checksum, capability id and version, and model digest.
3. **Given** the Manager's Docker launch restrictions (arbitrary non-root user, read-only root file system, no
   network, dropped capabilities, bounded memory), **When** the reference Analyst runs, **Then** it writes only into
   the result and scratch mounts and the bounded temporary space, and succeeds.
4. **Given** the image, **When** its contents are inspected, **Then** it contains no model weights, credentials,
   platform addresses, or network client, and it carries the decoding tools' license notice.

---

### User Story 3 - The capability's facts are correct and deterministic in SDK-level tests (Priority: P2)

A maintainer changes the reference capability or the SDK. The capability's own tests run it through the SDK against
prepared mounts with synthetic segment media created at test time, and prove the facts: one per covered match-second
inside the nominal window, decoded-frame counts and frame statistics that match the synthetic media, no facts from
keyframe context or unmapped media time, identical output for identical input, and the failure behavior the SDK
defines for missing or inconsistent media.

**Why this priority**: It catches capability and SDK regressions in seconds without Docker, and documents the
capability's exact behavior; it builds on the SDK's own conformance tests.

**Independent Test**: Run the capability project's tests with the environment's locked verification commands; every
fact rule, the determinism rule, and every failure case is covered and passes.

**Acceptance Scenarios**:

1. **Given** a segment job with staged synthetic media of a known frame rate whose requested interval is preceded by
   keyframe context, **When** the capability runs, **Then** it writes one fact per covered match-second of the nominal
   window with the expected frame count and statistics, and none for the keyframe context.
2. **Given** a segment whose nominal window is only partly covered by mapped media, or whose mapping has a gap,
   **When** the capability runs, **Then** facts exist only for mapped match time inside the window, clipped to it.
3. **Given** the same job and media twice, **When** the capability runs both times, **Then** the two fact artifacts
   are byte-identical.
4. **Given** a job without segment media, staged media whose digest differs from its manifest, or media the decoder
   cannot read, **When** the capability runs, **Then** it ends with the SDK's failure exit for that category and
   writes no result.

---

### User Story 4 - A developer can run the reference Analyst locally (Priority: P3)

A developer wants to see a real Analyst run in their local development environment. The documentation of the
capability explains how to build the image, push it to a registry the development stamp is configured for, register
it by digest, approve its profile, define a workflow with the reference segment node and make it the default, and run
a Manager with the Docker runtime against it, all through the existing Registry operations and without changing the
configured Registry seed.

**Why this priority**: Useful for exploring and debugging the real path, but the automated end-to-end test already
provides the evidence.

**Independent Test**: Follow the documented steps against a local development stamp and observe accepted results and
readable facts for a finalized recording.

**Acceptance Scenarios**:

1. **Given** a local development stamp, **When** a developer follows the documented steps, **Then** a finalized
   recording's run executes the reference Analyst for real and its facts are readable through the member fact read.

### Edge Cases

- A frame whose match time lies exactly on a second boundary belongs to the later second; a frame exactly at the
  nominal window's end belongs to the next segment.
- When the mapped coverage ends or starts inside a second of the nominal window, that second's fact is clipped to the
  covered part of the window.
- A match-second inside the mapped coverage in which no frame was decoded (for example a low or variable frame rate):
  it is reported with a frame count of zero and no frame statistics.
- Media time outside every mapped span, or before or after the requested source interval (keyframe context), never
  produces a fact.
- The job carries no segment media although the profile requires video (a Manager or platform defect): the Analyst
  ends with the SDK's input-failure exit (`segment-media-required`) before decoding; it never guesses.
- The staged media passes digest verification but cannot be decoded: the attempt fails with a sanitized failure
  category; no partial result exists.
- The Manager runs the container as a user that does not exist in the image and with a read-only root file system:
  the Analyst still runs, because it needs no home directory and writes only to its mounts and temporary space.
- The memory limit of the profile is reached on large media: decoding is streamed, so memory stays bounded by the
  frame size rather than the segment length.
- A job of match scope or of another capability reaches the image (a workflow misconfiguration): the SDK refuses the
  inputs as invalid.
- The pinned decoding-tool archive is no longer downloadable from its publisher: the image build fails with a named
  reason instead of falling back to another build.

## Requirements *(mandatory)*

### Functional Requirements

#### Capability project

- **FR-001**: The reference capability MUST be an independent, locked Python project under
  `src/analysts/capabilities/<name>/` that follows the environment feature's Python project convention, depends on the
  Analyst SDK only through its local project, and uses a capability id that is visibly a development reference and
  not a catalog capability.
- **FR-002**: The project MUST contain no model or weights, no network client, no credential, and no platform address,
  and the Analyst MUST NOT open network connections or take configuration from environment variables.
- **FR-003**: Its capability declaration MUST state a low-level tier, segment execution scope, no upstream
  dependencies, a `required` video access, an output schema version, and deterministic and idempotent execution, and
  MUST state the platform's no-model digest instead of a model digest.
- **FR-004**: The Analyst MUST read only the job, the attempt context, and the staged segment media through the SDK,
  and MUST write its result only through the SDK, with the output type `foundational-facts`.
- **FR-005**: For every whole match-second (half-open, aligned to multiples of 1000 ms) that overlaps both the job's
  nominal window and the media's mapped match time, the Analyst MUST write exactly one fact whose range is that
  second clipped to the window and the mapped coverage, stating the number of frames decoded in it and, when at least
  one frame was decoded, the minimum, mean, and maximum mean-luma of those frames with fixed rounding.
- **FR-006**: A decoded frame MUST be attributed to a second only by its match time, converted from its media
  presentation time through the segment manifest's mapped spans; frames outside every mapped span, outside the
  requested source interval, or outside the nominal window MUST NOT contribute to any fact.
- **FR-007**: For identical job and media inputs the Analyst MUST produce a byte-identical fact artifact, with facts
  ordered by start time.
- **FR-008**: The Analyst MUST decode the media in a streaming way, so its memory use does not grow with the segment's
  duration, and MUST end with the SDK's failure behavior and a sanitized category when decoding fails.

#### Image

- **FR-009**: The project MUST hold its Dockerfile and its `analyst-manifest.json` side by side and follow the
  [packaging convention](../../docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging): the
  manifest validates against the released manifest schema, the last layer contains only the manifest, and the
  `io.socalytics.*` labels equal the manifest's version, checksum, capability id and version, and model digest; a
  build whose stated manifest checksum differs from the file MUST fail.
- **FR-010**: The image build MUST use only pinned inputs: a base image pinned by digest, the project's locked
  dependencies installed without resolution, and the decoding tools of the environment feature's pinned toolchain,
  verified against their pinned hash, with their license notice kept in the image. Byte-identical image digests across
  builds are not required.
- **FR-011**: The image MUST run its Analyst through the SDK's entry point under any non-root user id, on a read-only
  root file system, without network, environment variables set by the Manager, a home directory, or writes outside
  the result and scratch mounts and the temporary space the Manager provides.
- **FR-012**: The image MUST target the `linux/amd64` platform that the Registry resolves and the pinned toolchain
  supports, and its declared resources and requirements MUST fit the Docker runtime's validated limits in the test
  environment.

#### Registration and workflows

- **FR-013**: The reference image MUST register by digest through the existing Registry operation without a change to
  the Registry, and an approved profile with a `required` video policy MUST bind it, so a workflow can include it as a
  segment node.
- **FR-014**: A development and test workflow MUST be able to include the reference capability so that a run executes
  it for real on the Docker runtime, through the existing Registry operations (image registration by digest, profile
  approval, workflow definition, and default-workflow selection); the configured Registry seed MUST stay unchanged.
- **FR-015**: The reference capability MUST be kept out of production stamps by convention only: its id marks it as a
  development reference, only tests and the documented local procedure register it, and no Registry refusal rule is
  added.

#### Evidence and documentation

- **FR-016**: The capability project MUST have automated tests that run the Analyst through the SDK against prepared
  mounts with synthetic segment media generated at test time, and that cover FR-004 to FR-008, the edge cases above,
  and the SDK failure exits for missing, inconsistent, and undecodable media.
- **FR-017**: Automated tests MUST verify that the Dockerfile's pinned decoding-tool inputs equal the environment
  feature's toolchain pins and that the image labels equal the manifest.
- **FR-018**: Docker-backed end-to-end tests MUST build the reference image, make it available to the Docker runtime
  the way the existing end-to-end tests provide Analyst images, register it by digest, create a run from a short
  synthetic recording with a short test segmentation policy, execute it with a Docker-runtime Manager against
  materialized segment media, and verify accepted results for every segment, facts readable through the member fact
  read whose ranges and decoded-frame counts equal fixed values derived from the synthetic recording's frame rate and
  the test policy and whose frame statistics are present and within their valid range, and a clean host afterwards.
- **FR-019**: This feature MUST NOT change contracts, the public API, persistence, or the Registry's import rules; the
  existing SDK, segment staging, acceptance, and fact indexing behavior MUST be used unchanged.
- **FR-020**: The capability MUST ship documentation of its behavior, its build and test commands, and the local run
  procedure; the Analysts area documentation and the architecture MUST describe it as development evidence that is
  not part of the production capability catalog.

### Key Entities

- **Reference Capability**: The development-only low-level, segment-scoped capability with its declaration, profile,
  and image; never a catalog capability.
- **Reference Image**: The digest-pinned OCI image built from the project, carrying the manifest, labels, the Analyst,
  the SDK, the locked dependencies, and the decoding tools.
- **Frame-Second Fact**: One foundational fact per covered match-second of a segment's nominal window: its half-open
  range, decoded-frame count, and frame statistics.
- **Test Segmentation Policy**: The short segmentation policy the end-to-end evidence uses so a short synthetic
  recording yields several segments.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the end-to-end test, 100% of the synthetic recording's segments end with an accepted reference result
  on the first attempt, and 100% of the facts read through the member fact read have the expected ranges and
  decoded-frame counts and frame statistics within their valid range.
- **SC-002**: 0 facts lie outside their segment's nominal window or come from keyframe context or unmapped media time,
  in both the capability tests and the end-to-end test.
- **SC-003**: Two runs of the capability over the same job and media produce byte-identical fact artifacts in 100% of
  the determinism tests.
- **SC-004**: The reference image is accepted by the Registry on its first registration, and 100% of its runs in the
  end-to-end test succeed under the Manager's non-root, read-only, network-less launch restrictions.
- **SC-005**: The capability project passes the environment's locked verification sequence, and an Analyst author can
  find every file a new Analyst needs (project, lockfile, manifest, Dockerfile, tests, and documentation) in the one
  reference project.

## Assumptions

### Dependencies

- [Python Analyst SDK](../20261017-220709-python-analyst-sdk/spec.md) (twin #160) provides the SDK, the completed
  container interface, the entry point and its exit statuses, and the single `facts.json` fact artifact; it MUST merge
  before this feature.
- [Materialized Video Segments and Analyst Input Staging](../20261017-222433-materialized-segments/spec.md) (twin #161)
  provides materialized segment media, analyst-job 1.2.0, staging at `/socalytics/media/`, the SDK's segment media
  model, the synthetic test recording, and the short test segmentation policy; it MUST merge before this feature.
- [Accepted Fact Indexing and Capability-Scoped Fact Queries](../20261017-175648-fact-indexing-queries/spec.md)
  (twin #144) provides the fact-artifact contract, fact indexing at acceptance, and the member fact read; it MUST merge
  before this feature.
- [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md) (merged)
  provides CPython, `uv`, the pinned FFmpeg and ffprobe build, and the locked verification of
  `src/analysts/capabilities/<name>/`.
- [Docker Desktop Runtime Adapter for Analyst Execution](../archive/20261015-092159-docker-runtime-adapter/spec.md),
  [Analyst Registry](../archive/20261012-152148-analyst-registry/spec.md),
  [Analyst Manager Work Execution](../archive/20261012-141233-manager-work-execution/spec.md), and
  [Analysis Result Manifests and Validated Acceptance](../archive/20261013-125502-analysis-result-manifests/spec.md)
  (merged) provide the digest-pinned pulls with platform-issued pull credentials, the launch restrictions, the image
  import by digest, the profiles and workflows, and the result acceptance this feature uses unchanged.

### Architecture Alignment

- Implements the first SDK-based Analyst image of
  [Analyst Containers](../../docs/architecture/analysts-models-and-hardware.md#analyst-containers) and
  [Image Metadata And Packaging](../../docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging),
  and adds real-container evidence to
  [Implementation Validation](../../docs/architecture/analysts-models-and-hardware.md#implementation-validation) and
  the container interface of
  [Runtime Preflight And Status](../../docs/architecture/analyst-runtime-and-recovery.md#runtime-preflight-and-status).
- The [Analyst Capability Catalog](../../docs/architecture/analyst-capability-catalog.md) names only production
  capabilities; this reference capability is development evidence and never enters it.

### Scope Boundaries

- Catalog capabilities, models and weights, accelerators, publishing to GHCR, release workflows, production promotion
  gates, and production profile values are out of scope.
- Manual Windows Docker Desktop evidence beyond what the Docker runtime adapter already requires is out of scope; the
  automated evidence runs against a Linux engine as the existing end-to-end tests do.
- The existing end-to-end test Analyst and the simulated Analyst stay unchanged.
