# Quickstart: Docker Push Digest from the Status Line

From the repository root, with the .NET SDK selected by `src/platform/global.json`.

## 1. Parsing tests (no Docker)

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~PushDigestReaderTests"
```

Expected: all pass in under 5 seconds in total (SC-001), covering aux only, status line only, both, neither, error entry, and malformed digests ([data-model.md](data-model.md)).

## 2. Probes (temporary)

Temporarily make the helper ignore the status line, run step 1, and expect failing assertions; restore it. Temporarily accept a 63-character digest, run step 1, and expect failing assertions; restore it (SC-003). Confirm with `git diff` that no probe remains.

## 3. Docker-backed image tests (Linux engine)

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ReferenceAnalystImage|FullyQualifiedName~ShotBoundaryImage"
```

Expected: both pass, and no End-to-End test fails with `The push did not report a manifest digest` (SC-002). The SoftHSM2 and Docker prerequisites are those of the End-to-End project.

## 4. Scope check

`git diff --name-only` lists only files under `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/` and this spec folder (SC-004). Results are development evidence only, not deployment support or production readiness.
