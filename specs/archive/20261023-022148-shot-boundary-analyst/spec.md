# Feature Specification: Shot-Boundary Media Context Analyst (Baseline)

**Feature Branch**: `20261023-022148-shot-boundary-analyst`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "The first real catalog Analyst capability: a deterministic, CPU-only baseline of
`media-context-classification` that detects shot boundaries (hard cuts and adaptive-threshold cuts) in the staged
segment media of a job and writes one fact per detected boundary, so analyses of fixed and broadcast footage finally
produce domain facts instead of the development `frame-second-probe` facts."

## Clarifications

### Session 2026-10-23

- Q: What should the capability do with frames in a staged segment that lie outside the segment's nominal window, such
  as lead-in frames before the requested start that stream-copy extraction can keep? → A: Decode them only as the
  predecessor of the first in-window frame, and report a boundary only when the first frame of the new shot lies
  inside the nominal window
- Q: What match-time range should a shot-boundary fact cover, given that it must start at the first frame of the new
  shot and be non-empty? → A: From the first frame of the new shot to the timestamp of the next frame (one frame
  interval), or to the segment window end for the last frame
- Q: Should this feature ship an opt-in Development and Test seed that registers the capability in the default
  workflow, or only a documented registration procedure through the existing Registry operations? → A: Only a
  documented registration procedure; the end-to-end test performs the same registration through the Registry
  operations and no seed is added
- Q: What concrete resource bounds must a one-minute 1080p segment meet when the capability analyzes it on a CPU-only
  host? → A: Peak memory of at most 2 GiB and completion within the attempt lease, checked in a Docker-backed test
- Q: Which licenses should the dependency audit check accept for the capability image's dependencies, so that a
  dependency with any other license fails verification? → A: B) Permissive licenses plus LGPL for separately packaged
  shared libraries (such as the FFmpeg libraries bundled with or installed beside OpenCV); GPL, AGPL, and unknown
  licenses fail verification

## User Scenarios & Testing *(mandatory)*

The users are Club Admins, who register a versioned Analyst image, bind it to a profile and the default workflow, and
then read the resulting facts in the connected analysis review; Coaches and other team members, who read the facts of a
recorded match; Analyst authors of later detector, tracking, and calibration capabilities, who will consume shot
boundaries to reset tracklets at cuts; and platform maintainers, who need evidence that a real catalog-named capability
passes every boundary from image registration to member-readable facts.

### User Story 1 - A recorded match yields shot-boundary facts (Priority: P1)

A Coach finalizes a match recording whose footage contains camera cuts. The run's default workflow contains the
`media-context-classification` capability as a segment-scoped node whose profile requires video. For every segment the
platform materializes the segment media and a Manager executes the capability's container against it. The capability
examines the decoded frames of the segment and writes one fact for every shot boundary it detects, each placed at the
match time of the first frame of the new shot. Members read these facts in the existing analysis review, in match-time
order, as facts of the capability and its fact kind.

**Why this priority**: It is the feature's purpose: the first domain facts produced by a real capability instead of
development probe facts. Every later visual capability that must reset at cuts depends on them.

**Independent Test**: Finalize a short synthetic recording that contains a known number of hard cuts at known frame
positions, execute the run end to end, and read the run's facts for the capability: every known cut has a fact at its
match time and no other fact exists.

**Acceptance Scenarios**:

1. **Given** a registered, approved profile and a default workflow with the capability as a segment node, **When** a
   Coach finalizes a recording with three hard cuts inside one segment, **Then** the segment's attempt is accepted and
   exactly three shot-boundary facts are readable, each at the match time of the first frame after its cut.
2. **Given** a recording whose cuts fall in two different segments, **When** the run completes, **Then** each fact lies
   inside the nominal window of the segment that produced it and no cut is reported twice.
3. **Given** a segment without any cut, **When** its attempt completes, **Then** the result is accepted with no
   shot-boundary facts and the review shows the segment as analyzed with an empty fact stream, not as failed or
   missing.
4. **Given** the same segment media, **When** the capability runs twice (including on a different host), **Then** both
   results contain identical facts.

---

### User Story 2 - Fixed-camera and gradual-change footage does not drown members in false boundaries (Priority: P1)

The capability distinguishes an abrupt content change from ordinary visual variation. A hard cut is a change between two
consecutive frames that exceeds a fixed content-change threshold. An adaptive cut is a change that is large relative to
the changes in the surrounding frames, so that it is detected in footage with strong motion or lighting changes where a
fixed threshold would either miss it or fire constantly. Each fact says which rule detected it.

