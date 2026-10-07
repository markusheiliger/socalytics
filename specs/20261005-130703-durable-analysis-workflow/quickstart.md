# Quickstart: Durable Analysis Workflow

Validation guide for [plan.md](plan.md). It lists how to prove each user story
and success criterion once the tasks are implemented. Contracts are described in
[contracts/README.md](contracts/README.md) and the persistence model in
[data-model.md](data-model.md); this guide does not repeat them.

Successful local runs are development evidence only. They do not establish
deployment support or production readiness, and every retry, lease, heartbeat,
timeout, and retention value used here is explicit development configuration.

## Prerequisites

- .NET SDK selected by `src/platform/global.json`.
- Docker running locally (Testcontainers starts PostgreSQL and `nats:2.14`; the
  AppHost starts PostgreSQL and NATS containers).
- The persistence foundation, Club and Identity foundation, and Recording
  Lineage and Upload features are merged (this feature consumes `IUnitOfWork`,
  the Migrator, `Integration.Tests`, `ITeamScopeResolver`, `IAuditTrail`,
  `IRecordingSetLookup`, the repository-root `contracts/` directory with its
  index, and `Contracts.Tests`).
- The environment feature
  [specs/20261007-115855-environment-verification-coverage](../20261007-115855-environment-verification-coverage/spec.md),
  which covers `contracts/` with the platform checks, is merged.
- Node.js with the Markdown linters for documentation checks.

## Build and full test run

From the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected: build with zero warnings; all architecture, host, contract, and
integration tests pass, including the persistence-owned structural test (every
new versioned and child table carries its trigger; no `club_id`).

## Focused scenario runs

Each command runs one test class. Test class names are the planned names from
the plan's project structure.

