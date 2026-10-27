# Feature Specification: Media Quality and Usable Intervals for Media Context Classification

**Feature Branch**: `20261025-193130-media-quality-usable-intervals`

**Created**: 2026-10-25

**Status**: Draft

**Input**: User description: "Extend the existing development capability `media-context-classification` beyond shot
boundaries with deterministic, model-free media quality and usable-interval outputs, so the Wave 1 foundation gains the
intervals that downstream detection capabilities need to skip unusable video. A new capability version (manifest 1.2.0)
keeps the accepted `shot-boundary` facts unchanged in meaning and additionally writes `media-quality` facts for
fixed-rule frame measures (black or near-black frames, frozen or duplicated frames, low-detail or blurred frames, each
with the measured value and the fixed rule that fired) and `usable-interval` facts derived deterministically from those
measures and the shot boundaries (start, end, usable or not, reason codes). Each new fact kind declares a payload
schema in the Analyst manifest and a contract document like the shot-boundary fact contract. Settings are fixed
development values, not calibrated. Out of scope: shot type, live, replay, and graphics classification and the learned
challenger, any quality gate, calibration, corpus, or promotion, model weights, GPU runtimes, and platform, Web UI, and
OpenAPI changes."

## Clarifications

### Session 2026-10-25

- Q: When a stretch of black frames also meets the frozen-frame and low-detail rules (black frames are identical to each
  other and have no detail), should those two rules also fire on the black frames? → A: No, black takes precedence:
  frozen and low-detail never fire on frames the black rule fired on, but frozen and low-detail can still fire together
  on other frames (for example a still, blurred scene).
- Q: When a defect stretch runs across a segment's window edge, is the minimum duration of its rule judged on the whole
  stretch (using lead-in or following frames as context) or only on the part inside the segment's nominal window? → A:
  Only on the in-window part: the clipped stretch must itself reach the minimum duration, so each segment decides from
  its own window and lead-in frames only serve as predecessors.
- Q: Must a media-quality stretch end at a shot boundary, or can one stretch continue across a cut (for example black
  footage that cuts to a different black or dark scene)? → A: A stretch is defined by frame measures only and may span a
  shot boundary; the shot boundary is reported separately and does not split the stretch.
- Q: For the frozen-frame rule, does a frozen stretch start at the first repeated frame (the first duplicate) or at the
  original frame that is then repeated? → A: It starts at the original frame that is then repeated, so the shown-still
  period is the whole range, and the frame count includes the original.

## User Scenarios & Testing *(mandatory)*

The users are Club Admins and Coaches, who review a run in the connected analysis review and need to know which
match-second ranges of each segment are usable for analysis and why others are not; authors of later Wave 1 detection
and tracking capabilities, who will consume usable intervals as an optional upstream dependency to skip unusable video;
Club Admins, who register the new capability version through the normal Registry path; and platform maintainers, who
need evidence that the extended capability passes every boundary from image to member-readable facts without claiming
more than a development baseline.

### User Story 1 - A segment yields usable and unusable intervals with reasons (Priority: P1)

A Coach finalizes a match recording. For every segment the extended capability examines the decoded frames, measures
fixed-rule frame properties, and writes `usable-interval` facts that together partition the segment's nominal window
into consecutive match-time ranges, each marked usable or not usable. A not-usable range names the reason codes that
made it unusable (for example black frames, frozen frames, or low detail). Members read these facts in the existing
analysis review, in match-time order, next to the shot boundaries of the same segment.

**Why this priority**: It is the feature's purpose: the intervals downstream capabilities need to skip unusable video.

**Independent Test**: Execute a run end to end on a synthetic recording that contains known black, frozen, blurred, and
normal stretches at known positions, then read the facts: the usable-interval facts match the known truth exactly.

**Acceptance Scenarios**:

1. **Given** a segment of normal footage with no defects, **When** its attempt completes, **Then** exactly one
   `usable-interval` fact covers the whole nominal window, marked usable with no reason codes.
