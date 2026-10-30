# Research: Ball Detection Analyst (Development Baseline)

All Technical Context unknowns are resolved here. Values marked "development value" are declared constants of
capability version 1.0.0, not calibrated or production values; a change to any of them is a new capability version.
The pattern (frame acquisition, window handling, audit, SBOM, image, registration, end-to-end design) follows the
implemented [shot-boundary analyst](../archive/20261023-022148-shot-boundary-analyst/research.md); this document records
what differs. The capability is independent of `person-role-detection` (spec FR-022): it shares no code, file, or image
layer with it, even where the choices below coincide.

## R-01 Model choice and licence

- **Decision**: YOLOX-S (Megvii, Apache-2.0 code and released weights), the official ONNX file of the upstream release,
  fed at 640x640, using the COCO class `sports ball` (class id 32). Training data is COCO 2017 (annotations CC BY 4.0,
  images under their Flickr terms); that term set is recorded in `model/model-record.json` and in the audit. The file is
  fetched at image build from the audited source at a SHA-256 pin recorded in the project, never committed (about 35 MB,
  over the repository's binary rule, FR-014) and never downloaded at run time. The exact digest, retrieval date, and
  upstream tag are recorded by the first implementation task that fetches the file; the build, the start of the
  capability, and the audit check all compare against that one pin.
- **Rationale**: Permissive code and weights; an ONNX file exists upstream, so no conversion is needed (the record states
  "no conversion"); a generic ball class exists in COCO; CPU inference is fast enough at 640x640; the catalog names
  YOLOX-S as the ball detector challenger family, so a later fine-tuned model keeps the same output handling. Candidates
  with copyleft or non-commercial weight terms (the Ultralytics YOLO family under AGPL-3.0) fail the audit rule.
- **Stop rule**: If the audit at implementation cannot confirm the upstream licence and file provenance, or the digest
  cannot be pinned, the implementation stops and the choice is amended through the spec flow; it never falls back to an
  unaudited model.
- **Alternatives considered**: RT-DETR family (heavier CPU cost, export variants vary); YOLOv5/8/11 (AGPL-3.0);
  SSD-MobileNet (poor small-object recall); a point-only ball detector (none permissively licensed found; geometry stays a
  box); committing the weights (size rule).

## R-02 Output handling and class selection

- **Decision**: The upstream YOLOX ONNX output is the undecoded head: per anchor `x, y, w, h, objectness, 80 class
  scores`. The capability decodes it with the standard grid-and-stride transform for strides 8, 16, and 32, scores each
  candidate as `objectness * class score`, and keeps only COCO class 32 (`sports ball`). The class id and name are written
  to every fact's provenance. No other class is emitted.
- **Rationale**: Spec FR-005 and FR-007; the pretrained model has a generic ball class, so no fine-tuning or mapping is
  needed.
- **Alternatives considered**: keeping other classes as ball surrogates (invented evidence).

## R-03 Preprocessing and postprocessing (development values)

| Setting | Value |
| --- | --- |
| Pass | one full-frame pass per analyzed frame, no tiles |
| Input | letterbox to 640x640: scale `min(640/w, 640/h)`, `cv2.INTER_LINEAR`, pad value 114, BGR order, float32, no normalization (the upstream ONNX convention), HWC to CHW |
| Confidence floor | 0.20 on `objectness * class score` (lower than a person detector's, because a small ball scores low; not calibrated) |
| Duplicate merge | greedy non-maximum suppression, IoU threshold 0.45, applied once per frame, highest confidence first, ties broken by smaller top, then smaller left |
| Per-frame maximum | 5 observations after the merge, highest confidence first with the same tie-break |
| Remap | undo padding and scale, clip to the frame, drop boxes with width or height below 1 px after clipping |
| Rounding | box and centre values rounded to 2 decimals, confidence to 4 decimals, after the maximum is applied |

- **Threads**: ONNX Runtime session options `intra_op_num_threads=1`, `inter_op_num_threads=1`,
  `ExecutionMode.ORT_SEQUENTIAL`, fixed `graph_optimization_level`, CPU provider only, and `cv2.setNumThreads(1)`. The
  result is independent of the CPU count (FR-011). Cost: lower throughput, acceptable at the sampling rate below.
- **Known limitation**: a 640x640 full-frame pass shrinks a 1080p frame by 3x, so distant balls of a few pixels vanish.
  This is why the architecture plans tiled profiles; the README states it plainly and records small-ball recall as
  unmeasured (FR-021).
- **Alternatives considered**: multi-threaded inference (non-identical floating-point order across thread counts); a
  higher floor (drops weak real balls before any calibration evidence exists).

## R-04 Sampling rule and frame acquisition (development value)

- **Decision**: Rule `fixed-5fps-v1`: a grid of match-time points every 200 ms anchored at the segment's nominal window
  start. For grid point `t`, the analyzed frame is the first in-window frame whose match time is at or after `t` and before
  `t + 200`; a grid point without such a frame is skipped (variable frame rate, gaps). Frame match times come from
  `ffprobe` `best_effort_timestamp` as in the shot-boundary analyst. Selected frames are decoded at full size to raw
  `bgr24` with FFmpeg's `select` filter over zero-based frame indices, batched at most **32** per invocation (about 200 MB
  of raw 1080p frames, so the argument and the memory stay bounded) with `-fps_mode passthrough`; the zero-based file index
  of each selected frame is recorded (lead-in frames counted, FR-009). A frame-index and timestamp count mismatch, or a
  missing frame in a batch, ends the attempt as `media-decode-failed`.
- **Rationale**: A ball crosses the frame quickly, so the rate is higher than a person detector's; 5 fps still bounds the
  cost per segment and keeps the output deterministic.
- **Time and memory bound (SC-005)**: one minute of 1080p yields about 300 analyzed frames; at an assumed single-thread
  cost of a few hundred milliseconds per frame plus decode, the bound is the manifest's `minExecutionTimeoutSeconds` of
  300 and `resources.memory` `2Gi`; the Docker-backed image test measures both on a generated one-minute 1080p clip and
  records the numbers. If either bound is exceeded the sampling rate is lowered (a version change decided before merge),
  the bound is not raised silently.
- **Alternatives considered**: every frame (cost); decode all frames and discard (heavy pipe traffic); OpenCV
  `VideoCapture` (inexact timestamps, decoder differs from the pinned build).

## R-05 Window and lead-in handling

- **Decision**: As the shot-boundary analyst: stream-copy extraction can keep lead-in frames before the nominal window;
  they are never analyzed. A fact's `startMs` lies in the half-open window; `endMs` is the match time of the next analyzed
  frame or the window end for the last one, and is always greater than `startMs`. A frame exactly at the window end is
  outside. A segment with no analyzable frame completes successfully with no facts.

## R-06 Visibility states and rule (FR-010)

- **Decision**: Declared set `confidently-visible` and `not-confidently-visible`. Rule `visibility-rule` version `1.0.0`:
  `confidently-visible` when the fact's confidence is at least **0.50**, else `not-confidently-visible` (confidence at or
  above the floor 0.20 and below 0.50). The rule uses the single frame's score only. No state means occluded, off-frame,
  or out of play; a missed ball is simply no fact.
