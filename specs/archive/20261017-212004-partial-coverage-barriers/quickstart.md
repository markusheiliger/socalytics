# Quickstart: Partial-Coverage Match Barriers

Validation guide for the feature. Prerequisites: the .NET 10 SDK, a running Docker-compatible container runtime, and
on Linux SoftHSM2 for the end-to-end tests (all provided by the environment actions).

## 1. Build

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
```

## 2. Contracts

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
```

Expected: the `1.1.0` schemas of `analyst-manifest`, `upstream-results`, and `result-manifest` validate their new
examples, stay compatible with their `1.0.0` baselines, and the new invalid examples are rejected
([contracts/analysis-contracts.md](contracts/analysis-contracts.md)).

## 3. Readiness, Registry, reads, and acceptance

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~Analysis|FullyQualifiedName~Registry"
```

Expected: partial barriers wait for final upstream jobs, become ready at or above the minimum and blocked below it,
record coverage in the input snapshot ([data-model.md](data-model.md)); the upstream read reports snapshot coverage;
coverage reports equal to the snapshot are accepted and others are rejected with `result-coverage-mismatch`; invalid
declarations are refused as `manifest-invalid` with the validation error `partial-coverage`; complete-coverage tests pass unchanged.

## 4. Run status read

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build
```

Expected: the committed OpenAPI snapshot contains `inputCoverage`
([contracts/run-status-input-coverage.md](contracts/run-status-input-coverage.md)).

## 5. Manager staging and simulated Analyst

```powershell
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
```

Expected: `coverage.json` carries the coverage mode; the simulated Analyst fails only configured segments, reports the
staged coverage, and its `coverage-mismatch` and `coverage-omitted` modes produce the deliberate violations.

## 6. End to end

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build
```

Expected: in the development workflow with the segment starting at `0` failing, `dev-match-node` is blocked,
`dev-partial-match-node` is accepted with coverage naming the failed segment, the run ends `PartiallyCompleted`, and
the run status read shows the partial node's input coverage (one failed, the rest accepted).

## 7. Local exploration (optional)

```powershell
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

With the development seed, a run has three nodes; configure the Manager's `Runtime:Simulated` capability options
(`FailSegmentStartsMs`) to fail one segment and read the run status through the Web UI or the API.