2. **Given** a segment with a known stretch of black frames in its middle, **When** its attempt completes, **Then** the
   intervals before and after are usable, the black stretch is one not-usable interval with the reason code for black
   frames, and the intervals together cover the nominal window without gaps or overlaps.
3. **Given** a segment with frozen frames and a segment with blurred frames, **When** the attempts complete, **Then**
   each affected stretch is a not-usable interval carrying the reason code of the rule that fired.
4. **Given** a stretch that triggers more than one rule, **When** the attempt completes, **Then** its interval carries
   every reason code that applies, in a fixed order.
5. **Given** the same segment media, **When** the capability runs twice, including on another host, **Then** both
   results contain byte-identical facts.

---

### User Story 2 - Members see the measured evidence behind each quality finding (Priority: P1)

For each defect the capability found, `media-quality` facts carry the measured value and the fixed rule that fired, so
a member can see why a stretch was judged unusable rather than receiving only a verdict. Each fact covers the match-time
range of the frames the rule applies to, one kind of defect per fact.

**Why this priority**: Evidence next to the verdict is what makes the intervals reviewable and the later calibration
possible; without it, the intervals are opaque.

**Independent Test**: Run the capability on generated clips with one known defect each and compare the facts' kinds,
ranges, measured values, and rules with the known truth.

**Acceptance Scenarios**:

1. **Given** a clip with a black stretch, **When** the capability runs, **Then** it writes one `media-quality` fact for
   the stretch naming the black-frame rule, the measured brightness, and the threshold it was compared against.
2. **Given** a clip with a frozen stretch (consecutive duplicated frames), **When** the capability runs, **Then** it
   writes one fact naming the frozen-frame rule, the measured frame difference, and the frame count, with the duration given by the fact's range.
3. **Given** a clip with a blurred or low-detail stretch, **When** the capability runs, **Then** it writes one fact
   naming the low-detail rule and the measured detail value.
4. **Given** a clip with normal footage, noise, and a camera pan, **When** the capability runs, **Then** it writes no
   `media-quality` fact.
5. **Given** a stretch shorter than the declared minimum duration of a rule, **When** the capability runs, **Then** it
   writes no fact for it, as a documented, deterministic behavior.

---

### User Story 3 - Shot boundaries keep their meaning and shape cuts into intervals (Priority: P1)

The new version keeps writing `shot-boundary` facts exactly as before. Usable intervals additionally respect shot
boundaries: an interval never silently spans a boundary where the usability verdict changes, and a boundary that falls
inside a stretch is not lost.

**Why this priority**: Existing consumers and the accepted baseline evidence must not break, and boundaries are an
input of the derived intervals.

**Independent Test**: Run the new version and the previous version on the same synthetic clips and compare the
`shot-boundary` facts; they are identical. Then check interval edges at known cuts.

**Acceptance Scenarios**:

1. **Given** any staged media the previous version analyzed, **When** the new version runs, **Then** its
   `shot-boundary` facts have the same kinds, ranges, rules, and measures as the previous version's, byte-identical
   (the settings provenance names the unchanged detector version `1.0.0`).
2. **Given** a hard cut between a normal stretch and a black stretch, **When** the capability runs, **Then** the usable
   interval edge coincides with the shot boundary's match time.
3. **Given** a normal stretch that contains a cut but no defect, **When** the capability runs, **Then** it is one usable
   interval (a cut alone does not make footage unusable).

---

### User Story 4 - A Club Admin registers the new version through the normal Registry path (Priority: P2)

A Club Admin registers the new digest-pinned image, approves a profile for the new capability version, and defines a
workflow that uses it. The Registry imports the declaration from the image's manifest, including the payload schemas of
both new fact kinds, and refuses an image whose labels and manifest differ. A documented procedure makes this
repeatable, and the previous version stays registrable and usable.

**Why this priority**: Without registration the extended capability cannot run; it is P2 because it uses existing
Registry operations and adds documentation, not behavior.