**Why this priority**: Fixed tactical-camera and broadcast footage are both first-release profiles. A baseline that
fires on every pan, flash, or lighting change is worse than none for the capabilities that consume it.

**Independent Test**: Run the capability on generated clips for each case and compare the facts to the known truth of
each clip.

**Acceptance Scenarios**:

1. **Given** a clip of a static scene with sensor-like noise and no cut, **When** the capability runs, **Then** it
   writes no fact.
2. **Given** a clip with a steady camera pan and no cut, **When** the capability runs, **Then** it writes no fact.
3. **Given** a clip with a cut between two low-contrast scenes whose change is below the fixed threshold but large
   relative to its surroundings, **When** the capability runs, **Then** it writes one fact whose detection rule is the
   adaptive rule.
4. **Given** a clip with a very high-contrast hard cut, **When** the capability runs, **Then** it writes one fact whose
   detection rule is the hard-cut rule, and never two facts for the same cut.
5. **Given** two cuts closer together than the capability's declared minimum shot length, **When** the capability runs,
   **Then** it writes only the first and records nothing for the second, as a documented, deterministic behavior.

---

### User Story 3 - A Club Admin registers and activates the capability through the normal Registry path (Priority: P2)

A Club Admin registers the capability's digest-pinned image, creates and approves a profile that binds the capability
version to that image digest with a `required` video policy, defines a workflow with the capability's segment node, and
makes it the default workflow. The Registry imports the capability declaration from the image's manifest and refuses an
image whose labels and manifest differ. A documented procedure makes this repeatable without hand-editing; no seed is
added.

**Why this priority**: Without a registration path the capability cannot be used; it is P2 because it uses existing
Registry operations and adds documentation, not a seed or new Registry behavior.

**Independent Test**: Register the built image by digest through the Registry operations; the imported declaration
shows the capability identity, version, scope, video policy, no dependencies, and the shot-boundary fact kind.

**Acceptance Scenarios**:

1. **Given** the built image, **When** a Club Admin registers it by digest, **Then** the import succeeds and the cached
   declaration names `media-context-classification`, its version, segment scope, `required` video policy, no
   dependencies, and its fact kind.
2. **Given** an imported image, **When** the Club Admin approves a profile, defines a workflow, and sets it as default,
   **Then** a new run resolves that profile and workflow once and pins them.
3. **Given** a Development and Test stamp, **When** a maintainer follows the documented registration procedure,
   **Then** the capability is usable in a default workflow without production-only values and without any seed.
4. **Given** a stamp that does not register the capability, **When** a run is created, **Then** behavior is unchanged.

---

### User Story 4 - Maintainers can trust the image's provenance (Priority: P2)

A maintainer or auditor can see, for the capability image, exactly what it contains: pinned base image by digest,
locked dependencies, the pinned media toolchain, the license and software bill of materials audit evidence of every
dependency, and the absence of any model weights. Nothing is downloaded at run time and no credentials or network are
needed.

**Why this priority**: The catalog requires an independent license and provenance audit for every named framework and
transitive dependency before a capability can ever be promoted; this feature must start that evidence and must not
claim more than it has.

**Independent Test**: Build the image from locked inputs, inspect its labels and its manifest, and check the audit
evidence against the installed dependency set; a dependency without an audit entry or a license outside the allowed
set fails verification.

**Acceptance Scenarios**:

1. **Given** the project's locked dependency set, **When** the audit check runs, **Then** every direct and transitive
   dependency has a recorded name, version, and license, and the check fails for one that is missing or disallowed
   (a GPL, AGPL, or unknown license).
2. **Given** the built image, **When** its labels and manifest are inspected, **Then** the labels equal the manifest,
   the model digest is the platform's no-model digest, and no model weights are present.
3. **Given** the image, **When** it runs under a non-root user on a read-only root file system without network access,
   **Then** it completes a job.

---

### Edge Cases

- A segment's media is shorter than two frames, or has a single frame: no boundary can be detected; the result is
  accepted with no facts.
- A cut exactly at the first frame of a segment: a cut between the last frame of one segment and the first frame of the
  next is reported by the next segment only when a lead-in predecessor frame was staged, and by neither segment
  otherwise; it is never reported by the earlier segment.
- Frames outside the segment's nominal window (for example lead-in frames before the requested start that stream-copy
  extraction keeps): they are decoded only as the predecessor of the first in-window frame; a boundary is reported
  only when the first frame of the new shot lies inside the nominal window, so a cut among lead-in frames is never
  reported and a cut between the last lead-in frame and the first in-window frame is.
