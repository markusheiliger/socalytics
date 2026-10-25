# Feature Specification: Ball Detection Analyst (Development Baseline)

**Feature Branch**: `20261025-201553-ball-detection-analyst`

**Created**: 2026-10-25

**Status**: Draft

**Input**: User description: "Coaches get the first model-based evidence of where the ball is in a recorded match: for each
segment of a run, the segment-scoped Analyst capability `ball-detection` writes ball boxes or points with confidence and
visibility evidence as indexed facts, readable through the existing member fact reads, the Web UI analysis review, and
evidence playback. A new Python capability project under src/analysts/capabilities/ on the Analyst SDK decodes the staged
segment media (`videoPolicy` `required`, media context optional but not consumed in this version), runs a CPU ONNX Runtime
inference graph at a fixed, declared frame sampling rate with full-frame preprocessing owned by the container (tiled and
hybrid profiles stay out), declares the payload schema of its ball-observation fact kind in the Analyst manifest 1.2.0
(small-object geometry, confidence, visibility state, source-frame coordinates, frame identity), and ships a recorded
dependency and model-weights licence and provenance audit with digest-pinned weights, a digest-pinned linux/amd64 OCI
image, a development registration procedure, Docker-backed end-to-end evidence (a synthetic recording with a rendered ball
at known positions: recall within a bound fixed in the plan, no fact outside its segment window, no fact without frame
identity), and an evaluation through the Analyst evaluation harness where it exists. It is development evidence only: not
the catalog's separately fine-tuned baseline, no quality gate is met, it is never published, and tracking-free
single-frame observations are all it writes. Out of scope: ball trajectory, interpolation, calibration-based pitch
coordinates, SAHI-style tiling, the YOLOX challenger, GPU/TensorRT execution, and any production threshold."

## User Scenarios & Testing *(mandatory)*

The users are Coaches and other team members, who read the first model-derived evidence of where the ball is in a recorded
match; Club Admins, who register the capability's digest-pinned image and bind it into the default workflow; Analyst
authors of the later ball trajectory and ball-control capabilities, who need per-frame source-coordinate ball observations
to start from; and platform maintainers, who need evidence that a small-object model-based capability passes every
boundary from image registration to member-readable facts and playable evidence. The capability is development evidence of
the first ball slice. It is not the catalog's separately fine-tuned baseline and meets no quality gate.

### User Story 1 - A recorded match yields ball observation facts (Priority: P1)

A Coach finalizes a match recording. The run's default workflow contains the `ball-detection` capability as a
segment-scoped node whose profile requires video. For every segment the platform materializes the segment media and a
Manager executes the capability's container against it. The capability decodes the segment, analyzes frames at a fixed
sampling rate, and writes one fact for each retained ball observation, carrying its geometry in source-frame coordinates,
its confidence, and its visibility state. Members read these facts in the existing analysis review, in match-time order, as
facts of the capability and its fact kind.

**Why this priority**: It is the feature's purpose: the first model-based evidence of the ball's position.

**Independent Test**: Finalize a short synthetic recording whose frames contain a rendered ball at known positions, execute
the run end to end, and read the run's facts for the capability: the recall over all rendered ball appearances at sampled
frames, matched by time interval and a declared geometric overlap or distance rule, meets the minimum bound fixed in the
plan; observations without a matching rendered ball are reported but do not fail the test; no fact lies outside its
segment's window; and every fact names the analyzed frame it came from.

**Acceptance Scenarios**:

1. **Given** a registered, approved profile and a default workflow with the capability as a segment node, **When** a Coach
   finalizes a recording that spans two segments, **Then** each segment's attempt is accepted and the facts of each lie
   inside the nominal window of the segment that produced them.
2. **Given** a segment whose analyzed frames show no detectable ball, **When** its attempt completes, **Then** the result is
   accepted with no ball facts and the review shows the segment as analyzed with an empty fact stream, not as failed or
   missing.
3. **Given** the same staged media, **When** the capability runs twice on the same image, **Then** both results contain
   identical facts in the same order.
