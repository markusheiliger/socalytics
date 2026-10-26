# Research: Person and Role Detection Analyst (Development Baseline)

All Technical Context unknowns are resolved here. Values marked "development value" are declared constants of
capability version 1.0.0, not calibrated or production values; a change to any of them is a new capability version.
The pattern (frame acquisition, window handling, audit, SBOM, image, registration, end-to-end design) follows the
implemented [shot-boundary analyst](../archive/20261023-022148-shot-boundary-analyst/research.md); this document only
records what differs.

## R-01 Model choice and licence

- **Decision**: YOLOX-S (Megvii, Apache-2.0 code and released weights), the official ONNX file of the upstream release,
  fed at 640x640. Its training data is COCO 2017 (annotations CC BY 4.0, images under their Flickr terms); that term
  set is recorded in `model/model-record.json` and in the audit. The file is fetched at image build from the audited
  source at a SHA-256 pin recorded in the project, never committed (about 35 MB, over the repository's binary rule, FR-013)
  and never downloaded at run time. The exact digest, retrieval date, and upstream tag are recorded by the first
  implementation task that fetches the file; the build, the start of the capability, and the audit check all compare
  against that one pin.
- **Rationale**: Permissive code and weights, an ONNX file exists upstream so no conversion is needed (the record states
  "no conversion"), CPU inference is fast enough at 640x640, and the catalog already names YOLOX-S as the detector
  family challenger, so a later fine-tuned model keeps the same output handling. Candidates with copyleft or
  non-commercial weight terms (the Ultralytics YOLO family under AGPL-3.0) fail the audit rule.
- **Stop rule**: If the audit at implementation cannot confirm the upstream licence and file provenance, or the digest
  cannot be pinned, the implementation stops and the choice is amended through the spec flow; it never falls back to an
  unaudited model.
- **Alternatives considered**: RT-DETR family (larger, heavier CPU cost, export variants vary); YOLOv5/8/11
  (AGPL-3.0); SSD-MobileNet (lower recall for people at broadcast scale); committing the weights (size rule).

## R-02 Output handling and class mapping

- **Decision**: The upstream YOLOX ONNX output is the undecoded head: per anchor `x, y, w, h, objectness, 80 class
  scores`. The capability decodes it with the standard grid-and-stride transform for strides 8, 16, and 32, scores each
  candidate as `objectness * class score`, and keeps only COCO class 0 (`person`). Class mapping `1.0.0`: COCO
  `person` maps to the role `person` (role-unknown); no other class is emitted. The capability therefore emits no
  specific role (`player`, `goalkeeper`, `referee`, `staff`); the role set of the payload schema keeps all five values so
  a later model can emit them within this fact kind, and the documentation and manifest description state which it can emit
  (only `person`).
- **Rationale**: Spec FR-007 and the clarified outcome: a generic COCO model cannot tell roles apart, and the honest
  output is role-unknown. Mapping version and COCO class id are written to every fact's provenance.
- **Alternatives considered**: colour heuristics for referees (invented role claims, calibration needed, out of scope).

## R-03 Preprocessing and postprocessing (development values)

| Setting | Value |
| --- | --- |
| Pass | one full-frame pass per analyzed frame, no tiles |
| Input | letterbox to 640x640: scale `min(640/w, 640/h)`, `cv2.INTER_LINEAR`, pad value 114, BGR order, float32, no normalization (the upstream ONNX convention), HWC to CHW |
| Confidence floor | 0.30 on `objectness * class score` |
| Duplicate merge | greedy non-maximum suppression, IoU threshold 0.45, applied once per frame, highest confidence first, ties broken by smaller top, then smaller left |
| Per-frame maximum | 100 detections after the merge, highest confidence first with the same tie-break |
| Remap | undo padding and scale, clip to the frame, drop boxes with width or height below 1 px after clipping |
| Rounding | box values rounded to 2 decimals, confidence to 4 decimals, after the maximum is applied |

- **Rationale**: Mirrors the upstream demo conventions so the pretrained weights behave as trained; the NMS threshold
  and floor are the upstream demo defaults and are not calibrated for football footage.
- **Decision on threads**: ONNX Runtime session options `intra_op_num_threads=1`, `inter_op_num_threads=1`,
  `ExecutionMode.ORT_SEQUENTIAL`, `graph_optimization_level` fixed, CPU provider only, and `cv2.setNumThreads(1)`. This
  makes the result independent of the CPU count (FR-010). Cost: lower throughput; acceptable at the sampling rate below.
- **Alternatives considered**: multi-threaded inference (non-identical floating-point summation order across thread
  counts); disabling graph optimization (slower, no gain in determinism once threads are fixed).

## R-04 Sampling rule and frame acquisition (development value)

- **Decision**: Rule `fixed-2fps-v1`: a grid of match-time points every 500 ms anchored at the segment's nominal window
  start. For grid point `t`, the analyzed frame is the first in-window frame whose match time is at or after `t` and
  before `t + 500`; a grid point without such a frame is skipped (variable frame rate, gaps). Frame match times come from
  `ffprobe` `best_effort_timestamp` as in the shot-boundary analyst. Selected frames are decoded at full size to raw
  `bgr24` with FFmpeg's `select` filter over zero-based frame indices (indices are batched, at most 256 per invocation,
  so the argument stays bounded) and `-fps_mode passthrough`; the staged file's zero-based frame index of each selected
  frame is recorded (lead-in frames counted, FR-009). A frame-index and timestamp count mismatch, or a missing frame in a
  batch, ends the attempt as `media-decode-failed`.