**Independent Test**: Register the built image by digest; the imported declaration shows the version, scope, video
policy, no dependencies, and the three fact kinds with their payload schemas.

**Acceptance Scenarios**:

1. **Given** the built image, **When** a Club Admin registers it by digest, **Then** the import succeeds and the cached
   declaration names the capability, the new version, and the payload schemas of `shot-boundary`, `media-quality`, and
   `usable-interval`.
2. **Given** a profile and workflow of the previous version, **When** the new version is registered, **Then** runs that
   pinned the previous version are unaffected (this is the existing Registry's immutable-pin behavior, verified by its
   platform tests only).
3. **Given** a result with a payload that violates a declared schema, **When** acceptance validates it, **Then** the
   attempt is rejected with the named payload reason and no fact becomes visible.

---

### User Story 5 - Maintainers can trust the extended image's provenance and limits (Priority: P2)

A maintainer sees, for the new image, the same provenance as the baseline: pinned base image by digest, locked
dependencies, the pinned media toolchain, an updated dependency and license audit, and no model weights. Every
description states that settings are fixed development values and that no quality gate is met.

**Why this priority**: The catalog requires audit evidence before any promotion; this feature must extend it without
overstating it.

**Independent Test**: Build the image from locked inputs and run the project's tests including the dependency audit;
inspect the labels against the manifest.

**Acceptance Scenarios**:

1. **Given** the locked dependency set, **When** the audit check runs, **Then** every dependency has an allowed-license
   record, and a missing or disallowed one fails verification.
2. **Given** the built image, **When** its labels and manifest are inspected, **Then** they match, the model digest is
   the platform's no-model digest, and no model weights are present.
3. **Given** a non-root user, a read-only root file system, and no network, **When** the image runs a job, **Then** it
   completes.

---

### Edge Cases

- A segment with fewer than two frames: no frame pair measures exist; the result is accepted. A single-frame segment
  still yields one usable-interval fact for its window based on the measures that need no predecessor; an empty segment
  yields no facts. Both are documented behaviors.
- A black or frozen stretch that continues past the segment window edges: the fact is clipped to the nominal window; a
  stretch is never reported by two segments. The rule's minimum duration is judged only on the clipped, in-window part:
  a clipped part shorter than the minimum yields no fact in that segment, even if the whole stretch would have reached
  it, and lead-in or following frames serve only as predecessors.
- A stretch of black frames also looks frozen and without detail: black takes precedence, so the frozen-frame and
  low-detail rules never fire on frames the black rule fired on; they can still fire together on other frames (for
  example a still, blurred scene), giving two facts and both reason codes.
- A defect stretch that contains a shot boundary (for example black footage cutting to a different black scene): it
  remains one stretch, because stretches are defined by frame measures only; the `shot-boundary` fact is reported
  separately and does not split the stretch.
- Lead-in frames outside the nominal window (kept by stream-copy extraction): decoded only as predecessors, and never
  produce quality or interval facts, as for shot boundaries.
- Variable frame rate or irregular timestamps: ranges derive from the frames' own timestamps mapped to match time, not
  from counting at a nominal rate.
- Uniformly dark but legitimate footage (night scene) versus black: the fixed rule decides by its declared threshold;
  this limitation is documented, not hidden.
- A static fixed camera on an empty pitch looks frozen: the frozen rule compares frame content only, so a still scene is
  indistinguishable from duplicated frames; with sensor noise it is not frozen. The limitation is documented.
- A fade to black or a dissolve: handled only by the black and low-detail rules; no gradual-transition fact exists.
- Adjacent stretches with identical verdicts and reasons are merged into one interval; intervals never overlap and never
  leave a gap in the nominal window.
- Corrupt media, missing media, no video stream, or unmappable timestamps: the attempt ends as failed with the same
  named reasons as the baseline and writes no partial result.
- Very long or high-resolution segments: processing stays within the attempt's lease and the memory bound of the
  baseline; a run beyond the execution timeout ends as a timeout, never a silent partial result.