4. **Given** a ball fact in the analysis review, **When** a Coach opens evidence playback for it, **Then** the existing
   dialog plays the segment media around the fact's match time without any Web UI change.

---

### User Story 2 - Every observation is validated, attributable, and honest about a single frame (Priority: P1)

Each fact states its small-object geometry, confidence, and visibility state in a shape the platform validates at
acceptance. A fact never claims more than one frame can show: the confidence is the model's own score after the declared
postprocessing, the visibility state is derived by a declared rule from that single frame's evidence only, and nothing is
interpolated, smoothed, or linked across frames. The provenance names the model digest, the preprocessing and
postprocessing settings, and the analyzed frame. A malformed result is rejected as a whole.

**Why this priority**: Later trajectory and ball-control capabilities consume these observations; an unvalidated or
overclaiming payload would poison them.

**Independent Test**: Run the capability on generated frames and validate every written fact against the declared payload
schema; submit result variants with a missing geometry, a coordinate outside the declared bounds, an undeclared visibility
state, an out-of-range confidence, and a missing frame identity through acceptance and confirm each is refused with the
named reason and leaves no visible fact.

**Acceptance Scenarios**:

1. **Given** the capability's manifest, **When** a Club Admin registers the image, **Then** the Registry caches the declared
   payload schema of the ball-observation fact kind with the declaration and each run's node snapshot pins it.
2. **Given** a result whose fact payload violates the schema (missing field, visibility state outside the declared set,
   confidence outside zero to one, a non-positive box size, a missing frame identity, an undeclared property, or a
   coordinate outside the declared bounds), **When** the attempt is accepted, **Then** acceptance ends the attempt as failed
   with a named payload reason and no fact of the result is visible.
3. **Given** a written fact, **When** a member reads it, **Then** its payload names the box and centre point in
   source-frame pixel coordinates together with the source frame size, the confidence, the visibility state and the rule
   version that derived it, the model identity and digest, the sampling rule, the preprocessing and postprocessing
   settings, and the zero-based index of the analyzed frame in the staged segment file.
4. **Given** several ball candidates in one frame, **When** the capability finishes the frame, **Then** its declared
   duplicate-merge rule and per-frame maximum have been applied once, no pair of boxes above the declared overlap threshold
   remains, and the retained candidates are ordered deterministically by confidence then position.
5. **Given** an observation whose evidence is weak but above the declared confidence floor, **When** it is written,
   **Then** its visibility state says so (for example not confidently visible) rather than presenting it as a clear
   sighting, and a ball that is occluded or off-frame is never invented from neighbouring frames.

---

### User Story 3 - Preprocessing is owned by the container, full-frame, and honest about small objects (Priority: P2)

The capability decodes each analyzed frame once, resizes and pads it to the model's input in memory as a full-frame pass,
runs the model, and remaps every box to source-frame coordinates. Neither the job, the profile, nor the Manager selects or
overrides these steps, and nothing is persisted besides facts. Tiled and hybrid inference do not exist in this version, and
the documentation states plainly that a full-frame pass loses distant, small balls, which is why the architecture plans
tiled profiles.

**Why this priority**: It implements the catalog's container-owned preprocessing rule for the first ball detector and keeps
later tiled profiles a version change of the container rather than a platform change.

**Independent Test**: Analyze frames of different source resolutions and aspect ratios containing a rendered ball at a known
position and confirm the written geometry is in source-frame coordinates and independent of the model's input size.

**Acceptance Scenarios**:

1. **Given** frames of two different source resolutions showing the same scene, **When** analyzed, **Then** geometry
   agrees in relative position within a stated tolerance and is expressed in each frame's own pixel coordinates.
2. **Given** the job document, **When** it carries any field that looks like a preprocessing, tiling, or threshold setting,
   **Then** the capability ignores it and the result is unchanged.
3. **Given** the capability's documentation, **When** a reader looks for tiled or hybrid profiles, **Then** it states that
   only the full-frame profile exists and that small-ball recall is a known, unmeasured limitation.

---

### User Story 4 - A Club Admin registers and activates the capability through the normal Registry path (Priority: P2)