- **Rationale**: Two frames per second bounds cost per segment, keeps one-minute 1080p inside the manifest timeout, and
  makes results deterministic. Full-size decode happens only for selected frames, so memory is one frame plus one batch
  buffer.
- **Time and memory bound (SC-005)**: one-minute 1080p yields about 120 analyzed frames; at an assumed single-thread
  cost of a few hundred milliseconds per frame plus decode, the bound is the manifest's `minExecutionTimeoutSeconds`
  of 300 and `resources.memory` `2Gi`; the Docker-backed test measures both on a generated one-minute 1080p clip and
  records the measured numbers. If it exceeds either bound the sampling rate is lowered (a version change decided before
  merge), not the bound raised silently.
- **Alternatives considered**: decode every frame and discard (about 9 GB of pipe traffic per minute at 1080p); OpenCV
  `VideoCapture` (inexact timestamps, bundled decoder differs from the pinned build); every frame analyzed (cost).

## R-05 Window and lead-in handling

- **Decision**: As the shot-boundary analyst: stream-copy extraction can keep lead-in frames before the nominal window;
  they are never analyzed, a fact's `startMs` lies in the half-open window, `endMs` is the match time of the next
  analyzed frame or the window end for the last one, and is always greater than `startMs`. A frame exactly at the window
  end is outside. A segment with no analyzable frame completes successfully with no facts.
- **Rationale**: FR-004, FR-008, and the clarification on the end of a fact's range.

## R-06 Fact envelope, payload, and schema

- **Decision**: See [contracts/person-role-detection-fact.md](contracts/person-role-detection-fact.md). One fact per
  retained detection, kind `person-role-detection`, `confidence` in the envelope equals the payload confidence, output
  type `foundational-facts` at schema version `1.0.0`. The payload schema is declared in the manifest under
  `output.factPayloadSchemas` (manifest `1.2.0`) and kept as the same JSON in `schemas/person-role-detection.payload.schema.json`
  for tests; a test asserts the two are identical. Schema dialect: the restricted self-contained subset the Registry
  accepts (internal `$defs` and `$ref`, no formats, no external references).
- **Cross-field limit**: JSON Schema cannot compare `box.left + box.width` to `frame.width`. The schema bounds every
  coordinate to `0` through `16384` and sizes to `exclusiveMinimum 0`; containment in the exact frame is guaranteed by
  the capability (clipping) and asserted in its tests. The acceptance variant "box outside the frame" therefore uses a
  negative or over-bound coordinate, which the schema refuses with `result-payload-invalid`. This is a documented
  limit, not a platform change.
- **Rationale**: FR-009 and FR-019 (no contract change; the existing fact reads show the kind generically).

## R-07 Failure reasons

- **Decision**: Stable reasons: `segment-media-required`, `media-decode-failed`, `media-no-video-stream`,
  `timestamp-unmappable` (as the shot-boundary analyst), plus `model-missing` (file absent or unreadable) and
  `model-digest-mismatch` (digest differs from the pin at start), plus `model-inference-failed` (the session cannot be
  created or run). None writes a result.
- **Rationale**: FR-011; the SDK already ends an attempt as failed with a stable reason on an uncaught named error.

## R-08 Dependency audit and SBOM

- **Decision**: Same layout and allow-list as the shot-boundary analyst: `audit/dependency-audit.json` (name, version,
  licence, source per direct and transitive Python dependency, the bundled native libraries inside the OpenCV and ONNX
  Runtime wheels, the FFmpeg build, and the model with source, version, licence, training-data terms, retrieval date,
  digest, and conversion "none"), `audit/sbom.cdx.json` (CycloneDX of the capability layers, base image by digest), and
  `scripts/generate_audit.py`. The audit test fails for a locked package without a record, a model without a record, a
  digest that differs from the pin, or a licence outside the allowed set of
  `src/analysts/capabilities/media-context-classification` (permissive, LGPL for separately packaged shared libraries, the
  GCC runtime library exception). ONNX Runtime and its transitive Python dependencies (for example `protobuf`, `flatbuffers`,
  `sympy`, `packaging`, `coloredlogs`) are recorded individually from `uv.lock`; a dependency whose licence is not in the
  allowed set stops the implementation instead of widening the set.
- **Rationale**: FR-014; the evidence is development evidence and is never described as the catalog's completed audit.

## R-09 Packaging and the image

