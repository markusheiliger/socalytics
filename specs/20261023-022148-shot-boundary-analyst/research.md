# Research: Shot-Boundary Media Context Analyst (Baseline)

All Technical Context unknowns are resolved here. Values marked "development value" are declared constants of
capability version 1.0.0, not calibrated or production values; a change to any of them is a new capability version.

## R-01 Frame acquisition and timestamps

- **Decision**: Two separate invocations of the image's pinned tools. First `ffprobe` lists `best_effort_timestamp` of
  every frame of the first video stream (the time base is read exactly, and `media_ms = floor(pts * time_base * 1000)`,
  as the reference capability does). Then `ffmpeg` decodes the same stream to `rawvideo` `bgr24` frames downscaled to
  64x36 with `scale=64:36:flags=area` and `-fps_mode passthrough` on stdout, one fixed-size record per frame. The
  detector pairs the n-th timestamp with the n-th frame; a count mismatch ends the attempt with `media-decode-failed`.
- **Rationale**: Frame timing must come from the frames' own timestamps (FR-007, variable frame rate), and OpenCV's
  `VideoCapture` position property is neither exact nor deterministic across builds. Reading the timestamps first keeps
  memory constant (a few thousand integers) and avoids reading two pipes in lockstep. Downscaling inside FFmpeg keeps a
  1080p segment at about 7 KiB per frame in Python, which is what meets the 2 GiB bound.
- **Alternatives considered**: `cv2.VideoCapture` (inexact timestamps, bundled decoder differs from the pinned build);
  decoding full frames into NumPy (memory and time); a single `showinfo` stderr parse (fragile pairing of two
  streams); `signalstats` like the reference (luma only, no color measure).

## R-02 OpenCV distribution and environment

- **Decision**: `opencv-python-headless` plus `numpy`, installed only from hash-locked wheels (`uv sync --locked
  --no-build`). OpenCV is used for the HSV conversion and the absolute difference; it never decodes media. Exact
  versions are chosen when `uv lock` runs at implementation and the lock is committed.
- **Rationale**: The headless wheel needs no system GUI libraries in the slim base image, is the catalog's named
  library, and is Apache-2.0 licensed. The environment actions already provide CPython 3.12, `uv`, and FFmpeg, and the
  verification sequence installs every locked project from wheels, like the SDK's dependencies, so no standalone
  environment spec is required. If implementation finds that a locked wheel cannot be installed by the locked sequence
  in the verification environment, the implementation stops and a standalone environment spec lands first (the spec's
  rule); the feature never edits the environment actions.