A Club Admin registers the capability's digest-pinned image, creates and approves a profile that binds the capability
version to that image digest and the pinned model digest with a `required` video policy, defines a workflow with the
capability's segment node, and makes it the default workflow. A documented procedure makes this repeatable without
hand-editing; no seed is added.

**Why this priority**: Without a registration path the capability cannot be used; it uses existing Registry operations and
adds documentation only.

**Independent Test**: Register the built image by digest through the Registry operations; the imported declaration shows the
capability identity, version, scope, video policy, optional media-context dependency, model digest, and the fact kind with
its payload schema.

**Acceptance Scenarios**:

1. **Given** the built image, **When** a Club Admin registers it by digest, **Then** the import succeeds and the cached
   declaration names `ball-detection`, its version, segment scope, `required` video policy, the model digest, and its fact
   kind.
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
and transitive dependency before promotion; this feature starts that evidence for the ball model and claims no more.

**Independent Test**: Build the image from locked inputs, inspect its labels, manifest, model file, and audit evidence; a
model file whose digest differs from the pinned digest, a dependency without an audit entry, or a licence outside the
allowed set fails verification.

**Acceptance Scenarios**:

1. **Given** the locked dependency set and the model, **When** the audit check runs, **Then** every direct and transitive
   dependency and the model have a recorded name, version, licence, and provenance, and the check fails for a missing or
   disallowed one.
2. **Given** the built image, **When** its labels and manifest are inspected, **Then** the labels equal the manifest and the
   model digest equals the digest of the model file inside the image.
3. **Given** the image, **When** it runs under a non-root user on a read-only root file system without network access,
   **Then** it completes a job.
4. **Given** a candidate model whose weights or training-data terms carry a copyleft or unknown licence, **When** the audit
   runs, **Then** it is refused, and the audit records why the chosen model was preferred.

---

### User Story 6 - The capability is evaluated through the evaluation harness (Priority: P3)

Where the Analyst Evaluation Harness has been implemented, the capability is evaluated on a rights-free synthetic corpus of
rendered balls at known positions, and the report records detection metrics per source profile, the comparator result, a
repeat-run determinism check, and the image and model lineage. The report states that no quality gate is met.

**Why this priority**: It starts the metric evidence the catalog requires, but the harness is a separate feature.

**Independent Test**: Run the harness on the capability and corpus; the report is content-addressed, names the image and
model digests, and its status is not promotable.

**Acceptance Scenarios**:

1. **Given** the harness and a synthetic corpus with ball labels, **When** the capability is evaluated, **Then** the report
   contains precision and recall at a stated overlap or distance bound for each source profile.
2. **Given** the report, **When** it is read, **Then** it states that the synthetic corpus supports no accuracy claim on real
   footage and that the capability is not promotable.
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
- Corrupt or undecodable media, media missing from the job, or a job without staged media: the attempt ends as failed with a
  stable named reason and never writes a partial result.
- A model file missing, unreadable, or differing from the pinned digest at start: the attempt fails with a named reason and
  writes no result.
- Geometry partly outside the frame: clipped to the frame; geometry that becomes empty after clipping is dropped.
- A frame with several ball-like objects (a second ball, ball-like marks, boots, heads): the capability keeps at most the
  declared per-frame maximum of highest-confidence candidates deterministically; it does not decide which one is the match
  ball.
- A ball that is occluded, in the air against a busy background, motion-blurred, or far from the camera: the full-frame pass
  will often miss it; a missing observation means "not detected in this frame", never "no ball"; this limitation is
  documented, not hidden.
- Replay, graphics, and non-live content: observations are written without judging whether the ball is in play, because
  media context is not consumed.
- Facts of the same frame sharing a match time: allowed; ordering within a frame is deterministic by confidence, then
  position.
- Very long or high-resolution segments: processing stays within the attempt's lease and a declared memory bound; a run
  beyond the execution timeout ends as a timeout, not as a silent partial result.
- Reprocessing after retry or rerun: results are identical, so accepted-fact indexing sees the same facts.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The feature MUST add one independent, locked Python project under `src/analysts/capabilities/` for the
  capability `ball-detection` at version 1.0.0, built on the implemented Analyst SDK (job, attempt context, staged segment
  media, result writing, entry point), and MUST NOT change the SDK's or the platform's contracts.
