# Research: Reference Analyst Container on the Python SDK

Decisions for [spec.md](spec.md). Each entry: decision, rationale, alternatives.

## R1 - Capability identity and project location

**Decision**: The capability id is `reference-media-probe` (version `1.0.0`, output schema version `1.0.0`), and the
project is `src/analysts/capabilities/reference-media-probe/` with the distribution name
`socalytics-reference-media-probe` and the package `reference_media_probe`. The one compatible Analyst profile id is
`reference-media-probe-dev`. The fact kind is `frame-second-probe`.

**Rationale**: The `reference-` prefix and the `-probe` suffix match no catalog capability
([Analyst Capability Catalog](../../../docs/architecture/analyst-capability-catalog.md)), say that it is a development
reference, and satisfy the common `key` pattern. The location is the one the environment feature verifies.

**Alternatives considered**: `media-context-probe` (rejected: too close to the catalog's `media-context-classification`);
a `dev-` prefix (rejected: the configured Development seed already uses `dev-*` placeholder capabilities that never run
for real, which would blur the distinction).

## R2 - Facts the capability writes

**Decision**: For every whole match-second `[k·1000, (k+1)·1000)` that overlaps the job's nominal window and the
mapped match time of the staged segment manifest's `timelineSpans`, the Analyst writes one fact of kind
`frame-second-probe` with confidence `1.0`, whose range is the smallest half-open interval covering the second's
mapped part inside the window. Its payload is `{ "decodedFrames": n }` and, when `n > 0`,
`"meanLuma": { "min": a, "mean": b, "max": c }`, where each frame's mean luma is the decoder's average of the 8-bit
luma plane (`lavfi.signalstats.YAVG`), values are rounded half-even to three decimals, and the mean is computed in
decimal arithmetic before rounding. A frame belongs to the second that contains its match time; its match time is
its presentation timestamp converted to integer milliseconds (floor of the exact rational value) and then through
the SDK's `SegmentMedia.to_match_ms`; frames for which that returns `None`, whose media time lies outside the
manifest's `requestedSourceInterval`, or whose match time lies outside the nominal window are ignored. Facts are
ordered by start time; the overall result confidence is `1.0` (or the SDK's default `0` when there is no fact).

**Rationale**: Frame counts and luma statistics are cheap, deterministic, exercise real decoding, and need no model;
whole-second alignment makes facts comparable across segments because nominal windows are whole multiples of the
segmentation duration. Rounding and decimal arithmetic make the bytes independent of floating-point summation order
(FR-007). The time rules restate the [Segment Contract](../../../docs/architecture/match-data-pipeline.md#segment-contract)
and the [container interface additions](../20261017-222433-materialized-segments/contracts/container-interface.md)
for this capability (FR-005, FR-006).

**Alternatives considered**: One fact per frame (rejected: thousands of facts per real segment for no evidence value);
histogram payloads (rejected: larger payloads without additional evidence); scene-cut detection (rejected: threshold
choices are policy, not a probe).

## R3 - Decoding

**Decision**: The Analyst runs the image's pinned `ffprobe` once per job as a subprocess, with arguments only (no
shell): `-v error -f lavfi -i movie=<escaped media path>,signalstats -show_entries
frame=best_effort_timestamp:frame_tags=lavfi.signalstats.YAVG -of csv=p=0`, plus a separate
`ffprobe -show_entries stream=time_base` call on the media to read the video stream's time base. It reads the CSV
output line by line, aggregates per second while streaming, never stores frames, and bounds the captured standard
error. A non-zero ffprobe exit, unparsable output, or a frame without a timestamp fails with the category
`media-decode-failed` (exit 1, because the SDK has already verified the inputs).

**Rationale**: The `movie` source and `signalstats` filter are part of the environment's pinned LGPL build, decode each
frame once, and keep source timestamps, which the materialized segment preserves. Streaming keeps memory bounded by
one frame (FR-008). No Python imaging or numeric dependency is needed.

**Alternatives considered**: Piping raw gray frames into Python (rejected: more bytes through a pipe and a slow pure
Python loop); PyAV, OpenCV, or NumPy (rejected: new native wheels to audit and lock, no evidence gain);
`ffmpeg -vf metadata=print` on standard error (rejected: log-format parsing).

## R4 - Exit behavior

**Decision**: The project's entry point is `reference_media_probe.__main__`, which calls the SDK's
`run(analyse, output_type="foundational-facts")`. The capability adds only `media-decode-failed` and
`segment-media-required` (raised when `context.media` is `None`, before any decoding) as `ResultError` and `InputError`
categories; every other failure category comes from the SDK.

**Rationale**: The SDK owns the exit statuses (0, 1, 2) and the single-line outputs; a job without media under a
`required` policy is an invalid input for this capability.

**Alternatives considered**: Writing an empty result for jobs without media (rejected: hides a platform or Manager
defect).

## R5 - Image base and pinned inputs

**Decision**: A two-stage Dockerfile on the official `python:3.12-slim-bookworm` image pinned by digest (the digest
current at implementation time, recorded in the Dockerfile and its README). The builder stage downloads the `uv` and
FFmpeg archives named in `.github/actions/environment-setup/analyst-media-toolchain.json` with a small Python script
of the project (`docker/fetch_verified.py`, standard library only) that verifies each archive's SHA-256 before it
extracts only the needed members (`uv`; `bin/ffmpeg`, `bin/ffprobe`, `LICENSE.txt`). The archive URLs and hashes are
`ARG` defaults in the Dockerfile, and a project test asserts they equal the toolchain JSON (FR-017). The final stage
copies the Python environment, `ffprobe`, `ffmpeg`, and the license notice to `/opt/reference-media-probe/`, with root
ownership and world-readable, world-executable permissions.

**Rationale**: Reuses exactly the toolchain the environment feature pins and verifies, so tests and image decode with
the same build; standard-library download and extraction avoid installing `curl` or `xz` from moving distribution
packages and avoid BuildKit-only `ADD --checksum` (the end-to-end tests may build with the classic builder through
Testcontainers). A glibc Debian base runs the static Linux builds and the official CPython 3.12.

**Alternatives considered**: Debian `ffmpeg` packages (rejected: not pinned to the environment's version, GPL
components); copying the uv image (rejected: an extra pinned image when the archive is already pinned and verified);
Alpine (rejected: musl and the official images' variant drift); python-build-standalone in a distroless base
(rejected: more moving parts for the first reference).

**Open item**: The FFmpeg publisher's release is not immutable (its retention policy); the image build fails with a
named reason when the archive is gone, as the environment setup does, and a new pin is an environment change.

## R6 - Locked dependency installation in the image

**Decision**: The builder copies `src/analysts/sdk/` and the capability project to the same relative paths and runs,
with `UV_PYTHON_DOWNLOADS=never`, `UV_PYTHON=/usr/local/bin/python3.12`, and `UV_PROJECT_ENVIRONMENT` pointing at
`/opt/reference-media-probe/venv`, the environment's locked bootstrap
(`uv sync --locked --all-groups --no-install-local --no-build`) so the locked build backend exists, then
`uv sync --locked --no-dev --no-editable --no-build-isolation --compile-bytecode` to install the SDK and the capability
as non-editable packages and remove the development group. The final stage copies that environment unchanged.

**Rationale**: Only locked versions are installed, build backends come from the lock rather than an isolated fresh
resolution, packages are not editable (no source tree needed at run time), and precompiled bytecode means no writes
under a read-only root (FR-010, FR-011).

**Alternatives considered**: `pip install` from an exported requirements file (rejected: a second resolver path);
building wheels on the host and copying them in (rejected: the documented build would depend on host tooling).

## R7 - Runtime under the Manager's restrictions

**Decision**: The image sets `PYTHONDONTWRITEBYTECODE=1`, `PYTHONUNBUFFERED=1`, and `PATH` to include
`/opt/reference-media-probe/bin`, declares `USER 65532:65532`, and uses
`ENTRYPOINT ["/opt/reference-media-probe/venv/bin/python", "-m", "reference_media_probe"]`. The Analyst writes only
through the SDK (result mount), passes no temporary file to ffprobe, and needs no home directory. ffprobe's working
directory is `/socalytics/scratch`.

**Rationale**: The Docker adapter runs the container as the Manager's own non-root uid or `65532:65532`, on a read-only
root with a `noexec` `/tmp` and no environment variables
([Docker adapter launch spec](../20261015-092159-docker-runtime-adapter/spec.md)); every file the Analyst
executes or reads lives in the image and is world-readable.

**Alternatives considered**: Creating a named user (rejected: the Manager overrides the user anyway).

## R8 - Manifest, labels, and the last layer

**Decision**: `analyst-manifest.json` beside the Dockerfile declares `reference-media-probe` 1.0.0, tier `low-level`,
scope `segment`, `upstream: []`, `videoAccess: required`, `output.schemaVersion: 1.0.0`,
`compatibleProfiles: ["reference-media-probe-dev"]`, `model.digest` equal to the platform's no-model digest
(`sha-256:` followed by 64 zeros, `RegistryWorkflowDefinitionResolver.NoModelDigest`), `runtime.constraints:
["linux-amd64"]`, `accelerators: []`, resources `memory: 512Mi`, `cpu: 1`, requirements `minMaxAttempts: 1`,
`minLeaseSeconds: 30`, `minStaleTimeoutSeconds: 60`, `minExecutionTimeoutSeconds: 300`, and determinism
`{ deterministic: true, idempotent: true }`. The Dockerfile states the five `io.socalytics.*` labels as literal
values; the builder stage verifies `sha256sum` of the manifest against the literal checksum and fails otherwise
(FR-009), and the final stage's last instruction is
`COPY …/analyst-manifest.json /opt/socalytics/analyst-manifest.json`. Project tests check the manifest against the
released schema and the labels against the manifest.

**Rationale**: Literal labels make the documented build command argument-free and reproducible; the build-time check
and the tests catch a manifest edit without a label update. The zero digest is the platform's existing no-model
convention used by the seed and the workflow resolver.

**Alternatives considered**: A build argument for the checksum like the end-to-end test Analyst (rejected: every
caller must compute it; a wrong argument would only be detected by the Registry); a fake model digest (rejected:
would claim a model that does not exist).

## R9 - Build context and command

**Decision**: The documented build runs from the repository root:
`docker build -f src/analysts/capabilities/reference-media-probe/Dockerfile -t <reference> .`, with a
`Dockerfile.dockerignore` next to the Dockerfile that admits only the SDK and capability sources and lockfiles (no
virtual environments, caches, or tests). The end-to-end support code assembles a temporary context with the same
relative paths from the repository checkout and builds it with Testcontainers' Dockerfile builder, then pushes it to
the test registry exactly as `TestAnalystImage` does.

**Rationale**: Matches the repository-root build context of the platform images
([container-image contract](../20261018-092503-oci-images/contracts/container-images.md)) while staying compatible with
a builder that ignores Dockerfile-specific ignore files.

**Alternatives considered**: Context `src/analysts/` (rejected: contains the Manager's build outputs); vendoring the SDK
into the capability (rejected: two copies of the SDK).

## R10 - Reproducibility

**Decision**: Reproducibility means pinned and verified inputs (clarification): base image by digest, uv lock
installation without resolution, hash-verified toolchain archives, and no network access at run time. Byte-identical
image digests across builds are not claimed and stay open evidence.

**Rationale**: Bit-identical images need builder features (timestamp rewriting) that the classic builder used in tests
does not offer; the provenance goal of the architecture is met by pinned inputs plus the immutable digest of each
pushed image.

**Alternatives considered**: `SOURCE_DATE_EPOCH` with BuildKit timestamp rewriting (deferred to the release and
publication work that is out of scope).

## R11 - Capability tests

**Decision**: pytest tests in the project create synthetic media at test time with the environment's `ffmpeg`
(`testsrc2`, 64×64, native MPEG-4, 25 fps, keyframe every 25 frames, a few seconds), write segment manifests, analyst-job
1.2.0 messages with `segmentMedia`, and attempt contexts into a temporary mount root, and call the SDK's `run` with
that root. Cases: full coverage, keyframe context before the requested interval, partial coverage with a two-span
mapping gap, a last partial second, a second without frames (a 0.5 fps clip, one frame every two seconds), determinism (two runs, byte comparison of
`facts.json`), job without `segmentMedia` (exit 2), media digest mismatch (exit 2 from the SDK), undecodable media with
a correct digest (exit 1), the payload schema, the manifest schema and labels, and the Dockerfile pins. Toolchain-
dependent tests fail visibly when `ffmpeg` or `ffprobe` is missing. A Linux-only test, skipped when Docker is not
available, is not added: image behavior is proven by the end-to-end test.

**Rationale**: Covers FR-004 to FR-008 and FR-016 in seconds through the real SDK, as the environment's verification
runs it (SC-003, SC-005).

**Alternatives considered**: Committed binary fixtures (rejected, as in the segment feature); mocking ffprobe
(rejected except for unparsable-output injection).

## R12 - End-to-end evidence

**Decision**: `ReferenceAnalystEndToEndTests` in `SocAlytics.Platform.EndToEnd.Tests` starts the harness with the OCI
registry fixture, builds and pushes the reference image (`Support/ReferenceAnalystImage.cs`), registers it, creates and
approves the `reference-media-probe-dev` profile (`videoPolicy: required`, zero model digest, values meeting the
manifest's requirements), defines a workflow with one segment node and the materialized-segments feature's 5-second
test segmentation policy and makes it the default, starts a Docker-runtime Manager, uploads and finalizes the
10-second synthetic recording of that feature's `SyntheticMedia` support, waits for the run to complete, and reads
`listAnalysisRunFactStreams` and `listAnalysisRunFacts` as a member with team read access. It asserts two accepted
segment results on the first attempt, ten facts with ranges `[k·1000, (k+1)·1000)` for `k = 0..9` and
`decodedFrames` equal to the synthetic frame rate, `meanLuma` values within `[0, 255]` with `min ≤ mean ≤ max`, full
`accepted` coverage, and no remaining container, attempt directory, or staged media.

**Rationale**: Proves FR-013, FR-014, FR-018, and SC-001, SC-002, SC-004 with the existing harness and the segment
feature's synthetic media, without changing platform or Manager code (FR-019).

**Alternatives considered**: Extending `SegmentMediaEndToEndTests` (rejected: mixes the test Analyst's `media` mode with
the reference capability); comparing luma values exactly in C# (rejected by clarification).

## R13 - Local run procedure

**Decision**: The capability README documents the local procedure: run a local OCI registry container, configure the
API's `Registry:Images` options for it (host, repository prefix, HTTP), build and push the image, register it and
approve the profile through the Registry operations (Web UI Registry administration where available, or the API),
define and select a workflow with the reference node and a short segmentation policy, and run a Manager with
`Runtime:Profile` `docker-desktop`. No AppHost, seed, or configuration change is added.

**Rationale**: Clarification answer for FR-014 and US4; keeps the configured seed unchanged.

**Alternatives considered**: An AppHost resource that builds and registers the image at start (rejected: slows every
local start and adds tooling to the AppHost for a reference).
