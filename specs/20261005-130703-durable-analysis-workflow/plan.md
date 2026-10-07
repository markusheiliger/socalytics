# Implementation Plan: Durable Analysis Workflow

**Branch**: `20261005-130703-durable-analysis-workflow` | **Date**: 2026-10-07 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261005-130703-durable-analysis-workflow/spec.md`

**Note**: This plan was produced by the `/speckit-plan` workflow. It follows the
shared planning conventions used by the five platform foundation plans.

## Summary

Turn a finalized recording-set version into one durable, restart-safe Analysis
Run whose PostgreSQL hierarchy (run, Workflow Nodes, dependency edges, Logical
Jobs, Execution Attempts, accepted-result references) is the single source of
truth. The Recordings finalized event record is relayed through the new
platform outbox to `matches.recordings-finalized`, and the API process consumes
that notification from NATS JetStream, confirms lineage through
`IRecordingSetLookup`, resolves the default workflow, computes analysis
segments from the lineage spans, validates the graph (cycles, dangling edges,
kinds, empty capability set), snapshots every resolved execution choice, fans
out hardware-neutral Logical Jobs per segment or per match, and evaluates
readiness (required, optional, conditional with a closed predicate vocabulary,
complete-coverage segment barriers, descendant blocking, run outcome) under a
per-run row lock. Analyst Managers claim ready work, heartbeat, and complete
through fenced, idempotent API operations; a Job Monitor worker detects lease
expiry from durable state. Every readiness occurrence and run transition writes
an outbox message in the same transaction; an in-process background publisher
delivers them to JetStream at least once with stable identities, bounded
retries, and observable state, and reconciliation republishes only currently
eligible work after startup, transport restoration, or an operator request.
Versioned JSON Schema 2020-12 contracts for the Analyst job, the attempt
completion, and the run-state-changed event, plus shared definitions, extend the
repository `contracts/` directory and the offline, deterministic contract test
suite that Recording Lineage and Upload introduces, adding same-major
compatibility checks.
Analyst execution is out of scope.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK `10.0.400` with `latestPatch` roll-forward from `src/platform/global.json`; nullable enabled, warnings as errors); PostgreSQL SQL for DbUp migrations; JSON Schema draft 2020-12 and OpenAPI 3.1 for contracts.

**Primary Dependencies**: ASP.NET Core minimal APIs with built-in OpenAPI (`Microsoft.AspNetCore.OpenApi` 10.0.0, existing); Npgsql 10.0.3 and Dapper 2.1.89 (from the persistence foundation); `NATS.Client.JetStream` 3.3.0 (Infrastructure only; `NATS.Net` 3.3.0 pinned centrally with transitive pinning so the Aspire hosting dependency also resolves to 3.3.0); `JsonSchema.Net` 8.0.5 (already pinned and used by the contract test project of Recording Lineage and Upload; this feature adds an Infrastructure reference for runtime validation; last MIT release); `Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.Diagnostics.HealthChecks` at the central floor 10.0.12 for directly referenced `Microsoft.Extensions.*` packages (Infrastructure workers and the `nats` and `analysis-workflow` health checks); `Aspire.Hosting.Nats` 13.4.6 (AppHost only, matching `Aspire.AppHost.Sdk/13.4.6`; `AddNats("nats").WithJetStream()` runs `nats:2.14`, confirmed by the platform spike); OpenTelemetry via ServiceDefaults (existing). See [research.md](research.md) R1, R2, R10.

**Storage**: PostgreSQL (one database per stamp, schema `socalytics`) is workflow truth: Analysis tables, the platform-wide `outbox_messages` table, and the sequence `analysis_fencing_token_seq`, created by four forward-only migrations (see [Migrations](#migrations)). NATS JetStream is transport only (events stream and analysis-jobs work-queue stream). No object-storage access: result manifests are referenced, not read.

**Testing**: xUnit v3 3.2.2, Shouldly 4.3.0, NSubstitute (version pinned by the persistence plan), NetArchTest.Rules 1.3.2, Aspire.Hosting.Testing 13.4.6, Microsoft.AspNetCore.Mvc.Testing 10.0.12, Testcontainers.PostgreSql 4.15.0, Testcontainers.Nats 4.15.0 (image `nats:2.14`, aligned with Aspire), Microsoft.Extensions.TimeProvider.Testing 10.10.0 (`FakeTimeProvider` for leases and retry schedules; pinned and referenced from Integration.Tests by Analyst Manager Registration, reused here without a new pin or reference), JsonSchema.Net 8.0.5 in the existing contract test project.

**Target Platform**: Linux OCI images for the API and the Migrator (cloud-neutral; Compose first, Azure Container Apps provisional); local development through the Aspire AppHost on Windows, macOS, or Linux with Docker.

**Project Type**: Web service — the layered-monolith control plane (Domain, Application, Infrastructure, Api) gains in-process hosted background workers; plus repository-level machine-readable contracts. No new deployable: the API hosts the workers; the persistence-owned Migrator stays the only extra host.

**Performance Goals**: Development evidence targets only (production objectives are Open / Blocking): run creation for a 20-node graph with 30 segments (about 600 Logical Jobs) commits in one transaction in under 2 s locally; claim, heartbeat, and completion p95 under 200 ms locally excluding readiness fan-out; outbox publication lag under 2 s with the transport available at the development poll interval of 1 s.

**Constraints**: PostgreSQL is the only workflow truth; NATS contents, consumer positions, and queue depth never influence readiness; delivery at least once with stable identities; the control plane never handles media bytes or issues access URLs; fencing tokens, payload bodies, secrets, and URLs never appear in logs, traces, or metric tags; every retry, lease, heartbeat, stale-timeout, execution-timeout, publication, and retention value is explicit configuration with development defaults, not adopted production values; workers must be safe with several API replicas (row locks with `SKIP LOCKED`, no leader election); lock order run → Logical Job → attempt.

**Scale/Scope**: One club per stamp; a full match of 90+ minutes at the 5-minute segmentation policy yields about 20 to 30 segments per recording, so a full-graph run has a few hundred Logical Jobs; tests use small fixture graphs plus one representative catalog-shaped graph. 46 functional requirements, 10 success criteria, five user stories.

All Technical Context unknowns are resolved in [research.md](research.md); no `NEEDS CLARIFICATION` remains.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-design gate (before Phase 0)

| Principle or rule | Status | Justification |
| --- | --- | --- |
| I. Architecture is the design authority | PASS | Design follows [job-processing.md](../../docs/architecture/job-processing.md) (four-level hierarchy, outbox boundary, readiness rules, blocking, run outcomes), [analyst-runtime-and-recovery.md](../../docs/architecture/analyst-runtime-and-recovery.md) (claim, lease, fencing, platform-detected expiry, retry budget), [match-data-pipeline.md](../../docs/architecture/match-data-pipeline.md) (analysis segment reference), [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md) (canonical `contracts/`, 2020-12, versioning, representation), and [platform-implementation.md](../../docs/architecture/platform-implementation.md) (layers, `version` triggers, state-guarded transitions). Clarifications the design adds are listed under [Required Architecture Updates](#required-architecture-updates) instead of diverging silently; unresolved production values stay unresolved. |
| II. Respect source-area ownership | PASS | All code is in `src/platform/` inside the existing layer projects (functional areas `Analysis`, `Registry`, plus shared `Messaging` and `Persistence` plumbing as namespaces). The repository-root `contracts/` directory is the architecture-designated contract location, not a source area under `src/`. No new first-level `src/` child; no empty placeholders. |
| III. API-first control plane | PASS | Analyst Managers interact only through REST claim, heartbeat, and completion operations; no component outside the platform reads the database; NATS messages confer no authority. Metadata only: manifests are opaque object keys, never URLs or bytes. Contracts are authoritative JSON Schema 2020-12 and OpenAPI 3.1, and the platform validates inbound payloads against the same schema files at runtime. |
| IV. Evidence over claims | PASS | Every FR and SC maps to an automated test (see [quickstart.md](quickstart.md)); host and architecture suites keep passing and gain NATS and layering assertions; local success is explicitly not production evidence. |
| V. Focused, minimal changes | PASS | Each new dependency is required by the spec or architecture (NATS client, NATS hosting, NATS Testcontainers, a 2020-12 validator, a fake clock). The Registry boundary is a thin port with a configuration-backed development stand-in because the spec allows fixtures; segment identities are computed from lineage the Recordings lookup already returns; no registry tables, Segment Service, or Analyst execution are built. |
| Technology constraints: deferred technologies | PASS | NATS JetStream and the transactional outbox are listed as deferred until a feature adopts them; this spec (FR-031 to FR-037) and plan adopt them. PostgreSQL, Dapper, and DbUp come from the persistence foundation. |
| Technology constraints: environment features | PASS with dependency | `environment-setup` already provides the .NET SDK and Docker (Testcontainers for PostgreSQL and NATS); no new SDK or tool is needed. The repository-root `contracts/` files this feature adds are covered by the environment feature [specs/20261007-115855-environment-verification-coverage](../20261007-115855-environment-verification-coverage/spec.md), which runs the platform restore, build, and tests for changes under `contracts/` while the platform solution exists. That feature is reviewed and merged first, and the coordinator names it under the spec's Assumptions → Dependencies. With it, the environment provides everything this feature's tasks need. |
| Quality gates | PASS | Markdown passes `node .github/scripts/check-markdown.mjs`; README and AGENTS.md updates for the new workers and the analysis contracts are planned (the contract command itself is documented by Recording Lineage and Upload); no product CI workflow is added. |

### Post-design re-check (after Phase 1)

| Principle or rule | Status | Evidence in the design |
| --- | --- | --- |
| I. Architecture authority | PASS | [data-model.md](data-model.md) realizes the hierarchy, immutable snapshots and accepted results, `version` triggers only on mutable aggregate roots (Analysis Run, Logical Job), and state-guarded transitions; fencing tokens come from a dedicated sequence, independent of `version`. The [Required Architecture Updates](#required-architecture-updates) (items 1 to 7) document clarifications (in-process workers, analysis contracts and shared definitions, compatibility baselines, predicate vocabulary, operator authorization, technology list); none contradicts an adopted decision. |
| II. Source-area ownership | PASS | [Project Structure](#project-structure) lists only existing `src/platform/` projects (including the contract test project created by Recording Lineage and Upload) and additions under the existing repository-root `contracts/`. |
| III. API-first | PASS | [contracts/openapi.yaml](contracts/openapi.yaml) exposes status, Manager, and operations endpoints; no run-creation endpoint exists because creation is notification-driven; schemas reject URLs, secrets, connection details, and media by construction ([contracts/README.md](contracts/README.md)). |
| IV. Evidence | PASS | [quickstart.md](quickstart.md) lists 18 runnable scenario classes, the contract command, and the AppHost smoke check, covering SC-001 to SC-010. |
| V. Focused changes | PASS | No new project: analysis contract tests extend the existing `Contracts.Tests` project, container-free Domain unit tests reuse `Integration.Tests`, and workers live in the API process. |
| Environment features | PASS with dependency | Unchanged: [specs/20261007-115855-environment-verification-coverage](../20261007-115855-environment-verification-coverage/spec.md) covers `contracts/`; no other SDK, tool, or check is needed. |

No violations require Complexity Tracking.

## Design Overview

### Components by layer

| Layer | Namespace | Responsibilities |
| --- | --- | --- |
| Domain | `SocAlytics.Platform.Domain.Analysis` | `WorkflowGraph` (validation: empty set, dangling edge, self edge, cycle via Kahn, edge kinds, predicate shape), `DependencyKind`, `ConditionalPredicate` (`capabilityRequested`, `nodeAccepted`, indeterminate → applicable), `RunGraphState` (immutable readiness input), `ReadinessEvaluator` (ready, waiting, blocked sets, input snapshots, node states), `RunOutcomePolicy`, `LogicalJobPlanner` (segment and match fan-out) over `PinnedJobSnapshot`, state enums with `AnalysisStateTransitions` (run, node, job, attempt; states persist as their PascalCase names) and `AnalysisPersistedNames` (kebab/lower-case names for scope, tier, and dependency kind only), value objects (`AnalysisSegmentReference`, `FencingToken`, `AttemptBudget`); digests reuse `SocAlytics.Platform.Domain.Recordings.Sha256Digest` instead of a second digest type |
| Application | `…Application.Abstractions` | `IOutbox`, `IMessageTransport`, `IContractValidator`, `ICanonicalJson`, `ICurrentAnalystManager` (new); `IUnitOfWork` (persistence), `ITeamScopeResolver`, `IAuditTrail` (Club and Identity) |
| Application | `…Application.Analysis` | `RunReadinessCoordinator` (the single place that applies readiness results under the run lock), `WorkflowSnapshotMapper` (resolved definition → graph, pinned job snapshots, run snapshot and digest); commands `HandleRecordingsFinalizedNotification`, `ClaimLogicalJob`, `RenewAttemptLease`, `CompleteAttempt`, `ExpireStaleAttempts`, `ReconcileAnalysisPublication`; queries `GetAnalysisRun`, `ListMatchAnalysisRuns`, `GetAnalysisOperationalSummary`, `ListNotificationReceipts`; port `IAnalysisSegmentSource`; store ports for runs, jobs, attempts, results, receipts, completion outcomes |
| Application | `…Application.Registry` | Ports `IWorkflowDefinitionResolver` returning `ResolvedWorkflowDefinition`, and `IWorkflowDefinitionAvailability` (current resolvability status) |
| Application | `…Application.Messaging` | Commands `PublishOutboxBatch`, `PurgeOutboxMessages`; queries `ListOutboxMessages`, `GetOutboxMessage`; retry and backoff policy |
| Infrastructure | `…Infrastructure.Persistence` (+ `Persistence/Migrations`) | Dapper stores for Analysis and the outbox, the four migrations, `IOutbox` over the current unit-of-work transaction |
| Infrastructure | `…Infrastructure.Messaging.Nats` | `NatsConnection` lifetime (connection attempt started at application startup when `ConnectionStrings:nats` is configured, so health is known without a publish), topology provisioning (development), `NatsJetStreamTransport : IMessageTransport`, `nats` health check (registered only when `ConnectionStrings:nats` is configured; degraded, never unhealthy, on failure), reconnect detection that triggers reconciliation |
| Infrastructure | `…Infrastructure.Registry` (availability) | `WorkflowDefinitionAvailability` (periodic and change-triggered resolvability check) and the readiness health check `analysis-workflow` (unhealthy with a sanitized category while no workflow definition resolves, FR-046) |
| Infrastructure | `…Infrastructure.Workers` | Hosted services `OutboxPublisherWorker`, `OutboxRetentionWorker`, `LeaseExpiryWorker` (Job Monitor), `RecordingsFinalizedConsumerWorker`, `PublicationReconciliationWorker` (startup and on reconnect), all on a shared internal `WorkerLoop`; each waits for the database readiness check, catches every non-shutdown failure (including `transport-unavailable`), and backs off without letting an exception stop the host; the NATS-dependent workers (publisher, consumer, reconciliation) return immediately when `ConnectionStrings:nats` is not configured, so such hosts stay up and `/health` stays `Healthy` |
| Infrastructure | `…Infrastructure.Contracts` | `JsonSchemaContractValidator : IContractValidator` over embedded copies of the `contracts/` schema files (the Recordings event schema, the shared definitions, and the analysis schemas); RFC 8785 canonicalizer for completion fingerprints |
| Infrastructure | `…Infrastructure.Registry` | Development stand-in `ConfiguredWorkflowDefinitionResolver` (reports "dependency unavailable" when unconfigured) |
| Infrastructure | `…Infrastructure.Analysis` | `LineageAnalysisSegmentSource : IAnalysisSegmentSource`, computing analysis-segment references from `IRecordingSetLookup` spans and the pinned segmentation policy per the Segment Contract (no materialization) |
| Application | `…Application.Recordings` (existing, owned by Recording Lineage and Upload) | The finalize command handler additionally enqueues its canonical recordings-finalized payload through `IOutbox` in the same unit of work; nothing else in Recordings changes |
| Api | `SocAlytics.Platform.Api.Analysis`, `…Api.Operations` | Minimal API endpoint groups from [contracts/openapi.yaml](contracts/openapi.yaml), the merged authorization policy `AuthorizationPolicyNames.AnalystManager` (scheme `AnalystManagerDPoPDefaults.AuthenticationScheme`, scope `analyst-manager`), `ICurrentAnalystManager` from the claim `AnalystManagerClaimTypes.ManagerId` of the authenticated principal, shared problem-details mapping (`urn:socalytics:problem:<code>`), weak ETag on run views |
| AppHost | — | `nats` resource with JetStream; API `.WithReference(nats).WaitFor(nats)` |

All Application and Infrastructure implementation types stay internal; only
`AddApplication()` and `AddInfrastructure(...)` are public.

### Key flows

0. **Recordings event relay**: the Recordings finalize handler enqueues the
   canonical payload (message id = `event_id`) through `IOutbox` in the same
   transaction that inserts its `recording_finalized_events` row; the outbox
   migration backfills rows finalized before this feature. The publisher
   delivers it to `matches.recordings-finalized`.
1. **Notification → run**: consumer validates the payload (schema), opens a unit
   of work, inserts the receipt (`ON CONFLICT DO NOTHING`; duplicate → recorded
   outcome), calls `IRecordingSetLookup.GetAsync`, compares match, team, ordered
   members, and digests, resolves the workflow, computes segments from the
   lineage spans and the pinned segmentation policy, validates the
   graph, inserts the run as `Requested` with its snapshot (invalid graph:
   `Failed` with reason, nothing else), inserts nodes, edges, node snapshots,
   jobs, and job snapshots, evaluates readiness (root jobs `Ready`, input
   snapshots, readiness occurrences, ready-work outbox messages), moves the run
   to `Running`, writes run-state-changed outbox messages, commits, and acks.
   Recordings lookup unavailable → rollback and negative acknowledgement with
   delay. Workflow definition unresolvable → the consumer has already stopped
   fetching and `analysis-workflow` reports not ready (FR-046); a message in
   hand is negatively acknowledged with delay.
2. **Claim**: reject a missing `Idempotency-Key` header with `400
   idempotency-key-missing`; lock job; look up `(manager_id, Idempotency-Key)` in
   `analysis_claim_keys` (same job and occurrence with a still-current attempt →
   replay the same attempt, lease expiry, and token with `200`; different job or
   occurrence → `409 idempotency-key-reused`; attempt no longer current → `409
   claim-obsolete`); otherwise guard `Ready`, current occurrence, budget; insert
   attempt `n = attempts_used + 1` with lease and a token from the sequence and
   the claim-key row; job → `Claimed`; `201`.
3. **Heartbeat**: lock job, then guarded update of the attempt lease
   (`Active`, not expired, token, Manager, job).
4. **Completion**: schema validation; replay check by `(logical_job_id,
   idempotency_key)` and fingerprint; lock run, job, attempt; fencing and
   snapshot and lineage checks; accept (result, attempt `Accepted`, job
   `Completed`) or record failure or rejection; readiness re-evaluation, node
   and run transitions, outbox messages; store the idempotency outcome; commit.
5. **Lease expiry**: worker selects expired `Active` attempts; per attempt lock
   run, job, attempt; re-check guard; attempt `Stale`; job back to `Ready` with
   a new occurrence and message, or `Failed` with blocking and run-outcome
   evaluation.
6. **Publication**: worker claims due `pending` outbox rows with
   `SKIP LOCKED`, publishes with `Nats-Msg-Id`, records the outcome or
   schedules the retry, and marks `failed` at the limit.
7. **Reconciliation**: in batches under run locks, resets the outbox messages of
   currently `Ready` jobs' current occurrences and of `failed` or `pending`
   run-state notifications to `pending` with `publication_round + 1`; never
   touches messages of non-ready jobs.

### Configuration (development defaults, not production values)

| Section | Keys and development defaults |
| --- | --- |
| `ConnectionStrings:nats` | Injected by Aspire locally; supplied by the deployment profile elsewhere |
| `Messaging:Nats` | `ProvisionTopology` (true in Development), `EventsStream` `SOCALYTICS_EVENTS` (subjects `matches.>`, `analysis.events.>`), `JobsStream` `SOCALYTICS_ANALYSIS_JOBS` (subjects `analysis.jobs.ready.>`, work-queue retention), `DuplicateWindowSeconds` 120 |
| `Messaging:Outbox` | `PollIntervalSeconds` 1, `BatchSize` 50, `MaxAttempts` 8, `BackoffBaseSeconds` 2, `BackoffMaxSeconds` 300, `RetentionPeriodHours` 168, `RetentionIntervalMinutes` 60 |
| `Analysis:Notifications` | `ConsumerName` `analysis-scheduler-recordings-finalized`, `NakDelaySeconds` 10, `ResolveRecheckSeconds` 15; the consumer always uses `MaxDeliver` -1 (a design rule, not a tunable) and pauses fetching while the workflow definition is unresolvable (FR-046) |
| `Analysis:Leases` | `MonitorIntervalSeconds` 5, `BatchSize` 50 |
| `Analysis:Reconciliation` | `OnStartup` true, `OnReconnect` true, `BatchSize` 200 |
| `Analysis:Workers` | `Enabled` true (tests may disable workers and drive the commands directly) |
| `Analysis:DefaultWorkflow` | Development fixture workflow (one segment-scoped and one match-scoped capability, segmentation policy v1 with a 300-second duration) in `appsettings.Development.json` |

Lease, stale-timeout, execution-timeout, and attempt-budget values come from the
resolved workflow snapshot, never from global configuration.

### Migrations

Named by description only; the sequence number is the next free number at
implementation time (`NNNN_<area>_<description>.sql` under
`src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/`):

1. `analysis_outbox_messages` — `outbox_messages` with indexes and the
   `(message_type, causation_id)` uniqueness; one-time backfill of existing
   `recording_finalized_events` rows (`INSERT … SELECT … ON CONFLICT DO
   NOTHING`). The area token is `analysis`, one of the areas the persistence
   foundation's migration-authoring contract lists, because this feature
   introduces the shared outbox.
2. `analysis_runs_and_workflow_graph` — runs (versioned), run snapshots, nodes,
   node snapshots, edges; `attach_version_trigger` and
   `attach_aggregate_child_triggers` calls; `reject_immutable_change()`
   triggers and `UPDATE`/`DELETE` revoked from `socalytics_app` on immutable
   tables.
3. `analysis_logical_jobs_and_attempts` — Logical Jobs (versioned), job
   snapshots, input snapshots, readiness occurrences, attempts, claim keys
   (`analysis_claim_keys`, immutable), sequence
   `analysis_fencing_token_seq` (`MAXVALUE 9007199254740991`; `USAGE` comes
   from the persistence default privileges); triggers and revokes.
4. `analysis_results_receipts_and_idempotency` — accepted results, completion
   outcomes, notification receipts; immutability triggers and revokes.

Each table without `version` is added to the persistence foundation's
`PersistedTableClassifications` manifest (see [data-model.md](data-model.md)).

### Interfaces consumed and provided

| Direction | Interface | Owner | Use |
| --- | --- | --- | --- |
| Consumed | `IUnitOfWork.BeginAsync` → `IUnitOfWorkScope` (`CommitAsync`, `RollbackAsync`); internal `IDbSession` for Dapper writes | Persistence foundation | One unit of work per command and per consumed message |
| Consumed | `socalytics.attach_version_trigger(regclass)`, `socalytics.attach_aggregate_child_triggers(regclass, regclass, name, name)`, manifest `Structure/PersistedTableClassifications.cs`, structural test, Migrator, test support `TestMigrationCatalogs` and `MigratorHarness` | Persistence foundation | Versioned, child, immutable, and unversioned tables; the outbox backfill test applies a catalog prefix |
| Consumed | `IRecordingSetLookup.GetAsync(Guid recordingSetVersionId, CancellationToken)` → `RecordingSetLineage?` (Match, Team, `FinalizedAt`, ordered members with recording-version and timeline-mapping identities, digests, and `Spans`) | Recording Lineage and Upload | Lineage confirmation and segment computation |
| Consumed | Table `recording_finalized_events`, schema `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`, the finalize command handler, `socalytics.reject_immutable_change()`, Domain `Sha256Digest`, the shared problem codes `idempotency-key-missing` (400) and `idempotency-key-reused` (409) in the Api constants class `SharedProblemCodes`, `RustFsContainerFixture` and the Integration.Tests host factory's object-storage test values | Recording Lineage and Upload | Event relay through the outbox (handler gains one `IOutbox` call; existing rows backfilled; row never updated); immutability trigger and digest type reused; claim and completion key errors; recording sets finalized in Analysis integration tests |
| Consumed | Repository-root `contracts/` with the index `contracts/README.md`, the `$id` convention `https://socalytics.invalid/contracts/<path>`, the test project `src/platform/Tests/SocAlytics.Platform.Contracts.Tests` (JsonSchema.Net 8.0.5) with its single `ContractCatalog` (`Schemas`, `IndexRows`, `Releases`, `LoadRegistry()`; released copies only in `Releases`), `ContractIndexTests` (whose path rule already accepts the shared-definitions path `contracts/common/v<major>/common.schema.json`), and `SchemaMetaValidationTests` (offline cross-file `$ref` resolution through `LoadRegistry()`), and the contract command `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` | Recording Lineage and Upload | Extended (never re-created) with shared definitions, analysis schemas, index rows, examples, released copies, and same-major compatibility checks over `ContractCatalog.Releases` |
| Consumed | Authenticated principal, `ITeamScopeResolver`, `IAuditTrail` (details allow-list extended with `readyJobsRepublished` and `notificationsRepublished`), Club Admin authorization, `X-CSRF-Token` antiforgery, `AuthorizationPolicyNames`, shared problem envelope (`urn:socalytics:problem:<code>`, `code`, `correlationId`, `errors`) | Club and Identity | Run status scope, operator authorization, audit of operator reconciliation (`analysis.publication-reconciled`), errors |
| Consumed | Authentication scheme `AnalystManagerDPoPDefaults.AuthenticationScheme` (`AnalystManagerDPoP`), policy `AuthorizationPolicyNames.AnalystManager`, scope `analyst-manager`, claim types `AnalystManagerClaimTypes.ManagerId` (`client_id`, a `uuid`), `RegistrationId`, and `Scope`, test support `Registry/Support/TestAnalystManager.cs`, package `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 | Analyst Manager Registration (merged before this feature) | Manager-facing operations require the policy and read the Manager id through the claim constant; tests use a test-only handler that emits the same claim types and also prove a successful claim and a revocation through the real scheme |
| Provided | `IOutbox`, `IMessageTransport`, `outbox_messages`, `OutboxPublisherWorker` | This feature (shared plumbing) | Any feature that publishes events; first used for the Recordings finalized event |
| Provided | `IContractValidator` (runtime validation); `contracts/common/v1/common.schema.json`; the same-major compatibility checker and released-copy baselines in `Contracts.Tests` | This feature | Later contracts and runtime validation |
| Provided | `IWorkflowDefinitionResolver` (port; development stand-in), `IAnalysisSegmentSource` (port; lineage-based implementation) | This feature; the Registry and Segment Service may later provide implementations | Run creation |
| Provided | `ICurrentAnalystManager` | This feature | Manager-facing operations |
| Provided | Readiness health check `analysis-workflow` (FR-046) | This feature | Deployment readiness and operators |
| Provided | REST operations in [contracts/openapi.yaml](contracts/openapi.yaml); subjects `matches.recordings-finalized` (relay), `analysis.jobs.ready.<capabilityId>`, and `analysis.events.run-state-changed`; schemas analyst-job v1, attempt-completion v1, run-state-changed v1 | This feature | Analyst Managers, future clients and agents |

## Project Structure

### Documentation (this feature)

```text
specs/20261005-130703-durable-analysis-workflow/
├── spec.md
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── README.md
│   ├── openapi.yaml
│   └── schemas/         # Mirrors repository-root contracts/
│       ├── common/v1/common.schema.json
│       ├── analysis/analyst-job/v1/analyst-job.schema.json
│       ├── analysis/attempt-completion/v1/attempt-completion.schema.json
│       └── analysis/run-state-changed/v1/run-state-changed.schema.json
├── checklists/requirements.md
└── tasks.md             # Phase 2 output (/speckit-tasks; not created here)
```

### Source Code (repository root)

```text
contracts/                                            # Existing (created by Recording Lineage and Upload)
├── README.md                                         # Existing index: + entries for the shared definitions and analysis schemas
├── common/v1/common.schema.json                      # New shared definitions
├── analysis/analyst-job/v1/{analyst-job.schema.json, examples/valid/, examples/invalid/, releases/1.0.0.schema.json}
├── analysis/attempt-completion/v1/{attempt-completion.schema.json, examples/, releases/1.0.0.schema.json}
├── analysis/run-state-changed/v1/{run-state-changed.schema.json, examples/, releases/1.0.0.schema.json}
└── recordings/recordings-finalized/v1/recordings-finalized.schema.json   # Existing, Recordings-owned; consumed only