- **FR-002**: The capability MUST declare, in a versioned `analyst-manifest.json` (manifest version 1.2.0) beside its
  Dockerfile that is valid against the released manifest contract: segment scope, `required` video policy, media context as
  an optional dependency that this version does not consume, the pinned model digest, and its capability-scoped fact kind
  for ball observations together with the fact payload schema of that kind in `output.factPayloadSchemas`.
- **FR-003**: The capability MUST read only the staged segment media, the model file inside the image, and the job document.
  It MUST NOT use network access, credentials, or upstream results, and MUST NOT download anything at run time.
- **FR-004**: The capability MUST decode the segment's primary video stream, select frames by a declared, deterministic
  sampling rule at a fixed number of analyzed frames per second of match time on the frames' own timestamps, and analyze
  only frames inside the segment's nominal window. The rate is chosen in the plan so that the cost per segment is bounded.
- **FR-005**: The capability MUST own and perform all preprocessing and postprocessing: a single full-frame pass per
  analyzed frame (decode once, in-memory resize and padding to the model input, normalization), mapping of every box back to
  source-frame coordinates, a declared confidence floor, a declared duplicate merge applied once per frame, and a declared
  maximum number of observations per frame. These settings MUST be declared constants recorded in the capability
  documentation and in every fact's provenance, and MUST NOT be job parameters or overridable by the profile or Manager. No
  tile, tiled, or hybrid inference MUST exist in this version, and nothing but facts MUST be persisted.
- **FR-006**: The capability MUST run the model with an ONNX Runtime CPU inference graph from the pinned CPU wheels the
  environment provides, and MUST NOT require GPU, CUDA, or TensorRT.
- **FR-007**: The capability MUST write tracking-free single-frame observations only: one fact per retained ball candidate
  per analyzed frame and no other fact. It MUST NOT link observations across frames, assign track or ball identifiers,
  interpolate, smooth, estimate velocity, or write pitch coordinates, and MUST NOT use any other frame than the analyzed one
  to produce a fact.
- **FR-008**: A fact's match-time range MUST start at the match time of the analyzed frame, lie inside the segment's nominal
  half-open window, be non-empty, and end at the match time of the next analyzed (sampled) frame, or at the segment window
  end for the last analyzed frame.
- **FR-009**: A fact's payload MUST contain at least: the geometry as a box (left, top, width, height) and its centre point in
  source-frame pixel coordinates, the source frame width and height, the confidence between zero and one, the visibility
  state from a small declared set derived by a declared single-frame rule that is versioned with the capability, the
  zero-based index of the analyzed frame in the staged segment file (lead-in frames included), the model identity and
  digest, the sampling rule, and the preprocessing and postprocessing settings that produced it. The declared payload schema
  MUST reject missing required fields, a visibility state outside the declared set, a confidence outside zero to one, a
  non-positive box size, a coordinate outside the declared bounds, a missing frame identity, and undeclared additional
  properties, and MUST be enforced by the SDK before writing and by result acceptance. Containment of the box in the exact
  frame cannot be expressed in the schema; the capability guarantees it and its tests assert it.
- **FR-010**: The visibility state MUST describe only what the analyzed frame supports (for example confidently visible
  versus not confidently visible), MUST NOT claim occlusion, out-of-play, or off-frame positions inferred from other frames,
  and the plan MUST fix the declared set and the rule that derives it, and record them in the documentation.
- **FR-011**: The capability MUST be deterministic: the same staged media on the same image and CPU architecture class MUST
  produce byte-identical facts in match-time order regardless of CPU count or thread scheduling (single-threaded or
  fixed-thread inference as the plan decides). Across hosts, the evidence MUST state the numeric tolerance for geometry and
  confidences, and counts, visibility states, and frame references MUST match exactly.
- **FR-012**: The capability MUST end the attempt as failed with a stable, named reason, and write no result, when the media
  is missing, undecodable, has no video stream, or its timestamps cannot be mapped to the segment's window, or when the model
  file is missing or its digest differs from the pinned digest. A segment without an analyzable frame MUST complete
  successfully with no facts.
