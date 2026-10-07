# Implementation Plan: Recording Lineage and Upload

**Branch**: `20261005-130702-recording-lineage-upload` | **Date**: 2026-10-07 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261005-130702-recording-lineage-upload/spec.md`

## Summary

Authorized Coaches and Club Admins upload a Match's source recordings directly
to S3-compatible object storage, accept each verified upload as an immutable
recording version with an immutable timeline mapping, and finalize an ordered
recording set whose frozen lineage and single recordings-finalized event record
commit atomically for Durable Analysis.

The platform issues a SigV4 presigned single-object `PUT` grant for one
platform-chosen key, with the declared SHA-256 bound as a signed
`x-amz-checksum-sha256` header, so storage itself rejects mismatched bytes and
the API never receives media. On completion the API reads storage-computed
integrity evidence (`HeadObject` with checksum mode) and fails closed when a
full-object SHA-256 is unavailable. Recording versions, timeline mappings,
recording-set versions, memberships, retry outcomes, and event records are
append-only tables protected by a trigger and revoked runtime privileges; the
upload session is the only mutable aggregate and transitions under a state
guard. Retry keys (`Idempotency-Key`) are persisted with request digests and
resource identities, never grants. `IRecordingSetLookup` gives Durable Analysis
the frozen lineage from the database alone. S3 access uses `AWSSDK.S3` in
Infrastructure only; locally Aspire and Testcontainers run a pinned RustFS
1.0.1 container ([research.md](research.md) R1–R4). This feature also creates
the repository-root `contracts/` folder with its index and first artifact, the
recordings-finalized event schema, plus the contract test project
`SocAlytics.Platform.Contracts.Tests` (R15). It stores the immutable event
record only; Durable Analysis later adds the `IOutbox` call to the finalize
handler and a backfill (R8).

## Technical Context

**Language/Version**: C# on .NET 10 (SDK 10.0.400 pinned by `src/platform/global.json`, roll-forward latest patch), nullable enabled, warnings as errors as already configured.

**Primary Dependencies**: ASP.NET Core minimal APIs with built-in OpenAPI (`/openapi/v1.json`); Npgsql + Dapper and DbUp (from the persistence foundation); ASP.NET Core Identity session, anti-forgery, `ITeamScopeResolver`, and `IAuditTrail` (from Club and Identity); new: `AWSSDK.S3` 4.0.103.4 (Infrastructure and AppHost only). Aspire AppHost SDK 13.4.6 with a plain RustFS container resource (no community hosting package; [research.md](research.md) R2).

**Storage**: PostgreSQL schema `socalytics` — new tables `recording_upload_sessions` (versioned), `recording_versions`, `recording_timeline_mappings`, `recording_set_versions`, `recording_set_members`, `recording_retry_outcomes`, `recording_finalized_events` (immutable) in one migration named `recordings_create_upload_and_lineage_tables` ([data-model.md](data-model.md)). S3-compatible object storage (one bucket per stamp; RustFS `rustfs/rustfs:1.0.1` locally) holds media bytes only.

**Testing**: xUnit v3, Shouldly, NSubstitute; NetArchTest.Rules (architecture); Testcontainers 4.15.0 — `Testcontainers.PostgreSql` (persistence) plus the generic `Testcontainers` container for RustFS — in `src/platform/Tests/SocAlytics.Platform.Integration.Tests`; Aspire.Hosting.Testing in Host.Tests; new `src/platform/Tests/SocAlytics.Platform.Contracts.Tests` (xUnit v3, Shouldly, `JsonSchema.Net` 8.0.5) as the repository contract command `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`.

**Target Platform**: Linux OCI containers for deployment profiles (not delivered here); local development on Windows, macOS, or Linux through the Aspire AppHost with Docker.

**Project Type**: Web service — the platform control-plane API (layered monolith; Recordings is a functional-area folder in each layer).

**Performance Goals**: SC-002 — each platform operation (start, grant, complete, finalize) acknowledged within 2 s at p95 in acceptance runs; completion performs one `HeadObject` (plus at most one `GetObjectAttributes` fallback); finalization is one transaction for ≤ 100 members.

**Constraints**: No media bytes through the API (FR-010; recording endpoints cap request bodies at 1 MiB and accept only JSON); one whole object per session, ≤ 5 GiB (S3 single-`PUT` limit; multipart out of scope); grant lifetime, maximum size, and allowed content types are required configuration with no production defaults; fail closed on missing integrity evidence (FR-011); no grants, credentials, or secrets in durable records, logs, or telemetry (FR-029); storage I/O never inside a database transaction.

**Scale/Scope**: Development stamp; tens of recordings and set versions per Match; lineage read returned as one unpaginated view per Match; 10 HTTP operations ([contracts/openapi.yaml](contracts/openapi.yaml)); 1 event contract ([recordings-finalized.schema.json](contracts/schemas/recordings/recordings-finalized/v1/recordings-finalized.schema.json), published as `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`).

No `NEEDS CLARIFICATION` items remain; every open choice is resolved in [research.md](research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Constitution version 1.1.0. Pre-design evaluation:

| Gate | Status | Justification |
| --- | --- | --- |
| I. Architecture Is the Design Authority | PASS | Follows [match-data-pipeline.md](../../docs/architecture/match-data-pipeline.md) (Planned Recording-Lineage Foundation, Segment Contract), [terminology-and-principles.md](../../docs/architecture/terminology-and-principles.md) (S3 as protocol, control plane never touches video), [platform-implementation.md](../../docs/architecture/platform-implementation.md) (layers, Dapper, version triggers, immutable records without `version`), and [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md) (`/api/v1`, `sha-256:` digests, ETags, retry keys). Where the design makes a decision the narratives leave open or describe as a target (event record before publication, adopted S3 client and local container, timeline-mapping form), the exact wording is listed under [Required Architecture Updates](#required-architecture-updates). Production-blocking items (POL-003, THR-003, GOV-OWN-004) stay explicitly unresolved. |
| II. Respect Source-Area Ownership | PASS | All production code lives in `src/platform/` inside existing layer projects under `Recordings` and `ObjectStorage` folders; no new production project. Tests go into the existing Host and Architecture test projects, the Integration.Tests project created by the persistence foundation, and the new `src/platform/Tests/SocAlytics.Platform.Contracts.Tests` (a test project, as the coordinator decided). The repository-root `contracts/` folder is the location [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md) defines for canonical contracts; it is not a child of `src/`. |
| III. API-First Control Plane | PASS | Every capability is a REST operation in the OpenAPI document; the API issues presigned access and reads metadata only, never media bytes; storage is reached only through the S3 protocol with path-style addressing, and `AWSSDK.S3` is confined to Infrastructure (architecture test), so the implementation is replaceable. Contracts are OpenAPI 3.1 and JSON Schema 2020-12; the event schema is a canonical repository-root contract validated by the contract command. |
| IV. Evidence Over Claims | PASS | Behavior is proven by integration tests against real PostgreSQL and RustFS containers, contract tests, and architecture and host tests; existing suites must keep passing. The plan and quickstart state that local evidence is not production readiness. |
| V. Focused, Minimal Changes | PASS | Adds only `AWSSDK.S3`, the generic `Testcontainers` package, and `JsonSchema.Net`; no publisher or outbox call, multipart upload, cleanup job, playback grants, or client code; one `IObjectStorage` abstraction with only the operations this feature uses; the contracts folder holds only this feature's schema and index. |
| Technology and Tooling Constraints | PASS | .NET 10 via `global.json` and the existing solution; supported commands unchanged; Aspire remains local-only composition; S3-compatible storage is a deferred technology that this spec adopts (FR-007–FR-011, Assumptions) and this plan adopts explicitly; `.gitignore` needs no change (no new tool output). |
| Environment-feature rule | PASS (depends on environment feature) | Tasks need the .NET SDK from `src/platform/global.json` (provided by `.github/actions/environment-setup`), Docker for Testcontainers (present on the runner; RustFS and PostgreSQL images pulled at test time), NuGet packages restored by `dotnet restore`, and Node Markdown linters (provided). The current `.github/actions/environment-verify` covers `src/platform/` and Markdown but would report changes under the new repository-root `contracts/` folder as uncovered. This feature therefore depends on the environment feature `specs/20261007-115855-environment-verification-coverage`, which extends `environment-verify` so `contracts/` changes run the platform checks (including `SocAlytics.Platform.Contracts.Tests`); it must be reviewed and merged before this feature starts, and the spec names it under Assumptions → Dependencies. With it merged, the environment provides everything the tasks need. |
| Development Workflow and Quality Gates | PASS | Spec Kit flow followed; `README.md` (composite-action coverage of `contracts/`, the contract command, repository structure), `contracts/README.md`, `src/platform/README.md`, and `AGENTS.md` updates are implementation tasks listed under [Documentation Updates](#documentation-updates); Markdown validated with `node .github/scripts/check-markdown.mjs`; no commits or pushes. |

Feature dependencies (merge order): the environment feature
`specs/20261007-115855-environment-verification-coverage`,
`specs/20261005-130700-platform-persistence-foundation`, and
`specs/20261005-130701-club-identity-foundation` must be merged first.

## Project Structure

### Documentation (this feature)

```text
specs/20261005-130702-recording-lineage-upload/
├── spec.md
├── plan.md                                   # This file
├── research.md                               # Phase 0 decisions R1–R15
├── data-model.md                             # Tables, rules, state transitions, lookup shape
├── quickstart.md                             # Validation scenarios and manual run
├── contracts/
│   ├── openapi.yaml                          # OpenAPI 3.1 fragment (10 operations)
│   └── schemas/                              # mirrors the repository-root contracts/ tree
│       └── recordings/
│           └── recordings-finalized/
│               └── v1/
│                   └── recordings-finalized.schema.json   # JSON Schema 2020-12 event payload
├── checklists/
│   └── requirements.md
└── tasks.md                                  # Phase 2 output (/speckit-tasks; not created here)
```

### Source Code (repository root)

```text
contracts/                                                # new repository-root canonical contracts folder
├── README.md                                             # index: artifact, owner, version, example, validation command
└── recordings/
    └── recordings-finalized/
        └── v1/
            └── recordings-finalized.schema.json          # copy of the mirrored schema; $id .../v1/recordings-finalized.schema.json