- **Rationale**: The only single-frame evidence a pretrained detector yields is its score; the threshold is a development
  value, not calibrated. The rule version is written to every fact.
- **Alternatives considered**: size- or position-based states (claim more than one frame supports); an `occluded` state
  (needs neighbouring frames, forbidden by FR-007).

## R-07 Fact envelope, payload, and schema

- **Decision**: See [contracts/ball-observation-fact.md](contracts/ball-observation-fact.md). One fact per retained
  observation, kind `ball-observation`, envelope `confidence` equal to the payload confidence, output type
  `foundational-facts` at schema version `1.0.0`. The payload schema is declared in the manifest under
  `output.factPayloadSchemas` (manifest `1.2.0`) and kept as the same JSON in `schemas/ball-observation.payload.schema.json`
  for tests; a test asserts the two are identical. Dialect: the restricted self-contained subset the Registry accepts
  (internal `$defs` and `$ref`, no formats, no external references).
- **Cross-field limit**: JSON Schema cannot compare `box.left + box.width` to `frame.width`. The schema bounds every
  coordinate to `0` through `16384` and sizes to `exclusiveMinimum 0`; containment in the exact frame is guaranteed by the
  capability (clipping) and asserted in its tests. The acceptance variant "coordinate outside the bounds" uses a negative
  or over-bound coordinate, which the schema refuses with `result-payload-invalid`. This is a documented limit, not a
  platform change.

## R-08 Failure reasons

- **Decision**: Stable reasons `segment-media-required`, `media-decode-failed`, `media-no-video-stream`,
  `timestamp-unmappable`, `model-missing`, `model-digest-mismatch`, and `model-inference-failed`, with the meanings of the
  other capabilities. None writes a result. The SDK ends an attempt as failed with the named reason on an uncaught named
  error.

## R-09 Dependency audit and SBOM

- **Decision**: Same layout and allow-list as the shot-boundary analyst: `audit/dependency-audit.json` (name, version,
  licence, source per direct and transitive Python dependency, the bundled native libraries of the OpenCV and ONNX Runtime
  wheels, the FFmpeg build, and the model with source, version, licence, training-data terms, retrieval date, digest, and
  conversion "none"), `audit/sbom.cdx.json` (CycloneDX of the capability layers, base image by digest), and
  `scripts/generate_audit.py`. The audit test fails for a locked package without a record, a model without a record, a
  digest that differs from the pin, or a licence outside the allowed set of
  `src/analysts/capabilities/media-context-classification` (permissive, LGPL for separately packaged shared libraries, the
  GCC runtime library exception). A dependency whose licence is not in the allowed set stops the implementation instead of
  widening the set. The evidence is development evidence and never described as the catalog's completed audit.

## R-10 Packaging and the image