| # | Scenario | Command filter (`dotnet test src/platform/SocAlytics.Platform.slnx --no-build --filter …`) | Expected outcome |
| --- | --- | --- | --- |
| 1 | Run creation from a finalized notification (US1, FR-001 to FR-007, SC-002, SC-010) | `FullyQualifiedName~RunCreationTests` | Finalizing a recording set through the Recordings API produces exactly one outbox message with the event id, its publication to `matches.recordings-finalized`, and one run; event rows finalized before the outbox migration are backfilled once; one run per valid notification, including 10 concurrent duplicate deliveries; fabricated, nonexistent, mismatched, and other-team notifications leave a `rejected` receipt and no run; Recordings lookup unavailable leaves no receipt and the message is negatively acknowledged; changing the configured workflow afterwards leaves stored run and node snapshots byte-identical; reload after host restart returns identical identities, values, ordering, and relationships. |
| 2 | Graph validation (US1 scenario 4, FR-008 to FR-010, SC-004) | `FullyQualifiedName~WorkflowGraphTests` | Direct cycle, indirect cycle, self-dependency, dangling edge, undeclared edge kind, and empty capability set are rejected; integration variant shows a `Failed` run with `invalid-graph` reason, zero nodes, edges, Logical Jobs, and outbox ready-work messages. |
| 3 | Fan-out and hardware neutrality (US1 scenario 7, FR-011 to FR-013) | `FullyQualifiedName~LogicalJobPlanningTests` | Segments computed from lineage spans follow the Segment Contract (half-open 300-second windows, clipped coverage never renumbers, empty intersections produce no segment); N segment jobs plus one match job; every job of a capability carries the same pinned profile, image, and model digests; segment jobs carry the complete analysis-segment identity; no host, Manager, or runtime column or field exists. |
| 4 | Readiness fixture tables (US2, FR-015 to FR-020, SC-005) | `FullyQualifiedName~ReadinessEvaluatorTests` | Every row of the required, optional, conditional (determinate true, determinate false, indeterminate), barrier, and failure-propagation tables yields the expected ready, waiting, and blocked sets, input snapshots, and terminal run state. |
| 5 | Readiness across restarts (US2 scenario 7) | `FullyQualifiedName~ReadinessRecoveryTests` | Recording accepted and failed upstream outcomes, restarting the host between evaluations, and re-evaluating gives the same sets; no blocked job ever appears in the ready-work subject. |
| 6 | Claims, heartbeats, leases (US3 scenarios 1 to 4, 8, FR-021 to FR-025, SC-006) | `FullyQualifiedName~ExecutionAttemptTests` | With 10 simultaneous claimants per job, attempt numbers are consecutive from 1 without gaps and never exceed the budget; stale, fabricated, other-Manager, and expired-lease heartbeats return 409 and change nothing; advancing the fake clock past the lease, including while the host is stopped, marks the attempt `Stale` and either republishes (new readiness occurrence) or fails the job permanently and blocks descendants; a revoked Manager's heartbeats and completions are refused by authentication and its attempt recovers through lease expiry; a Manager registered through the real `AnalystManagerDPoP` scheme claims successfully and its attempt records its Manager id. |
| 6a | Claim retry key (US3 scenario 9, FR-045, edge case "claim response lost") | `FullyQualifiedName~ClaimIdempotencyTests` | A claim without `Idempotency-Key` returns `400 idempotency-key-missing`; after a successful claim whose response is discarded, the same Manager retrying with the same key returns `200`, `replayed=true`, and the same attempt id, fencing token, and lease expiry, with no new attempt and unchanged `attempts_used`; concurrent retries with one key create one attempt; the same key for another Logical Job or readiness occurrence returns `409 idempotency-key-reused`; the same key after the attempt went stale returns `409 claim-obsolete`; another Manager with the same key gets `409 claim-obsolete` and no attempt. |
| 7 | Completion acceptance and idempotency (US3 scenarios 5 to 7, FR-026 to FR-030, SC-003) | `FullyQualifiedName~CompletionTests` | Exactly one accepted-result reference per completed job with full lineage; replay with the same key and reordered properties returns `replayed=true` and the same accepted-result id; same key with different content returns 409 `idempotency-key-reused`; late completion from a fenced attempt returns 409 `attempt-obsolete`; lineage mismatch from the current attempt returns 409 `completion-rejected` and retries the job; completion and expiry racing for one attempt produce exactly one winner; after a host restart, attempts and accepted-result references reload with identical identities, values, and ordering (FR-007); a new run created after a workflow version change recomputes every node and imports no accepted result of the earlier run (FR-006). |
| 8 | Outbox atomicity and publication (US4 scenarios 1 to 4, 7, FR-031 to FR-033, SC-008) | `FullyQualifiedName~OutboxPublicationTests` | A rolled-back change leaves no outbox row; with NATS stopped, messages stay `pending` with increasing `attempt_count` and advancing `next_attempt_at`, then become `failed` at the configured limit while workflow state is unchanged; a lost publication acknowledgement causes a republish that JetStream reports as duplicate; operator listing shows state, attempts, last and next attempt, failure category, age, and lag without reading NATS. |
| 9 | Transport recovery and reconciliation (US4 scenarios 5 and 6, FR-035, FR-036, SC-007) | `FullyQualifiedName~TransportRecoveryTests` | After replacing the NATS container with an empty one, reconciliation republishes exactly the currently ready jobs and pending notifications with their original message ids; completed, blocked, exhausted, and claimed jobs are not republished; a stale ready-work copy is claimed with 409 and acknowledged as obsolete. |
| 10 | Retention (US4 scenario 8, FR-034) | `FullyQualifiedName~OutboxRetentionTests` | Only `published` and `discarded` messages past the retention period whose occurrence is no longer current are removed; `pending` and `failed` remain. |
| 11 | Restart matrix (SC-001, SC-002) | `FullyQualifiedName~RestartMatrixTests` | At least 20 injected host restarts at distinct lifecycle points (run creation, readiness evaluation, claim, lease expiry, completion, publication), with every notification and ready-work message delivered at least twice, reach the same terminal state and accepted-result set as the uninterrupted reference run, with zero lost or duplicated accepted results. |
| 12 | Contract validation (US5, FR-038 to FR-042, SC-009) | `FullyQualifiedName~SocAlytics.Platform.Contracts.Tests` | The existing Recordings event checks still pass; all analysis valid examples pass, all analysis invalid examples fail with the expected keyword, the breaking-revision fixtures fail the same-major compatibility check against the released copies, and repeating the suite in one run yields identical outcomes. |
| 13 | Platform payload conformance (FR-039, FR-040) | `FullyQualifiedName~PayloadConformanceTests` | Every ready-work and run-state-changed message read back from NATS validates against its schema; the completion bodies sent by the test Manager validate against the completion schema. |
| 14 | Telemetry minimisation (FR-043, FR-044) | `FullyQualifiedName~AnalysisTelemetryTests` | Counters and gauges for lease expiries, rejected stale completions, rejected notifications, blocked runs, outbox pending, failed, oldest age, and publication lag are emitted; captured logs, traces, and metric tags contain no fencing token, payload body, secret, or URL. |
| 15 | HTTP surface and authorization | `FullyQualifiedName~AnalysisEndpointTests` | `/openapi/v1.json` lists every operation of [openapi.yaml](contracts/openapi.yaml); cross-team run reads return 404; non-Manager principals get 403 on Manager operations; non-Club-Admin members get 403 on operations endpoints; unauthenticated requests get 401 without redirects. |
| 16 | Unresolvable workflow definition (FR-046, edge case) | `FullyQualifiedName~WorkflowAvailabilityTests` | With `Analysis:DefaultWorkflow` removed, `/health` returns 503 `Unhealthy` and the `analysis-workflow` entry reported by `HealthCheckService` is unhealthy with a sanitized category (no configuration values); the operational summary shows `unresolvable`; finalized notifications stay pending in JetStream (no receipt, no run, consumer delivery count unchanged while paused); restoring the configuration turns the check healthy and every pending notification produces its run exactly once. |
| 17 | Worker resilience without NATS (FR-032, FR-033) | `FullyQualifiedName~WorkerResilienceTests` | With workers enabled and no `ConnectionStrings:nats`, the API host stays up, `/health` stays 200 `Healthy`, the NATS-dependent workers do nothing, and pending outbox rows stay `pending`; with NATS configured but stopped, the host stays up with aggregate `Degraded`, and worker failures are logged and retried after back-off without stopping the host. |

