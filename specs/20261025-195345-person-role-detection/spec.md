# Feature Specification: Person and Role Detection Analyst (Development Baseline)

**Feature Branch**: `20261025-195345-person-role-detection`

**Created**: 2026-10-25

**Status**: Draft

**Input**: User description: "Coaches get the first model-based visual evidence of a match: for each segment of a run,
the Analyst capability `person-role-detection` writes player, goalkeeper, referee, and staff boxes with confidence as
indexed facts, readable through the existing member fact reads, the Web UI analysis review, and evidence playback. A new
Python capability project on the Analyst SDK decodes the staged segment media (`videoPolicy` `required`, media context
optional) and runs a CPU model-inference graph with full-frame preprocessing owned by the container, declares the fact
payload schemas of its detection fact kind in the Analyst manifest 1.2.0, and ships with a development registration
procedure, Docker-backed end-to-end evidence, and an evaluation through the Analyst evaluation harness where it exists.
The model weights are a permissively licensed pretrained model that passes a recorded licence and provenance audit, are
pinned by digest, and are explicitly development evidence."

## Clarifications

### Session 2026-10-25

- Q: How does a pretrained model that only knows generic classes (for example "person") produce the four role labels? → A: The capability maps the model's own output classes to the four roles with a declared, versioned mapping recorded in the capability documentation and in every fact's provenance; roles the chosen model cannot distinguish are not emitted as those roles, and the capability states which of the four roles it can emit (superseded in part by the next answer, which adds the role-unknown `person` value).
- Q: Does the capability consume media-context results (shot boundaries, usable intervals)? → A: No. Media context stays an optional dependency that is declared but not consumed in this version; the capability analyzes every decoded frame position it samples and never relies on upstream results for correctness.
- Q: Which frames of a segment does the capability analyze? → A: A fixed, declared sampling rule (a constant number of analyzed frames per second of match time, chosen in the plan) so that cost per segment is bounded and results are deterministic; every fact names the frame it came from.
- Q: What counts as the same result for determinism on different hosts? → A: Identical facts on repeated runs on the same image and CPU architecture class; across hosts, boxes and confidences are compared within a declared numeric tolerance that the evidence states, and the roles, counts, and frame references must match exactly.
- Q: Which licences are allowed for the dependency and model audit? → A: The rule of the `media-context-classification` audit (permissive licences, LGPL for separately packaged shared libraries, the GCC runtime library exception for bundled GCC runtime libraries); the model weights' licence must be permissive and the model's training-data terms must be recorded, and anything unknown fails verification.
- Q: If the chosen pretrained model only knows a generic "person" class and cannot tell players from referees, goalkeepers, or staff, which role label should the capability put on those detections? → A: Extend the declared payload schema with an explicit `person` (role-unknown) value in addition to the four roles, and emit generic detections with it.
- Q: Should a detection fact's time range end at the next sampled (analyzed) frame, or at the next decoded frame of the stream? → A: End at the match time of the next analyzed (sampled) frame, or the segment window end for the last analyzed frame.
- Q: When the end-to-end evidence uses rendered synthetic figures that a pretrained real-world model may not detect reliably, what should the evidence require? → A: Require the figures only for the roles the model can emit, and verify the rest of the pipeline (schema, coordinates, windows, acceptance) with a fixed minimum recall bound stated in the plan.

## User Scenarios & Testing *(mandatory)*

The users are Coaches and other team members, who read the first model-derived visual facts of a recorded match; Club
Admins, who register the capability's digest-pinned image and bind it into the default workflow; Analyst authors of the
later tracking, team, and identity capabilities, who need merged source-frame detections to start from; and platform
maintainers, who need evidence that a model-based capability passes every boundary from image registration to
member-readable facts and playable evidence. The capability is development evidence of the first detection slice. It is
not the catalog's fine-tuned baseline and meets no quality gate.

### User Story 1 - A recorded match yields person-role detection facts (Priority: P1)

A Coach finalizes a match recording. The run's default workflow contains the `person-role-detection` capability as a
segment-scoped node whose profile requires video. For every segment the platform materializes the segment media and a
Manager executes the capability's container against it. The capability decodes the segment, analyzes frames at a fixed
sampling rate, and writes one fact for each detected person-like object, carrying its role, its box in source-frame
coordinates, and its confidence. Members read these facts in the existing analysis review, in match-time order, as facts
of the capability and its fact kind.