- Variable frame rate or irregular timestamps in the staged media: fact positions derive from the frames' own
  timestamps mapped to match time, never from frame counting at a nominal rate.
- Corrupt or undecodable media, or media missing from the job: the attempt ends as failed with a named reason, and
  never writes a partial result.
- A job without staged media (for example, a stale job or profile that does not require video): the capability fails
  the attempt with the reason `segment-media-required` and writes no result (tasks T013 and T019).
- Black frames, fades, dissolves, and flashes: only the rules above apply; a gradual transition is not reported as a
  cut unless the adaptive rule fires at one frame pair, and this limitation is documented, not hidden.
- Very long or high-resolution segments: processing stays within the attempt's lease and the memory bound of SC-005
  (at most 2 GiB peak for a one-minute 1080p segment); a run beyond the execution timeout ends as a timeout, not as a
  silent partial result.
- Two facts mapping to the same match time: impossible by construction, since one frame pair yields at most one fact.
- Reprocessing after retry or rerun: results are identical, so accepted-fact indexing sees the same facts.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The feature MUST add one independent, locked project under `src/analysts/capabilities/` for the
  capability `media-context-classification` at version 1.0.0, built on the implemented Analyst SDK (job, attempt
  context, staged segment media, result writing, entry point), and MUST NOT change the SDK's or the platform's
  contracts.
- **FR-002**: The capability MUST declare, in a versioned `analyst-manifest.json` beside its Dockerfile that is valid
  against the released manifest contract: segment scope, `required` video policy, no dependencies, the platform's
  no-model digest, and its capability-scoped fact kind for shot boundaries.
- **FR-003**: The capability MUST read only the staged segment media and the job document. It MUST NOT use network
  access, credentials, upstream results, model weights, or randomness.
- **FR-004**: The capability MUST decode every frame of the segment's primary video stream and compare each pair of
  consecutive frames by a content-change measure that depends only on the pixel content. Frames outside the segment's
  nominal window (such as lead-in frames kept by stream-copy extraction) MUST be decoded only as the predecessor of
  the first in-window frame and MUST NOT otherwise produce facts.
- **FR-005**: The capability MUST report a hard cut where the change between consecutive frames exceeds a fixed
  threshold, and an adaptive cut where the change exceeds both a lower absolute floor and a declared multiple of the
  change observed in a declared window of surrounding frames. The thresholds, window, minimum shot length, and
  detection order MUST be declared constants recorded in the manifest or capability documentation and in every fact's
  provenance, so that results are traceable to the settings that produced them.
- **FR-006**: The capability MUST write exactly one fact per detected boundary, no fact for any other frame pair, and
  no boundary twice. A boundary within the minimum shot length of the previous reported boundary MUST NOT be reported.
- **FR-007**: A fact's match-time range MUST start at the match time of the first frame of the new shot, lie inside the
  segment's nominal half-open window, and be non-empty. It MUST end at the match time of the next frame (one frame
  interval), or at the segment window end for the last frame. A boundary MUST be reported only when the first frame
  of the new shot lies inside the nominal window. Its position MUST derive from the frame timestamps and the staged
  segment's position on the match timeline.
- **FR-008**: A fact's payload MUST contain at least: the detection rule (`hard-cut` or `adaptive-cut`), the content
  change measure of the frame pair and the threshold(s) it was compared against, the zero-based frame index in the
  decoded video stream of the staged segment file (lead-in frames included) of the first frame of the new shot, and the last frame time of the previous shot. The payload shape MUST be
  documented in the capability's contract; where the Capability Fact Payload Schemas feature has landed, the shape
  MUST also be declared as that feature's payload schema in the manifest and enforced by the SDK and by result
  acceptance.
- **FR-009**: The capability MUST NOT emit shot type, live, replay, graphics, quality, or usable-interval facts. The
  declared outputs of version 1.0.0 are shot boundaries only.
- **FR-010**: The capability MUST be deterministic: the same staged media MUST produce byte-identical facts, in
  match-time order, regardless of host, CPU count, or thread scheduling.
- **FR-011**: The capability MUST end the attempt as failed with a stable, named reason, and write no result, when the
  media is missing, undecodable, has no video stream, or its timestamps cannot be mapped to the segment's window. A
  segment with fewer than two frames MUST complete successfully with no facts.