src/platform/
├── SocAlytics.Platform.slnx                              # + Tests/SocAlytics.Platform.Contracts.Tests
├── Directory.Packages.props                              # + AWSSDK.S3 4.0.103.4, Testcontainers 4.15.0, JsonSchema.Net 8.0.5
├── README.md                                             # current status: object storage adopted (task)
├── SocAlytics.Platform.Domain/
│   └── Recordings/
│       ├── Sha256Digest.cs                               # `sha-256:<hex>` value object
│       ├── RecordingDescriptor.cs                        # display name, description, content type
│       ├── UploadSession.cs                              # pending → completed rules
│       ├── UploadSessionState.cs
│       ├── RecordingVersion.cs
│       ├── TimelineSpan.cs
│       ├── TimelineMapping.cs                            # validation + canonical digest
│       ├── RecordingSetVersion.cs                        # membership rules (FR-019/FR-020)
│       └── RecordingSetMember.cs
├── SocAlytics.Platform.Application/
│   ├── Abstractions/
│   │   └── ObjectStorage/
│   │       ├── IObjectStorage.cs                         # presign PUT, probe, integrity evidence
│   │       ├── ObjectUploadGrant.cs
│   │       ├── ObjectIntegrityEvidence.cs
│   │       └── ObjectStorageUnavailableException.cs
│   └── Recordings/
│       ├── IRecordingSetLookup.cs                        # public; consumed by Durable Analysis
│       ├── RecordingSetLineage.cs                        # lookup result records
│       ├── RecordingAccessPolicy.cs                      # recordings:manage / recordings:read over ITeamScopeResolver
│       ├── RecordingUploadOptions.cs                     # GrantLifetime, MaxObjectSizeBytes, AllowedContentTypes, KeyPrefix
│       ├── RecordingAuditActions.cs
│       ├── CanonicalJson.cs                              # deterministic digests for mappings and retry requests
│       ├── IRecordingStore.cs                            # write-side persistence port
│       ├── IRecordingRetryOutcomeStore.cs
│       ├── IRecordingLineageQueries.cs                   # read-side projections
│       ├── Commands/
│       │   ├── StartRecordingUploadHandler.cs
│       │   ├── IssueRecordingUploadGrantHandler.cs
│       │   ├── CompleteRecordingUploadHandler.cs
│       │   ├── ReviseTimelineMappingHandler.cs
│       │   └── FinalizeRecordingSetHandler.cs
│       └── Queries/
│           ├── GetRecordingUploadSessionHandler.cs
│           ├── GetRecordingVersionHandler.cs
│           ├── GetTimelineMappingHandler.cs
│           ├── GetRecordingSetVersionHandler.cs
│           └── GetMatchRecordingLineageHandler.cs
├── SocAlytics.Platform.Infrastructure/
│   ├── ObjectStorage/
│   │   ├── ObjectStorageOptions.cs                       # ServiceUrl, Region, AccessKey, SecretKey, Bucket, ForcePathStyle
│   │   └── S3ObjectStorage.cs                            # AWSSDK.S3 adapter (internal)
│   ├── Recordings/
│   │   ├── RecordingStore.cs                             # Dapper writes
│   │   ├── RecordingRetryOutcomeStore.cs
│   │   ├── RecordingLineageQueries.cs                    # Dapper projections
│   │   └── RecordingSetLookup.cs                         # IRecordingSetLookup implementation
│   └── Persistence/
│       └── Migrations/
│           └── NNNN_recordings_create_upload_and_lineage_tables.sql   # NNNN = next free number at implementation
├── SocAlytics.Platform.Api/
│   └── Recordings/
│       ├── RecordingEndpoints.cs                         # MapRecordingEndpoints(): 10 operations under /api/v1/matches/{matchId}
│       ├── RecordingContracts.cs                         # request/response DTOs matching contracts/openapi.yaml
│       └── RecordingProblemMapping.cs                    # outcome → problem details codes
├── SocAlytics.Platform.AppHost/
│   ├── SocAlytics.Platform.AppHost.csproj                # + AWSSDK.S3 (local bucket creation)
│   └── Program.cs                                        # + objectstorage RustFS container, secret parameters, bucket init, API WaitFor
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/
    │   └── Recordings/
    │       ├── RustFsContainerFixture.cs
    │       ├── TimelineMappingCanonicalFormTests.cs      # pure, no containers
    │       ├── UploadWorkflowTests.cs
    │       ├── CompletionVerificationTests.cs
    │       ├── UploadGrantScopeTests.cs
    │       ├── FinalizationTests.cs
    │       ├── RetryKeyTests.cs
    │       ├── ImmutabilityTests.cs
    │       ├── RecordingAuthorizationTests.cs
    │       ├── RecordingSetLookupTests.cs
    │       ├── ObjectStorageUnavailableTests.cs
    │       └── SecretHygieneTests.cs
    ├── SocAlytics.Platform.Architecture.Tests/
    │   └── RecordingsArchitectureTests.cs                # no Amazon.* outside Infrastructure; internal implementations
    ├── SocAlytics.Platform.Contracts.Tests/              # new; repository contract command
    │   ├── SocAlytics.Platform.Contracts.Tests.csproj    # xUnit v3, Shouldly, JsonSchema.Net; links ../../../../contracts/**/* into output
    │   ├── ContractCatalog.cs                            # discovers *.schema.json under contracts/
    │   ├── SchemaMetaValidationTests.cs                  # 2020-12 meta-schema; $id ↔ path; x-socalytics-version
    │   ├── SchemaExampleTests.cs                         # every `examples` entry validates (format assertion on)
    │   └── ContractIndexTests.cs                         # every schema listed in contracts/README.md
    └── SocAlytics.Platform.Host.Tests/
        └── RecordingsOpenApiTests.cs                     # operationIds, response codes, no binary request bodies
