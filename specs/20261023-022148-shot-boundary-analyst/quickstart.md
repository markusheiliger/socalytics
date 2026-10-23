# Quickstart: Validating the Shot-Boundary Analyst

Development evidence only. Run every command from the repository root. Contract and data details are in
[contracts/shot-boundary-fact.md](contracts/shot-boundary-fact.md) and [data-model.md](data-model.md).

## Prerequisites

- Python 3.12, `uv`, and the pinned `ffmpeg` and `ffprobe` on `PATH` (the environment actions provide them).
- Docker-compatible engine on Linux, for the image and end-to-end checks.
- .NET 10 SDK from `src/platform/global.json`; for the end-to-end test also SoftHSM2.

## 1. Unit and audit evidence (no Docker)

```bash
cd src/analysts/capabilities/media-context-classification
export UV_PYTHON_DOWNLOADS=never
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
```

Expected: all tests pass, covering hard cut, adaptive cut, no cut with noise, no cut with pan, closely spaced cuts,
single-frame and empty media, determinism across runs and CPU limits, non-zero segment offset mapping, lead-in frames,
every named failure reason, the payload schema, and the dependency audit (every locked package has a record and an
allowed license; a disallowed or missing one fails).

## 2. Image evidence (Docker)

```bash
docker build -f src/analysts/capabilities/media-context-classification/Dockerfile -t media-context-classification:local .
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundaryImage"
```

Expected: labels equal the manifest, the model digest is the no-model digest, no model weights are present, the image
completes a job as a non-root user on a read-only root file system without network, installed distributions match the
audit, and a one-minute 1080p segment stays at or below 2 GiB peak memory within the attempt lease.

## 3. End to end (Docker, Linux engine)

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundaryEndToEnd"
```

Expected: the test registers the image by digest, approves a profile, defines and defaults a workflow, finalizes a
synthetic recording with known cuts under a short segmentation policy, runs it, and reads exactly the known
`shot-boundary` facts (kinds, match-time positions, rules, count) through the member fact operations; a segment without
a cut is analyzed with an empty fact stream.

## 4. Registration procedure for a Development and Test stamp

Follow the numbered procedure in the capability README: build and push the image inside the stamp's registry host and
prefix, register by digest, approve the profile `media-context-classification-dev`, define the workflow, and set it as
default. No seed and no manual data edit are involved.

## 5. Regression and documentation

```bash
cd src/analysts/capabilities/reference-media-probe && uv run --no-sync pytest
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ReferenceAnalyst"
node .github/scripts/check-markdown.mjs
```

Expected: the reference capability and its evidence are unchanged and pass; Markdown validation passes.