**Why this priority**: It is the feature's purpose: the first model-based visual evidence of a match.

**Independent Test**: Finalize a short synthetic recording whose frames contain rendered figures of known roles at known
positions, execute the run end to end, and read the run's facts for the capability: the recall over all rendered figure
appearances at sampled frames, matched by role person, time interval, and IoU of at least 0.5, meets the fixed minimum
bound stated in the plan (0.6); facts without a matching figure are reported but do not fail the test; a specific role is
required only for roles the class mapping can emit (none in version 1.0.0); and no fact lies outside its segment's
window.

**Acceptance Scenarios**:

1. **Given** a registered, approved profile and a default workflow with the capability as a segment node, **When** a
   Coach finalizes a recording that spans two segments, **Then** each segment's attempt is accepted and the facts of
   each lie inside the nominal window of the segment that produced them.
2. **Given** a segment whose analyzed frames contain no detectable person, **When** its attempt completes, **Then** the
   result is accepted with no detection facts and the review shows the segment as analyzed with an empty fact stream,
   not as failed or missing.
3. **Given** the same staged media, **When** the capability runs twice on the same image, **Then** both results contain
   identical facts in the same order.
4. **Given** a fact in the analysis review, **When** a Coach opens evidence playback for it, **Then** the existing
   dialog plays the segment media around the fact's match time without any Web UI change.

---

### User Story 2 - Every detection fact is validated, attributable, and honest about the model (Priority: P1)

Each fact states its role, box, and confidence in a shape the platform validates at acceptance. A fact never claims more
than the model said: the confidence is the model's own score after the declared postprocessing, the role is the result of
the declared class mapping, and the provenance names the model digest, the preprocessing and postprocessing settings, and
the sampled frame. A malformed result is rejected as a whole.

**Why this priority**: Downstream tracking and team classification consume these boxes; an unvalidated or untraceable
payload would poison them.

**Independent Test**: Run the capability on generated frames and validate every written fact against the declared
payload schema; submit result variants with a missing box, a box with a negative coordinate, an undeclared role, and an
out-of-range confidence through acceptance and confirm each is refused with the named reason. Containment of the box
in the exact frame is guaranteed by the capability and asserted in its tests.

**Acceptance Scenarios**:

1. **Given** the capability's manifest, **When** a Club Admin registers the image, **Then** the Registry caches the
   declared payload schema of the detection fact kind with the declaration and each run's node snapshot pins it.
2. **Given** a result whose fact payload violates the schema (missing field, role outside the declared roles `player`,
   `goalkeeper`, `referee`, `staff`, and `person`, confidence outside zero to one, a non-positive box size, or a coordinate outside the declared bounds; containment of the
   box in the exact frame is guaranteed by the capability and asserted in its tests, research.md R-06), **When** the attempt is
   accepted, **Then** acceptance ends the attempt as failed with a named payload reason and no fact of the result is
   visible.
3. **Given** a written fact, **When** a member reads it, **Then** its payload names the role, the box in source-frame
   pixel coordinates together with the source frame size, the confidence, the model identity and digest, the class
   mapping version, the sampling rule, the preprocessing and postprocessing settings, and the zero-based index of the
   analyzed frame in the staged segment file.
4. **Given** two overlapping detections of the same role, **When** the capability finishes a frame, **Then** its declared
   duplicate-merge rule has been applied once per frame and no pair of same-role boxes above the declared overlap
   threshold remains in that frame.

---

### User Story 3 - Preprocessing is owned by the container and is full-frame (Priority: P2)

The capability decodes each analyzed frame once, resizes and pads it to the model's input in memory as a full-frame
pass, runs the model, and remaps every box to source-frame coordinates. Neither the job, the profile, nor the Manager
selects or overrides these steps, and nothing is persisted besides facts. Tiled and hybrid inference do not exist in this
version.

**Why this priority**: It implements the catalog's container-owned preprocessing rule for the first detector and keeps
later tiled profiles a version change of the container rather than a platform change.

**Independent Test**: Analyze frames of different source resolutions and aspect ratios containing a figure at a known
position and confirm the written boxes are in source-frame coordinates and independent of the model's input size.

