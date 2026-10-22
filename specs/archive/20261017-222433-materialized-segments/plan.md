# Implementation Plan: Materialized Video Segments and Analyst Input Staging

**Branch**: `20261017-222433-materialized-segments` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261017-222433-materialized-segments/spec.md`

## Summary

Implement the Segment Service as the `Segments` functional area of the platform: a durable record per materialized
identity with the states `absent`, `materializing`, `available`, and `failed`; a leased background worker in the API
host that extracts the primary video stream of a requested source interval by stream copy with the pinned FFmpeg and
ffprobe toolchain, writes the media and a segment manifest (new contract `segments/segment-manifest` 1.0.0) under
candidate-specific object keys, verifies the stored digest, and publishes exactly one association per identity. Run
creation requests the segments of segment-scoped jobs whose pinned video policy is `required` or `optional`; readiness
waits for them, pins the association into the job's input snapshot, and emits analyst-job 1.2.0 with `segmentMedia`.
The Manager obtains attempt-fenced, lease-bounded read grants through `getAnalysisSegmentMedia` (new response contract
`analysis/segment-media` 1.0.0), verifies and stages the files read-only at `/socalytics/media/`, and the SDK exposes
them. Club Admins list and purge a match's materialized segments (research R1–R12).

## Technical Context

**Language/Version**: C# on .NET 10 (platform and Manager); Python 3.12 for the SDK extension (from the Python Analyst
SDK feature); shell invocation of FFmpeg and ffprobe 8.1 (LGPL build pinned by the environment feature).

**Primary Dependencies**: Existing only: ASP.NET Core minimal APIs, Npgsql and Dapper, DbUp, the AWS S3 SDK adapter
(`S3ObjectStorage`), NATS JetStream through the outbox, xUnit v3, Testcontainers; the SDK's locked `pydantic` and
`jsonschema`. External executables: `ffmpeg` and `ffprobe` (configured paths, version prefix checked).

**Storage**: PostgreSQL `socalytics` schema: new versioned table `materialized_segments` (one forward-only migration
`NNNN_segments_materialized_segments.sql`, which also adds the nullable `logical_jobs.segment_rejection_reason`); node
and job snapshot JSON gain `videoPolicy` and `segmentMedia`. Object
storage: segment media and manifests under `segments/…/{candidateId}/` in the existing bucket.

**Testing**: xUnit v3 Domain and Integration tests (PostgreSQL and RustFS containers, toolchain on `PATH`), Contracts
tests for the new and changed schemas, Host tests (OpenAPI snapshot, architecture rules), Manager tests (both
runtimes), Docker end-to-end tests with a `media` mode of the test Analyst, pytest for the SDK.

**Target Platform**: The API host on Linux x64 in verification and on developer machines with a matching toolchain;
Manager on its existing targets; Analyst containers on Linux x64.

**Project Type**: Additions to the existing web service (platform), desktop and headless agent (Manager), and internal
Python library (SDK).

**Performance Goals**: Stream-copy extraction of one 5-minute segment completes well inside the configured execution
timeout in development; materialization concurrency is bounded per instance (`MaxConcurrentPerInstance`, development
`2`). No production throughput target.

**Constraints**: No API operation reads or relays media bytes; no credentials or network in Analyst containers; grants
expire no later than the attempt lease; only the primary video stream is kept; no transcoding; no automatic eviction;
production storage, cost, retention, and toolchain values stay unresolved.

**Scale/Scope**: One new table, three new HTTP operations (one Manager, two member), two new contracts and one additive
contract version, one worker, Manager staging extension, SDK media model.

## Constitution Check

| Gate | Before research | After design |
| --- | --- | --- |
| I. Architecture authority | Pass: implements On-Demand Video Segmentation and readiness step 1 as the architecture defines them | Pass: design decisions (host, extraction, manifest spans, policy binding, media mount, purge) applied to the owning narratives as decided, not yet implemented (see Architecture Updates) |
| II. Source-area ownership | Pass: platform code in `src/platform` layers, Manager in `src/analysts/manager`, SDK in `src/analysts/sdk` | Pass: no new project or first-level area; `Segments` folders appear with their first artifacts |
| III. API-first control plane | Pass: the Segment Service is data-plane processing co-hosted in the control-plane deployment as the architecture decides; no API operation proxies video | Pass: object storage only through the S3 adapter; Manager and members use API operations; contracts in `contracts/` are authoritative |
| IV. Evidence over claims | Pass: unit, integration, contract, Manager, SDK, and end-to-end tests required | Pass: development evidence only; production values stay explicit and unresolved |
| V. Focused changes | Pass: one policy version, one media profile, no Web UI, no member media access | Pass: no transcoding profiles, retention policy, or GPU work |
| Environment prerequisite | Pass: [Analyst and Media Verification Environment](../20261017-163801-analyst-media-environment/spec.md) (#143) provides pinned FFmpeg and ffprobe 8.1, Python 3.12, and `uv`; .NET, Docker, and SoftHSM2 are already provided | Pass: no other framework, SDK, or tool is needed; toolchain-dependent tests run where setup installs the pinned build |

No violation; Complexity Tracking is empty.

## Architecture Updates

**Narratives**:

- [Match Data Pipeline → On-Demand Video Segmentation](../../../docs/architecture/match-data-pipeline.md#on-demand-video-segmentation),
  [Segment Contract](../../../docs/architecture/match-data-pipeline.md#segment-contract),
  [Segment And Match Analysis Scope](../../../docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope),
  and [Implementation Validation](../../../docs/architecture/match-data-pipeline.md#implementation-validation).
- [Job Processing → Readiness And Dependencies](../../../docs/architecture/job-processing.md#readiness-and-dependencies)
  (step 1) and the Scheduler sequence diagram.
- [Analyst Runtime and Recovery → Runtime Preflight And Status](../../../docs/architecture/analyst-runtime-and-recovery.md#runtime-preflight-and-status)
  (container interface) and [Analyst Manager Responsibilities](../../../docs/architecture/analyst-runtime-and-recovery.md#analyst-manager-responsibilities).
- [Analysts, Models, and Hardware → Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access).
- [Contracts and Compatibility → Scope And Dependencies](../../../docs/architecture/contracts-and-compatibility.md#scope-and-dependencies)
  and the contract-family table (logical-segment row).
- [Platform Implementation Profile → Data Organization](../../../docs/architecture/platform-implementation.md#data-organization).
- [Terminology and Principles → 4. Control Plane Never Touches Video](../../../docs/architecture/terminology-and-principles.md#4-control-plane-never-touches-video)
  and the segment terms.
- [Security and Data Governance → POL-004](../../../docs/architecture/security-and-data-governance.md#pol-004-segments-and-media-caches)
  and `DAT-004`.

**Design updates applied now** (worded as decided, not yet implemented; the documentation task turns them into
present tense):

- `match-data-pipeline.md` (Segment Contract, after the purge paragraph): new paragraph on the Segment Service's host,
  durable record and lease publication, stream-copy policy v1 with the primary video stream only, multi-span requested
  interval, manifest timeline spans and contract location, policy-digest binding, and the per-match Club Admin purge
  that keeps in-use segments.
- `job-processing.md` (Readiness step 1): video-policy-gated materialization requests, waiting, pinned association,
  `segment-unavailable` for `required`, ready without media for `optional`.
- `analyst-runtime-and-recovery.md` (Runtime Preflight And Status): new paragraph on Manager-mediated segment media
  (`getAnalysisSegmentMedia`, verification, `/socalytics/media/`); Analyst Manager Responsibilities gains the media
  staging bullet.
- `analysts-models-and-hardware.md` (Capability-Scoped Result Access): the AC no longer "may request short-lived
  presigned object-storage access for specific source segments"; the AM stages its segment media.
- `contracts-and-compatibility.md` (Scope And Dependencies): the segment manifest, segment-media, and analyst-job 1.2.0
  contracts.
- `platform-implementation.md` (Data Organization): the worker in the API host and its toolchain need.
- `terminology-and-principles.md` (section 4): video traffic runs through Managers on behalf of Analysts; the diagram
  shows Manager, not Analyst, exchanging bytes with object storage.
- `security-and-data-governance.md` (POL-004): the manual per-match purge in development, without resolving the policy.

**Status statements at merge** (each must become true for the merged state):

- `match-data-pipeline.md`, Segment Contract: "Machine-readable segment schemas will be introduced with the Segment
  Service implementation and must encode these identities without redefining them." → the segment manifest contract
  exists at `contracts/segments/segment-manifest/v1/` and encodes them.
- `match-data-pipeline.md`, the new "decided as follows and not yet implemented" paragraph → present tense.
- `match-data-pipeline.md`, Implementation Validation: "A prototype must verify keyframe-aligned extraction, stable
  logical boundaries, concurrent cache-miss behavior, storage cost, and policy-version migration." → state which of
  these the tests now evidence (extraction, boundaries, concurrency, policy coexistence) and that storage cost and
  migration at scale remain open.
- `job-processing.md`, Readiness step 1 "Decided, not yet implemented: …" → present tense.
- `analyst-runtime-and-recovery.md`, the new segment-media paragraph and the responsibilities bullet "(decided, not yet
  implemented)" → present tense; the mount sentence ("the Logical Job message read-only at `/socalytics/job.json`, the
  staged upstream results …") names `/socalytics/media/`.
- `analysts-models-and-hardware.md`, Capability-Scoped Result Access: "real Analyst execution is implemented on the
  Docker Desktop profile, while production evidence remains future work, as does source-video access for ACs" →
  segment media access through Manager staging is implemented; production evidence remains future work; and the new
  "(decided, not yet implemented)" sentence → present tense.
- `contracts-and-compatibility.md`, Scope And Dependencies: "Materialized Video Segments will add … (decided, not yet
  implemented)" → present tense; "The remaining documented Analyst workflow contracts (logical segments and capability
  declarations) will follow" and "Future machine-readable schemas will encode its decisions" → logical segments are
  encoded; only what still remains is named.
- `platform-implementation.md`, Data Organization: "it is decided, not yet implemented, as a background worker …" →
  present tense.
- `security-and-data-governance.md`, POL-004 addition "(decided, not yet implemented …)" → present tense; the policy
  itself stays `Unresolved / Blocking`.
- `terminology-and-principles.md`, section 4: stays true; check that no other text claims Analysts exchange bytes with
  object storage.
- Any other statement found at implementation time that says no media is materialized, that Analysts receive no
  video, or that Analysts may obtain presigned source-video access.

The documentation task MUST make the listed narratives and every status statement the feature affects true for the
merged state, re-reading `docs/architecture/` at implementation time; the rewrites above are examples, not the whole
job. It also updates `README.md` (toolchain prerequisite for the API host and tests), `AGENTS.md` (Current State,
segment options, the media mount, the minimum Manager version that stages media), `src/platform/README.md`,
`src/analysts/README.md` and the SDK README, and `contracts/README.md` index rows.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261017-222433-materialized-segments/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── segment-contracts.md
│   ├── segment-media-read.md
│   ├── container-interface.md
│   └── member-operations.md
├── checklists/requirements.md
└── tasks.md                 # /speckit-tasks
```