- **FR-012**: The capability's image MUST be a digest-pinned `linux/amd64` OCI image built from its own Dockerfile at
  the repository root, following the packaging convention (manifest valid and copied last, literal `io.socalytics.*`
  labels verified against the manifest at build time, no tag references, only pinned build inputs: base image by
  digest, locked dependencies, and hash-verified toolchain). It MUST run under any non-root user on a read-only root
  file system without network access, and MUST contain no model weights.
- **FR-013**: The feature MUST record, in the project, audit evidence of every direct and transitive dependency
  (name, version, license, and a software bill of materials of the capability layers: Python environment, FFmpeg
  build, and base image reference by digest, with operating-system packages identified only by the base image
  digest), MUST verify the evidence against the locked
  dependency set in the project's tests, and MUST fail verification for a dependency without evidence or with a
  license outside the allowed set. The allowed SPDX expressions are: permissive licenses; LGPL for separately
  packaged shared libraries (such as the FFmpeg libraries bundled with or installed beside OpenCV); and
  `GPL-3.0-with-GCC-exception` (runtime library exception) only for the bundled GCC runtime libraries listed in the
  audit with their parent wheel. Any other GPL or AGPL expression, and any unknown license, MUST fail
  verification. The evidence is development evidence and MUST NOT be presented as the
  catalog's completed license and provenance audit.
- **FR-014**: The project MUST include unit tests that generate small synthetic clips at test time (no committed
  binary media beyond what the tests generate), covering at least: hard cut, adaptive cut, no cut with noise, no cut
  with pan, closely spaced cuts, single-frame and empty media, determinism across runs, timestamp mapping with a
  non-zero segment offset, and each named failure reason.
- **FR-015**: The Development and Test stamp MUST have a documented registration procedure through the existing
  Registry operations, so that a default workflow can execute the capability for real. The feature MUST NOT add a
  seed (opt-in or otherwise); the end-to-end test performs the same registration through the Registry operations, and
  the default configured seed and every production-oriented configuration MUST stay unchanged.
- **FR-016**: Docker-backed end-to-end evidence MUST, on a Linux engine, build and register the image by digest,
  approve a profile, define and default a workflow, finalize a short synthetic recording with known cuts under a short
  test segmentation policy, execute the run through the Segment Service, Manager execution, result acceptance, and fact
  indexing, and read the facts through the member fact read operations (fact listing and fact stream listing). It MUST
  compare the facts' kinds, match-time positions, detection rules, and counts with the known truth of the recording
  exactly.
- **FR-017**: The facts MUST appear in the existing connected analysis review without Web UI changes. The feature MUST
  NOT add endpoints, screens, contracts, or Registry behavior.
- **FR-018**: The feature MUST be labelled development evidence everywhere it is described: it MUST NOT be promoted to a
  production workflow, MUST NOT be published to a registry, and MUST state that the catalog's quality gates on fixed
  and broadcast subsets are not met. No accuracy claim MAY be made beyond the synthetic fixtures' known truth.
- **FR-019**: The existing `reference-media-probe` capability and its evidence MUST stay unchanged and keep passing.
- **FR-020**: Documentation MUST be corrected where it becomes false: the architecture catalog entry for
  `media-context-classification` and Wave 1 (baseline of shot boundaries implemented as development evidence; types,
  live/replay/graphics, quality, usable intervals, the challenger, and gates not implemented), the packaging section
  (first catalog-named capability image), the repository README and AGENTS guidance, and the Analyst source-area
  README.

### Key Entities

- **Shot boundary**: The point in match time where one continuous camera take ends and the next begins. Attributes:
  the match range from the first frame of the new shot to the next frame (or the segment window end), the detection
  rule, the change measure and thresholds, the zero-based frame index in the decoded video stream of the staged segment file (lead-in frames included), and the end time of the previous
  shot's last frame.
- **Shot-boundary fact**: A fact of the capability `media-context-classification` with its capability-scoped fact kind,
  one per boundary, belonging to one segment's accepted result.
- **Detection settings**: The declared thresholds, window, minimum shot length, and rule order that determine the
  facts; part of the capability version, so a change makes a new version.
- **Capability image**: The digest-pinned image bundling the capability, its manifest, and its locked dependencies,
  with audit evidence and no model weights.
- **Dependency audit record**: The recorded name, version, and license of one dependency, plus the image's software
  bill of materials.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the end-to-end evidence, 100% of the known cuts of the synthetic recording appear as facts at their
  exact match-time positions and no unexpected facts exist, for recordings spanning at least two segments.