**Acceptance Scenarios**:

1. **Given** frames of two different source resolutions showing the same scene, **When** analyzed, **Then** boxes agree
   in relative position within a stated tolerance and are expressed in each frame's own pixel coordinates.
2. **Given** the job document, **When** it carries any field that looks like a preprocessing, tiling, or threshold
   setting, **Then** the capability ignores it and the result is unchanged.
3. **Given** the capability's documentation, **When** a reader looks for tiled or hybrid profiles, **Then** it states
   that only the full-frame profile exists.

---

### User Story 4 - A Club Admin registers and activates the capability through the normal Registry path (Priority: P2)

A Club Admin registers the capability's digest-pinned image, creates and approves a profile that binds the capability
version to that image digest and the pinned model digest with a `required` video policy, defines a workflow with the
capability's segment node, and makes it the default workflow. A documented procedure makes this repeatable without
hand-editing; no seed is added.

**Why this priority**: Without a registration path the capability cannot be used; it uses existing Registry operations
and adds documentation only.

**Independent Test**: Register the built image by digest through the Registry operations; the imported declaration shows
the capability identity, version, scope, video policy, optional media-context dependency, model digest, and the fact kind
with its payload schema.

**Acceptance Scenarios**:

1. **Given** the built image, **When** a Club Admin registers it by digest, **Then** the import succeeds and the cached
   declaration names `person-role-detection`, its version, segment scope, `required` video policy, the model digest, and
   its fact kind.
2. **Given** an imported image, **When** the Club Admin approves a profile, defines a workflow, and sets it as default,
   **Then** a new run resolves that profile and workflow once and pins them.
3. **Given** a stamp that does not register the capability, **When** a run is created, **Then** behavior is unchanged.

---

### User Story 5 - Maintainers can trust the model and image provenance (Priority: P2)

A maintainer or auditor sees exactly what the image contains: the pinned base image by digest, locked dependencies, the
pinned media toolchain, a recorded licence and provenance audit of the model weights (source, version, licence, training
data terms, digest) and of every dependency, a CycloneDX software bill of materials, and a statement that the weights are
development evidence. Nothing is downloaded at run time and no credentials or network are needed.

**Why this priority**: The catalog requires an independent licence and provenance audit for every named framework, model,
and transitive dependency before promotion; this feature starts that evidence and claims no more.

**Independent Test**: Build the image from locked inputs, inspect its labels, manifest, model file, and audit evidence; a
model file whose digest differs from the pinned digest, a dependency without an audit entry, or a licence outside the
allowed set fails verification.

**Acceptance Scenarios**:

1. **Given** the locked dependency set and the model, **When** the audit check runs, **Then** every direct and transitive
   dependency and the model have a recorded name, version, licence, and provenance, and the check fails for a missing or
   disallowed one.
2. **Given** the built image, **When** its labels and manifest are inspected, **Then** the labels equal the manifest and
   the model digest equals the digest of the model file inside the image.
3. **Given** the image, **When** it runs under a non-root user on a read-only root file system without network access,
   **Then** it completes a job.

---

### User Story 6 - The capability is evaluated through the evaluation harness (Priority: P3)

Where the Analyst Evaluation Harness has been implemented, the capability is evaluated on a rights-free synthetic corpus
of rendered figures of known roles, and the report records per-role detection metrics, the comparator result, a repeat-run
determinism check, and the image and model lineage. The report states that no quality gate is met.

**Why this priority**: It starts the metric evidence the catalog requires, but the harness is a separate feature.

**Independent Test**: Run the harness on the capability and corpus; the report is content-addressed, names the image and
model digests, and its status is not promotable.

**Acceptance Scenarios**:

1. **Given** the harness and a synthetic corpus with box labels, **When** the capability is evaluated, **Then** the report
   contains per-role precision and recall at a stated overlap bound for each source profile.
2. **Given** the report, **When** it is read, **Then** it states that the synthetic corpus supports no accuracy claim on
   real footage and that the capability is not promotable.
3. **Given** the harness has not been implemented when this feature is implemented, **When** the feature's tests run,
   **Then** the evaluation requirement is recorded as deferred in the feature's documentation and no test depends on the
   harness.

---

### Edge Cases

