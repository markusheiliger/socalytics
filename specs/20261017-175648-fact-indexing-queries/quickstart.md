# Quickstart: Accepted Fact Indexing and Capability-Scoped Fact Queries

Validation guide for the feature. Contracts: [contracts/](contracts/). Model:
[data-model.md](data-model.md).

## Prerequisites

- .NET 10 SDK (`src/platform/global.json`), a running Docker-compatible
  runtime, and on Linux SoftHSM2 for the Manager and end-to-end tests.
- Run every command from the repository root.

## 1. Build

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
```

## 2. Contract

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
```

Expected: the `fact-artifact` v1 schema, its released copy, its examples, the
index row in `contracts/README.md`, and the OpenAPI document pass.

## 3. Indexing at acceptance

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~Analysis.Results|FullyQualifiedName~Analysis.Facts|FullyQualifiedName~Analysis.Domain"
```

Expected:

- an accepted result's facts are stored exactly once with result, run, node,
  team, and match identity (SC-001);
- contract-invalid, header-mismatched, out-of-window, count-mismatched, and
  oversized fact artifacts fail the attempt with `result-facts-invalid`,
  `result-facts-out-of-window`, `result-facts-count-mismatch`, or
  `result-facts-too-large`, and nothing is stored (SC-002);
- a replayed completion does not duplicate facts; a result without facts is
  accepted as before.

## 4. Member reads

Same command as step 3. Expected:

- `listAnalysisRunFactStreams` lists every workflow node with fact counts,
  provenance, and coverage counts;
- `listAnalysisRunFacts` pages through at least three pages in stable time
  order without duplicates or gaps (SC-003), filters by overlapping range,
  reports coverage windows for every expected unit, and contains no
  segmentation or storage identifiers (SC-004);
- Club Admins, Coaches, and Viewers of the team read; other members get `404`
  (SC-005); missing sessions get `401`; another run's facts never appear.

## 5. OpenAPI snapshot

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter OpenApiSnapshotTests
```

Expected: passes with the committed snapshot that contains both operations
(refresh it during implementation with
`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1`, never in the verification
environment).

## 6. Simulated Analyst and end to end

```powershell
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build
```

Expected: the simulated Analyst writes a valid fact artifact (and an invalid one
in `invalid-facts` mode); the end-to-end test completes the development
workflow and reads back every accepted fact of the run as a team member with
accepted coverage for the whole match (SC-006).

## 7. Full suites

```powershell
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
node .github/scripts/check-markdown.mjs
```