- **Decision**: A Dockerfile like the shot-boundary image: `python:3.12-slim-bookworm` by digest, `uv` and FFmpeg fetched
  with `docker/fetch_verified.py` against pins equal to the environment's toolchain file, the model fetched by the same
  helper against `model/model-record.json`'s pin into `/opt/model/yolox_s.onnx`, `MANIFEST_SHA256` checked before the
  build, literal `io.socalytics.*` labels checked against the manifest at build time (including the model digest label),
  manifest copied last, `USER 65532:65532`, no network at run time. Manifest: `resources.memory` `2Gi`, `cpu` `2`,
  `minExecutionTimeoutSeconds` `300`, `executionScope` `segment`, `videoAccess` `required`, media context declared as an
  optional unconsumed upstream, `compatibleProfiles` `["ball-detection-dev"]`, `model.digest` the model pin. Beyond the
  manifest the file system holds exactly one non-code data file: the model.

## R-11 Test strategy without committed media or weights

- **Decision**: Unit tests of the mechanics (letterbox remap across resolutions and aspect ratios, confidence floor,
  visibility rule, merge, per-frame maximum, sampling and timestamp mapping with a non-zero offset, empty and single-frame
  media, schema conformance, every failure reason, determinism across thread counts, absence of cross-frame state) run
  against an inference seam: a `Detector` protocol with a deterministic scripted fake that returns raw head tensors for
  known boxes, so they need no model file. Tests that need the real model (a smoke detection and byte-identical repeats)
  run when the file is present at the path in `BALL_DETECTION_MODEL_PATH` or in the image, and otherwise skip with an
  explicit reason; the Docker-backed tests always run the real model. Clips are generated by the pinned FFmpeg at test
  time; no `onnx` package is added.

## R-12 End-to-end evidence design and recall bound

- **Decision**: A C# helper generates, with the pinned FFmpeg, a 15-second 25 fps 1280x720 H.264 recording from lavfi
  sources: a green pitch-like background with one rendered ball (a white disk with dark patches drawn by a `geq` or overlay
  filter, diameter 48 px, so about 24 px in the model input) moving along a known piecewise-linear path, over three
  segments under a 5-second test segmentation policy; the middle segment contains a stretch with no ball so a segment
  analyzed with no fact is covered. Truth is recorded per frame as centre and diameter.
- **Matching rule**: matching is by match time. The test derives the analyzed frames by applying the sampling rule
  `fixed-5fps-v1` to each segment's nominal window over the recording's known 25 fps timeline (grid points every 200 ms
  from each window start, that is every 5th recording frame). Each such frame in which the truth has the ball counts as a
  ball appearance; a fact matches it when its `startMs` equals that frame's match time and the distance between the
  fact's centre and the truth centre is at most the truth diameter. `frameIndex` is only asserted to be present, an
  integer >= 0, and consistent with the segment file; it is not compared with the recording's truth index. The **fixed minimum recall over all rendered ball appearances at analyzed frames is
  0.5**; facts without a matching ball are reported but do not fail the test (no precision claim). No fact may lie outside
  its segment's window, and every fact must carry a frame identity and validate against the schema.
- **Calibration stop rule**: The ball appearance and path are chosen and fixed by the implementation task that first runs
  the real model on the generated recording, then committed as constants. If the pinned model cannot reach the bound on any
  reasonable drawn ball, the implementation stops for a spec amendment through the diagnosis flow; the bound is never
  lowered and the geometry is never tuned per run.
- **Schema-violation variants**: Five result variants (missing box, a negative coordinate, an undeclared visibility state,
  a confidence above one, a missing frame index) are submitted through the existing result-write and acceptance path as the
  shot-boundary and payload-schema evidence does; each is refused with the named payload reason and leaves no visible fact.
- **Alternatives considered**: real footage (rights); a ball texture file (committed binary); exact-match comparison (a
  pretrained model is not exact on synthetic balls).

## R-13 Registration procedure

- **Decision**: The capability README documents the numbered procedure with the existing Registry operations only: build
  and push the image to the stamp's registry host and prefix, `registerAnalystImage` by digest, create and approve the
  profile `ball-detection-dev` binding capability 1.0.0 to the digest and the model digest with a `required` video policy,
  define a workflow with a segment node for the capability, and set it as default. No seed, Registry rule, or production
  value is added.

## R-14 Evaluation harness

- **Decision**: The [Analyst Evaluation Harness](../archive/20261023-082736-analyst-evaluation-harness/spec.md) spec exists but the
  harness is not implemented in this repository state. FR-019 is recorded as deferred in the capability README and the
  architecture status statements; no test depends on the harness. If the harness is implemented before this feature
  merges, the implementation adds a ball label kind with a centre-distance matching rule and the report through the harness;
  the tasks mark that work conditional.

## R-15 Numeric tolerances

- **Decision**: Across hosts, box and centre coordinates agree within 1.0 px and confidences within 0.001, while visibility
  states, counts, and frame references match exactly. Across source resolutions of the same scene, the relative centre
  position (coordinates divided by the frame size) agrees within 0.01. Cross-host comparison is a documented contract; the
  tests run on one architecture class and assert byte-identical results there.