- **FR-013**: The capability's image MUST be a digest-pinned `linux/amd64` OCI image built from its own Dockerfile (base image
  by digest, locked dependencies, hash-verified toolchain and model, no tag references, manifest valid and copied last,
  literal `io.socalytics.*` labels verified against the manifest at build time). It MUST run under any non-root user on a
  read-only root file system without network access, and its model digest label MUST equal the digest of the model file it
  contains.
- **FR-014**: The model weights MUST be a permissively licensed pretrained model that passes a recorded licence and
  provenance audit (source location, version, licence, training-data terms, retrieval date, digest, and the conversion to
  the inference format if any), MUST be pinned by digest in the project, MUST be verified against that digest when the image
  is built and when the capability starts, and MUST be labelled development evidence everywhere. The model MUST NOT be a
  SocAlytics-fine-tuned model, and no weights MUST be committed to the repository if the plan finds that exceeds the
  repository's size rules for binaries; in that case the build MUST fetch them from the audited source at a pinned digest.
  The audit MUST consider the licence of the weights, of the training-data terms, and of the framework the weights were
  produced with, and a copyleft or unknown licence on any of them fails the audit.
- **FR-015**: The project MUST record audit evidence of every direct and transitive dependency (name, version, licence)
  together with a CycloneDX software bill of materials of the capability layers (Python environment, FFmpeg build, model, and
  base image reference by digest, with operating-system packages identified only by the base image digest), MUST verify the
  evidence against the locked dependency set in its tests, and MUST fail verification for a dependency without evidence, a
  model without an audit record, or a licence outside the allowed set of the `media-context-classification` audit (permissive
  licences, LGPL for separately packaged shared libraries, the GCC runtime library exception for bundled GCC runtime
  libraries). The evidence is development evidence and MUST NOT be presented as the catalog's completed audit.
- **FR-016**: The project MUST include unit tests that generate small synthetic frames and clips at test time (no committed
  binary media beyond the model), covering at least: detection of a rendered small ball, box and centre remapping across
  source resolutions and aspect ratios, confidence floor, visibility derivation, duplicate merge, per-frame maximum,
  sampling and timestamp mapping with a non-zero segment offset, empty and single-frame media, determinism across runs and
  thread counts, payload schema conformance of every fact, absence of any cross-frame state, and each named failure reason.
- **FR-017**: The Development and Test stamp MUST have a documented registration procedure through the existing Registry
  operations. The feature MUST NOT add a seed, and the default configured seed and every production-oriented configuration
  MUST stay unchanged.