```

**Structure Decision**: The feature extends the existing layered monolith in
`src/platform/` without new production projects. Recordings code sits in the
`Recordings` folder of each layer; the reusable S3 port lives in
`SocAlytics.Platform.Application.Abstractions.ObjectStorage` and its adapter in
`SocAlytics.Platform.Infrastructure.ObjectStorage`; SQL lives in the shared
`Persistence/Migrations` folder. Registration happens inside the existing public
`AddApplication()` and `AddInfrastructure(...)` methods, keeping all
implementation types internal. The repository-root `contracts/` folder, its
index, and the `SocAlytics.Platform.Contracts.Tests` project are created here
as the first canonical contract baseline; Durable Analysis extends them
([research.md](research.md) R15).

## Design Overview

### Operation flow

Every handler follows the same order, which makes denials fail closed and
replays safe (FR-004, FR-005, FR-025, FR-026):

1. Authenticate (Club and Identity session; `401` otherwise).
2. Resolve the route Match through `ITeamScopeResolver` to Team and Season state
   (`404` missing Match; `403` unresolvable scope).
3. Authorize with `RecordingAccessPolicy`: `recordings:manage` = active Club
   Admin or Coach of that Team; `recordings:read` additionally allows Viewer
   (`403`, denial audit event committed alone).
4. For mutations, reject an archived Season (`409 season-archived`).
5. Validate the request and the `Idempotency-Key` (`400`).
6. Look up the retry outcome; same digest → replay; different → `409`.
7. Perform storage I/O outside any transaction (probe, `HeadObject`), mapping
   outages to `503`.
8. In one `IUnitOfWork`: re-check scope and state guards, write rows, the retry
   outcome, and the audit event; commit. A unique violation on the retry key
   means a concurrent duplicate won: roll back and replay.
9. After commit, presign grants where the operation returns one
   (`Cache-Control: no-store`).

### Key decisions

- **Grant**: presigned single-object `PUT`, signed `Content-Type` and
  `x-amz-checksum-sha256`, configured lifetime; never stored ([research.md](research.md) R3).
- **Verification**: `HeadObject` with checksum mode; size, full-object
  SHA-256, and checksum type must match; fail closed otherwise; `ETag` is
  recorded only as non-authoritative evidence (R4, R5).
- **Immutability**: no `version` on immutable tables; shared
  `socalytics.reject_immutable_change()` trigger and revoked `UPDATE`/`DELETE`;
  composite foreign keys make cross-Match members and mismatched mapping pairs
  impossible in the database too (R9, [data-model.md](data-model.md)).
- **Upload session**: the only mutable aggregate; `version` with the shared
  advance trigger and a strong ETag; completion guarded by `state = 'pending'`;
  no `If-Match` on transitions (R10).
- **Timeline mapping**: ordered 1:1 spans in seconds (millisecond precision);
  digest over canonical integer-millisecond JSON; identical content on the same
  recording version deduplicates (R6).
- **Retry keys**: one append-only table keyed by operation, Match, and key with
  request digest and resource identities; replays re-read immutable resources;
  start-upload replays issue a fresh grant (R7).
- **Finalized event record**: one immutable row per set version (unique
  constraint) holding the schema-valid payload; this feature makes no outbox
  call. Durable Analysis adds the `IOutbox` call to the finalize handler, a
  backfill for earlier records, and publication state in a separate structure
  (R8).
- **Contracts baseline**: repository-root `contracts/` with index
  `contracts/README.md`, the first schema at
  `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`
  (`$id` under `https://socalytics.invalid/contracts/…`, exact version in
  `x-socalytics-version`), and the contract command
  `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` (R15).
