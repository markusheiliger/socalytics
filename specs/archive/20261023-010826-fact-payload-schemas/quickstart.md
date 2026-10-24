# Quickstart: Validating Capability Fact Payload Schemas

Run from the repository root. Docker with a Linux engine is needed for items 3 and 4; Python 3.12, `uv`, and the pinned
`ffmpeg` and `ffprobe` for item 2. Contracts and data: see `contracts/payload-schema-subset.md` and `data-model.md`.

1. **Contract and platform checks** (no Docker for the contract tests):

   ```bash
   dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
   dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~FactPayload|FullyQualifiedName~AnalystImageImport"
   ```

   Expect: manifest `1.2.0` examples and the shared payload cases give the expected verdicts; earlier baselines are
   byte-identical; an image with a conforming result is accepted and its facts read back identical; a result with one
   bad payload among many ends `failed` with `result-payload-invalid` and exposes no fact; a result with an undeclared
   kind ends `failed` with `result-payload-kind-undeclared`; a run created before a newer registration keeps its schemas.

2. **SDK** (from `src/analysts/sdk`, the locked sequence of the repository guidelines):

   ```bash
   uv run --no-sync pytest
   uv run --no-sync python scripts/sync_contracts.py --check
   ```

   Expect: the shared payload cases give the platform's verdicts; a nonconforming fact raises `fact-payload-invalid`
   and no result is written; a manifest without schemas behaves as before.

3. **Reference capability** (from `src/analysts/capabilities/reference-media-probe`, locked sequence):

   ```bash
   uv run --no-sync pytest
   ```

   Expect: the 1.1.0 manifest validates against manifest `1.2.0`, its schema equals the schema file, and 1.0.0 files are
   unchanged.

4. **Docker-backed end to end**:

   ```bash
   dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ReferenceAnalyst"
   ```

   Expect: 1.0.0 evidence unchanged and passing; 1.1.0 accepted with read-back payloads equal to the written ones; the
   derived stricter-schema image ends `failed` with `result-payload-invalid` and exposes no fact.

5. **Repository checks**: `node .github/scripts/check-markdown.mjs`.
