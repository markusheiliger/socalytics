# Research: Durable Analysis Workflow

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md)

Every decision below resolves an unknown from the plan's Technical Context or
records a technology or pattern choice. Package versions were verified against
the NuGet feed proxy (`packagefeedproxy.microsoft.io`) on 2026-10-07; nuget.org
is not directly reachable from this environment.

## R1. .NET NATS client

- **Decision**: Use `NATS.Client.JetStream` **3.3.0** (with `NATS.Client.Core`
  3.3.0) from the official NATS.Net v3 family, referenced only by
  `SocAlytics.Platform.Infrastructure`. Do not reference the `NATS.Net`
  meta-package in production code (it also pulls Key-Value, Object Store,
  Services, and hosting extensions that this feature does not use). Enable
  `CentralPackageTransitivePinningEnabled` and pin `NATS.Net` 3.3.0 in
  `Directory.Packages.props`, so the `NATS.Net` 2.7.3 dependency of
  `Aspire.Hosting.Nats` 13.4.6 resolves to 3.3.0 as well and no project mixes
  NATS majors. Payloads are serialized by the platform with `System.Text.Json`
  and passed to NATS as raw bytes, so no NATS serializer package is needed.
- **Rationale**: NATS.Net v3 is the current, stable major (3.0.0 released
  2026-07-10; 3.3.0 on 2026-09-30); the last 2.x release is 2.8.2. A greenfield
  feature should not start on the previous major. The calls this feature uses
  are source-compatible between 2.8.2 and 3.3.0 (additions only): stream and
  consumer management (`CreateOrUpdateStreamAsync`,
  `CreateOrUpdateConsumerAsync`), `PublishAsync` with headers returning
  `PubAckResponse` with `Duplicate`, de-duplication through `Nats-Msg-Id`, and
  pull consumers with `AckAsync`, `NakAsync(delay)`, and `AckTerminateAsync`.
  The NATS.Net 3 spike (2026-10-07) built a replica of the AppHost,
  `Aspire.Hosting.Testing` host test, and Infrastructure library: NuGet raises
  the AppHost's NATS dependency to 3.3.0 with no NU1605/NU1608/NU1107, Aspire's
  in-process NATS health check reports Healthy against `NATS.Client.Core`
  3.3.0 with no binding exceptions (its 17 member references all resolve), and
  publish with duplicate detection, ack, delayed nak, and terminate behave as
  required. With transitive pinning, every NATS package in the AppHost and the
  test project resolves to 3.3.0.
- **Implementation notes**: v3 changes some defaults: `RequestReplyMode` is
  `Direct`, `SubPendingChannelCapacity` is 16384, and `NatsHeaders` is no
  longer read-only after publish, so the publisher creates one `NatsHeaders`
  instance per message. `SkipSubjectValidation` is obsolete and must not be
  used (warnings are errors). `EnsureSuccess()` throws on duplicates; the
  publisher treats `Duplicate = true` as success and checks `Error` instead.
- **Alternatives considered**:
  - *NATS.Net 2.8.2*: matches the Aspire hosting dependency's major, but is the
    previous major with no further feature development; rejected after the
    spike showed no conflict with 3.3.0.
  - *Replacing `Aspire.Hosting.Nats` with a plain `AddContainer("nats", ...)`*:
    also verified by the spike and removes every NATS client dependency from
    the AppHost, but loses generated credentials and the connection-string
    resource; kept as the fallback if a future Aspire upgrade conflicts.
  - *Legacy `NATS.Client` (v1)*: synchronous-first and in maintenance mode.
  - *`Aspire.NATS.Net` client integration*: convenient registration, health
    check, and telemetry, but it binds production configuration to Aspire
    component conventions; the architecture keeps Aspire to local composition.
    The API instead reads the standard `ConnectionStrings:nats` value that
    Aspire injects locally and that Compose or other profiles can supply.

## R2. Aspire local composition for NATS

- **Decision**: Add `Aspire.Hosting.Nats` **13.4.6** to
  `SocAlytics.Platform.AppHost` and declare `builder.AddNats("nats").WithJetStream()`
  (confirmed by the platform spike: the package exists for 13.4.6, depends on
  NATS.Net 2.7.3, and runs the `nats:2.14` image). The API resource gets
  `.WithReference(nats).WaitFor(nats)` in addition to the PostgreSQL and
  Migrator ordering defined by the persistence foundation.