- **Alternatives considered**: pure NumPy HSV conversion (drops the catalog's named library and risks a different
  measure); `opencv-python` (pulls GUI libraries); PySceneDetect (extra dependency, its own behavior across versions
  would become part of the capability's results).
- **License note**: The wheel bundles FFmpeg shared libraries (LGPL) beside OpenCV. They are separately packaged shared
  libraries and fall in the allowed set. The audit lists them explicitly (R-07) and the capability never calls them.

## R-03 Content-change measure

- **Decision**: For two consecutive downscaled frames, convert both to HSV with OpenCV (`cv2.COLOR_BGR2HSV`, 8-bit), take
  the per-pixel absolute difference per channel (hue without wrap-around), and define the measure as
  `(sum |dH| + sum |dS| + sum |dV|) / (3 * pixels)` computed from integer sums and quantized to three decimals with
  `ROUND_HALF_EVEN` on an exact `Fraction`/`Decimal`. The measure range is 0 to 255.
- **Rationale**: Combines luminance and color (the spec's default), is the established content-detector shape, and is
  integer-exact before the final rounding, so it is byte-identical on every host and with any CPU count.
- **Alternatives considered**: histogram distance (needs float binning), SSIM (heavier, float-dependent), luma only
  (misses color-only cuts between low-contrast scenes).

## R-04 Detection rules and settings (development values)

- **Decision**: Constants in `settings.py`, recorded in the README and in every fact's provenance:
  `HARD_THRESHOLD = 30.0`; `ADAPTIVE_FLOOR = 8.0`; `ADAPTIVE_RATIO = 3.0`; `ADAPTIVE_WINDOW = 4` frame pairs on each
  side (only pairs inside the nominal window; edges use the pairs available); `MIN_SHOT_LENGTH_MS = 500`; rule order:
  hard cut first, then adaptive. A pair is a hard cut when `measure >= HARD_THRESHOLD`; otherwise an adaptive cut when
  `measure >= ADAPTIVE_FLOOR` and `measure >= ADAPTIVE_RATIO * mean(surrounding measures)`, where the surrounding
  measures exclude the pair itself and the mean uses exact arithmetic. A boundary is reported only when its new-shot
  start is at least `MIN_SHOT_LENGTH_MS` after the previous reported boundary's start (the first boundary of a segment
  has no predecessor and is never suppressed); a suppressed boundary produces no fact and does not move the reference
  point.
- **Rationale**: The values reproduce the catalog's "content/adaptive cut detection" with a high threshold for obvious
  cuts and a relative rule for low-contrast or high-motion footage. They are tuned only against the synthetic fixtures
  and are not calibrated; no accuracy claim is made beyond them (FR-018). A time-based minimum shot length stays valid
  for variable frame rates.
- **Alternatives considered**: frame-count minimum (wrong at variable rates); a trailing-only window (cannot see a
  following calm period, and is a worse test for a pan); learned thresholds (out of scope).

## R-05 Window and lead-in handling

- **Decision**: Frames before the requested source interval or whose mapped match time is outside the nominal
  half-open window are never reported. The last such frame before the first in-window frame is kept only as the
  predecessor of that frame. A pair is scored when its second frame is inside the nominal window and the requested
  source interval, using the mapping `SegmentMedia.to_match_ms` and the job's `segment_window`; the surrounding
  measures for the adaptive rule come only from pairs whose second frame is inside the window.
- **Rationale**: Implements the clarified lead-in rule exactly and keeps a boundary in the segment that contains its
  new shot's first frame. The first in-window frame without any predecessor yields no pair (the documented
  segment-edge limitation).
- **Alternatives considered**: scoring lead-in pairs for context (leaks out-of-window content into the rule).

## R-06 Fact range, confidence, and payload

- **Decision**: A fact starts at the match time of the first frame of the new shot and ends at the match time of the
  next frame in the window, or at the nominal window end for the last frame; a result that is not after the start
  (equal millisecond after flooring) becomes `start + 1`, never beyond the window end. Confidence is the fixed value
  `0.5`, documented as "rule-based, uncalibrated". The payload shape is in [contracts/shot-boundary-fact.md](
  contracts/shot-boundary-fact.md). Payload schema validation: the open
  [Capability Fact Payload Schemas](../archive/20261023-010826-fact-payload-schemas/spec.md) feature has not merged, so this
  plan does not declare the schema in the manifest or depend on it; the capability ships
  `schemas/shot-boundary.payload.schema.json`, validated in its own tests with `jsonschema`, exactly like the
  reference capability's schema. Declaring it in the manifest is a follow-up that a later version would make.
- **Rationale**: Stays inside the existing contracts (FR-001, FR-017) and keeps the facts valid whichever way the open
  feature lands.
- **Alternatives considered**: waiting for the payload schema feature (blocks this feature for no gain); declaring the
  schema in an unreleased manifest field (would not validate against the released contract).

## R-07 Dependency audit and SBOM evidence

