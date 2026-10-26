# Payload Conformance Evidence

## Evidence

### T001 Baseline

- Docker: available (`docker info` succeeded).
- `dotnet restore src/platform/SocAlytics.Platform.slnx`: succeeded.
- `dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests`: succeeded, 0 warnings, 0 errors (26 s).
- `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~PayloadConformanceTests"`: Passed 2, Failed 0, Skipped 0, Total 2; test duration 8 s (wall clock 35 s including startup).

### T002 Repeated runs

Unchanged class, `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~PayloadConformanceTests"`, Docker available, 4 cores.

- Alone: 30 runs, 30 passed (2 of 2 tests each), 0 failed, 0 skipped; test duration 9 s (28 runs) or 10 s (2 runs); wall clock 19-20 s per run. No failing assertion lines.
- Under CPU load (one busy-loop process per core, 4 processes, load average rose to about 11): 10 runs, 10 passed, 0 failed, 0 skipped; test duration 15-18 s; wall clock 32-34 s per run. No failing assertion lines. The load processes were stopped afterward.
- Result: the failure did not reproduce in 40 runs in this environment. The observed maximum test duration is 18 s (loaded), the basis for the hang-guard timeout of T006 (at least ten times that, 180 s).

### T003 Premature read

### T004 Staggered reads

### T005 Shared state and production code