- **Rationale**: Matches the pinned `Aspire.AppHost.Sdk/13.4.6`; mirrors the
  architecture's local order (PostgreSQL, Migrator, then API) with NATS as an
  additional dependency. No data volume is configured by default so that a
  local restart of the AppHost exercises the "transport restored empty" path;
  a developer may opt in to a volume.
- **Alternatives considered**: A plain `AddContainer("nats", "nats")` resource
  (loses Aspire's connection-string and health integration); running NATS
  outside Aspire (contradicts "Aspire AppHost is the supported local entry
  point").

## R3. NATS in integration tests

- **Decision**: Add `Testcontainers.Nats` **4.15.0** to
  `Tests/SocAlytics.Platform.Integration.Tests` (the project introduced by the
  persistence foundation), pinning the `nats:2.14` image (the same image Aspire
  13.4.6 runs locally) instead of the module default (`nats:2.9`). The module
  starts the server with `--jetstream`.
- **Rationale**: Same Testcontainers major as `Testcontainers.PostgreSql`
  4.15.0; a real JetStream server is required by the architecture's evidence
  list (broker outage, publisher restart, emptied transport). Docker is
  available on the GitHub runner, so `environment-setup` needs no change for it.
- **Fault injection**: Transport unavailability is produced by stopping the
  container; "restored empty" by replacing the container (new server, no
  streams); "accepted but not recorded" (lost publication acknowledgement) by a
  test decorator of the Application port `IMessageTransport` that publishes and
  then throws before returning.
- **Alternatives considered**: An in-memory NATS fake only (cannot prove
  JetStream de-duplication, acknowledgements, or redelivery); Aspire testing
  for all integration tests (slower, and the persistence plan standardises on
  Testcontainers).

## R4. Transactional outbox and background publisher

- **Decision**: One platform-wide table `socalytics.outbox_messages` written by
  the Application abstraction `IOutbox` inside the current `IUnitOfWork`
  transaction. A hosted service in the API process (`OutboxPublisherWorker`,
  Infrastructure) repeatedly invokes the Application command
  `PublishOutboxBatch`, which:
  1. selects due `pending` rows ordered by `created_at` with
     `FOR UPDATE SKIP LOCKED` (safe with several API replicas),
  2. publishes each through `IMessageTransport` with header
     `Nats-Msg-Id = <message id>` (or `<message id>.r<round>` after a
     reconciliation round),
  3. records `published` (with `published_at`) or increments `attempt_count`,
     records `last_attempt_at`, a sanitized `failure_category`, and
     `next_attempt_at` from capped exponential backoff, and marks `failed`
     when `attempt_count` reaches the configured maximum.
  Publication is at least once; publication state, attempt count, age, and lag
  are queryable without reading NATS.
- **Worker resilience**: every worker runs on one internal loop that catches
  every non-shutdown failure (including `transport-unavailable`), logs a
  sanitized category, and backs off, so no exception escapes a hosted service
  and stops the API. The NATS connection attempt starts at application startup
  when `ConnectionStrings:nats` is configured, so the `nats` check reflects the
  real state without a prior publish; without that connection string the
  publisher, consumer, and reconciliation workers return immediately, outbox
  rows stay `pending`, and `/health` stays `Healthy`.
- **Rationale**: Architecture mandates the PostgreSQL outbox as the durable
  publication boundary and at-least-once delivery; row locks with
  `SKIP LOCKED` give work distribution without leader election; the stable
  message id makes JetStream de-duplicate retries inside its duplicate window
  and lets consumers de-duplicate beyond it. Outbox rows are operational
  records with no edit operation, so they carry no `version` column; their
  transitions are guarded by row lock plus state.
- **Ready-work hygiene**: When a Logical Job leaves `Ready` without being
  claimed (blocked or failed), the same transaction marks its not-yet-published
  ready-work message `discarded`, so a blocked job is never published even if
  the transport was down when it became ready.
- **Retention**: `OutboxRetentionWorker` deletes only `published` or
  `discarded` rows older than the configured retention period whose readiness
  occurrence is no longer the current ready occurrence of its job; `pending` and
  `failed` rows are never removed.
- **Recordings finalized events**: Recording Lineage and Upload commits one
  immutable `recording_finalized_events` row per finalized set and expects this
  feature to publish it without updating the row. This feature (a) extends the
  Recordings finalize command handler to enqueue the same canonical payload
  through `IOutbox` in its existing unit of work (message id = `event_id`,
  message type and subject `matches.recordings-finalized`, causation id =
  `event_id`), and (b) backfills rows committed before this feature with a
  set-based `INSERT … SELECT … ON CONFLICT DO NOTHING` in the outbox migration.
  Publication state for these events lives only in `outbox_messages`.
- **Persistence classification**: `outbox_messages` is classified
  `Unversioned("operational publication record; transitions guarded by row
  lock and state")` in the persistence foundation's
  `PersistedTableClassifications` manifest.
- **Alternatives considered**: Publish-after-commit (rejected by architecture);
  PostgreSQL `LISTEN/NOTIFY` wake-ups (an optimisation that may be added later;
  polling with a short configurable interval is sufficient and simpler);
  per-area outbox tables (one shared table keeps one publisher and one operator
  view); a third-party outbox library (adds a dependency and its own schema
  conventions, conflicting with DbUp-owned SQL); a database trigger on
  `recording_finalized_events` that writes the outbox row (atomic, but hides a
  state change outside Application command handlers, which the architecture
  routes all state changes through); a relay that polls
  `recording_finalized_events` for rows without an outbox entry (retention of
  published rows would make it republish them).

## R5. Inbound notifications and idempotent consumers

- **Decision**: The API hosts `RecordingsFinalizedConsumerWorker`, a JetStream
  durable pull consumer (explicit ack) on subject `matches.recordings-finalized`.
  For each message it validates the payload against the Recordings-owned
  v1 schema `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`, then calls the Application command
  `HandleRecordingsFinalizedNotification` in one unit of work. The handler
  inserts a `analysis_notification_receipts` row keyed by `eventId`
  (`ON CONFLICT DO NOTHING`); a duplicate returns the recorded outcome. It then
  calls `IRecordingSetLookup.GetAsync(recordingSetVersionId)` and requires the
  match, team, and ordered members (recording-version and timeline-mapping
  identities and digests) to equal the payload. Outcomes:
  - **run created / run already existed** → ack;
  - **rejected** (lookup returns `null`, or match, team, members, or digests
    differ; schema violation) → receipt with sanitized rejection category, ack;
    member recording digests are the Recordings composite form
    `sha-256-parts:<partSize>:<partCount>:<hex>` and are compared only for
    equality;
  - **Recordings lookup unavailable** (throws) → transaction rolled back, no
    receipt, `NakAsync` with the configured delay.
  - **Workflow definition unresolvable** (FR-046) → no receipt; the consumer
    stops pulling (see R12) and naks any message already in hand with the
    configured delay.
  The consumer is created with unlimited `MaxDeliver` (-1) by design, not only
  in development, so transient unavailability never exhausts a delivery limit
  and drops a notification.
- **Rationale**: FR-002 and FR-036; run uniqueness is additionally enforced by a
  unique constraint on `(recording_set_version_id, workflow_id,
  workflow_version)`, so differing event identities for the same version cannot
  create two runs.
- **Alternatives considered**: Relying on JetStream de-duplication alone (only
  covers a time window and only the publisher side); creating runs directly
  inside the Recordings finalize transaction (would couple functional areas and
  bypass the architecture's event subject).

## R6. Readiness evaluation and concurrency

- **Decision**: Readiness is evaluated synchronously, inside the transaction of
  every change that can affect it (run creation, completion acceptance, explicit
  failure, attempt rejection, lease expiry). Each such transaction first locks
  the run row (`SELECT … FROM analysis_runs WHERE id = @RunId FOR UPDATE`), then
  the affected Logical Job rows, then attempt rows. Claims and heartbeats, which
  do not affect readiness, lock the Logical Job row and then the attempt row.
  The pure Domain service `ReadinessEvaluator` computes ready, waiting, and
  blocked sets, input snapshots, node states, and the run outcome from a
  snapshot of the run's durable state.
- **Rationale**: Without per-run serialization two concurrent completions of
  different upstream jobs could each miss the other's uncommitted accepted
  result and leave a dependent waiting forever. The single lock order
  (run → job → attempt) prevents deadlocks. Workflow transitions use state
  guards (`WHERE id = @Id AND state = @ExpectedState`), never `If-Match`.
- **Alternatives considered**: A separate polling Scheduler loop (adds latency
  and a second source of transitions; kept only as startup reconciliation);
  `SERIALIZABLE` isolation (retries on every conflict, harder to reason about);
  advisory locks keyed by run (equivalent, but a row lock is visible and needs
  no key derivation).

## R7. Dependency semantics and conditional predicates

- **Decision**:
  - *Segment → segment*: a segment job depends on the upstream job for the same
    analysis-segment identity.
  - *Segment → match*: the match job depends on every upstream segment job of
    that node (complete-coverage barrier; partial-coverage policies are deferred).
  - *Match → segment or match*: depends on the single upstream match job.
  - Edge kinds `required`, `optional`, `conditional`; unknown kinds invalidate
    the graph.
  - Conditional predicates use a closed, versioned v1 vocabulary evaluated only
    from durable state: `capabilityRequested` (applies when the named
    capability is in the run's requested set; always determinate) and
    `nodeAccepted` (applies when the named node has accepted results for all its
    jobs; determinate only once that node is terminal and accepted, otherwise
    indeterminate). Indeterminate predicates are applicable (required), per the
    spec's conservative default. Additional kinds are additive contract
    changes owned by the capability registry.
  - A Logical Job's input snapshot is fixed the first time it becomes ready and
    reused for every retry.
- **Rationale**: Encodes FR-016 to FR-018 and the spec's edge cases with
  deterministic, testable rules; the vocabulary is small enough for fixture
  tables (SC-005).
- **Alternatives considered**: An expression language (unbounded, hard to
  validate offline); evaluating predicates from result-manifest contents
  (manifest reading is explicitly deferred).

## R8. Execution attempts, leases, and fencing

- **Decision**:
  - Attempt numbers are `attempts_used + 1` under the Logical Job row lock, with
    a unique `(logical_job_id, attempt_number)` constraint as backstop.
  - Fencing tokens are positive `bigint` values drawn from the database
    sequence `socalytics.analysis_fencing_token_seq` (bounded to
    `9007199254740991` so they remain exact JSON integers in every consumer
    language). They are unique, strictly increasing, unrelated to attempt
    numbers and to row `version` values, and never logged or emitted in
    telemetry.
  - A claim requires `state = 'ready'`, a matching `current_readiness_occurrence_id`,
    and remaining budget; otherwise it is rejected as obsolete (HTTP 409) or
    unknown (HTTP 404). Both are deterministic, so the Manager acknowledges the
    delivery.
  - Claims are retry-safe (FR-045): every claim carries an `Idempotency-Key`
    header scoped to the authenticated Manager. The table
    `analysis_claim_keys` maps `(manager_id, idempotency_key)` to
    `(logical_job_id, readiness_occurrence_id, attempt_id)` and is written in
    the attempt's transaction. Under the job row lock, a repeated key for the
    same job and occurrence whose attempt is still current (`Active`, lease not
    expired, still the job's current attempt) returns `200` with the same
    attempt, current lease expiry, and fencing token (`replayed = true`)
    without creating anything, consuming budget, or renewing the lease; a key
    bound to a different job or occurrence returns `409
    idempotency-key-reused`; a key whose attempt is no longer current returns
    `409 claim-obsolete`. Returning the stored fencing token is safe because
    only the same authenticated Manager can present its own key.
  - Heartbeats and completions must match attempt id, Logical Job, Manager,
    fencing token, `state = 'active'`, and `lease_expires_at > now`.
  - Lease expiry is detected by `LeaseExpiryWorker` (the architecture's Job
    Monitor) from durable state using the injected `TimeProvider`; it locks run
    → job → attempt, re-checks the guard, marks the attempt `stale`, and makes
    the job ready again (new readiness occurrence and outbox message) or fails it
    permanently when the budget is exhausted, then re-evaluates blocking and
    the run outcome.
  - Completion that is well-formed but semantically mismatched (wrong digests,
    scope, schema, or lineage) from the current attempt closes that attempt as
    `rejected` and applies the same retry-or-fail rule as an explicit failure.
- **Rationale**: Implements FR-021 to FR-030 and the contracts convention
  "fencing tokens are positive integers whose ordering is interpreted only by
  the owning workflow". Using `TimeProvider` keeps lease tests deterministic
  (`FakeTimeProvider`); replicas rely on synchronised host clocks, so stale
  timeouts must exceed the heartbeat interval plus tolerated clock skew.
- **Alternatives considered**: Token = attempt number (trivially predictable and
  conflates two concepts); random 53-bit tokens (no ordering, collision
  handling needed); database `now()` as the clock (correct across replicas but
  makes lease tests depend on wall-clock sleeps); claim retries without a key
  (a lost response would cost an attempt and wait for lease expiry); keys scoped
  per Logical Job instead of per Manager (would let one Manager's key collide
  with another's).
- **Revocation**: every Manager request passes the `AnalystManagerDPoP`
  authentication of Analyst Manager Registration, which checks the active
  registration on each call, so a revoked Manager can no longer claim,
  heartbeat, or complete. Its active attempts stop renewing and are recovered
  by normal lease expiry, so no separate fencing step is required here.

## R9. Idempotent completion and canonical comparison

- **Decision**: The completion payload carries its idempotency key. The handler
  stores `(logical_job_id, idempotency_key, request_fingerprint, outcome,
  accepted_result_id)` in `analysis_completion_outcomes` for accepted and
  failure-recorded outcomes. The fingerprint is `sha-256:` over the RFC 8785
  JSON Canonicalization Scheme (JCS) form of the schema-validated payload, so
  field order and insignificant whitespace do not matter. The completion
  contract contains no floating-point numbers, which keeps JCS number
  serialization trivial. Replays with an equal fingerprint return the stored
  outcome; a different fingerprint returns `409` (`idempotency-key-reused`).
  Deterministic rejections are not stored and are re-derived on replay.
- **Rationale**: FR-029 and the US5 ordering scenario; JCS is a published
  standard that Python and TypeScript consumers can reproduce.
- **Alternatives considered**: Hashing the raw request bytes (order-sensitive);
  an `Idempotency-Key` header (the spec puts the key in the completion
  contract so it survives transport changes).

## R10. JSON Schema validation library and contract command

- **Decision**: Use `JsonSchema.Net` **8.0.5** (MIT, targets net10.0, full
  draft 2020-12), which Recording Lineage and Upload already pins for the
  contract test project `Tests/SocAlytics.Platform.Contracts.Tests` it creates.
  This feature adds a reference from Infrastructure for runtime validation of
  inbound notifications and completion bodies through the Application
  abstraction `IContractValidator`, and extends the existing test project with
  the analysis schemas. Schemas are registered from the repository
  `contracts/` directory (embedded into Infrastructure and read by the test
  project from the same files), so validation never fetches remote documents.
  The deterministic contract command stays
  `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`
  (with `--no-build` after the documented solution build).
- **Rationale**: 8.0.5 is the last MIT release; 9.x is published under the
  Open Source Maintenance Fee EULA with required licence acceptance, which this
  plan does not adopt silently. Reusing the existing project and command keeps
  one contract authority and keeps it inside the platform build and tests.
- **Alternatives considered**: `JsonSchema.Net` 9.4.0 (licence change, needs an
  explicit decision); `Corvus.Json.Validator` 5.7.3 (Apache-2.0, but compiles
  generated code with Roslyn at runtime, a heavy production dependency);
  `NJsonSchema` (incomplete 2020-12 support); a Node/AJV script (would need
  npm dependencies that `environment-setup` does not install).

## R11. Contract layout, identities, and compatibility checking

- **Decision**:
  - Recording Lineage and Upload creates the repository-root `contracts/`
    directory, its index `contracts/README.md`, the `$id` convention
    `https://socalytics.invalid/contracts/<repository-relative path>`, and its
    event schema
    `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`.
    This feature extends them; it creates neither the directory nor the index.
  - Additions follow the same layout,
    `contracts/<area>/<contract>/v<major>/<contract>.schema.json`, with
    `examples/valid/*.json`, `examples/invalid/*.json`, and immutable released
    copies under `releases/<exact-version>.schema.json`: the shared
    definitions `contracts/common/v1/common.schema.json` and the analyst-job,
    attempt-completion, and run-state-changed schemas under
    `contracts/analysis/`. Each gets an index entry with owner, exact version,
    examples, and the contract command.
  - Under that `$id` convention, `$ref` values are relative, so authoring
    references stay repository-relative and resolve offline: the merged
    `ContractCatalog.LoadRegistry()` of the contract test project registers
    every catalog schema by `$id`, and a relative `$ref` resolves against the
    referencing schema's `$id` (for example
    `../../../common/v1/common.schema.json` from an analysis schema resolves to
    `https://socalytics.invalid/contracts/common/v1/common.schema.json`). This
    feature adds no second schema loader to the test project. The shared
    definitions path is the one form outside
    `contracts/<area>/<contract>/v<major>/<contract>.schema.json`; it is named
    by the architecture, and the merged index path rule already accepts it. The
    exact version
    is carried by `x-socalytics-version` and by each payload's
    `contractVersion` (`^1\.\d+\.\d+$`, any 1.x accepted).
  - Every object is closed (`additionalProperties: false`), which rejects
    hardware selections, secrets, URLs, connection details, and extra fields by
    construction; string patterns additionally reject URI schemes and presigned
    query markers in reference fields.
  - Same-major compatibility is a rule-based structural diff, added by this
    feature to `Contracts.Tests`, between the current schema and every released
    copy in the same major: removed or renamed properties, newly required
    properties, narrowed `type`/`enum`/`const`/`pattern`/`format`/numeric or
    length bounds, and newly closed objects fail. It runs for every schema in
    `contracts/`, including the Recordings event schema once that has a released
    copy. Proposed breaking revisions are kept as test fixtures to prove the
    checker fails them.
- **Rationale**: Implements contracts-and-compatibility.md and FR-038 to FR-042
  without a second contract mechanism.
- **Alternatives considered**: A separate analysis contract project or command
  (two authorities for one contract tree); URN `$id`s (cannot resolve relative
  references); OpenAPI-embedded schemas only (the architecture makes JSON
  Schema the authority for Analyst payloads).

## R12. Workflow definitions and analysis segments (boundaries owned elsewhere)

- **Decision**:
  - `IWorkflowDefinitionResolver` (namespace
    `SocAlytics.Platform.Application.Registry`) returns the stamp's default
    resolved workflow, including every node's capability declaration, Analyst
    profile, digests, policies, resources, attempt budget, timeouts, edges, and
    the segmentation policy (version, fixed positive duration, digest).
    Infrastructure provides the configuration-backed development stand-in
    `ConfiguredWorkflowDefinitionResolver` bound to `Analysis:DefaultWorkflow`
    (reloaded through `IOptionsMonitor`). Tests supply fixtures.
  - **Unresolvable definition (FR-046)**: the singleton
    `WorkflowDefinitionAvailability` re-checks resolvability at startup, on
    configuration change, and every `Analysis:Notifications:ResolveRecheckSeconds`.
    While unresolvable, the readiness health check `analysis-workflow` reports
    unhealthy with a sanitized diagnostic (category `not-configured`,
    `invalid-definition`, or `registry-unavailable`; no configuration values),
    the operational summary shows the same status, a warning is logged once per
    state change, and `RecordingsFinalizedConsumerWorker` stops fetching, so
    notifications stay pending in JetStream without deliveries counting against
    any limit. A message already in hand is negatively acknowledged with the
    configured delay. When the definition becomes resolvable, the check turns
    healthy and the consumer resumes. A definition that resolves but whose
    graph is invalid is not covered by this rule: it produces a `Failed` run
    (FR-008, FR-009).
  - `IAnalysisSegmentSource` (namespace
    `SocAlytics.Platform.Application.Analysis`) returns the analysis-segment
    references of a finalized lineage. Its Infrastructure implementation
    `LineageAnalysisSegmentSource` computes them deterministically from the
    `Spans` (mapped match-time coverage) that `IRecordingSetLookup` returns for
    each member and from the pinned segmentation policy, exactly as the Segment
    Contract defines: segment `n` exists for a member when the nominal window
    `[(n - 1) * D, n * D)` intersects that member's mapped coverage. It performs
    no materialization; the Segment Service may later implement the same port.
- **Rationale**: The spec's assumptions place workflow definitions in the
  Registry boundary (fixtures allowed) and segment identities in the segment
  boundary; segment identity is fully defined by the architecture and by data
  the Recordings lookup already returns, so no stand-in is needed for it.
- **Alternatives considered**: Persisting registry tables now (out of scope); a
  configured segment-count stand-in (unnecessary once lineage spans are
  available, and it would not follow the Segment Contract); redelivering
  notifications indefinitely while the definition is missing (hides the
  misconfiguration and inflates delivery counts; replaced by FR-046);
  rejecting notifications as invalid (they are valid; only the platform is
  not ready).

## R13. HTTP surface and authorization

- **Decision**:
  - Team members read run status (`GET /api/v1/analysis/runs/{runId}`,
    `GET /api/v1/matches/{matchId}/analysis-runs`) through `ITeamScopeResolver`;
    cross-team requests return 404. Views return a weak ETag.
  - Analyst Manager operations (claim, heartbeat, completion) require the
    authorization policy `AnalystManager`: authentication scheme
    `AnalystManagerDPoP` and scope `analyst-manager`, both provided by Analyst
    Manager Registration (which also enforces the Active registration on every
    request). The Application abstraction `ICurrentAnalystManager` exposes the
    Manager identity (the registration's `client_id`, a `uuid`) read from the
    claim named by the merged constant `AnalystManagerClaimTypes.ManagerId`;
    the policy and scheme are referenced through
    `AuthorizationPolicyNames.AnalystManager` and
    `AnalystManagerDPoPDefaults.AuthenticationScheme`, never string literals.
    Most tests replace the scheme's handler with a test-only handler that
    issues the same claim types (through the same constants) and scope; a
    successful claim and a revocation are also proven through the real
    `AnalystManagerDPoP` scheme.
  - Operational inspection and reconciliation
    (`/api/v1/operations/...`) are restricted to Club Admins until a dedicated
    stamp-operator role exists; state-changing operator requests require the
    `X-CSRF-Token` antiforgery header of Club and Identity, and reconciliation
    requests are recorded through `IAuditTrail`.
  - Errors use the shared envelope of Club and Identity:
    `type = urn:socalytics:problem:<code>` with members `code`,
    `correlationId`, and `errors`. This feature adds the codes
    `claim-obsolete`, `attempt-obsolete`, and `completion-rejected`, and reuses
    the shared codes `idempotency-key-missing` (400, missing `Idempotency-Key`
    header on a claim) and `idempotency-key-reused` (409, key reused with
    different content or for a different claim) introduced by Recording Lineage
    and Upload, through its Api constants class `SharedProblemCodes`.
  - There is no HTTP operation that creates runs: the spec defers a
    user-facing analysis request, so the creation interface is the inbound
    `recordings-finalized` notification.
- **Rationale**: API-first control plane, conventions for problem details and
  scope, and the spec's statement that authenticated Manager exposure follows
  from the Manager registration feature.
- **Alternatives considered**: Exposing Manager operations without
  authorization (unsafe); waiting for Manager registration before defining any
  endpoint (the user and the job contract need the claim outcome semantics now).

## R14. Telemetry

- **Decision**: Meters `SocAlytics.Platform.Analysis` and
  `SocAlytics.Platform.Messaging` emit counters (lease expiries, rejected stale
  completions, rejected heartbeats, rejected notifications, runs blocked,
  published and failed publications), observable gauges (pending, failed, and
  oldest-pending age of outbox messages), and a publication-lag histogram.
  Metric tags are limited to low-cardinality values (capability, message type,
  outcome, failure category); run, Logical Job, attempt, and Manager ids appear
  only in traces and log scopes. Fencing tokens, payload bodies, secrets, and
  URLs are never emitted.
- **Rationale**: FR-043, FR-044, production-operations.md observability table,
  and POL-010 minimisation.
- **Alternatives considered**: Metrics tagged with ids (cardinality explosion);
  logging payloads for diagnosis (violates minimisation).

## R15. Environment features (constitution 1.1.0)

- **Decision**: `environment-setup` already provides the .NET SDK and Docker
  (Testcontainers). Coverage of the repository-root `contracts/` files (platform
  restore, build, and tests when they change, while the platform solution
  exists) comes from the environment feature
  `specs/20261007-115855-environment-verification-coverage`, which merges first
  and is named under this spec's Assumptions → Dependencies by the coordinator.
  This feature needs no other SDK, tool, or check.
- **Rationale**: Constitution "Technology and Tooling Constraints" and the
  shared planning conventions for files outside `src/platform/`.
- **Alternatives considered**: A separate environment feature for this plan
  (superseded by the shared coverage feature); placing schemas under
  `src/platform/` (violates the architecture's repository-root `contracts/`
  location).