src/platform/
├── Directory.Packages.props                          # + NATS.Client.Core/JetStream, NATS.Net, Aspire.Hosting.Nats,
│                                                     #   Testcontainers.Nats, hosting and health-check abstractions,
│                                                     #   each only if absent (JsonSchema.Net 8.0.5 is pinned by Recordings;
│                                                     #   TimeProvider.Testing 10.10.0 by Analyst Manager Registration)
├── SocAlytics.Platform.slnx                          # Unchanged (no new project)
├── SocAlytics.Platform.Domain/Analysis/
│   ├── WorkflowGraph.cs, DependencyKind.cs, ConditionalPredicate.cs
│   ├── ReadinessEvaluator.cs, RunOutcomePolicy.cs, LogicalJobPlanner.cs
│   ├── AnalysisRunState.cs, WorkflowNodeState.cs, LogicalJobState.cs, ExecutionAttemptState.cs
│   ├── AnalysisStateTransitions.cs, AnalysisPersistedNames.cs, RunGraphState.cs, PinnedJobSnapshot.cs
│   └── AnalysisSegmentReference.cs, FencingToken.cs, AttemptBudget.cs   # digests reuse Domain/Recordings/Sha256Digest.cs
├── SocAlytics.Platform.Application/
│   ├── Recordings/                                   # Existing finalize handler: + IOutbox enqueue of its event
│   ├── Abstractions/IOutbox.cs, IMessageTransport.cs, IContractValidator.cs, ICurrentAnalystManager.cs
│   ├── Analysis/                                     # Commands, queries, store ports, IAnalysisSegmentSource
│   ├── Registry/IWorkflowDefinitionResolver.cs, ResolvedWorkflowDefinition.cs
│   └── Messaging/PublishOutboxBatch.cs, PurgeOutboxMessages.cs, ListOutboxMessages.cs, OutboxRetryPolicy.cs
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/Analysis/                         # Dapper stores
│   ├── Persistence/Messaging/OutboxStore.cs, Outbox.cs
│   ├── Persistence/Migrations/NNNN_analysis_outbox_messages.sql
│   ├── Persistence/Migrations/NNNN_analysis_runs_and_workflow_graph.sql
│   ├── Persistence/Migrations/NNNN_analysis_logical_jobs_and_attempts.sql
│   ├── Persistence/Migrations/NNNN_analysis_results_receipts_and_idempotency.sql
│   ├── Messaging/Nats/NatsConnectionProvider.cs, NatsTopology.cs, NatsJetStreamTransport.cs, NatsHealthCheck.cs
│   ├── Workers/WorkerLoop.cs, OutboxPublisherWorker.cs, OutboxRetentionWorker.cs, LeaseExpiryWorker.cs,
│   │           RecordingsFinalizedConsumerWorker.cs, PublicationReconciliationWorker.cs
│   ├── Contracts/JsonSchemaContractValidator.cs, JsonCanonicalizer.cs   # Embeds ../../../contracts/**/*.schema.json
│   ├── Registry/ConfiguredWorkflowDefinitionResolver.cs, WorkflowDefinitionAvailability.cs, AnalysisWorkflowHealthCheck.cs
│   └── Analysis/LineageAnalysisSegmentSource.cs
├── SocAlytics.Platform.Api/
│   ├── Analysis/AnalysisRunEndpoints.cs, AnalystManagerEndpoints.cs, AnalystManagerAuthorization.cs
│   ├── Operations/WorkflowOperationsEndpoints.cs
│   ├── Program.cs                                    # Maps the endpoint groups
│   └── appsettings.Development.json                  # Development workflow stand-in
├── SocAlytics.Platform.AppHost/Program.cs            # + nats resource, API reference and wait
└── Tests/
    ├── SocAlytics.Platform.Architecture.Tests/       # + Domain/Application free of NATS, Npgsql, Dapper, JsonSchema.Net;
    │                                                 #   workers and transports internal
    ├── SocAlytics.Platform.Host.Tests/               # + nats resource, OpenAPI operations present, aggregate /health
    │                                                 #   Healthy with nats registered
    ├── SocAlytics.Platform.Integration.Tests/        # From persistence; + NATS fixture and Analysis/ test classes
    │                                                 #   (per-check health through HealthCheckService):
    │   ├── Analysis/                                 #   RunCreationTests, ReadinessRecoveryTests, ExecutionAttemptTests,
    │                                                 #   CompletionTests, OutboxPublicationTests, TransportRecoveryTests,
    │                                                 #   OutboxRetentionTests, RestartMatrixTests, PayloadConformanceTests,
    │                                                 #   AnalysisTelemetryTests, AnalysisEndpointTests, ClaimIdempotencyTests,
    │                                                 #   WorkflowAvailabilityTests, WorkerResilienceTests
    │   └── Analysis/Domain/                          #   WorkflowGraphTests, LogicalJobPlanningTests, ReadinessEvaluatorTests
    └── SocAlytics.Platform.Contracts.Tests/          # Existing (Recordings): + analysis rows and example outcomes through
                                                      #   ContractCatalog.LoadRegistry(), compatibility checker,
                                                      #   breaking-revision fixtures, determinism
