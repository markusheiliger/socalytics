# Quickstart: Validate the Push Digest Parsing

## Prerequisites

- .NET 10 SDK from `src/platform/global.json`; run from the repository root.
- For the Docker-backed step: a Linux Docker engine at `/var/run/docker.sock`.

## 1. Parsing tests (no Docker)

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~PushOutputTests"
```

Expected: all cases pass for the `aux` style, the status-line style, both together (`aux` wins), an `errorDetail` stream (with and without a digest line), non-matching `sha256:` lines, wrong-length and uppercase hex, stray non-JSON lines, and a stream without a digest. The rules are in [data-model.md](data-model.md).

## 2. Docker-backed evidence (Linux engine)

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundaryImage|FullyQualifiedName~ReferenceAnalystImage"
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests
```

Expected: no test fails with `The push did not report a manifest digest`.

## 3. Scope check

```bash
git diff --name-only
```

Expected: only files of this spec folder, `TestAnalystImage.cs`, and `PushOutputTests.cs`.
