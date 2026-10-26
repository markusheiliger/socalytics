# Payload Conformance Evidence

## Evidence

### T001 Baseline

- Docker: available (`docker info` succeeded).
- `dotnet restore src/platform/SocAlytics.Platform.slnx`: succeeded.
- `dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests`: succeeded, 0 warnings, 0 errors (26 s).
- `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~PayloadConformanceTests"`: Passed 2, Failed 0, Skipped 0, Total 2; test duration 8 s (wall clock 35 s including startup).

### T002 Repeated runs

### T003 Premature read

### T004 Staggered reads

### T005 Shared state and production code
