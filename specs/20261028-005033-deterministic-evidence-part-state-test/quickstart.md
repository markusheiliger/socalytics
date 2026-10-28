# Quickstart: Validating the Deterministic Evidence Part State Test

Prerequisites: .NET 10 SDK from `src/platform/global.json`, a running Docker-compatible runtime (PostgreSQL and RustFS containers), the pinned `ffmpeg` and `ffprobe` on `PATH`, a restored and built solution.

## 1. Build

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
```

## 2. The evidence classes pass

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~Analysis.Evidence"
```

Expected: all tests pass, including `Never_materialized_segment_is_materializing_then_available_with_a_link_after_the_worker_ran`.

## 3. Repeated runs (SC-001)

Run the filter `FullyQualifiedName~Never_materialized_segment_is_materializing` at least 20 times in a row, and the class filter at least 5 times in a row while the machine is under CPU load: N concurrent busy loops with N equal to the core count (for example `stress-ng --cpu $(nproc)`), or the same number of parallel `dotnet test` runs. Expected: no failure.

## 4. Probes (SC-002, temporary, removed afterward)

- The before-fix reproduction is the T003 result recorded in `research.md`. After the fix, optionally re-enable the real worker in a temporary run through `extraSettings` (`Analysis:Workers:Enabled=true` with a short `Segments:Materialization:PollInterval`) as a still-reproduces check. The default scenario (workers off) passes the same probe-free run 20 times.
- Break demand recording or report `available` before materialization: expect the test to fail on its unchanged assertions.

Confirm with `git diff` that no probe remains and no assertion is removed.

## 5. Neighbors and repository checks

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~Segments"
node .github/scripts/check-markdown.mjs
```

Expected: the Segment Service tests and the other evidence tests pass unchanged.
