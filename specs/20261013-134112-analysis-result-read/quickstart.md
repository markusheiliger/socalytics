# Quickstart: Validate the Analysis Result Read for Jobs

Development evidence only. This does not show deployment support or production
readiness.

## Prerequisites

- Analysis Result Manifests and Validated Acceptance
  (`specs/20261013-125502-analysis-result-manifests`) and Analyst Manager Work
  Execution (`specs/archive/20261012-141233-manager-work-execution`) merged.
- The .NET SDK from `src/platform/global.json`, a running Docker-compatible
  container runtime, and SoftHSM2 on Linux (all provided by `environment-setup`).

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
```

| Suite | Proves |
| --- | --- |
| `SocAlytics.Platform.Contracts.Tests` | The upstream-results envelope and its examples (SC-006) |
| `SocAlytics.Platform.Integration.Tests` | Snapshot-bound results and coverage (SC-003), paging, refusals without disclosure (SC-002), lease-bounded grants (SC-004), audit without grant URLs ([upstream-results-api.md](contracts/upstream-results-api.md)) |
| Manager tests | Staging, digest and limit refusals without launch (SC-005), cleanup at end and start (SC-007), [staged-inputs.md](contracts/staged-inputs.md) layout |
| `SocAlytics.Platform.EndToEnd.Tests` | The match node stages segment results and its accepted lineage equals them (SC-001); `ignore-inputs` is rejected for lineage (FR-012) |

## Manual exploration

1. Run the AppHost and a Manager with the simulated Analyst (Development), as
   in the Work Execution quickstart.
2. Finalize a recording set; while the match-level attempt runs, inspect the
   Manager's attempt directory `inputs/` and its `coverage.json`.
3. After completion, the attempt directory is gone and
   `GET /api/v1/analysis/runs/{runId}` shows both nodes completed.
