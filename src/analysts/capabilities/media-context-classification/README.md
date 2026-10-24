# Media Context Classification (Shot-Boundary Baseline)

A development Analyst capability (`media-context-classification` 1.0.0, distribution
`socalytics-media-context-classification`, package `media_context_classification`) built on the
[Python Analyst SDK](../../sdk/README.md). It decodes the staged segment media of one job and writes one
`shot-boundary` fact for every shot boundary it detects, using fixed rules without any model.

## Status

This is development evidence only. It is not production ready and is never published. It is not a catalog capability
that meets the quality gates of the
[Analyst Capability Catalog](../../../../docs/architecture/analyst-capability-catalog.md): the catalog's quality gates
and the independent dependency audit are not met. The detection settings are not calibrated, and no accuracy claim is
made beyond the synthetic fixtures the tests generate. It has no model (it declares the platform's no-model digest)
and needs no network, environment variables, or credentials. No seed, Registry rule, or production value names it.

## Detection settings and limitations

The settings are declared in `src/media_context_classification/settings.py` and recorded in every fact's `settings`
provenance.

| Setting | Value |
| ------- | ----- |
| Detector version | `1.0.0` |
| Analysis frame size | 64 x 36 pixels, area-scaled, from every frame of the first video stream |
| Change measure | mean absolute HSV difference of consecutive frames, 0 to 255, three decimals |
| Hard cut | change measure of at least `30.0` |
| Adaptive cut | change measure of at least `8.0` and at least `3.0` times the mean of the surrounding measures (window of 4) |
| Rule order | hard cut first, adaptive cut second; one frame pair yields at most one fact |
| Minimum shot length | `500` ms; a boundary closer than that to the previous reported boundary is not reported |
| Confidence | `0.5` (rule-based, uncalibrated) |

Limitations of the baseline:

- **Segment-edge cuts**: a cut between the last frame of one segment and the first frame of the next is detected by
  neither segment. The first in-window frame of a segment without a predecessor yields no pair.
- **Fades and dissolves** are not detected as such; an adaptive cut may fire at one frame pair of a gradual change.
- **Flashes** are not detected as such and can produce one or two boundaries.
- **Minimum shot length**: boundaries closer than 500 ms are dropped, so very fast cut sequences are under-reported.
- Frames before the requested interval, in unmapped time, or outside the nominal window are never reported; a cut
  between the last lead-in frame and the first in-window frame is reported.
- A segment with fewer than two usable frames, or without a boundary, completes with no facts.

## Facts

One fact per detected boundary, in `startMs` order, byte-identical for identical staged media:

| Field | Value |
| ----- | ----- |
| `kind` | `shot-boundary` |
| `startMs` | match time of the first frame of the new shot, inside the segment's nominal half-open window |
| `endMs` | match time of the next in-window frame, or the nominal window end for the last frame; greater than `startMs` and never beyond the window end |
| `confidence` | `0.5` |
| payload | `rule` (`hard-cut` or `adaptive-cut`), `changeMeasure`, `hardThreshold`, `frameIndex`, `previousShotLastFrameMs` (integer or null), `settings`; adaptive cuts add `adaptiveFloor`, `adaptiveRatio`, and `contextMean` |

The payload is described by [schemas/shot-boundary.payload.schema.json](schemas/shot-boundary.payload.schema.json),
which the project's tests validate with `jsonschema`. The `1.0.0` manifest (`analyst-manifest.json`) declares no
payload schema. The full contract is in the
[shot-boundary fact contract](../../../../specs/archive/20261023-022148-shot-boundary-analyst/contracts/shot-boundary-fact.md).

## Failure reasons

The attempt ends as failed and no result is written for:

| Reason | When |
| ------ | ---- |
| `segment-media-required` | the job has no staged segment media |
| `media-decode-failed` | a tool cannot start or exits non-zero, the media is undecodable, or timestamps and frames differ in count |
| `media-no-video-stream` | the staged file has no video stream |
| `timestamp-unmappable` | a frame in the requested interval cannot be mapped to the segment's window |

Every other failure category comes from the SDK.

## Tests

The tests need CPython 3.12, `uv`, and the pinned `ffmpeg` and `ffprobe` on `PATH`. From this directory (see
[quickstart.md](../../../../specs/archive/20261023-022148-shot-boundary-analyst/quickstart.md)):

```text
UV_PYTHON_DOWNLOADS=never
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
```

## Audit evidence

The `audit/` folder holds the dependency and license evidence of the capability, generated without network access:

- `audit/dependency-audit.json` has one record per entry with `name`, `version`, `license` (an SPDX expression),
  `scope`, and `source`. The scope is `runtime` or `dev` for every package of `uv.lock`, `bundled-library` for the
  shared libraries bundled inside installed runtime wheels (OpenCV's FFmpeg libraries among them; each names its
  parent wheel), or `toolchain` for the pinned FFmpeg build (`ffmpeg` and `ffprobe`).
- `audit/sbom.cdx.json` is a CycloneDX 1.5 document of the same components plus the base image reference by digest.
  Operating-system packages of the base image are identified only by that digest and are not enumerated.

Regenerate both files after any change to `uv.lock`, the Dockerfile's FFmpeg or base image pins, or the toolchain pin
in `.github/actions/environment-setup/analyst-media-toolchain.json`. From this directory, with the locked
environment installed:

```text
uv run --no-sync python scripts/generate_audit.py
uv run --no-sync python scripts/generate_audit.py --check
```

`--check` writes nothing and exits non-zero when a file is stale. The output is deterministic (no timestamps). Licenses
come from installed distribution metadata; the few packages without usable metadata are listed in the script's
`LICENSE_OVERRIDES`, and a package without a license mapping stops the script instead of being guessed.

`tests/test_audit.py` verifies the evidence against `uv.lock`: every locked package has a record of the same name and
version, every license is in the allowed set, the SBOM matches the audit, and no model weight file exists in the
project. A missing or differing record and a disallowed license fail the verification. The allowed set is:

- permissive licenses (MIT, ISC, 0BSD, BSD-2-Clause, BSD-3-Clause, Apache-2.0, PSF-2.0, Zlib, CC0-1.0, Libpng, and
  OpenSSL);
- LGPL licenses only for separately packaged shared libraries (`bundled-library`) and the `toolchain` FFmpeg build;
- `GPL-3.0-with-GCC-exception` only for bundled GCC runtime libraries that name their parent wheel;
- MPL-2.0 only for a `dev` package that is never shipped in the image.

Any other GPL or AGPL expression and any unknown or missing license fail. The image test
(`ShotBoundaryImageTests`) compares the Python distributions installed in the image with `audit/dependency-audit.json`.

This evidence starts the Analyst Capability Catalog's independent license and provenance audit but does not complete
it: it is generated by the capability's own tooling from its lock and metadata, not performed or reviewed by an
independent party, and it does not cover operating-system packages, model provenance, or production approval.

## Image

From the repository root (a Linux engine is required; the image is `linux/amd64`):

```text
docker build -f src/analysts/capabilities/media-context-classification/Dockerfile -t media-context-classification:local .
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundaryImage"
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundaryEndToEnd"
```

The build takes no build arguments; the `ARG` defaults are the pins and must not be overridden for registered images.
The `python:3.12-slim-bookworm` base image, the `uv` and FFmpeg archives, and the locked packages of `uv.lock` are
pinned, and the SHA-256 of `analyst-manifest.json` must equal the literal `io.socalytics.analyst-manifest.sha256`
label value. Byte-identical rebuilds are not claimed; each pushed image is identified by its immutable digest.

## Registration procedure for a Development and Test stamp

This procedure uses only existing Registry operations. It adds no seed, Registry rule, or configuration to the
repository.

1. **Build the image** as shown above.
2. **Push it inside the stamp's registry host and repository prefix**, the `Registry:Images` options of the API
   (`RegistryHost`, `RepositoryPrefix`; `UseHttp` only in Development and Test), and note the digest the push prints:

   ```text
   docker tag media-context-classification:local <registry host>/<repository prefix>/media-context-classification:local
   docker push <registry host>/<repository prefix>/media-context-classification:local
   ```

3. **Register the image by digest** as a Club Admin (`registerAnalystImage`, `POST /api/v1/registry/analyst-images`,
   or the Web UI's Registry administration) with the reference
   `<registry host>/<repository prefix>/media-context-classification@sha256:<digest>`.
4. **Create and approve the profile `media-context-classification-dev`** (`createAnalystProfile`, then
   `approveAnalystProfile`) for that image digest with this content (the limits are at least the manifest's
   requirements):

   ```json
   {
     "capabilityId": "media-context-classification",
     "capabilityVersion": "1.0.0",
     "modelDigest": "sha-256:0000000000000000000000000000000000000000000000000000000000000000",
     "runtimeConstraints": ["linux-amd64"],
     "accelerators": [],
     "resources": { "memory": "2Gi", "cpu": "2" },
     "maxAttempts": 3,
     "leaseSeconds": 60,
     "staleTimeoutSeconds": 120,
     "executionTimeoutSeconds": 900,
     "videoPolicy": "required",
     "inputSchemaVersions": {},
     "outputSchemaVersion": "1.0.0"
   }
   ```

5. **Define a workflow with a segment node** (`defineWorkflow`): `requestedCapabilities`
   `["media-context-classification"]`, `completionPolicy` `all-requested-capabilities`, a short `segmentationPolicy`
   such as `{ "version": 1, "durationSeconds": 5, "digest": "sha-256:<64 hex digits>" }`, one node
   `{ "nodeKey": "shots", "profileId": "media-context-classification-dev", "profileVersion": <version> }`, and no
   edges.
6. **Set it as the default workflow** (`setDefaultWorkflow`).
7. **Run a Manager** with `Runtime:Profile` `docker-desktop` (see the [Analyst Manager README](../../manager/README.md)),
   registered with the stamp and approved by a Club Admin, then upload and finalize a recording for a match.
8. **Read the facts** after the run completes with `listAnalysisRunFactStreams` and `listAnalysisRunFacts` (or the
   Web UI's analysis review): `shot-boundary` facts at the match times of the first frame of each new shot, and an
   empty stream for a segment without a boundary.

The platform's Segment Service needs `ffmpeg` and `ffprobe` of the pinned toolchain found through
`Segments:Toolchain` (the AppHost reads `SOCALYTICS_FFMPEG_PATH` and `SOCALYTICS_FFPROBE_PATH`).