- Reprocessing after retry or rerun yields identical facts, so accepted-fact indexing sees the same facts.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The feature MUST extend the existing project `src/analysts/capabilities/media-context-classification`
  with a new capability version (a new version of the same capability, with analyst manifest version 1.2.0), built on
  the implemented Analyst SDK, and MUST NOT change the SDK's or the platform's contracts.
- **FR-002**: The new version MUST keep writing `shot-boundary` facts whose kinds, ranges, detection rules, measures,
  and counts are unchanged in meaning from the baseline version for identical media, and MUST keep its payload schema
  compatible with the accepted baseline facts; the facts are byte-identical to the baseline's, their settings provenance naming the unchanged detector version `1.0.0`.
- **FR-003**: The capability MUST additionally write `media-quality` facts, one per detected defect stretch, for three
  fixed rules: black or near-black frames, frozen or duplicated frames, and low-detail or blurred frames. Each fact MUST
  carry the rule that fired, the measured value, the threshold it was compared against, the minimum duration applied,
  and the number of frames in the stretch. The black-frame rule takes precedence: the frozen-frame and low-detail rules
  MUST NOT fire on frames the black rule fired on, while they MAY fire together on other frames. A frozen stretch
  starts at the original frame that is then repeated (not at the first duplicate), so it covers the whole shown-still
  period and its frame count includes the original frame. A stretch is defined by frame measures only and MAY span a
  shot boundary; shot boundaries never split it.
- **FR-004**: Frame measures MUST depend only on pixel content and the staged frame timestamps, MUST use only the
  already-locked image-processing and numeric libraries and the pinned media toolchain, and MUST NOT use network
  access, credentials, upstream results, model weights, or randomness.
- **FR-005**: Every rule's thresholds, minimum stretch durations, analysis frame size, and rule order MUST be fixed,
  declared constants recorded in the capability documentation and in every new fact's provenance. They are development
  values; the feature MUST NOT calibrate them or claim accuracy beyond the synthetic fixtures' known truth.
- **FR-006**: A `media-quality` fact's match-time range MUST start at the first frame of the stretch and end at the
  match time of the first frame after it (or the nominal window end), lie inside the segment's nominal half-open
  window, and be non-empty. Stretches MUST be reported only for in-window frames, and no stretch MUST be reported by
  two segments. A rule's minimum duration MUST be judged only on the in-window part of a stretch (lead-in and
  following frames serve only as predecessors), so each segment decides from its own window.
- **FR-007**: The capability MUST write `usable-interval` facts that, together, partition the segment's nominal window
  into consecutive, non-overlapping ranges without gaps. Each fact MUST state whether the range is usable and, when it
  is not, one or more reason codes from a closed, declared set derived from the quality rules, in a fixed order.
- **FR-008**: Usable intervals MUST be derived deterministically from the `media-quality` stretches and the shot
  boundaries only. A range is not usable if and only if at least one quality stretch covers it, except that the span before the first in-window frame takes the first frame's verdict; a shot boundary alone
  MUST NOT make a range unusable. A boundary at which the verdict changes MUST be an interval edge; a boundary inside
  a stretch that does not change the verdict or reasons is not an edge. Adjacent ranges
  with identical verdict and reasons MUST be merged.
- **FR-009**: Each new fact kind MUST declare a payload schema in the Analyst manifest's fact payload schema
  declaration, within the restricted schema subset and limits the Registry, result acceptance, and the Python SDK
  already enforce, and the baseline `shot-boundary` kind MUST declare its schema there too. The schemas MUST forbid
  undeclared properties.
- **FR-010**: Each new fact kind MUST have a contract document like the shot-boundary fact contract, covering the
  envelope, payload, behavior that is part of the contract, failure reasons, and manifest declaration summary. The
  authoritative copies MUST be the capability's README and its payload schema files, validated by the project's tests.
- **FR-011**: The output MUST be deterministic: the same staged media MUST produce byte-identical facts of all three
  kinds in match-time order, regardless of host, CPU count, or thread scheduling.