- Media shorter than the sampling interval, a single frame, or no decodable frame in the nominal window: a segment with at
  least one analyzable frame is analyzed; one without completes successfully with no facts.
- Frames outside the segment's nominal window (lead-in frames kept by stream-copy extraction): never analyzed for facts; a
  fact's match time lies inside the nominal half-open window.
- Variable frame rate or irregular timestamps: sampling and fact positions derive from the frames' own timestamps mapped to
  match time, never from frame counting at a nominal rate.
- Corrupt or undecodable media, media missing from the job, or a job without staged media: the attempt ends as failed with
  a stable named reason and never writes a partial result.
- A model file missing, unreadable, or differing from the pinned digest at start: the attempt fails with a named reason and
  writes no result.
- Boxes partly outside the frame: clipped to the frame; boxes that become empty after clipping are dropped.
- A frame with very many detections: the capability caps detections per frame at a declared maximum and keeps the
  highest-confidence ones, deterministically (ties broken by position).
- Very small or distant people, motion blur, occlusion, and crowds (staff, benches, spectators): the full-frame pass will
  miss or merge some of them; this limitation is documented, not hidden, and is the reason tiled profiles exist in the
  architecture.
- Spectators or other people the model cannot distinguish from players: the capability never emits a spectator role and
  does not decide that a person is in play; this limitation is documented.
- Facts of the same frame sharing a match time: allowed; ordering within a frame is deterministic by confidence, then
  position.
- Very long or high-resolution segments: processing stays within the attempt's lease and a declared memory bound; a run
  beyond the execution timeout ends as a timeout, not as a silent partial result.
- Reprocessing after retry or rerun: results are identical, so accepted-fact indexing sees the same facts.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The feature MUST add one independent, locked Python project at
  `src/analysts/capabilities/person-role-detection` for the capability `person-role-detection` at version 1.0.0, built on
  the implemented Analyst SDK (job, attempt context, staged segment media, result writing, entry point), and MUST NOT
  change the SDK's or the platform's contracts.
- **FR-002**: The capability MUST declare, in a versioned `analyst-manifest.json` (manifest version 1.2.0) beside its
  Dockerfile that is valid against the released manifest contract: segment scope, `required` video policy, media context as
  an optional dependency that this version does not consume, the pinned model digest, and its capability-scoped fact kind
  for person-role detections together with the fact payload schema of that kind in `output.factPayloadSchemas`.
- **FR-003**: The capability MUST read only the staged segment media, the model file inside the image, and the job
  document. It MUST NOT use network access, credentials, or upstream results, and MUST NOT download anything at run time.
- **FR-004**: The capability MUST decode the segment's primary video stream, select frames by a declared, deterministic
  sampling rule on the frames' own timestamps, and analyze only frames inside the segment's nominal window.
- **FR-005**: The capability MUST own and perform all preprocessing and postprocessing: a single full-frame pass per
  analyzed frame (decode once, in-memory resize and padding to the model input, normalization), mapping of every box back to
  source-frame coordinates, a declared confidence floor, a declared duplicate merge applied once per frame, and a declared
  maximum number of detections per frame. These settings MUST be declared constants recorded in the capability
  documentation and in every fact's provenance, and MUST NOT be job parameters or overridable by the profile or Manager.
  No tile, tiled, or hybrid inference MUST exist in this version, and nothing but facts MUST be persisted.
- **FR-006**: The capability MUST run the model with an ONNX Runtime CPU inference graph from the pinned CPU wheels the
  environment provides, and MUST NOT require GPU, CUDA, or TensorRT.
- **FR-007**: The capability MUST map the model's output classes to the roles `player`, `goalkeeper`, `referee`, `staff`,
  and the explicit role-unknown value `person` by a declared, versioned mapping. The declared payload schema's role set is
  these five values. The capability MUST emit a specific role (`player`, `goalkeeper`, `referee`, or `staff`) only when the
  chosen model can distinguish it, MUST emit every other detection of a person-like object with `person`, and MUST state in
  its documentation and manifest description which specific roles it can emit and which it cannot.
- **FR-008**: The capability MUST write one fact per retained detection and no other fact. A fact's match-time range MUST
  start at the match time of the analyzed frame, lie inside the segment's nominal half-open window, be non-empty, and end
  at the match time of the next analyzed (sampled) frame (or the segment window end for the last analyzed frame).
