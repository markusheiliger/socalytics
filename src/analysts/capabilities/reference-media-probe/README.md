# Reference Media Probe

A development reference Analyst capability (`reference-media-probe` 1.0.0, distribution
`socalytics-reference-media-probe`, package `reference_media_probe`) built on the
[Python Analyst SDK](../../sdk/README.md). It decodes the staged segment media of one job and writes one small fact per
match-second, so that the whole analysis path (segment materialization, Manager staging, a restricted container, result
acceptance, and the fact read) can be exercised with a real Analyst container.

It is not a catalog capability: its id matches no entry of the
[Analyst Capability Catalog](../../../../docs/architecture/analyst-capability-catalog.md), and it is never published.
It is kept out of production stamps by convention only: no production profile, workflow, or registry configuration
names it, and nothing in the platform enforces that. It has no model (it declares the platform's no-model digest) and
needs no network, environment variables, or credentials.

## Facts

For every whole match-second `[k·1000, (k+1)·1000)` that overlaps the job's nominal window and the mapped match time of
the staged segment manifest, the Analyst writes one fact:

| Field | Value |
| --- | --- |
| `kind` | `frame-second-probe` |
| range | the smallest half-open interval covering the second's mapped part inside the nominal window |
| `confidence` | `1.0` |
| payload | `{ "decodedFrames": n }` and, when `n > 0`, `"meanLuma": { "min", "mean", "max" }` (8-bit luma, rounded half-even to three decimals) |

The payload is described by [schemas/frame-second-probe.payload.schema.json](schemas/frame-second-probe.payload.schema.json).
The `1.0.0` manifest (`analyst-manifest.json`) declares no payload schema.

## Payload-validated variant (1.1.0)

`reference-media-probe` `1.1.0` is a development variant that declares the schema above for `frame-second-probe` in
`analyst-manifest.payload-validated.json` (manifest `1.2.0`, `output.factPayloadSchemas`; a project test checks that the
declared schema equals the schema file). It is built from its own `Dockerfile.payload-validated`, whose
`io.socalytics.analyst-manifest.sha256` label must equal the SHA-256 of that manifest. From the repository root:

```text
docker build -f src/analysts/capabilities/reference-media-probe/Dockerfile.payload-validated -t reference-media-probe:payload-validated .
```

The `1.0.0` files and image are unchanged. The platform and the SDK validate the facts of this variant against the
declared schema; it is development evidence only and, like `1.0.0`, not a catalog capability.
Frames in keyframe context before the requested interval, in unmapped time, or outside the nominal window are ignored.
Two runs over the same job and media write byte-identical `facts.json`. A job without segment media exits 2
(`segment-media-required`); media that cannot be decoded exits 1 (`media-decode-failed`); every other failure category
comes from the SDK.

## Tests

The tests need CPython 3.12, `uv`, and the pinned `ffmpeg` and `ffprobe` on `PATH` (see the Analyst and media
verification environment). From this directory:

```text
UV_PYTHON_DOWNLOADS=never
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
```

## Image

From the repository root (a Linux engine is required; the image is `linux/amd64`):

```text
docker build -f src/analysts/capabilities/reference-media-probe/Dockerfile -t reference-media-probe:local .
```

The build takes no build arguments; the `ARG` defaults are the pins and must not be overridden for registered images.
Its pinned inputs are:

- the `python:3.12-slim-bookworm` base image, by digest;
- the `uv` and FFmpeg (LGPL build) archives, by URL and SHA-256, equal to
  `.github/actions/environment-setup/analyst-media-toolchain.json` (a project test checks this);
- the locked Python packages of `uv.lock`, installed without resolution;
- `analyst-manifest.json`, whose SHA-256 must equal the literal `io.socalytics.analyst-manifest.sha256` label value.

Open items: byte-identical rebuilds (identical image digests) are not claimed; reproducibility here means pinned and
verified inputs, and each pushed image is identified by its immutable digest. The FFmpeg publisher's release is not
immutable (its retention policy removes old releases), so the build fails with a named reason once the archive is gone,
and a new pin is an environment change.

## Local procedure

This procedure runs the capability against a local development stamp. It changes no AppHost, seed, or configuration of
the repository: the configured Registry seed stays unchanged.

1. **Start a local OCI registry**, for example `docker run -d --name socalytics-registry -p 5000:5000 registry:2`.
2. **Configure the API's `Registry:Images` options** for it: `RegistryHost` `localhost:5000`, a `RepositoryPrefix` such
   as `socalytics`, and `UseHttp` `true` (Development and Test only). Start the stamp (the AppHost) with those options,
   for example as `Registry__Images__RegistryHost`, `Registry__Images__RepositoryPrefix`, and
   `Registry__Images__UseHttp` environment variables of the API.
3. **Build and push the image**, then note the digest that the push prints:

   ```text
   docker build -f src/analysts/capabilities/reference-media-probe/Dockerfile -t localhost:5000/socalytics/reference-media-probe:local .
   docker push localhost:5000/socalytics/reference-media-probe:local
   ```

4. **Register the image by digest** as a Club Admin (`registerAnalystImage`, `POST /api/v1/registry/analyst-images`,
   or the Web UI's Registry administration) with the reference
   `localhost:5000/socalytics/reference-media-probe@sha256:<digest>`.
5. **Create and approve the profile `reference-media-probe-dev`** (`createAnalystProfile`, then `approveAnalystProfile`)
   for that image digest with this content (the limits are at least the manifest's requirements):

   ```json
   {
     "capabilityId": "reference-media-probe",
     "capabilityVersion": "1.0.0",
     "modelDigest": "sha-256:0000000000000000000000000000000000000000000000000000000000000000",
     "runtimeConstraints": ["linux-amd64"],
     "accelerators": [],
     "resources": { "memory": "512Mi", "cpu": "1" },
     "maxAttempts": 3,
     "leaseSeconds": 60,
     "staleTimeoutSeconds": 120,
     "executionTimeoutSeconds": 900,
     "videoPolicy": "required",
     "inputSchemaVersions": {},
     "outputSchemaVersion": "1.0.0"
   }
   ```

6. **Define and select a workflow** (`defineWorkflow`, then `setDefaultWorkflow`): `requestedCapabilities`
   `["reference-media-probe"]`, `completionPolicy` `all-requested-capabilities`, a short `segmentationPolicy` such as
   `{ "version": 1, "durationSeconds": 5, "digest": "sha-256:<64 hex digits>" }`, one node
   `{ "nodeKey": "probe", "profileId": "reference-media-probe-dev", "profileVersion": <version> }`, and no edges.
7. **Run a Manager** with `Runtime:Profile` `docker-desktop` (see the
   [Analyst Manager README](../../manager/README.md)), registered with the stamp and approved by a Club Admin. Its
   preflight must report the local Linux engine as supported, and it must be able to pull from the local registry
   through the credentials the platform issues.
8. **Upload and finalize a short recording** for a match (a few seconds of video, such as a 10-second clip, which
   yields two 5-second segments) through the Web UI's match workflow.
9. **Read the facts** after the run completes: the run's node status shows accepted segment results, and
   `listAnalysisRunFactStreams` and `listAnalysisRunFacts` (or the Web UI's analysis review) return one
   `frame-second-probe` fact per covered second with ranges `[k·1000, (k+1)·1000)`, `decodedFrames` equal to the
   recording's frame rate, and `meanLuma` within `[0, 255]`.

The platform's Segment Service needs `ffmpeg` and `ffprobe` of the pinned toolchain found through
`Segments:Toolchain` (the AppHost reads `SOCALYTICS_FFMPEG_PATH` and `SOCALYTICS_FFPROBE_PATH`).

## Validation Record

Observed on Linux (amd64) with CPython 3.12.15, `uv` 0.13.5, the pinned FFmpeg and ffprobe 8.1 on `PATH`, and Docker
Engine 28.0.4. It is development evidence only.

| Step | Command | Observed outcome |
| ---- | ------- | ---------------- |
| Capability tests | From this folder: `UV_PYTHON_DOWNLOADS=never`, `uv sync --locked --all-groups --no-install-local --no-build`, `uv sync --locked --all-groups --no-build-isolation`, `uv build --wheel --no-build-isolation --python .venv/bin/python`, `uv run --no-sync pytest` | The wheel `socalytics_reference_media_probe-1.0.0-py3-none-any.whl` built; 57 passed, 0 failed |
| Image build | `docker build -f src/analysts/capabilities/reference-media-probe/Dockerfile -t reference-media-probe:local .` | Built without build arguments |
| Image inspection | `docker image inspect reference-media-probe:local` | `linux/amd64`; user `65532:65532` (non-root); labels equal the manifest (`io.socalytics.analyst-manifest.sha256` `31c94a2a…56c5f` equals the SHA-256 of `analyst-manifest.json`, version `1.0.0`, capability `reference-media-probe` `1.0.0`) |
| Last layer | `docker save` and listing the last layer of `manifest.json` | The last layer holds only `opt/socalytics/analyst-manifest.json` |
| End to end | `dotnet build src/platform/SocAlytics.Platform.slnx`, then `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build --filter ReferenceAnalyst` | Build with 0 errors; 2 passed, 0 failed, 0 skipped (`ReferenceAnalystEndToEndTests` and `ReferenceAnalystImageTests`) |
| SDK tests | From `src/analysts/sdk`: the locked sync sequence, then `uv run --no-sync pytest` | 306 passed, 0 failed |
| Platform solution tests | `dotnet test src/platform/SocAlytics.Platform.slnx --no-build` | Not run for this record: the full solution suite is run by the verification that follows the task, so no outcome is recorded here |
