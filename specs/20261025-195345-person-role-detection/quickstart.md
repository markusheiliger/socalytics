# Quickstart: Validating the Person and Role Detection Analyst

Development evidence only. Run every command from the repository root. Contract and data details are in
[contracts/person-role-detection-fact.md](contracts/person-role-detection-fact.md) and [data-model.md](data-model.md).

## Prerequisites

- Python 3.12, `uv`, and the pinned `ffmpeg` and `ffprobe` on `PATH`; the pinned CPU OpenCV, NumPy, and ONNX Runtime
  wheels from the [CV and Inference Verification Environment](../20261023-085421-cv-inference-environment/spec.md), which
  must be implemented first.
- Docker-compatible engine on Linux, for the image and end-to-end checks; .NET 10 SDK and SoftHSM2 for the end-to-end test.

## 1. Unit and audit evidence (no Docker)

```bash
cd src/analysts/capabilities/person-role-detection
export UV_PYTHON_DOWNLOADS=never
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
```

Expected: all tests pass: letterbox remap across resolutions, confidence floor, merge, per-frame maximum, sampling with a
non-zero offset, empty and single-frame media, determinism across runs and thread counts, schema conformance, every named
failure reason, and the audit (every locked package and the model have records with an allowed licence). Tests that need the
real model skip with a stated reason when the file is absent; set `PERSON_ROLE_MODEL_PATH` to run them.

## 2. Image evidence (Docker)

```bash
docker build -f src/analysts/capabilities/person-role-detection/Dockerfile -t person-role-detection:local .
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~PersonRoleDetectionImage"
```

Expected: labels equal the manifest, the model digest label equals the digest of the model file, the only data file beyond
the manifest is the model, the image completes a job as a non-root user on a read-only root file system without network,
and a one-minute 1080p segment stays within 2 GiB and the 300-second timeout.

## 3. End to end (Docker, Linux engine)

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~PersonRoleDetectionEndToEnd"
```

Expected: the test registers the image by digest, approves a profile, defines and defaults a workflow, finalizes a
three-segment synthetic recording of rendered figures, runs it, and reads `person-role-detection` facts through the member
fact operations: recall of at least 0.6 at IoU 0.5 over the known figures, every fact valid and inside its segment's window,
the empty stretch analyzed with an empty fact stream, and four schema-violating variants refused at acceptance.

## 4. Registration procedure for a Development and Test stamp

Follow the numbered procedure in the capability README: build and push the image inside the stamp's registry host and
prefix, register by digest, approve the profile `person-role-detection-dev`, define the workflow, set it as default. No seed
and no manual data edit are involved.

## 5. Regression and documentation

```bash
cd src/analysts/capabilities/media-context-classification && uv run --no-sync pytest
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundary|FullyQualifiedName~ReferenceAnalyst"
node .github/scripts/check-markdown.mjs
```

Expected: existing capabilities and their evidence are unchanged and pass; Markdown validation passes.
