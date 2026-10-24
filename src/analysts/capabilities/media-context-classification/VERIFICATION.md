# Verification

Final verification of the `media-context-classification` 1.0.0 capability (quickstart.md of its spec).

- `uv sync --locked --all-groups --no-install-local --no-build`, `uv sync --locked --all-groups --no-build-isolation`, and `uv build --wheel --no-build-isolation --python .venv/bin/python` in this folder: passed.
- `uv run --no-sync pytest` in this folder: 160 passed.
- The same locked sequence in `src/analysts/capabilities/reference-media-probe`: passed; `uv run --no-sync pytest`: 64 passed (unchanged).
- `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundary"` on a Linux engine: 2 passed, 0 failed.
- `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ReferenceAnalyst"` on a Linux engine: 5 passed, 0 failed.
- `node .github/scripts/check-markdown.mjs`: 0 issues in 0 files.