- **Local storage**: RustFS container in the AppHost (`objectstorage`
  resource, generated secret credentials, bucket created by the AppHost after
  `/health` succeeds, API waits for it); the same image in Testcontainers (R2,
  R12).

### Interfaces consumed

| From | Interface | Use in this feature |
| --- | --- | --- |
| Persistence foundation | `IUnitOfWork` | One transaction per command (rows, retry outcome, audit event) |
| Persistence foundation | `socalytics.advance_version()`, migration runner, `socalytics_app` / `socalytics_migrator` roles, structural test | Version trigger on `recording_upload_sessions`; new migration; privilege revocation on immutable tables |
| Persistence foundation | Integration.Tests PostgreSQL fixture | Shared database container; this feature adds the RustFS fixture |
| Club and Identity | Authenticated principal, session cookie, anti-forgery | Authentication and CSRF on all mutations |
| Club and Identity | `ITeamScopeResolver` | Resolve Match → Team and Season state, and the caller's current Club Admin / Coach / Viewer standing; re-evaluated on every request (FR-004) |
| Club and Identity | `IAuditTrail` | Minimized audit events in the same unit of work (FR-030) |
| Club and Identity | Problem-details catalog, Match table | Shared problem envelope; FK target for `match_id` |

If the sibling plans finalize different member signatures for
`ITeamScopeResolver` or `IAuditTrail`, only `RecordingAccessPolicy` and the
handlers' audit calls adapt; the behavior above is unchanged.

