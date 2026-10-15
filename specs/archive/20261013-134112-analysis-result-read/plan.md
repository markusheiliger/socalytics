# Implementation Plan: Analysis Result Read for Jobs

**Branch**: `20261013-134112-analysis-result-read` | **Date**: 2026-10-13 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261013-134112-analysis-result-read/spec.md`

## Summary

Let match-level jobs consume their accepted upstream results without giving
Analysts any authority.

- **Platform (`src/platform`)**:
  - **Read operation**: a Manager-facing
    `GET /api/v1/analysis/attempts/{attemptId}/upstream-results`, for the
    current attempt only.
  - **Results**: exactly the accepted results recorded in the job's input
    snapshot, each with its stored manifest (verified against the accepted
    checksum) and per-object presigned read grants bounded by the lease.
  - **Coverage**: one entry per expected upstream segment (accepted, missing,
    failed, blocked, or skipped), derived from the snapshot and the upstream
    jobs.
  - **Paging and audit**: stable paging, and audit of out-of-scope and
    foreign reads.
- **Contract**: a new `upstream-results` v1 envelope in `contracts/analysis/`.
- **Analyst Manager (`src/analysts/manager`)**:
  - `UpstreamStager` reads all upstream dependencies before launch, checks the
    staging limit and free space, downloads and verifies every object, and
    writes coverage files into the attempt's input mount.
  - It refuses to launch on any staging failure, and deletes staged data when
    the attempt ends and at start.
  - The simulated Analyst builds match-level lineage from the staged inputs
    and has an `ignore-inputs` mode.
- **End-to-end**: the development workflow's match node stages, consumes, and
  is accepted with matching lineage.

Design rationale: [research.md](research.md). Sources and mappings:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 in both solutions; nullable enabled,
warnings as errors.

**Primary Dependencies**: existing `AWSSDK.S3` behind `IObjectStorage` (new
`PresignGetObject`), `JsonSchema.Net` 8.0.5 through `JsonSchemaContractValidator`,
Dapper, the `AnalystManager` policy with DPoP, `IAuditTrail`; Manager
`PlatformClient`, `AttemptRunner`, and the storage `HttpClient` from spec 3. No
new packages.

**Storage**: no new tables and no migration; reads use
`analysis_logical_job_inputs`, `analysis_logical_jobs`,
`analysis_accepted_results`, and `analysis_accepted_result_manifests`. Object
storage is read through presigned `GET` grants.

**Testing**: xUnit v3; contracts tests, platform integration tests with
PostgreSQL and RustFS, Manager unit tests with fakes and temporary directories,
and the existing `SocAlytics.Platform.EndToEnd.Tests`.

**Target Platform**: Platform in Linux containers; Manager on Windows, macOS,
and Linux; automated verification on Linux.

**Project Type**: Distributed system (web service and desktop worker).

**Performance Goals**: no load targets; staging of the development workflow's
small results completes within the existing SC-001 budget of Work Execution.

**Constraints**:

- Reads are bound to the current attempt and the job's snapshot (FR-001,
  FR-002, FR-004).
- Grants never outlive the lease (FR-003).
- No credentials reach the Analyst (FR-007).
- No launch on a staging failure (FR-008).
- Host cleanup (FR-014).
- All limits are explicit non-production configuration.

**Scale/Scope**: one stamp, the two development capabilities, tens of segments
in tests.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Implements `analysts-models-and-hardware.md` § Capability-Scoped Result Access and the high-level part of the `job-processing.md` sequence. The decided change (the Manager reads and stages instead of the Analyst querying the API) is applied to those narratives and to `analyst-runtime-and-recovery.md` with this plan (see Architecture Updates). | Pass |
| II. Source-area ownership | Platform code in `src/platform`, Manager code in `src/analysts/manager`, the contract in the repository-root `contracts/`. | Pass |
| III. API-first control plane | The read is an API operation; bytes come only from object storage through grants; the envelope contract is authoritative over both solutions' types. | Pass |
| IV. Evidence over claims | Integration, Manager, contract, and end-to-end tests for every requirement; no production readiness claimed. | Pass |
| V. Focused, minimal changes | No new tables or packages; no video access, indexing, or human-facing reads. | Pass |
| Tooling constraints | Needs only .NET, Docker (PostgreSQL, NATS, RustFS), and SoftHSM2, all provided by `environment-setup`. | Pass |

Post-design re-check: passes; no Complexity Tracking entries.

## Architecture Updates

**Narratives**:

- [analysts-models-and-hardware.md](../../../docs/architecture/analysts-models-and-hardware.md) § Capability-Scoped Result Access, § Implementation Validation (high-level API authorization evidence).
- [job-processing.md](../../../docs/architecture/job-processing.md) § Match Processing sequence (high-level part).
- [analyst-runtime-and-recovery.md](../../../docs/architecture/analyst-runtime-and-recovery.md) § OCI Container Runtime Boundary (launch defaults: input mount), § Analyst Manager Responsibilities.
- [intelligence-and-agents.md](../../../docs/architecture/intelligence-and-agents.md) § Intelligence Layer (text and diagram).
- [contracts-and-compatibility.md](../../../docs/architecture/contracts-and-compatibility.md) § Scope And Dependencies ("accepted upstream facts" contract).
- [platform-implementation.md](../../../docs/architecture/platform-implementation.md) § Acceptance Evidence, § Architecture Reassessment.

**Design updates** (applied with this plan as decided, not implemented; the implementation turns them into present tense):

- **The Manager reads on the Analyst's behalf** (decided with the user): the AM reads each upstream dependency for the current attempt, downloads and verifies manifests and artifacts through per-object read grants, and stages them with coverage into a read-only input mount. The read returns exactly the job's input-snapshot results. Staged inputs are size-bounded and deleted when the attempt ends. Applied to:
  - `analysts-models-and-hardware.md` § Capability-Scoped Result Access: a new paragraph; "It retrieves accepted upstream data" became "Its accepted upstream data is retrieved".
  - `job-processing.md`: the sequence now runs read, download and verify, and run with staged inputs, replacing "Analysts->>API: Query accepted upstream facts".
  - `analyst-runtime-and-recovery.md`: the launch defaults gain the read-only input mount.
  - `intelligence-and-agents.md` § Intelligence Layer: ACs consume facts that the Manager stages, and the diagram label changed.
- **Coverage states** (refined at analysis): coverage per expected upstream segment is accepted, missing, failed, blocked, or skipped. "superseded" was removed from § Capability-Scoped Result Access, because accepted results are never superseded within a run and reads never cross runs.
- **To turn into present tense at merge**: "This Manager-mediated read is decided but not yet implemented" and "(decided, not yet implemented)" for the input mount.

**Status statements at merge**:

- **`analysts-models-and-hardware.md` § Capability-Scoped Result Access**: "This Manager-mediated read is decided but not yet implemented." It becomes implemented for the read, staging, and coverage. Source-video access for ACs stays future.
- **`analyst-runtime-and-recovery.md` launch defaults**: "a read-only input mount … (decided, not yet implemented)" becomes implemented.
- **`analyst-runtime-and-recovery.md` § Analyst Manager Responsibilities**: it gains "read and stage accepted upstream results before launching high-level attempts".
- **`contracts-and-compatibility.md` § Scope And Dependencies**: "(logical segments, result manifests, capability declarations, and accepted upstream facts) will follow" must name the upstream-results envelope as added. Logical segments and capability declarations remain future.
- **`analysts-models-and-hardware.md` § Implementation Validation**: "Workflow evidence must additionally validate … high-level API authorization" must record that development evidence for high-level read authorization exists, with production evidence still required.
- **`platform-implementation.md` § Acceptance Evidence and § Architecture Reassessment**: they must list the Manager-mediated upstream read as implemented development evidence.

The documentation task must make these narratives and every status statement this feature affects true for the merged state. It re-reads `docs/architecture/` at implementation time; the updates listed here are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261013-134112-analysis-result-read/
├── plan.md
├── research.md          # Decisions R1–R7
├── data-model.md        # Sources, coverage mapping, query, configuration, staged layout
├── quickstart.md
├── contracts/
│   ├── upstream-results-api.md   # GET /api/v1/analysis/attempts/{attemptId}/upstream-results
│   └── staged-inputs.md          # Input mount layout for Analysts
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contracts/analysis/upstream-results/v1/          # envelope schema, release 1.0.0, examples
contracts/README.md                               # index row

src/platform/
├── SocAlytics.Platform.Application/
│   ├── Abstractions/ObjectStorage/IObjectStorage.cs       # + PresignGetObject
│   └── Analysis/Results/                                  # ListUpstreamResults, CoverageBuilder, AnalysisReadOptions, audit events
├── SocAlytics.Platform.Infrastructure/
│   ├── ObjectStorage/S3ObjectStorage.cs                   # + presigned GET
│   └── Persistence/Analysis/UpstreamResultReader.cs       # snapshot, accepted results, manifests, upstream job states
├── SocAlytics.Platform.Api/Analysis/AnalystManagerEndpoints.cs   # + upstream-results operation
├── SocAlytics.Platform.AppHost/Program.cs                         # Analysis__Reads__* development values
└── Tests/
    ├── SocAlytics.Platform.Contracts.Tests/
    ├── SocAlytics.Platform.Integration.Tests/Analysis/Results/   # read, coverage, paging, refusals, grants, audit
    └── SocAlytics.Platform.EndToEnd.Tests/                       # staging and lineage end to end

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/
│   ├── Platform/PlatformClient.cs                         # + ReadUpstreamResultsAsync
│   ├── Work/UpstreamStager.cs, Work/AttemptRunner.cs      # staging before launch, cleanup
│   ├── Work/StagingOptions.cs                             # Work:Staging:MaxBytesPerAttempt
│   └── Runtime/Simulated/                                 # lineage from staged inputs, ignore-inputs mode
└── Tests/SocAlytics.Analysts.Manager.Tests/Work/, Runtime/Simulated/

docs/architecture/ (see Architecture Updates), src/platform/README.md, src/analysts/manager/README.md, AGENTS.md
```

**Structure Decision**: Extend the Analysis `Results/` area from spec 3 and the
Manager's `Work/` and `Runtime/Simulated/` folders; add one contract folder.

## Complexity Tracking

No constitution violations.
