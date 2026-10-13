# Implementation Plan: Analysis Result Manifests and Validated Acceptance

**Branch**: `20261013-125502-analysis-result-manifests` | **Date**: 2026-10-13 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261013-125502-analysis-result-manifests/spec.md`

## Summary

Give accepted analysis results real, validated content.

- **Platform (`src/platform`)**:
  - **Grants**: a Manager-facing operation issues single-object presigned
    write grants under an attempt-specific result location. Each grant signs
    the declared size and SHA-256 and is bounded by the attempt's lease.
    Declarations are stored immutably.
  - **Contract**: a new versioned `result-manifest` contract.
  - **Acceptance**: `CompleteAttempt` reads and verifies the manifest before
    accepting: checksum, schema, scope, provenance, lineage, artifacts, and
    validity. A failed check turns into a failed attempt with a named reason,
    so the existing retry rules apply. Storage unavailability is a temporary,
    state-free `503`.
  - **Persistence**: one immutable manifest record per accepted result.
  - **Audit**: audit events for misuse-indicating grant refusals and for
    acceptance rejections.
- **Analyst Manager (`src/analysts/manager`)**:
  - `ResultPublisher` validates the Analyst's manifest against the embedded
    contract before requesting grants. When it is invalid, nothing is uploaded
    and the completion fails.
  - Otherwise it uploads through the grants and completes with the manifest
    reference and checksum.
  - The simulated Analyst writes deterministic manifests and artifacts, with
    configurable invalid modes.
- **End-to-end**: the existing cross-solution test project gains RustFS and
  proves write, acceptance, rejection with retry, and the Manager-side
  contract stop.

Design rationale: [research.md](research.md). Entities and tables:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 in both solutions (SDK pinned identically by
`src/platform/global.json` and `src/analysts/manager/global.json`); nullable
enabled, warnings as errors.

**Primary Dependencies**:

- **Platform**: existing `AWSSDK.S3` behind `IObjectStorage`,
  `JsonSchema.Net` 8.0.5 through `JsonSchemaContractValidator`, Dapper and
  DbUp, the unit of work, the `AnalystManager` authorization policy with DPoP,
  `IAuditTrail`, and OpenTelemetry metrics from ServiceDefaults.
- **Manager**: existing `PlatformClient` with DPoP and
  `Microsoft.Extensions.Http`. Core gains a reference to `JsonSchema.Net`, the
  version already pinned in `src/analysts/manager/Directory.Packages.props`.
- No new package versions.

**Storage**:

- PostgreSQL through the persistence foundation: one forward-only migration
  `NNNN_analysis_result_manifests.sql`, numbered at implementation time, with
  two immutable tables classified in `PersistedTableClassifications.cs`.
- Stamp-local S3-compatible object storage: RustFS locally and in tests.

**Testing**: xUnit v3.

- **Contracts tests**: the manifest and grants fixtures.
- **Platform integration tests**: PostgreSQL and RustFS containers.
- **Manager unit tests**: a fake platform and fake storage.
- **`SocAlytics.Platform.EndToEnd.Tests`**: adds a RustFS container with the
  same pinned image.

**Target Platform**: Platform in Linux containers (Aspire locally); Manager on
Windows, macOS, and Linux; automated verification on Linux.

**Project Type**: Distributed system: web service and desktop worker connected
by REST, NATS JetStream, and object storage.

**Performance Goals**: no load targets. SC-001 extends #66's SC-001: a ready
job reaches an accepted result within 30 s plus the simulated run time.

**Constraints**:

- No result bytes through the platform (FR-004).
- Grants never outlive the lease (FR-002).
- No storage reads inside database transactions.
- Fail closed when storage cannot attest checksums.
- No secrets in logs or audit (FR-017).
- All limits are explicit non-production configuration.

**Scale/Scope**: one stamp, two development capabilities, small manifests (well
under 1 MiB) in tests.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Implements `analysts-models-and-hardware.md` § Result Manifest And Lineage, `job-processing.md` § Durable Workflow Model (result write and acceptance), and `analyst-runtime-and-recovery.md` § Registration And Runtime Credentials (short-lived, narrowly scoped object-storage grants). It makes true the Implementation Profile statement that versioned JSON Schemas become canonical for result manifests. The documentation task applies the plan's Architecture Updates section. | Pass |
| II. Source-area ownership | Platform code in `src/platform`, Manager code in `src/analysts/manager`, contracts in the repository-root `contracts/`; the end-to-end project stays in `src/platform/Tests` with its existing test-only Manager reference. | Pass |
| III. API-first control plane | Grants and acceptance are API operations; result bytes go only to object storage through the S3 protocol; the manifest contract is authoritative over both solutions' types. | Pass |
| IV. Evidence over claims | One test per acceptance check, contract tests in both solutions, the end-to-end loop; no production readiness claimed. | Pass |
| V. Focused, minimal changes | No new packages; no read API, SDK, fact indexing, or retention; two immutable tables. | Pass |
| Tooling constraints | Needs only .NET, Docker (PostgreSQL, NATS, RustFS containers), and SoftHSM2, all provided by `environment-setup`; no environment feature is needed. | Pass |

Post-design re-check: passes; no Complexity Tracking entries.

## Architecture Updates

**Narratives**:

- [analysts-models-and-hardware.md](../../docs/architecture/analysts-models-and-hardware.md) § Analyst Containers, § Implementation Profile, § Result Manifest And Lineage, § Implementation Validation (result-manifest validation evidence).
- [analyst-runtime-and-recovery.md](../../docs/architecture/analyst-runtime-and-recovery.md) § OCI Container Runtime Boundary (launch defaults), § Registration And Runtime Credentials (object-storage grants), the execution-attempt sequence, § Analyst Manager Responsibilities.
- [job-processing.md](../../docs/architecture/job-processing.md) § Match Processing sequence (result write and acceptance).
- [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md) § Scope And Dependencies.
- [platform-implementation.md](../../docs/architecture/platform-implementation.md) § Analyst Technology, § Acceptance Evidence, § Architecture Reassessment.
- [match-data-pipeline.md](../../docs/architecture/match-data-pipeline.md) § Segment And Match Analysis Scope (validation of accepted low-level manifests).

**Design updates** (applied with this plan as decided, not implemented; the implementation turns them into present tense):

- **Who stores result objects** (decided with the user): the AC writes results and the manifest into the job's result mount and receives no object-storage write credentials. The AM checks the manifest against the contract, stores the objects through per-object write grants that the API issues for the attempt's result location, and then completes. Applied to:
  - `analysts-models-and-hardware.md`: § Analyst Containers ("Result uploader" became "Result writer for the job's result mount") and § Result Manifest And Lineage (write path, the AM's contract check, storage-attested artifacts, failed attempt on a rejected manifest, "decided but not yet implemented");
  - `analyst-runtime-and-recovery.md`: the launch defaults ("no object-storage write credentials …"), the execution-attempt sequence (grants and store steps, `Storage` participant), and the Manager responsibilities (check and store before reporting);
  - `job-processing.md`: both write steps of the match processing sequence now go through the Manager with grants.
- **Rejected manifests**: a manifest that fails any acceptance check ends the attempt as failed with a named reason under the normal retry rules. This was added to § Result Manifest And Lineage.
- **To turn into present tense at merge**: every "decided, not yet implemented" wording added above. Fact indexing stays future.

**Status statements at merge**:

- **`analysts-models-and-hardware.md` § Implementation Profile**: "Versioned JSON Schema documents will become canonical for job inputs and result manifests when the first Analyst implementation slice introduces those contracts." It must say that the job and result-manifest schemas in `contracts/analysis/` are canonical and validated by the platform and the Manager, and that the Analyst SDK's Pydantic models remain future.
- **`analysts-models-and-hardware.md` § Result Manifest And Lineage**: "The result write grants, the result-manifest contract, and this validation are decided but not yet implemented" must become implemented. Fact indexing stays future.
- **`analyst-runtime-and-recovery.md`**: "(decided, not yet implemented)" in the launch defaults must be removed.
- **`contracts-and-compatibility.md` § Scope And Dependencies**: "The remaining documented Analyst workflow contracts (logical segments, result manifests, capability declarations, and accepted upstream facts) will follow" must name the result-manifest and result-grants contracts as added. The others remain future.
- **`platform-implementation.md` § Analyst Technology**: "Future versioned JSON Schema documents will be canonical for Analyst job and result contracts" must state that they are canonical.
- **`platform-implementation.md` § Acceptance Evidence and § Architecture Reassessment**: they must list result write grants and validated result acceptance with the simulated Analyst as implemented evidence. Real Analyst images and cross-runtime schema agreement with the Python SDK remain unsupported.
- **`match-data-pipeline.md` § Segment And Match Analysis Scope**: "The API validates and indexes accepted low-level result manifests" must state that validation is implemented while indexing remains future.

The documentation task must make these narratives and every status statement this feature affects true for the merged state. It re-reads `docs/architecture/` at implementation time; the updates listed here are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261013-125502-analysis-result-manifests/
├── plan.md
├── research.md          # Decisions R1–R8
├── data-model.md        # Result location, tables, configuration, simulated modes
├── quickstart.md
├── contracts/
│   ├── result-grants-api.md     # POST /api/v1/analysis/attempts/{attemptId}/result-grants
│   ├── result-manifest.md       # contracts/analysis/result-manifest/v1/
│   └── result-acceptance.md     # Ordered checks and outcomes in completion
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contracts/analysis/result-manifest/v1/              # schema, release 1.0.0, examples, expectations
contracts/analysis/result-grants/v1/                # grants request/response fixtures
contracts/README.md                                 # index rows

src/platform/
├── SocAlytics.Platform.Application/
│   ├── Abstractions/ObjectStorage/IObjectStorage.cs        # + PresignPutObject, GetObjectChecksumAsync, ReadObjectAsync
│   ├── Analysis/Results/                                   # IssueResultGrants, ResultManifestVerifier, AnalysisResultOptions, stores, AnalysisAuditEvents
│   └── Analysis/Commands/CompleteAttempt.cs                # + verification and failed-attempt path
├── SocAlytics.Platform.Infrastructure/
│   ├── ObjectStorage/S3ObjectStorage.cs                    # + single-object presign, checksum, bounded read
│   ├── Persistence/Analysis/ResultDeclarationStore.cs, AcceptedResultManifestStore.cs
│   └── Persistence/Migrations/NNNN_analysis_result_manifests.sql
├── SocAlytics.Platform.Api/Analysis/AnalystManagerEndpoints.cs   # + result-grants operation
├── SocAlytics.Platform.Api/openapi/v1.json                       # regenerated snapshot (once Web UI Foundation provides it)
├── SocAlytics.Platform.AppHost/Program.cs                         # Analysis__Results__* development values
└── Tests/
    ├── SocAlytics.Platform.Contracts.Tests/                # manifest and grants fixtures
    ├── SocAlytics.Platform.Integration.Tests/Analysis/Results/   # grants, acceptance checks, conformance, audit
    ├── SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs
    └── SocAlytics.Platform.EndToEnd.Tests/                 # + RustFS, result write and acceptance

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/
│   ├── Platform/PlatformClient.cs                          # + RequestResultGrantsAsync, completion outcome "failed"
│   ├── Work/ResultPublisher.cs                             # contract check, digests, grants, upload, renewal
│   ├── Work/AttemptRunner.cs                               # publish before completing
│   ├── Runtime/Simulated/                                  # manifest and artifact writer, result modes
│   └── SocAlytics.Analysts.Manager.Core.csproj             # JsonSchema.Net reference, embedded result-manifest schema
└── Tests/SocAlytics.Analysts.Manager.Tests/                # ResultPublisher, simulated modes, fixtures

docs/architecture/ (see Architecture Updates), src/platform/README.md, src/analysts/manager/README.md, AGENTS.md
```

**Structure Decision**: Extend the Analysis functional area of the platform
layers (a `Results/` folder) and the Manager's `Work/` and `Runtime/Simulated/`
folders from Work Execution; add two contract folders; reuse the existing
end-to-end project.

## Complexity Tracking

No constitution violations.