- **FR-009**: A fact's payload MUST contain at least: the role, the box in source-frame pixel coordinates (left, top, width,
  height), the source frame width and height, the confidence between zero and one, the zero-based index of the analyzed
  frame in the staged segment file (lead-in frames included), the model identity and digest, the class mapping version, the
  sampling rule, and the preprocessing and postprocessing settings that produced it. The declared payload schema MUST
  reject missing required fields, undeclared roles, a confidence outside zero to one, a non-positive box size, a coordinate
  outside the declared bounds, and undeclared additional properties (containment of the box in the exact frame cannot be
  expressed in the schema; the capability guarantees it and its tests assert it, research.md R-06), and MUST be enforced by the SDK before writing and by result acceptance.
- **FR-010**: The capability MUST be deterministic: the same staged media on the same image and CPU architecture class MUST
  produce byte-identical facts in match-time order regardless of CPU count or thread scheduling (single-threaded or
  fixed-thread inference as the plan decides). Across hosts, the evidence MUST state the numeric tolerance for boxes and
  confidences, and roles, counts, and frame references MUST match exactly.
- **FR-011**: The capability MUST end the attempt as failed with a stable, named reason, and write no result, when the media
  is missing, undecodable, has no video stream, or its timestamps cannot be mapped to the segment's window, or when the
  model file is missing or its digest differs from the pinned digest. A segment without an analyzable frame MUST complete
  successfully with no facts.
- **FR-012**: The capability's image MUST be a digest-pinned `linux/amd64` OCI image built from its own Dockerfile (base
  image by digest, locked dependencies, hash-verified toolchain and model, no tag references, manifest valid and copied
  last, literal `io.socalytics.*` labels verified against the manifest at build time). It MUST run under any non-root user
  on a read-only root file system without network access, and its model digest label MUST equal the digest of the model file
  it contains.
- **FR-013**: The model weights MUST be a permissively licensed pretrained model that passes a recorded licence and
  provenance audit (source location, version, licence, training-data terms, retrieval date, digest, and the conversion to
  the inference format if any), MUST be pinned by digest in the project, MUST be verified against that digest when the
  image is built and when the capability starts, and MUST be labelled development evidence everywhere. The model MUST NOT
  be a SocAlytics-fine-tuned model, and no weights MUST be committed to the repository if the plan finds that exceeds the
  repository's size rules for binaries; in that case the build MUST fetch them from the audited source at a pinned
  digest.
- **FR-014**: The project MUST record audit evidence of every direct and transitive dependency (name, version, licence)
  together with a CycloneDX software bill of materials of the capability layers (Python environment, FFmpeg build, model,
  and base image reference by digest, with operating-system packages identified only by the base image digest), MUST verify
  the evidence against the locked dependency set in its tests, and MUST fail verification for a dependency without
  evidence, a model without an audit record, or a licence outside the allowed set of the `media-context-classification`
  audit. The evidence is development evidence and MUST NOT be presented as the catalog's completed audit.
- **FR-015**: The project MUST include unit tests that generate small synthetic frames and clips at test time (no committed
  binary media beyond the model), covering at least: detection and role mapping on rendered figures, box remapping across
  source resolutions and aspect ratios, confidence floor, duplicate merge, per-frame maximum, sampling and timestamp
  mapping with a non-zero segment offset, empty and single-frame media, determinism across runs and thread counts, payload
  schema conformance of every fact, and each named failure reason.
- **FR-016**: The Development and Test stamp MUST have a documented registration procedure through the existing Registry
  operations. The feature MUST NOT add a seed, and the default configured seed and every production-oriented configuration
  MUST stay unchanged.