- **Decision**: A Dockerfile like the shot-boundary image: `python:3.12-slim-bookworm` by digest, `uv` and FFmpeg fetched
  with `docker/fetch_verified.py` against the pins equal to the environment's toolchain file, the model fetched by the
  same helper against `model/model-record.json`'s pin into a fixed path (`/opt/model/yolox_s.onnx`), `MANIFEST_SHA256`
  checked before the build, literal `io.socalytics.*` labels checked against the manifest at build time (including the
  model digest label), manifest copied last, `USER 65532:65532`, no network at run time. Manifest: `resources.memory`
  `2Gi`, `cpu` `2`, `minExecutionTimeoutSeconds` `300`, `executionScope` `segment`, `videoAccess` `required`, upstream
  declares media context as optional (not consumed), `compatibleProfiles` `["person-role-detection-dev"]`,
  `model.digest` the model pin. The file system holds exactly one non-code data file beyond the manifest: the model.
- **Rationale**: FR-012, FR-013; reuses the proven convention.

## R-10 Test strategy without committed media or committed weights

- **Decision**: Unit tests of the mechanics (letterbox remap across resolutions, confidence floor, merge, per-frame
  maximum, sampling and timestamp mapping with a non-zero offset, empty and single-frame media, schema conformance, every
  failure reason, determinism across thread counts) run against an inference seam: a `Detector` protocol with a
  deterministic scripted fake that returns raw head tensors for known boxes, so they need no model file. Tests that need
  the real model (a smoke detection and byte-identical repeats) run when the model file is present at the path in
  `PERSON_ROLE_MODEL_PATH` or in the image, and otherwise skip with an explicit reason; the Docker-backed image and
  end-to-end tests always run the real model, so the real model is always exercised before merge. Clips are generated by
  the pinned FFmpeg at test time; no `onnx` package is added.
- **Rationale**: FR-015 without a committed binary or a test-only dependency; the audit stays smaller.
- **Alternatives considered**: a tiny test ONNX graph built with the `onnx` package (extra audited dependency); committing
  a small model (a second model to audit, still binary).

## R-11 End-to-end evidence design and recall bound

- **Decision**: A C# helper generates, with the pinned FFmpeg, a 15-second 25 fps H.264 recording from lavfi sources:
  a green background with rendered figures (rectangle-composed head, torso, and legs drawn with `drawbox`, with distinct
  colours per nominal role so the truth lists roles) at known positions and sizes, moving by known linear paths, over
  three segments under a 5-second test segmentation policy; the middle segment contains one empty-frame stretch so that
  a segment with no detection is covered. Because the mapping can emit only `person`, every figure is expected as
  `person`; the truth records the nominal role for documentation only, and the test requires no specific-role fact.
  Comparison: a fact matches a known figure when its role is expected, its match time lies inside the figure's visible
  interval, and the IoU of the boxes is at least **0.5**; the **fixed minimum recall over all rendered figure
  appearances at sampled frames is 0.6**, and facts without a matching figure are reported but do not fail the test (the
  capability makes no precision claim). No fact may lie outside its segment's window, and every fact must validate
  against the schema.
- **Calibration stop rule**: The figure geometry is chosen and fixed by the implementation task that first runs the real
  model on the generated recording, and committed as constants. If the pinned model cannot reach the recall bound on any
  drawn geometry, the implementation stops for a spec amendment through the diagnosis flow; the bound is never lowered
  and the geometry is never tuned per run.
- **Schema-violation variants**: Four result variants (missing box, a box with a negative coordinate, an undeclared
  role, a confidence above one) are submitted through the existing result-write and acceptance path as the
  shot-boundary and fact-payload-schema evidence does, and each is refused with the named payload reason and no visible
  fact.
- **Alternatives considered**: real footage (rights); a figure texture file (committed binary); an exact-match
  comparison (a pretrained model is not exact on synthetic figures).

## R-12 Registration procedure

- **Decision**: The capability README documents the numbered procedure with the existing Registry operations only: build
  and push the image to the stamp's registry host and prefix, `registerAnalystImage` by digest, create and approve the
  profile `person-role-detection-dev` binding capability 1.0.0 to the digest and the model digest, define a workflow with
  a segment node for the capability, and set it as default. No seed, Registry rule, or production value is added.
- **Rationale**: FR-016, SC-004.

## R-13 Evaluation harness

- **Decision**: The [Analyst Evaluation Harness](../archive/20261023-082736-analyst-evaluation-harness/spec.md) spec exists but is
  not implemented in this repository state. FR-018 is therefore recorded as deferred in the capability README and the
  architecture status statements; no test depends on the harness. If the harness is implemented before this feature
  merges, the implementation adds the box label kind and report through the harness as FR-018 requires; the tasks mark
  that work conditional.
- **Rationale**: Spec User Story 6 scenario 3.

## R-14 Numeric tolerances

- **Decision**: Across hosts, box coordinates agree within 1.0 px and confidences within 0.001, while roles, counts, and
  frame references match exactly; across source resolutions of the same scene, the relative box position (coordinates
  divided by the frame size) agrees within 0.02. Cross-host comparison is a documented contract; the tests run on one
  architecture class and assert byte-identical results there.
- **Rationale**: Spec FR-010 and User Story 3 scenario 1.
