# Quickstart: Reference Analyst Container on the Python SDK

Validation guide for the implemented feature. Details: [data-model.md](data-model.md),
[contracts/reference-media-probe.md](contracts/reference-media-probe.md), [research.md](research.md).

## Prerequisites

- The environment of [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md):
  CPython 3.12.15, `uv` 0.13.1, and the pinned FFmpeg and ffprobe on `PATH` (on Linux CI from `environment-setup`;
  locally any CPython 3.12 with `uv` and that FFmpeg build).
- The merged [Python Analyst SDK](../20261017-220709-python-analyst-sdk/spec.md),
  [Materialized Video Segments](../20261017-222433-materialized-segments/spec.md), and
  [Fact Indexing](../20261017-175648-fact-indexing-queries/spec.md).
- The .NET 10 SDK and a Docker-compatible runtime with a Linux engine (see `AGENTS.md`); Linux for the end-to-end test,
  as for the existing Docker end-to-end tests.

## 1. Capability project: locked build and tests

From `src/analysts/capabilities/reference-media-probe/`, the environment's command sequence:

```text
UV_PYTHON_DOWNLOADS=never
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
```

Expected: every test passes, including one fact per covered second with the expected frame counts, no facts from
keyframe context or unmapped time, clipped partial seconds, a zero-frame second without `meanLuma`, byte-identical
`facts.json` for two runs, exit 2 without segment media or with a media digest mismatch, exit 1 for undecodable media,
the payload schema, the manifest schema and labels, and the Dockerfile pins equal to the environment's toolchain JSON.

## 2. Image build and inspection

From the repository root:

```text
docker build -f src/analysts/capabilities/reference-media-probe/Dockerfile -t reference-media-probe:local .
docker image inspect reference-media-probe:local
```

Expected: the build succeeds without build arguments; the labels equal the manifest; the configured user is non-root;
the last layer holds only `opt/socalytics/analyst-manifest.json`. Changing one hash `ARG` default or one manifest byte
without updating the label makes the build fail with a message naming the input.

## 3. End to end

```powershell
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build --filter ReferenceAnalyst
```

Expected: the image builds and registers by digest; the 10-second synthetic recording under the 5-second test policy
produces two accepted results on their first attempts; the member fact read returns ten `frame-second-probe` facts with
ranges `[k·1000, (k+1)·1000)`, `decodedFrames` equal to the synthetic frame rate, and `meanLuma` within `[0, 255]`;
coverage is fully accepted; no container, attempt directory, or staged media remains.

## 4. Local run (manual)

Follow the capability README's local procedure: start a local OCI registry, point the API's `Registry:Images` options
at it, build and push the image, register it, approve `reference-media-probe-dev`, define and select a workflow with a
short segmentation policy, run a Manager with `Runtime:Profile` `docker-desktop`, upload and finalize a short
recording, and read the facts. Expected: accepted results and readable facts, as in step 3.