- **SC-002**: On the generated no-cut clips (static noise and steady pan), the capability writes zero facts.
- **SC-003**: Running the capability repeatedly (at least three times, including with different CPU limits) on the same
  media yields byte-identical results every time.
- **SC-004**: A Club Admin can go from a built image to a run that produces facts by following one documented
  procedure using only existing Registry operations, with no manual edits of stored data.
- **SC-005**: A segment of the evidence's size (a few seconds of video) is analyzed in at most 30 seconds of wall time
  on a CPU-only host, and a one-minute 1080p segment is analyzed with a peak memory of at most 2 GiB and a wall time
  of at most 300 seconds (the manifest's `minExecutionTimeoutSeconds`), checked in a Docker-backed test.
- **SC-006**: 100% of direct and transitive dependencies of the image have audit records with an allowed license
  (permissive, LGPL for separately packaged shared libraries, or the GCC runtime library exception for bundled GCC
  runtime libraries), a software bill of materials of the capability layers (Python environment, FFmpeg build, base
  image reference by digest, with operating-system packages identified only by the base image digest) exists, and
  the image contains zero model weights.
- **SC-007**: All existing reference-analyst, SDK, Manager, platform, and Web UI tests keep passing unchanged, and the
  feature adds no new platform contract.
- **SC-008**: No description of this capability, in code, documentation, or run output, claims production readiness or
  promoted quality.

## Assumptions

### Dependencies

- Depends on the implemented Python Analyst SDK, the Reference Analyst Container, Materialized Video Segments and
  Analyst Input Staging, the Analyst and Media Verification Environment, the Analyst Registry, Analysis Result
  Manifests and Validated Acceptance, and Accepted Fact Indexing and Capability-Scoped Fact Queries features. All are
  merged.
- The [Capability Fact Payload Schemas](../20261023-010826-fact-payload-schemas/spec.md) feature is open. This feature
  does not require it: the payload shape is documented and tested in the capability's own tests; if that feature has
  merged when this one is planned, the plan declares the payload schema through it.
- The environment actions provide CPython, `uv`, the pinned FFmpeg and ffprobe build, and locked verification of
  `src/analysts/capabilities/<name>/`. If the chosen OpenCV distribution or its system libraries are not available
  there, a standalone environment spec MUST land first; this feature never changes the environment actions itself.

### Decisions Made By Default

- The capability id is the catalog's `media-context-classification` and its first version is 1.0.0. Its fact kind is
  `shot-boundary`. The detector uses OpenCV-based content-change and adaptive rules, as the catalog names for the
  baseline.
- A boundary is attributed to the segment that contains both frames of the pair; a cut between the last frame of one
  segment and the first frame of the next is reported by the next segment only when a lead-in predecessor frame was
  staged, and by neither segment otherwise; it is never reported by the earlier segment (the capability reads one
  segment at a time and segments do not overlap in the examples used here). This is a documented limitation of the baseline; handling
  overlapping or handover frames is deferred.
- The detection rules use the luminance and color change of a downscaled frame; the exact measure is a plan decision
  and a declared part of the capability version.
- Facts carry a fixed confidence value in the fact envelope that states "rule-based, uncalibrated"; calibrated
  confidence is out of scope.
- Registration uses the existing Registry operations; the Registry gains no refusal rule that keeps a capability out of
  production stamps, in line with the reference Analyst.

### Architecture Alignment

- Implements the `media-context-classification` row of
  [Low-Level Capabilities](../../../docs/architecture/analyst-capability-catalog.md#low-level-capabilities) (baseline of
  shot boundaries only), Wave 1 of
  [Implementation Waves](../../../docs/architecture/analyst-capability-catalog.md#implementation-waves), the packaging
  rules of [Catalog Rules](../../../docs/architecture/analyst-capability-catalog.md#catalog-rules), and
  [Image Metadata And Packaging](../../../docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging).

### Scope Boundaries

- Out of scope: shot type; live, replay, and graphics classification; quality and usable-interval classification (need
  learned classifiers and a labelled corpus); the golden corpus, quality gates, and any promotion to a production
  workflow; GPU or accelerator profiles; detectors; tracking; calibration; new Web UI screens; a production image
  registry and publication; and production values.
- The capability remains development evidence until the catalog's validation gates are met separately on fixed and
  broadcast subsets; this feature does not meet them.
- The reference capability, simulated Analyst, and existing end-to-end tests stay unchanged.