- **FR-017**: Docker-backed end-to-end evidence (`PersonRoleDetectionEndToEndTests` and `PersonRoleDetectionImageTests` in
  the `SocAlytics.Platform.EndToEnd.Tests` project) MUST, on a Linux engine, build and register the image by digest,
  approve a profile, define and default a workflow, finalize a short synthetic recording with rendered figures of known
  roles under a short test segmentation policy, execute the run through the Segment Service, Manager execution, result
  acceptance with payload validation, and fact indexing, and read the facts through the member fact read operations. It
  MUST compare facts with the known truth of the recording by the recall over all rendered figure appearances at sampled
  frames, matched by role person, time interval, and IoU of at least 0.5, against the fixed minimum bound stated in the
  plan (0.6), MUST report facts without a matching figure without failing on them, MUST require a specific role only for
  roles the class mapping can emit (none in version 1.0.0), MUST verify the rest of the pipeline (schema,
  coordinates, windows, acceptance), and MUST
  show that a payload variant violating the schema is refused at acceptance. The image tests MUST cover the image's labels,
  digest equality, non-root read-only no-network execution, and absence of any file beyond the declared model.
- **FR-018**: Where the Analyst Evaluation Harness has been implemented, the capability MUST be evaluated through it on a
  synthetic, rights-free corpus with a new box label kind and matching rule, producing a content-addressed report with
  per-role metrics and the image and model lineage that states no gate is met. Where the harness is not yet implemented,
  the documentation MUST record the evaluation as deferred.
- **FR-019**: The facts MUST appear in the existing connected analysis review and evidence playback without Web UI,
  platform, endpoint, contract, or Registry changes. A fact of this kind MUST be readable by team members through the
  existing fact listing and fact stream operations and playable through the existing evidence playback.
- **FR-020**: The feature MUST be labelled development evidence everywhere it is described. It MUST NOT be promoted to a
  production workflow, MUST NOT be published to a registry, MUST NOT be presented as a catalog capability, and MUST state
  that the catalog's quality gates (per-role AP, small-object AP, tile-boundary recall, duplicate rate, throughput) on fixed
  and broadcast subsets are not met and that threshold calibration is not done. No accuracy claim MAY be made beyond the
  synthetic fixtures' known truth.
- **FR-021**: The existing `reference-media-probe` and `media-context-classification` capabilities and their evidence MUST
  stay unchanged and keep passing.
- **FR-022**: Documentation MUST be corrected where it becomes false: the architecture catalog entry for
  `person-role-detection` and the Wave 1 statement (only the first detection slice of person-role detection with a
  pretrained development model exists, the fine-tuned RT-DETRv2-S, the YOLOX challenger, tiled and hybrid inference, CUDA
  and TensorRT profiles, per-role AP gates, and calibration are not implemented, and no Wave 1 gate is met), the
  low-level capabilities implemented-status paragraph, the container-owned preprocessing section where it refers to
  implementation status, the packaging statements about capability images, the repository README and AGENTS guidance, and
  the Analyst source-area README.

### Key Entities

- **Person-role detection**: One detected person-like object in one analyzed frame: role, box in source-frame
  coordinates, confidence, and the frame it came from.
- **Detection fact**: A fact of the capability `person-role-detection` with its capability-scoped fact kind, one per
  retained detection, belonging to one segment's accepted result and validated against the declared payload schema.
- **Class mapping**: The declared, versioned mapping from the model's output classes to the five role values (`player`,
  `goalkeeper`, `referee`, `staff`, and the role-unknown `person`), including which specific roles the model cannot
  distinguish.
- **Detection settings**: The declared sampling rule, full-frame preprocessing, confidence floor, duplicate-merge rule,
  and per-frame maximum; part of the capability version, so a change makes a new version.
- **Model record**: The pinned pretrained weights with source, version, licence, training-data terms, digest, and the
  statement that they are development evidence.
- **Capability image**: The digest-pinned image bundling the capability, its manifest, locked dependencies, and the model,
  with audit evidence and a CycloneDX bill of materials.
- **Evaluation report**: Where the harness exists, the content-addressed record of per-role metrics on the synthetic
  corpus with lineage and a not-promotable status.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the end-to-end evidence, the recall over all rendered figure appearances at sampled frames, matched by
  role person, time interval, and IoU of at least 0.5, meets the fixed minimum bound stated in the plan (0.6); facts
  without a matching figure are reported but do not fail the test; a specific role is required only for roles the class
  mapping can emit (none in version 1.0.0); the recording spans at least two segments, and no fact lies outside its
  segment's window.
- **SC-002**: 100% of facts written in the unit and end-to-end evidence validate against the declared payload schema, and
  each of the four schema-violating result variants is refused at acceptance with its named reason and leaves no visible
  fact.
