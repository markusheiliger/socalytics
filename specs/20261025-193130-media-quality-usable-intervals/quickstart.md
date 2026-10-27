# Quickstart: Media Quality and Usable Intervals

Validation guide only; see [data-model.md](data-model.md), [contracts/](contracts/), and [research.md](research.md).

## Prerequisites

CPython 3.12, `uv`, and the pinned `ffmpeg` and `ffprobe` on `PATH`; for the image checks a Linux Docker engine.

## 1. Project tests (no Docker)

From `src/analysts/capabilities/media-context-classification`:

```text
UV_PYTHON_DOWNLOADS=never
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
uv run --no-sync python scripts/generate_audit.py --check
```

Expected: every test passes, including the synthetic defect clips, the interval partition properties, the
shot-boundary equivalence to the baseline, determinism, the payload schema and manifest equality tests, and the
dependency and license audit.

## 2. Image and end-to-end evidence (Linux engine)

From the repository root:

```text
docker build -f src/analysts/capabilities/media-context-classification/Dockerfile -t media-context-classification:local .
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundaryImage"
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundaryEndToEnd"
```

Expected: labels match the `1.2.0` manifest, no model weights, the installed distributions equal the audit, and the
run over a synthetic recording with known defects and cuts yields exactly the expected `shot-boundary`,
`media-quality`, and `usable-interval` facts through the member fact reads (SC-001 to SC-003, SC-007, SC-008).

## 3. Registration

Follow the README's registration procedure with capability version `1.1.0` and output schema version `1.1.0`; the
import's cached declaration names the three payload schemas, and runs pinned to `1.0.0` are unaffected.

## 4. Regression

```text
node .github/scripts/check-markdown.mjs
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ReferenceAnalyst"
```