- **Decision**: `audit/dependency-audit.json` records `name`, `version`, `license` (SPDX expression), and `source` for
  every package of the locked environment (direct and transitive, including the dev group excluded from the image but
  marked as such) plus the bundled OpenCV FFmpeg libraries and the pinned FFmpeg build; `audit/sbom.cdx.json` is a
  CycloneDX 1.5 document of the capability layers (Python environment, FFmpeg build, base image reference by digest).
  `scripts/generate_audit.py` regenerates both from `uv.lock` and installed distribution metadata. The pytest audit
  test fails when a locked package has no record or a record differs from the lock, and when a license is outside the
  allowed set: permissive licenses such as MIT, BSD, Apache-2.0, PSF, ISC; LGPL for separately packaged shared
  libraries; and `GPL-3.0-with-GCC-exception` (runtime library exception) only for the bundled GCC runtime libraries
  listed in the audit with their parent wheel. Any other GPL or AGPL expression and any unknown license fail. A Docker-backed image test lists the installed Python distributions in the
  image and compares them with the audit.
- **Rationale**: Starts the catalog's required evidence (Wave 0) without claiming the completed independent audit.
  The SBOM states that the base image's operating-system packages are identified only by the base image digest; the
  full operating-system package audit and the catalog's independent audit remain open.
- **Alternatives considered**: a third-party SBOM generator (new tooling in the environment); license data fetched
  from the network at test time (not allowed).

## R-08 Failure reasons

- **Decision**: Stable named reasons, raised through the SDK's `InputError` or `ResultError` and never with a partial
  result: `segment-media-required` (no media in the job), `media-decode-failed` (tool failure, undecodable media,
  timestamp and frame count mismatch), `media-no-video-stream` (no video stream), and `timestamp-unmappable` (a frame
  in the requested interval cannot be mapped to the segment window). Fewer than two frames completes with no facts.
- **Rationale**: One reason per spec failure class, matching the reference capability's naming style.

## R-09 Packaging and the image

- **Decision**: A Dockerfile with the same structure as the reference image: `python:3.12-slim-bookworm` by digest,
  `uv` and FFmpeg downloaded with `docker/fetch_verified.py` against the SHA-256 pins that equal the environment's
  toolchain file, `MANIFEST_SHA256` checked before the build, literal `io.socalytics.*` labels, the manifest copied last,
  `USER 65532:65532`, `ENTRYPOINT` of the project's venv python. The manifest declares `resources.memory` `2Gi`,
  `cpu` `2`, `minExecutionTimeoutSeconds` `300`, the no-model digest, `executionScope` `segment`, `videoAccess`
  `required`, no upstream, `compatibleProfiles` `["media-context-classification-dev"]`.
- **Rationale**: Reuses the proven convention (FR-012) and the existing end-to-end image helpers' pattern.

## R-10 End-to-end evidence design

- **Decision**: `CutMedia` generates, with the pinned FFmpeg, a 15-second 25 fps H.264 recording from lavfi sources
  with known hard cuts at frame positions chosen so that three segments (5-second test policy) result, the first and
  third each contain at least one cut, one cut sits exactly on the second frame of its segment, no cut sits on a
  segment edge (the documented limitation), and the middle segment is cut-free. `ShotBoundaryEndToEndTests` follows the reference end-to-end test: build and push by digest, register
  through the Registry operations, approve a profile, define and default a workflow, finalize, run, and read facts
  with `listAnalysisRunFacts` and `listAnalysisRunFactStreams`, comparing kind, match-time start, rule, and count with
  the generated truth exactly. A memory and time test runs the image on a generated one-minute 1080p clip in a
  restricted container and reads the container's peak memory from its cgroup, failing above 2 GiB or beyond the
  attempt lease.
- **Rationale**: Exact comparison is possible because the clips are generated with known frame content, and stream-copy
  extraction keeps the requested interval's frames, which the lead-in rule handles.

## R-11 Documented registration procedure

- **Decision**: The capability README documents the procedure with the existing Registry operations only: build the
  image, push it to a registry inside the stamp's configured host and repository prefix, `registerAnalystImage` by
  digest, create and approve a profile (`media-context-classification-dev` binding capability version 1.0.0 to the
  digest with the no-model digest), define a workflow with a segment node for the capability, and set it as the
  default. No seed, no Registry rule, and no production value is added; the end-to-end test performs the same steps.
- **Rationale**: FR-015, FR-017, SC-004.