- **SC-003**: Running the capability at least three times, including with different CPU limits, on the same media yields
  byte-identical results on the same image and CPU architecture class.
- **SC-004**: A Club Admin can go from a built image to a run that produces member-readable, playable detection facts by
  following one documented procedure using only existing Registry operations, with no manual edits of stored data.
- **SC-005**: A segment of the evidence's size (a few seconds of video) is analyzed in at most 120 seconds of wall time on a
  CPU-only host, and the one-minute 1080p bound of the plan's declared sampling rule is met within the manifest's declared
  minimum execution timeout and a declared memory bound, checked in a Docker-backed test.
- **SC-006**: 100% of direct and transitive dependencies and the model have audit records with an allowed licence, a
  CycloneDX software bill of materials exists, and the model file in the image has the pinned digest.
- **SC-007**: All existing reference-analyst, media-context, SDK, Manager, platform, and Web UI tests keep passing
  unchanged, and the feature adds no platform contract, endpoint, or Web UI change.
- **SC-008**: No description of this capability, in code, documentation, or run output, claims production readiness,
  catalog status, a met quality gate, or accuracy on real footage.

## Assumptions

### Dependencies

- Depends on the environment spec [CV and Inference Verification Environment](../20261023-085421-cv-inference-environment/spec.md),
  which provides the pinned CPU OpenCV, NumPy, and ONNX Runtime wheels and the capability verification sequence. It MUST be
  implemented first; this feature never changes the environment actions itself.
- Depends on the implemented
  [Shot-Boundary Media Context Analyst](../archive/20261023-022148-shot-boundary-analyst/spec.md) (the packaging, audit, SBOM,
  registration, and end-to-end pattern), the
  [Capability Fact Payload Schemas](../archive/20261023-010826-fact-payload-schemas/spec.md),
  the [Python Analyst SDK](../archive/20261017-220709-python-analyst-sdk/spec.md), and
  [Materialized Video Segments](../archive/20261017-222433-materialized-segments/spec.md).
- The [Media Quality and Usable Intervals](../20261025-193130-media-quality-usable-intervals/spec.md) feature is an optional
  upstream: this capability declares media context as an optional dependency but does not consume it, so it does not wait
  for it to be implemented.
- The [Analyst Evaluation Harness](../archive/20261023-082736-analyst-evaluation-harness/spec.md) is required only for the
  evaluation report (FR-018); the rest of the feature does not depend on it.
- The existing evidence playback, fact reads, and analysis review are implemented and need no change.

### Decisions Made By Default

- The capability id is the catalog's `person-role-detection` and its first version is 1.0.0. Its fact kind is
  `person-role-detection`; the plan may refine the name within the capability scope.
- The model is a pretrained object detector exported to the ONNX format, chosen in the plan from permissively licensed
  candidates after the recorded audit. Because such a model typically distinguishes only a generic person class, the
  expected outcome is that generic detections are emitted with the role-unknown `person` value and a specific role only
  where the model can distinguish it (possibly none); the plan records the final mapping, and the spec's role
  requirements for specific roles apply to the roles the mapping can emit.
- The sampling rule, confidence floor, duplicate-merge overlap threshold, per-frame maximum, and thread policy are
  provisional development values chosen in the plan and recorded with the capability; they are not calibrated.
- Fact positions are attributed to the segment whose nominal window contains the analyzed frame; handling of overlapping or
  handover frames is deferred as for the shot-boundary baseline.
- The Development and Test registration procedure is documentation plus the end-to-end test's use of the Registry
  operations; no seed is added.
- The synthetic evidence uses rendered figures; it proves the pipeline, schema, and coordinate handling, not detection
  quality on real footage.

### Out of Scope

- Ball detection, tracking, team classification, jersey recognition, and identity.
- The SocAlytics-fine-tuned RT-DETRv2-S baseline, the YOLOX challenger, tiled and hybrid inference, CUDA and TensorRT
  profiles, per-role AP and small-object AP gates, calibration of thresholds, and any promotion, publication, or catalog
  status; these stay unresolved.
- Consuming media-context results, spectator or in-play classification, and persistence of crops or tiles.
- Platform, Registry, SDK contract, Web UI, or environment-action changes beyond what the existing generic fact reads and
  evidence playback already show.
- Production model, storage, throughput, and cost values.