- **FR-012**: The capability MUST end the attempt as failed with the baseline's stable named reasons, and write no
  result, when media is missing, undecodable, without a video stream, or its timestamps are unmappable. Failure in any
  new rule MUST NOT yield a partial result.
- **FR-013**: The capability MUST NOT emit shot type, live, replay, or graphics facts, MUST NOT include a learned
  classifier or model weights, and MUST NOT require an accelerator.
- **FR-014**: The new version's image MUST be a digest-pinned `linux/amd64` OCI image built from the project's own
  Dockerfile, following the packaging convention (manifest valid and copied last, labels verified against the manifest
  at build time, pinned inputs only), runnable as non-root on a read-only root file system without network access, and
  MUST contain no model weights. Its resources MUST be within the baseline's declared bounds, or the manifest MUST
  declare larger bounds with the reason.
- **FR-015**: The dependency and license audit evidence MUST be updated so that every dependency of the new image has a
  record and an allowed license, with the same allowed-license rule as the baseline, and verification MUST fail for a
  missing record or a disallowed license. The feature MUST add no new runtime dependency unless unavoidable, and any
  added one MUST pass the audit.
- **FR-016**: The project's tests MUST generate synthetic clips at test time with the pinned media toolchain and cover
  at least: black, near-black, frozen, duplicated, blurred, low-detail, and normal stretches, noise and pan without
  defects, stretches shorter than their minimum duration, stretches at window edges, interval partition properties
  (no gap, no overlap, merge, reason order), derivation at shot boundaries, baseline shot-boundary equivalence,
  determinism, non-zero segment offsets, each failure reason, and each payload against its declared schema.
- **FR-017**: Docker-backed end-to-end evidence on a Linux engine MUST build and register the image by digest, approve a
  profile, define and default a workflow, finalize a short synthetic recording with known defects and cuts under a short
  segmentation policy, execute the run through segment materialization, Manager execution, result acceptance, and fact
  indexing, and read the facts through the member fact read operations. It MUST compare the kinds, positions, rules,
  reasons, and counts of all three fact kinds with the recording's known truth exactly, in the style of the existing
  shot-boundary end-to-end and image evidence.
- **FR-018**: The previous capability version MUST remain registrable and usable, and the reference capability and its
  evidence MUST stay unchanged and keep passing.
- **FR-019**: The feature MUST NOT add endpoints, screens, contracts, Registry behavior, or OpenAPI changes; the new
  facts MUST appear in the existing connected analysis review and fact reads as declared fact kinds of the capability.
- **FR-020**: A documented registration procedure through the existing Registry operations MUST cover the new version;
  no seed is added and defaults and production-oriented configuration stay unchanged.
- **FR-021**: The capability MUST be labelled development evidence everywhere it is described: not a catalog capability
  that meets gates, never published, no quality gate, calibration, corpus, or promotion claimed.
- **FR-022**: Documentation MUST be corrected where it becomes false: the `media-context-classification` row and the
  statement of implemented outputs in the Low-Level Capabilities section of the Analyst Capability Catalog, the Wave 1
  statement of Implementation Waves, the development-image description of the Analysts, Models, and Hardware architecture document (its result manifest
  and lineage statements are not made false: the capability declares no upstream dependency and keeps the manifest and
  lineage shape; its fact payload schemas and Docker verification statements are corrected), the Analyst Runtime and
  Recovery architecture document (Docker verification statements), the Platform Implementation architecture
  document (capability statement), the Analysts source-area README (`src/analysts/README.md`), the project README, the
  repository README, and AGENTS guidance. Shot types, live, replay, graphics, the learned challenger, and every gate MUST remain stated as not
  implemented.

### Key Entities

- **Media-quality fact**: A fact of the capability with the kind `media-quality`, one per defect stretch of one
  segment: the stretch's match-time range, the rule that fired, the measured value, the threshold, the minimum
  duration, and the frame count.
- **Usable-interval fact**: A fact of the capability with the kind `usable-interval`: one range of the segment's
  nominal window, a usable flag, and an ordered list of reason codes when not usable.