- **FR-018**: Docker-backed end-to-end evidence (`BallDetectionEndToEndTests` and `BallDetectionImageTests` in the
  `SocAlytics.Platform.EndToEnd.Tests` project) MUST, on a Linux engine, build and register the image by digest, approve a
  profile, define and default a workflow, finalize a short synthetic recording with a rendered ball at known positions under
  a short test segmentation policy, execute the run through the Segment Service, Manager execution, result acceptance with
  payload validation, and fact indexing, and read the facts through the member fact read operations. It MUST compare facts
  with the known truth of the recording by the recall over all rendered ball appearances at sampled frames against the
  minimum bound fixed in the plan, deriving the sampled frames by applying the sampling rule `fixed-5fps-v1` to each
  segment's nominal window over the recording's known 25 fps timeline (every 200 ms from each window start) and matching
  a fact to an appearance by match time (`startMs` equal to the frame's match time) and a centre within the truth
  diameter of the truth centre, with `frameIndex` only checked for presence, integer value >= 0, and consistency with the
  segment file, MUST report facts without a matching ball without failing on them, MUST assert that no
  fact lies outside its segment's window and that every fact carries a frame identity, and MUST show that a payload variant
  violating the schema is refused at acceptance. The image tests MUST cover the image's labels, digest equality, non-root
  read-only no-network execution, and absence of any file beyond the declared model.
- **FR-019**: Where the Analyst Evaluation Harness has been implemented, the capability MUST be evaluated through it on a
  synthetic, rights-free corpus with a ball label kind and a matching rule suited to small objects, producing a
  content-addressed report with metrics per source profile and the image and model lineage that states no gate is met. Where
  the harness is not yet implemented, the documentation MUST record the evaluation as deferred.
- **FR-020**: The facts MUST appear in the existing connected analysis review and evidence playback without Web UI,
  platform, endpoint, contract, or Registry changes. A fact of this kind MUST be readable by team members through the
  existing fact listing and fact stream operations, under the existing team-scoped authorization, and playable through the
  existing evidence playback.
- **FR-021**: The feature MUST be labelled development evidence everywhere it is described. It MUST NOT be promoted to a
  production workflow, MUST NOT be published to a registry, MUST NOT be presented as a catalog capability, and MUST state
  that the catalog's quality gates (ball AP and small-object AP, tile-boundary recall, false merges, latency) on fixed and
  broadcast subsets are not met and that threshold calibration is not done. No accuracy claim MAY be made beyond the
  synthetic fixtures' known truth.
- **FR-022**: The existing `reference-media-probe` and `media-context-classification` capabilities and their evidence MUST
  stay unchanged and keep passing, and this feature MUST NOT depend on the `person-role-detection` capability, so the two
  may be implemented in either order.
- **FR-023**: Documentation MUST be corrected where it becomes false: the architecture catalog entry for `ball-detection` and
  the Wave 1 statement (only a first single-frame ball slice with a pretrained development model exists; the separately
  fine-tuned RT-DETRv2-S, the YOLOX-S challenger, SAHI-style tiled and hybrid inference, CUDA and TensorRT profiles, ball AP
  gates, trajectory, and calibration are not implemented, and no Wave 1 gate is met), the low-level capabilities
  implemented-status paragraph, the container-owned preprocessing section where it refers to implementation status, the
  packaging statements about capability images, the repository README and AGENTS guidance, and the Analyst source-area
  README.

### Key Entities

- **Ball observation**: One ball candidate in one analyzed frame: geometry in source-frame coordinates (box and centre),
  confidence, visibility state, and the frame it came from. It is not linked to any other observation.
- **Ball observation fact**: A fact of the capability `ball-detection` with its capability-scoped fact kind, one per
  retained observation, belonging to one segment's accepted result and validated against the declared payload schema.
- **Visibility state**: A value from a small declared set derived by a versioned single-frame rule; it never encodes
  occlusion or position inferred from other frames.
- **Detection settings**: The declared sampling rate, full-frame preprocessing, confidence floor, duplicate-merge rule, and
  per-frame maximum; part of the capability version, so a change makes a new version.
- **Model record**: The pinned pretrained weights with source, version, licence, training-data terms, digest, and the
  statement that they are development evidence.
- **Capability image**: The digest-pinned image bundling the capability, its manifest, locked dependencies, and the model,
  with audit evidence and a CycloneDX bill of materials.
- **Evaluation report**: Where the harness exists, the content-addressed record of metrics on the synthetic corpus with
  lineage and a not-promotable status.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the end-to-end evidence, the recall over all rendered ball appearances at sampled frames (the frames the
  sampling rule `fixed-5fps-v1` selects in each segment's nominal window over the recording's known 25 fps timeline,
  matched to facts by match time and centre distance) meets the minimum
  bound fixed in the plan; observations without a matching rendered ball are reported but do not fail the test; the
  recording spans at least two segments; no fact lies outside its segment's window; and no fact lacks a frame identity.
- **SC-002**: 100% of facts written in the unit and end-to-end evidence validate against the declared payload schema, and
  each schema-violating result variant (missing geometry, out-of-bounds coordinate, undeclared visibility state,
  out-of-range confidence, missing frame identity) is refused at acceptance with its named reason and leaves no visible
  fact.
- **SC-003**: Running the capability at least three times, including with different CPU limits, on the same media yields
  byte-identical results on the same image and CPU architecture class.
- **SC-004**: A Club Admin can go from a built image to a run that produces member-readable, playable ball facts by
  following one documented procedure using only existing Registry operations, with no manual edits of stored data.
- **SC-005**: A segment of the evidence's size (a few seconds of video) is analyzed in at most 120 seconds of wall time on a
  CPU-only host, and the plan's one-minute 1080p bound for the declared sampling rate is met within the manifest's declared
  minimum execution timeout and a declared memory bound, checked in a Docker-backed test.
- **SC-006**: 100% of direct and transitive dependencies and the model have audit records with an allowed licence, a
  CycloneDX software bill of materials exists, and the model file in the image has the pinned digest.
- **SC-007**: All existing reference-analyst, media-context, SDK, Manager, platform, and Web UI tests keep passing
  unchanged, and the feature adds no platform contract, endpoint, or Web UI change.
- **SC-008**: No description of this capability, in code, documentation, or run output, claims production readiness, catalog
  status, a met quality gate, trajectory or tracking behaviour, or accuracy on real footage.

## Assumptions

### Dependencies

- Depends on the environment spec
  [CV and Inference Verification Environment](../20261023-085421-cv-inference-environment/spec.md), which provides the pinned
  CPU OpenCV, NumPy, and ONNX Runtime wheels and the capability verification sequence. It MUST be implemented first; this
  feature never changes the environment actions itself.
- Depends on the implemented
  [Shot-Boundary Media Context Analyst](../archive/20261023-022148-shot-boundary-analyst/spec.md) (the packaging, audit, SBOM,
  registration, and end-to-end pattern), the
  [Capability Fact Payload Schemas](../archive/20261023-010826-fact-payload-schemas/spec.md), the
  [Python Analyst SDK](../archive/20261017-220709-python-analyst-sdk/spec.md), and
  [Materialized Video Segments](../archive/20261017-222433-materialized-segments/spec.md), plus the existing accepted fact
  indexing, analysis review, and evidence playback.
- It is independent of the [Person and Role Detection Analyst](../20261025-195345-person-role-detection/spec.md) and may be
  implemented before or after it.
- The [Analyst Evaluation Harness](../20261023-082736-analyst-evaluation-harness/spec.md) is required only for the
  evaluation report (FR-019); the rest of the feature does not depend on it.

### Decisions Made By Default

- The capability id is the catalog's `ball-detection` and its first version is 1.0.0. Its fact kind is
  `ball-observation`; the plan may refine the name within the capability scope.
- The model is a pretrained object detector exported to the ONNX format, chosen in the plan from permissively licensed
  candidates after the recorded audit, including its training-data and framework licence terms. A model that distinguishes a
  generic ball class is expected; a candidate whose weights are copyleft is refused.
- Geometry is a source-frame box plus its centre point, so the catalog's "boxes or points" are both available; a point-only
  detector's output is stored as a box of its declared nominal size or is out of scope for the chosen model.
- The sampling rate, confidence floor, duplicate-merge overlap threshold, per-frame maximum, visibility set and rule, thread
  policy, and the recall bound are provisional development values fixed in the plan and recorded with the capability; they
  are not calibrated.
- Fact positions are attributed to the segment whose nominal window contains the analyzed frame; handling of overlapping or
  handover frames is deferred as for the shot-boundary baseline.
- The synthetic evidence uses a rendered ball; it proves the pipeline, schema, and coordinate handling, and a recall bound
  the plan sets with respect to what a pretrained real-world model can find in synthetic frames, not detection quality on
  real footage.

### Out of Scope

- Ball trajectory, interpolation, smoothing, velocity, calibration-based pitch coordinates, ball-control candidates, and any
  cross-frame linkage or identity of balls.
- SAHI-style tiled and hybrid inference, the separately fine-tuned RT-DETRv2-S baseline, the YOLOX challenger, CUDA and
  TensorRT profiles, ball AP and small-object AP gates, threshold calibration, and any promotion, publication, or catalog
  status; these stay unresolved.
- Consuming media-context results, in-play or replay classification, and persistence of crops or tiles.
- Platform, Registry, SDK contract, Web UI, or environment-action changes beyond what the existing generic fact reads and
  evidence playback already show.
- Production model, storage, throughput, and cost values.
