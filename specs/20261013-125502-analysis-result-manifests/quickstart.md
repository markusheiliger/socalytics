# Quickstart: Validate Analysis Result Manifests and Validated Acceptance

Development evidence only. This does not show deployment support or production
readiness.

## Prerequisites

- Analyst Manager Work Execution (`specs/archive/20261012-141233-manager-work-execution`)
  merged, with its end-to-end test project.
- The .NET SDK from `src/platform/global.json`, a running Docker-compatible
  container runtime, and SoftHSM2 on Linux for the Manager tests (all provided
  by `environment-setup`).

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build   # contracts, grants, acceptance checks, migration, end-to-end
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build   # ResultPublisher, simulated modes, fixtures
```

| Suite | Proves |
| --- | --- |
| `SocAlytics.Platform.Contracts.Tests` | [result-manifest.md](contracts/result-manifest.md) and [result-grants-api.md](contracts/result-grants-api.md) examples (SC-006) |
| `SocAlytics.Platform.Integration.Tests` | Grants and refusals (SC-004), one test per acceptance check (SC-002), storage conformance, idempotent completions (SC-005), audit without secrets (SC-007) |
| `SocAlytics.Platform.EndToEnd.Tests` | The development workflow with the simulated Analyst writes manifests through grants and both nodes are accepted; a lineage mismatch fails and retries; a contract-invalid manifest uploads nothing (SC-001, SC-003) |
| Manager tests | Contract check before grants, grant renewal, refusal stops the attempt, the simulated modes |

## Manual exploration

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost`; register and
   run a Manager with the simulated Analyst (Development) as in the Work
   Execution quickstart.
2. Finalize a recording set; in the RustFS console, find
   `results/<run>/<job>/<attempt>/manifest.json` and the artifact.
3. Read the run with `GET /api/v1/analysis/runs/{runId}` and see the jobs
   completed; configure `lineage-mismatch` for the match capability and see its
   attempt fail with `result-lineage-mismatch` and retry.