## Contract command

The command is introduced by Recording Lineage and Upload; this feature adds
the analysis schemas and compatibility checks to it.

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests --no-build
```

Expected: identical pass and fail outcomes on every run; no network access is
required (disconnecting the network does not change the result).

## Local composition smoke check

```powershell
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

Expected in the Aspire dashboard: `postgres`, then `migrator` (completes
successfully), `nats` (`nats:2.14` with JetStream), then `api`, which reports
healthy on `/health`. With
`appsettings.Development.json` providing the development workflow stand-in, the
API logs that it ensured the development streams and consumers
and completed startup reconciliation with zero republished messages. The `nats`
health check is registered because Aspire supplies `ConnectionStrings:nats`
(hosts without that connection string register no `nats` check). Stopping the
`nats` resource leaves `/health` reachable with HTTP 200 and the aggregate
status `Degraded`, not `Unhealthy`, because the `nats` check degrades while
PostgreSQL remains the workflow authority. Running without the development
workflow configuration instead makes `/health` return 503 `Unhealthy` because
the `analysis-workflow` check fails with a sanitized category (FR-046), which
the API logs as a warning; per-check results are asserted in tests through
`HealthCheckService`, because the `/health` body carries only the aggregate
status.
Stop the AppHost with `Ctrl+C`.

## Documentation check

```powershell
node .github/scripts/check-markdown.mjs
```

Expected: zero issues.