```

Domain unit tests (`WorkflowGraphTests`, `LogicalJobPlanningTests`,
`ReadinessEvaluatorTests`) need no containers; they are placed in
`Tests/SocAlytics.Platform.Integration.Tests/Analysis/Domain/` so no further
test project is added, and they reach any internal types through
`InternalsVisibleTo` granted to the test assemblies.

Repository documentation updated at implementation: `AGENTS.md` (analysis
contracts, worker behavior), `src/platform/README.md` (NATS resource, workers),
and the existing `contracts/README.md` index (new entries); `README.md` only if
the documented commands change.

**Structure Decision**: Keep the layered monolith unchanged: Analysis behavior is
a functional area inside the existing Domain, Application, Infrastructure, and
Api projects, background processing runs as hosted services inside the API
process, and no project is added. Analysis contracts extend the
repository-root `contracts/` directory and the contract test project that
Recording Lineage and Upload creates.

## Required Architecture Updates

The coordinator applies these; this plan does not edit `docs/architecture/` or
`spec.md`.

1. **`docs/architecture/platform-implementation.md`, section "Persistence And
   CQRS"** — replace the paragraph beginning "Database changes and outgoing
   events commit atomically" with:
   > Database changes and outgoing events commit atomically through a
   > PostgreSQL transactional outbox (`socalytics.outbox_messages`). The API
   > process hosts the background workers: an outbox publisher that delivers
   > pending records to NATS JetStream at least once with bounded retries and
   > records each outcome, a retention worker that removes only records whose
   > publication outcome is known, the Job Monitor that detects expired attempt
   > leases from durable state, the consumer of `matches.recordings-finalized`,
   > and publication reconciliation at startup and after transport
   > reconnection. Workers claim rows with `FOR UPDATE SKIP LOCKED`, so several
   > API replicas run them safely without leader election, and they add no
   > deployable. Consumers and completion handlers remain idempotent.
2. **`docs/architecture/platform-implementation.md`, section "Source And Runtime
   Baseline"** — no change from this feature. The
   `Tests/SocAlytics.Platform.Contracts.Tests` list item arrives with Recording
   Lineage and Upload, which creates that project; this feature only extends it.
3. **`docs/architecture/platform-implementation.md`, section "API And
   Identity"** — append:
   > Until a dedicated stamp-operator role is defined, operational workflow
   > inspection and reconciliation operations under `/api/v1/operations` are
   > restricted to Club Admins and audited when they change state.
4. **`docs/architecture/contracts-and-compatibility.md`, section "Contract
   Authority"** — after the text with which Recording Lineage and Upload
   replaces "No machine-readable contracts exist yet…", append:
   > Recording Lineage and Upload introduced the first artifact (the
   > recordings-finalized event schema), the `$id` convention, and the contract
   > test project. The Durable Analysis Workflow adds the shared definitions in
   > `contracts/common/v1/` and the Analyst job, attempt completion, and
   > run-state-changed schemas under `contracts/analysis/`.
5. **`docs/architecture/contracts-and-compatibility.md`, section
   "Representation Conventions"** — after the `$id` convention bullet added by
   Recording Lineage and Upload, add the bullet:
   > Shared definitions live in `contracts/common/v1/common.schema.json` and
   > are referenced through relative `$ref` values. Every released exact
   > version of a schema is kept as an immutable copy under its
   > `releases/<version>.schema.json` and is the baseline for same-major
   > compatibility checks; the exact version is recorded in
   > `x-socalytics-version` and in each payload's `contractVersion`.
   And in section "Validation Authority", after the sentence naming the
   contract command `dotnet test
   src/platform/Tests/SocAlytics.Platform.Contracts.Tests`, add:
   > It also checks every schema against its immutable released copies in the
   > same major version and fails on removed or renamed properties, newly
   > required inputs, narrowed values, or newly closed objects.
6. **`docs/architecture/job-processing.md`** — in section "Planned Durable
   Foundation", replace "Machine-readable job and completion schemas will be
   introduced with the first Scheduler and Analyst implementation slice and
   must preserve these identities and fencing boundaries." with:
   > Machine-readable job and completion schemas are introduced by the Durable
   > Analysis Workflow slice, ahead of Analyst execution, under
   > `contracts/analysis/`; they preserve these identities and fencing
   > boundaries and evolve additively within their major version.
   In section "Readiness And Dependencies", after "A conditional dependency is
   required only when its declared predicate applies; otherwise it behaves as
   an optional input." add:
   > Predicates come from a closed, versioned vocabulary and are evaluated only
   > from durable run state and accepted results; a predicate that cannot yet
   > be determined is treated as applicable. Readiness changes for one run are
   > serialized so that concurrent completions cannot hide each other's
   > accepted results.
7. **`docs/architecture/tenancy-and-technology.md`, section "Technology
   Stack"** — under "Event Backbone" add "NATS.Net v3 client
   (`NATS.Client.JetStream`)"; under "Control Plane" add "JSON Schema 2020-12
   validation of contract payloads with JsonSchema.Net (MIT 8.x line)" unless
   Recording Lineage and Upload already added it.
8. **Environment dependency (resolved by the coordinator)** — the
   repository-root `contracts/` coverage comes from
   [specs/20261007-115855-environment-verification-coverage](../20261007-115855-environment-verification-coverage/spec.md);
   the coordinator names it in this feature's `spec.md`. No separate
   environment feature is requested.
9. **Specification alignment (`spec.md`, Assumptions, bullet "Dependency on
   recording lineage and upload")** — confirmed by the coordinator, who
   applies it. Replace "Publishing that
   notification is owned by that feature; until it is available end to end,
   this feature is exercised with canonical injected notifications validated
   against its boundary." with:
   > The Recordings feature durably records the notification; this feature
   > introduces the platform outbox and publishes that record through it
   > without modifying it. Tests may also inject canonical notifications
   > directly into the message transport.

## Risk Register

| ID | Risk | Disposition | Evidence / Owner | Revisit trigger |
| --- | --- | --- | --- | --- |
| DAW-R1 | JsonSchema.Net licence: fixes after 8.0.5 ship only in the OSMF-EULA 9.x line | **Accepted**: pinned to 8.0.5, the last MIT release, which fully supports draft 2020-12 for the planned use | Platform spike and risk brief confirm 8.0.5 MIT; [research.md](research.md) R10. Owner: platform maintainers | A security advisory or required fix affecting 8.0.5, or a needed feature only in 9.x |
| DAW-R2 | NATS.Net 3.3.0 in Infrastructure while `Aspire.Hosting.Nats` 13.4.6 was built against NATS.Net 2.7.3 | **Mitigated** | NATS.Net 3 spike: no restore warnings, Aspire's in-process NATS health check and all used JetStream calls pass on 3.3.0; central transitive pinning keeps every NATS package on 3.3.0; plain NATS container verified as fallback ([research.md](research.md) R1) | Any Aspire upgrade (re-run the host tests), or a NATS.Net major release |
| DAW-R3 | Active attempts of a revoked Analyst Manager | **Mitigated** | Every Manager call passes `AnalystManagerDPoP`, which checks the active registration on each request, so a revoked Manager can no longer claim, heartbeat, or complete; its attempts stop renewing and recover through lease expiry (`LeaseExpiryWorker`, FR-024, FR-025; [research.md](research.md) R8) | Revocation must free work faster than the stale timeout, or Manager authentication changes |
| DAW-R4 | Missing or unresolvable workflow definition stalls notifications or exhausts delivery limits | **Mitigated** (FR-046) | `analysis-workflow` readiness check reports not ready with a sanitized category; the consumer pauses fetching with `MaxDeliver` -1 and resumes when resolvable ([research.md](research.md) R5, R12; quickstart scenario 16) | Registry boundary replaces the development stand-in |
| DAW-R5 | Clock skew between API replicas affects lease expiry | **Accepted** with a configuration rule: stale timeout must exceed the heartbeat interval plus tolerated clock skew; hosts use synchronized clocks | Leases use the injected `TimeProvider` ([research.md](research.md) R8); production values follow Production Deployment and Operations. Owner: operations owner | Observed premature expiry, or multi-host deployment without clock synchronization |
| DAW-R6 | Lost claim response costs an attempt and waits for lease expiry | **Mitigated** (FR-045) | Claim `Idempotency-Key` scoped to the Manager; `analysis_claim_keys` replays the still-current attempt, lease, and token ([data-model.md](data-model.md), [contracts/openapi.yaml](contracts/openapi.yaml)) | Manager protocol changes or keys need expiry under POL-006 |
| DAW-R7 | Large run creation (hundreds of rows in one transaction under the run lock) | **Deferred**: measure before production objectives are set | Development target in Technical Context. Owner: Scheduler and Job Registry owner, with GOV-OBJ job-acceptance objective | Production capacity work, or local run creation exceeding the development target |
| DAW-R8 | Cross-area relay of the Recordings finalized event | **Mitigated**: decided by the coordinator | The Recordings finalize handler enqueues through `IOutbox`; existing rows are backfilled; the Recordings row is never updated ([research.md](research.md) R4; spec clarification) | Recordings changes its event record or finalize handler |
| DAW-R9 | Recordings schema registration and contract test ownership diverge | **Mitigated**: canonical names | Recording Lineage and Upload owns `contracts/`, the `$id` base `https://socalytics.invalid/contracts/`, `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`, and `Tests/SocAlytics.Platform.Contracts.Tests` with its single `ContractCatalog` (`LoadRegistry()` resolves cross-file `$ref`s offline; released copies exposed only through `Releases`; the index path rule already accepts `contracts/common/v<major>/common.schema.json`); this feature only appends schemas, index rows, and tests ([research.md](research.md) R10, R11) | Contract layout, test-project registration, or the path rule changes |