- **Reason code**: One member of the closed set that names the quality rule behind a not-usable interval.
- **Quality settings**: The fixed thresholds, minimum durations, analysis frame size, and rule order; part of the
  capability version, so a change makes a new version.
- **Shot-boundary fact**: Unchanged from the baseline; an input of interval derivation and still written.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the end-to-end evidence, 100% of the known defect stretches and usable-interval edges of a synthetic
  recording spanning at least two segments appear as facts at their exact positions with the expected rules and
  reasons, and no unexpected facts exist.
- **SC-002**: For every segment of the evidence, the usable-interval facts cover the nominal window exactly, with zero
  gaps and zero overlaps.
- **SC-003**: On the generated defect-free clips (static noise, steady pan, cuts between normal scenes), the capability
  writes zero `media-quality` facts and one usable interval per segment.
- **SC-004**: For the media of the baseline's evidence, the `shot-boundary` facts of the new version equal the
  baseline's facts in 100% of cases.
- **SC-005**: Running the capability at least three times, including with different CPU limits, on the same media
  yields byte-identical results every time.
- **SC-006**: A Club Admin can go from a built image to a run that produces all three fact kinds by following one
  documented procedure using only existing Registry operations.
- **SC-007**: A segment of the evidence's size is analyzed within 30 seconds on a CPU-only host, and a one-minute 1080p
  segment stays within the baseline's memory bound and the execution timeout the manifest declares, checked in a
  Docker-backed test.
- **SC-008**: 100% of dependencies of the image have audit records with an allowed license, and the image contains zero
  model weights.
- **SC-009**: All existing reference-analyst, baseline, SDK, Manager, platform, and Web UI tests keep passing, and the
  feature adds no platform contract.
- **SC-010**: No description of the capability claims production readiness, calibrated settings, or met quality gates.

## Assumptions

### Dependencies

- Depends on the implemented specs Shot-Boundary Media Context Analyst (Baseline), Capability Fact Payload Schemas,
  Python Analyst SDK, Materialized Video Segments and Analyst Input Staging, and Analyst and Media Verification
  Environment.
- Depends on [Computer-Vision and Inference Verification Environment](../archive/20261023-085421-cv-inference-environment/spec.md)
  (twin 256), so that the verification of the locked image-processing and numeric libraries runs on the automation; the
  implementation starts only after it has merged.
- May use the [Analyst Evaluation Harness](../archive/20261023-082736-analyst-evaluation-harness/spec.md) (twin 255) once
  available for reporting, but does not require it; without it, the capability's own tests and the end-to-end evidence
  are the verification.

### Decisions Made By Default

- The capability keeps its id; its new version is `1.1.0` (additive facts, shot-boundary meaning unchanged) and its
  analyst manifest version is `1.2.0`, the version that carries fact payload schemas. The previous `1.0.0` stays
  registrable.
- The fact kinds are `media-quality` and `usable-interval`, capability-scoped like `shot-boundary`. Rule identifiers
  and reason codes are short, lowercase, hyphenated names fixed by the plan, one per quality rule.
- Quality rules are evaluated on a downscaled frame, and the frame measures and thresholds are fixed development values
  chosen by the plan; they are not calibrated against any corpus and carry no accuracy claim.
- Confidence of the new facts follows the baseline's convention for rule-based, uncalibrated facts.
- The usable-interval verdict is a pure function of the quality stretches in the segment; whether a downstream
  capability treats usable intervals as an optional upstream dependency is decided by those later capabilities, and
  this feature makes no platform change to enable it.
- Cuts across segment boundaries remain a documented limitation of the baseline and carry over unchanged.
- Out of scope: shot type, live, replay, and graphics classification and the learned challenger (they need learned
  classifiers and a labelled corpus); any quality gate, calibration, corpus, or promotion; model weights and GPU
  runtimes; platform, Web UI, and OpenAPI changes; and production values. The capability stays development evidence,
  never published.