### Interfaces provided to other features

- **`IRecordingSetLookup`** (namespace
  `SocAlytics.Platform.Application.Recordings`):
  `Task<RecordingSetLineage?> GetAsync(Guid recordingSetVersionId, CancellationToken cancellationToken)`
  returning `RecordingSetLineage(RecordingSetVersionId, MatchId, TeamId, FinalizedAt, Members)`
  with ordered `RecordingSetLineageMember(Position, RecordingVersionId, RecordingContentDigest, TimelineMappingId, TimelineMappingDigest, Spans)`;
  `null` when unknown; database-only, no authorization (FR-028, SC-009). The
  `Spans` member is additive to the shared convention so Durable Analysis can
  compute segment coverage without reinterpreting lineage. Full shape in
  [data-model.md](data-model.md#application-read-model-for-other-features).
- **Recordings-finalized event record**: table `recording_finalized_events`
  (immutable, one row per set version) with payload schema
  `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`
  (mirrored here as
  [recordings-finalized.schema.json](contracts/schemas/recordings/recordings-finalized/v1/recordings-finalized.schema.json);
  `$id` `https://socalytics.invalid/contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`),
  event type `matches.recordings-finalized`, contract version `1.0.0`. This
  feature makes no outbox call; Durable Analysis adds the `IOutbox` call to the
  finalize handler, backfills outbox entries for records finalized earlier,
  and tracks publication state keyed by `event_id` without updating the row.
- **Contracts baseline**: the repository-root `contracts/` folder, its index
  `contracts/README.md`, and `src/platform/Tests/SocAlytics.Platform.Contracts.Tests`
  (contract command), which Durable Analysis extends with `contracts/common/v1`
  and its own schemas.
- **`IObjectStorage`** (namespace
  `SocAlytics.Platform.Application.Abstractions.ObjectStorage`): presign a
  single-object `PUT`, probe reachability, and read integrity evidence. Later
  data-plane features may extend it additively.

## Constitution Check (post-design)

| Gate | Status | Evidence in the design |
| --- | --- | --- |
| I. Architecture Is the Design Authority | PASS | Data model realizes the planned Recording-Lineage Foundation (immutable versions, mappings, set versions, ordered memberships, scoped idempotency outcomes, typed lineage query); the architecture changes the design depends on are stated verbatim below; no unresolved governance value is presented as adopted (grant lifetime, size limits, content types are configuration without production defaults). |
| II. Respect Source-Area Ownership | PASS | Project Structure lists only existing `src/platform/` production projects, the new `Tests/SocAlytics.Platform.Contracts.Tests` test project, and the repository-root `contracts/` folder defined by the contracts architecture; no new child of `src/`. |
| III. API-First Control Plane | PASS | 10 OpenAPI operations validated with an OpenAPI 3.1 validator; event payload validated as JSON Schema 2020-12 and made canonical under `contracts/` with an offline `$id`; no endpoint accepts binary bodies; `IObjectStorage` hides the S3 SDK; architecture test forbids `Amazon.*` outside Infrastructure and the AppHost. |
| IV. Evidence Over Claims | PASS | [quickstart.md](quickstart.md) maps every user story, edge case, and success criterion to an automated scenario against real PostgreSQL and RustFS, including atomicity under injected failure, 10-way concurrency, and secret scanning; the contract command validates the schema, its `$id`, version, index entry, and examples. |
| V. Focused, Minimal Changes | PASS | Three packages, seven tables, one migration, no new production project, one test project and one contract artifact required by the coordinator's contract baseline; deferred work (publication, multipart, cleanup, CORS, playback) stays deferred. |
| Technology and Tooling Constraints | PASS | No change to the supported commands; the contract command is an ordinary `dotnet test` of a solution project; the AppHost gains one container resource; nothing assumes Aspire in production. |
| Environment-feature rule | PASS (depends on environment feature) | Re-confirmed: the design needs only the .NET SDK, Docker, NuGet restore, and the Markdown linters, all provided by `environment-setup`. Verification of the new repository-root `contracts/` folder requires `specs/20261007-115855-environment-verification-coverage`, which extends `environment-verify` to run the platform checks for `contracts/` changes and must be merged before this feature starts; with it, the environment provides everything the tasks need. |
| Development Workflow and Quality Gates | PASS | Plan artifacts pass `node .github/scripts/check-markdown.mjs`; README (including the `environment-verify` coverage of `contracts/` that the environment feature cannot document), contract index, platform README, and `AGENTS.md` changes are planned under [Documentation Updates](#documentation-updates). |

## Documentation Updates

Implementation tasks of this feature make these repository documentation
changes. The environment feature
`specs/20261007-115855-environment-verification-coverage` may change only the
two action folders, so the README description of its behavior is updated here.

1. **`README.md`, section "Requesting Implementation on GitHub", list "In this
   repository:" under the composite actions** — replace the
   `environment-verify` bullet with:

   ```markdown
     - `environment-verify` runs the platform restore, build, and tests when
         `src/platform/**` or the repository-root `contracts/**` changed (the
         contract coverage is active because the platform solution exists, and
         the contract tests run as part of the platform tests) and the Markdown
         check when Markdown changed. It lists the paths it covers in
         `COVERED`; changed files outside them are reported as not covered,
         including platform and contract files when the platform solution is
         missing.
   ```

2. **`README.md`, section "Development"** — add a subsection after
   "Platform Host":

   ````markdown
   ### Contracts

   Canonical machine-readable contracts live in [`contracts/`](contracts/README.md),
   whose index lists every artifact with its owner, version, example, and
   validation command. Validate them from the repository root with the
   contract command:

   ```powershell
   dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
   ```

   The platform `dotnet test` command above also runs these tests.
   ````

3. **`README.md`, section "Repository Structure"** — add after the
   `docs/` bullet:

   ```markdown
   - [`contracts/`](contracts/README.md) holds the canonical machine-readable
       contracts (JSON Schema 2020-12) and their index.
   ```

4. **`README.md`, section "Platform Host"** — in the current-evidence
   paragraph add recording upload, lineage, and finalization with the local
   RustFS object storage resource, and remove "S3-compatible storage," from the
   deferred list (coordinated with the persistence and Club and Identity
   edits of the same paragraph).
5. **`contracts/README.md`** (new) — the index table with columns Artifact,
   Owner, Version, Example, Validation command, and one row:
   `recordings/recordings-finalized/v1/recordings-finalized.schema.json`,
   Recordings (control plane), `1.0.0`, embedded `examples`,
   `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`; plus
   the path, `$id`, and `x-socalytics-version` convention from
   [research.md](research.md) R15.
6. **`src/platform/README.md`** — current status gains recording upload and
   lineage, the RustFS AppHost resource, the Contracts.Tests project, and the
   contract command; remove "S3-compatible storage" from the deferred list.
7. **`AGENTS.md`, sections "Current State" and "Repository Setup"** — list the
   Contracts.Tests project and the repository-root `contracts/` folder in the
   current state, remove S3 from the absent infrastructure, and add the
   contract command to the supported platform commands.

## Required Architecture Updates

The coordinator applies these to `docs/architecture/`; this plan does not edit
them.

1. **`docs/architecture/match-data-pipeline.md`, section "Planned
   Recording-Lineage Foundation", first paragraph** — replace the sentence
   beginning "The planned Recordings functional area will persist" with:

   > The Recordings functional area persists, in the platform's application
   > schema, upload sessions as its only mutable aggregate, and immutable
   > recording versions, immutable timeline mappings, finalized recording-set
   > versions, ordered memberships, scoped idempotency outcomes, and exactly
   > one immutable recordings-finalized event record per recording-set
   > version, committed in the same transaction as the set. The Durable
   > Analysis workflow introduces the platform outbox and NATS publisher, adds
   > the outbox call to the finalization handler, and backfills records
   > finalized before it; it publishes them to `matches.recordings-finalized`,
   > tracks publication state separately, and never rewrites the record.

2. **`docs/architecture/match-data-pipeline.md`, section "Upload"** — add after
   the paragraph that ends "rather than overwriting accepted evidence.":

   > An upload grant is a presigned single-object `PUT` for one
   > platform-chosen key whose declared SHA-256 is bound as a signed
   > `x-amz-checksum-sha256` header. Completion accepts the object only when
   > object storage reports the declared size and a full-object SHA-256
   > checksum equal to the declaration; when storage cannot report that
   > evidence, completion fails closed. The API never reads or relays the
   > media.

3. **`docs/architecture/match-data-pipeline.md`, section "Segment Contract",
   bullet "A timeline mapping …"** — replace the bullet with:

   > - A **timeline mapping** immutably maps that recording version's media
   >   time to match time as an ordered list of spans, each mapping a
   >   half-open media interval to match time at a 1:1 rate, strictly
   >   increasing and non-overlapping in both media and match time. Its digest
   >   is `sha-256` over a canonical JSON form with integer milliseconds and
   >   changes whenever the mapping changes.

4. **`docs/architecture/platform-implementation.md`, new subsection
   "Object Storage" after "Persistence And CQRS"**:

   > ### Object Storage
   >
   > The Recordings area adopts S3-compatible object storage through the S3
   > protocol only. Infrastructure uses `AWSSDK.S3` with path-style addressing
   > against a configured endpoint; Application and Domain depend only on a
   > storage abstraction. The API issues presigned single-object upload grants
   > and reads storage-reported integrity evidence; it never receives media
   > bytes. Locally the Aspire AppHost runs a pinned RustFS container and
   > creates the development bucket, and integration tests use the same image
   > through Testcontainers. Bucket provisioning, production storage
   > selection, encryption, credentials, browser CORS, grant lifetime, and
   > lifecycle policy remain governed by Production Deployment and Operations
   > and Security and Data Governance.

   In the architecture file, link the two topic names to
   `production-operations.md` and `security-and-data-governance.md`.

5. **`docs/architecture/platform-implementation.md`, section "Source And
   Runtime Baseline", sentence "Locally, the Aspire AppHost will mirror that
   order: PostgreSQL, then the Migrator, then the API, which waits for the
   Migrator to complete."** — replace with:

   > Locally, the Aspire AppHost will mirror that order: PostgreSQL, then the
   > Migrator, then the API, which waits for the Migrator to complete and for
   > the S3-compatible RustFS container to report healthy.

6. **`docs/architecture/platform-implementation.md`, section "Persistence And
   CQRS", first sentence** — once this feature is implemented, remove
   "S3-compatible storage," from the list of absent infrastructure, in the
   same edit the persistence foundation makes to that sentence.

7. **`docs/architecture/platform-implementation.md`, section "Source And
   Runtime Baseline", list of projects under `src/platform`** — add the item
   `` `Tests/SocAlytics.Platform.Contracts.Tests` `` after
   `` `Tests/SocAlytics.Platform.Architecture.Tests` ``, and change "except for
   the two test projects grouped under `src/platform/Tests`" to "except for the
   test projects grouped under `src/platform/Tests`".

8. **`docs/architecture/contracts-and-compatibility.md`, section "Contract
   Authority", first paragraph** — replace the paragraph beginning "No
   machine-readable contracts exist yet." with:

   > Canonical machine-readable contracts live under the repository-root
   > `contracts/` directory. Recording Lineage and Upload introduces the
   > directory, its index `contracts/README.md`, which identifies every
   > published artifact, owner, version, example, and validation command, and
   > the first artifact, the recordings-finalized event schema. Each schema
   > lives at `contracts/<area>/<name>/v<major>/<name>.schema.json`, declares
   > the `$id`
   > `https://socalytics.invalid/contracts/<area>/<name>/v<major>/<name>.schema.json`
   > on a reserved domain that is never dereferenced, and records its exact
   > semantic version in `x-socalytics-version`. Until a behavior has a
   > canonical artifact, the architecture topics linked below remain
   > authoritative for it.

9. **`docs/architecture/contracts-and-compatibility.md`, section "Scope And
   Dependencies", first paragraph** — replace the sentence beginning "The
   first future contract baseline will cover the documented Analyst
   workflow:" with:

   > Recording Lineage and Upload establishes the contract baseline with the
   > recordings-finalized event schema. The documented Analyst workflow
   > contracts (shared resources and errors under `contracts/common/v1`,
   > logical segments, Analyst jobs and result manifests, capability
   > declarations, accepted upstream facts, and fenced attempt completion)
   > follow with Durable Analysis and extend the same directory, index, and
   > contract command.

   Keep the following sentence ("It excludes service implementation, …")
   unchanged.

10. **`docs/architecture/contracts-and-compatibility.md`, section "Validation
    Authority", first paragraph** — replace the sentence beginning "Once
    contracts exist, one repository contract command will be the
    deterministic authority" with:

    > The repository contract command,
    > `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`,
    > introduced by Recording Lineage and Upload, is the deterministic
    > authority for JSON Schema syntax and 2020-12 dialect, `$id` and version
    > metadata, offline references, index coverage, and example outcomes.
    > Features that add further contract kinds extend the same command with
    > unique-identifier checks, OpenAPI linting and bundling, and same-major
    > compatibility checks.

    Keep the remaining sentences of that paragraph (independent .NET and
    Python fixture validation, Kiota generation) as future validation.

## Open Risks

- **Sibling-plan interface drift**: exact signatures of `ITeamScopeResolver`
  and `IAuditTrail`, the problem `type` URI prefix, the session cookie and
  anti-forgery header names, and the Club match table name (FK target) are
  defined in parallel; the contract marks them as Club-owned, and tasks must
  align before implementation.
- **Event publication handoff**: per the coordinator decision, this feature
  stores the immutable record only. Until Durable Analysis merges and adds the
  `IOutbox` call plus its backfill, finalized events are recorded but not
  delivered; the backfill must cover every record created in that window.
- **Contract baseline extension**: Durable Analysis adds
  `contracts/common/v1`; definitions this schema keeps local (identifier,
  digest) may later be replaced by shared references in an additive revision
  that keeps the v1 `$id`.
- **5 GiB single-object limit**: 90-minute recordings can exceed it; multipart
  upload is out of scope and may block real use before analysis is useful.
- **Unbounded presigned `PUT` size**: storage abuse is possible until grant
  expiry (THR-003 Open / Blocking); oversize objects never become lineage.
- **Storage compatibility**: fail-closed verification requires full-object
  SHA-256 checksums; a production S3 implementation or gateway that cannot
  report them rejects every completion. RustFS gained this in July 2026, so
  the integration suite guards against regressions in the pinned image.
- **Endpoint reachability**: grants are signed for the configured
  `ServiceUrl`; deployments where clients and the API reach storage through
  different hosts need a separate public endpoint, which is deferred.
- **Browser uploads**: direct browser `PUT` needs bucket CORS; no client is in
  scope, so CORS is a deployment and client concern.
- **Abandoned sessions and objects** accumulate until POL-003 cleanup exists.
- **CI image pulls**: tests pull `rustfs/rustfs:1.0.1` from Docker Hub at run
  time, like the PostgreSQL image.

## Complexity Tracking

No constitution violations; no entries required.