### Source Code (repository root)

```text
contracts/
├── README.md                                                 # rows: segment-manifest, segment-media; analyst-job 1.2.0
├── segments/segment-manifest/v1/                             # new schema, releases/1.0.0, examples
└── analysis/
    ├── segment-media/v1/                                     # new schema, releases/1.0.0, examples
    └── analyst-job/v1/                                       # 1.2.0, releases/1.2.0, new examples

src/platform/
├── SocAlytics.Platform.Domain/Segments/                      # MaterializedSegmentIdentity, SegmentWindowCalculator,
│                                                             # MaterializedSegmentState, SegmentFailureReasons
├── SocAlytics.Platform.Application/
│   ├── Abstractions/ContractSchemaIds.cs                     # segment-manifest, segment-media ids
│   ├── Abstractions/ObjectStorage/IObjectStorage.cs          # streaming put with full-object checksum
│   ├── Segments/                                             # SegmentOptions, ISegmentStore, ISegmentExtractor,
│   │                                                         # RequestSegmentMaterialization, MaterializeSegment,
│   │                                                         # SegmentManifestBuilder, ListMaterializedSegments,
│   │                                                         # PurgeMaterializedSegments, SegmentAuditEvents
│   └── Analysis/
│       ├── WorkflowSnapshotMapper.cs                         # node snapshot videoPolicy
│       ├── Commands/HandleRecordingsFinalizedNotification.cs # materialization demand at run creation
│       ├── RunReadinessCoordinator.cs                        # media gate, segmentMedia pin, analyst-job 1.2.0
│       └── Results/GetSegmentMedia.cs                        # Manager media read and grants
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/Migrations/NNNN_segments_materialized_segments.sql
│   ├── Persistence/Segments/SegmentStore.cs
│   ├── Persistence/Analysis/LogicalJobStore.cs              # segment_rejection_reason
│   ├── Segments/FfmpegSegmentExtractor.cs, ToolchainProbe.cs
│   ├── ObjectStorage/S3ObjectStorage.cs                      # streaming put
│   ├── Workers/SegmentMaterializationWorker.cs
│   └── InfrastructureServiceCollectionExtensions.cs
├── SocAlytics.Platform.Api/
│   ├── Analysis/AnalystManagerEndpoints.cs                   # getAnalysisSegmentMedia
│   ├── Segments/MaterializedSegmentEndpoints.cs              # list and purge
│   ├── appsettings.Development.json                          # Segments section
│   └── openapi/v1.json                                       # refreshed snapshot
├── SocAlytics.Platform.AppHost/                              # Segments environment for the API
└── Tests/
    ├── SocAlytics.Platform.Contracts.Tests/                  # ContractCatalog, SegmentContractsTests, AnalysisExampleTests
    ├── SocAlytics.Platform.Integration.Tests/
    │   ├── Segments/                                         # domain, store, worker, concurrency, failure, purge, access
    │   ├── Analysis/                                         # readiness gate, media read
    │   ├── Structure/PersistedTableClassifications.cs        # only if a table without version is added (none planned)
    │   └── Support/SyntheticMedia.cs                         # test-time synthetic recording
    ├── SocAlytics.Platform.Host.Tests/                       # options validation, OpenAPI snapshot
    └── SocAlytics.Platform.EndToEnd.Tests/
        ├── SegmentMediaEndToEndTests.cs
        └── TestAnalyst/analyst.py, media manifest variant    # media mode

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/
│   ├── Platform/PlatformClient.cs, PlatformWorkContracts.cs  # getAnalysisSegmentMedia
│   ├── Work/SegmentMediaStager.cs, AttemptRunner.cs, AnalystJobMessage.cs
│   ├── Runtime/Docker/DockerLaunchSpec.cs, Runtime/AttemptExecution.cs   # media mount
│   └── Runtime/Simulated/SimulatedResultWriter.cs            # verifies staged media
└── Tests/SocAlytics.Analysts.Manager.Tests/
    ├── Work/SegmentMediaStagerTests.cs, AttemptDirectoryTests.cs, AttemptCleanupTests.cs
    └── Runtime/Docker/DockerLaunchSpecTests.cs, Runtime/SimulatedRuntimeTests.cs

src/analysts/sdk/
├── src/socalytics_analyst/models/segment.py, media.py, context.py, contracts/   # schema copies
└── tests/test_media.py, test_contract_examples.py
```

**Structure Decision**: The `Segments` functional area is added as folders inside the existing Domain, Application,
Infrastructure, and Api layers; the worker joins the existing Infrastructure workers hosted by the API. Manager and SDK
changes stay in their existing projects.

## Complexity Tracking

No violations.
