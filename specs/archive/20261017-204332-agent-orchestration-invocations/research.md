# Research: Agent Orchestration Direct Invocations

All Technical Context items are resolved; no `NEEDS CLARIFICATION` remains. Each decision builds on code that exists
under `src/platform/` and on [Intelligence and Agents](../../../docs/architecture/intelligence-and-agents.md).

## R1 - Functional area and layering

- **Decision**: Add an `AgentOrchestration` folder and namespace to each platform layer (Domain, Application,
  Infrastructure, Api), with handlers in the Application layer and Dapper stores in the Infrastructure layer, exactly as
  the Analysis functional area is organized. No new production project.
- **Rationale**: [Agent Orchestration Authority](../../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority)
  places the functional area inside the platform's layers and stamp database; `AGENTS.md` forbids functional areas as
  separate projects; the architecture tests already enforce inward dependencies.
- **Alternatives considered**: A separate agent service or database (rejected by the architecture); placing the code in
  the Analysis folders (rejected: other areas must use Agent Orchestration only through its handlers).

## R2 - Workload identity and authentication

- **Decision**: Agent workload identities are configured in protected deployment configuration
  (`AgentOrchestration:Workloads:Clients`, each with a client id, an ECDSA P-256 public JWK, and an enabled flag). A
  workload obtains short-lived access tokens from a new token operation
  `POST /api/v1/agent-orchestration/workload/token` with the OAuth 2.0 client credentials grant, a `private_key_jwt`
  client assertion, and a DPoP proof, and calls workload operations with DPoP-bound tokens. Token issuance and request
  authentication reuse the existing proof validation of Analyst Manager machine access (`IManagerProofValidator`
  validates DPoP proofs and client assertions against a given JWK), with a new token store
  (`agent_workload_access_tokens`, hashed tokens with key thumbprint and expiry), a new authentication scheme
  `AgentWorkloadDPoP`, and a new authorization policy `AgentWorkload` carrying the scope `agent-workload`. Every
  authenticated request re-reads the client's configured enabled state, so disabling a client stops its tokens at once.
- **Rationale**: The architecture requires Hermes to authenticate "as a stamp-local workload"; the platform already
  validates `private_key_jwt` and DPoP for Analyst Managers and runs no general-purpose authorization server
  ([API And Identity](../../../docs/architecture/platform-implementation.md#api-and-identity)). A workload is deployed by
  the stamp operator, not paired by members, so its key is configured rather than registered through device-code
  pairing.
- **Alternatives considered**: Reusing Analyst Manager registrations (rejected: wrong actor, member pairing, Manager
  scopes); a static bearer secret (rejected: not sender-constrained, weaker than the existing pattern); mutual TLS
  (rejected: new infrastructure, no precedent in the platform).

## R3 - Work notifications and workload queue access

- **Decision**: Submission, retry, and reconciliation write an `agent-invocation-ready` message to the existing
  transactional outbox (`IOutbox`, `outbox_messages`); the existing outbox publisher publishes it to a new JetStream
  work-queue stream `AGENT_WORK` on subject `agents.invocations.ready`, with message id `<invocationId>:<readyOccurrence>`
  for broker deduplication, and `NatsTopology` provisions the stream and the durable pull consumer `agent-workload`. When
  `Nats:Authentication:Mode` is `Jwt`, the workload obtains a short-lived NATS user JWT from
  `POST /api/v1/agent-orchestration/workload/queue-credentials`, limited to pulling and acknowledging from that consumer,
  through the existing `IQueueCredentialIssuer` signing mechanics; in `None` mode (Development and Test only) it
  connects without credentials, as Managers do.
- **Rationale**: [Client, Transport, And Runtime Protocol](../../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)
  requires minimal, at-least-once JetStream notifications from the outbox; the platform already publishes analysis
  work this way and issues scoped queue credentials to Managers.
- **Alternatives considered**: Workload polling a "next work" operation (rejected: contradicts the architecture's
  notification transport); reusing the analysis jobs stream (rejected: mixes agent and Analyst work and their
  credentials).

## R4 - Claims, leases, fencing, and idempotent workload operations

- **Decision**: Mirror the Analysis logical-job pattern (`ClaimLogicalJob`, `RenewAttemptLease`, `CompleteAttempt`,
  `CloseAttemptAsStale`, `ExpireStaleAttempts`): the invocation row is locked for every workload command; a claim
  creates an attempt with the next attempt number and a fencing token that increases per invocation and is returned as
  an opaque string; renewals, checkpoints, completions, and failures require the attempt to be the invocation's current
  attempt, state `active`, the presented token, an unexpired lease, and an unreached attempt deadline. Every workload
  command carries an operation id; `agent_workload_operations` stores client, operation id, kind, a SHA-256 hash of the
  canonical request, and the recorded outcome, so a repeat with an equal hash returns the recorded outcome and a repeat
  with a different hash is refused with `operation-id-reused`. Stale calls are answered `409` with problem code
  `agent-attempt-stale` and a `disposition` of `attempt-not-current`, `lease-expired`, `attempt-fenced`,
  `invocation-ended`, or `scope-lost`; obsolete claims are answered `409` with `claim-obsolete`.
- **Rationale**: The pattern is proven by the analysis workflow tests and matches
  [Durable State And Transaction Boundaries](../../../docs/architecture/intelligence-and-agents.md#durable-state-and-transaction-boundaries).
- **Alternatives considered**: Advisory locks only (rejected: the existing pattern uses row locks and version checks);
  client-generated fencing tokens (rejected: tokens come from durable authority).

## R5 - Versioned execution policy

- **Decision**: `AgentOrchestration:Execution` options (max attempts, lease seconds, max attempt seconds, envelope
  history turns, max question and advice characters, max checkpoint note characters, max unfinished invocations per
  owner, suggested polling seconds) form the execution policy `direct-invocation/1`; submission stores the policy
  version and its resolved values on the invocation, and every later decision about that invocation uses the stored
  values. Development values: 3 attempts, 30-second lease, 300-second attempt maximum, 10 history turns, 4,000 question
  characters, 16,000 advice characters, 1,000 note characters, 5 unfinished invocations, 2-second polling.
- **Rationale**: The architecture requires "versioned retry limits"; storing the snapshot keeps running invocations
  stable when configuration changes, as the analysis `AttemptBudget` snapshot does.
- **Alternatives considered**: Reading live configuration at each decision (rejected: changes limits of running work).

## R6 - Authorization of reads, submissions, and workload operations

- **Decision**: A new `ITeamScopeSource` for resource kind `agent-conversation` (and `agent-invocation`) resolves the
  owning team; member operations call `IAccessAuthorizer.AuthorizeTeamResourceAsync` (creation, participants, and
  submission with `TeamPermission.Write`; reads with `TeamPermission.Read`) and then require the caller to be the
  owner, answering every non-owner with the same not-found problem. Workload operations evaluate the stored owner
  through a new `IInvocationScopeEvaluator` that loads the owner's access snapshot (`IMemberAccountStore`) and grants
  only an active owner without a pending password change who is Club Admin or Coach of the team.
- **Rationale**: [Authorization, Lineage, And Lifecycle](../../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle)
  makes ordinary reads private to the initiating user with current team access and requires re-evaluation at claim,
  checkpoint, and completion; `TeamPermission.Write` is the existing Club Admin or Coach rule.
- **Alternatives considered**: Viewer submissions (rejected: Viewers are read-only); granting Club Admins ordinary
  read access (rejected by the architecture; governance review is a separate future operation).

## R7 - Monitor and reconciliation

- **Decision**: A new `AgentAttemptMonitorWorker` (the existing `WorkerLoop` pattern) runs `MonitorAgentAttempts`,
  which ends attempts whose lease expired or whose deadline passed as `expired`, re-evaluates the owner's scope of
  every active attempt and fences scope-lost ones, and requeues or fails their invocations; a new
  `AgentWorkReconciliationWorker` republishes ready notifications for queued invocations whose latest notification is
  older than the configured bound. Development intervals: 5 seconds (monitor), 30 seconds (reconciliation),
  republish bound 60 seconds.
- **Rationale**: The architecture requires bounded recovery and reconciliation from PostgreSQL and the outbox; the
  analysis workflow uses the same worker types.
- **Alternatives considered**: Broker redelivery only (rejected: JetStream is not authority).

## R8 - Conditional polling

- **Decision**: Conversation, turns, invocation, and lineage reads return a strong `ETag` derived from the
  conversation version or the invocation's progress version, and answer `If-None-Match` matches with `304`, as
  `AnalysisRunEndpoints` and `RecordingEndpoints` do; the invocation read sets `Retry-After` to the policy's polling
  interval while the invocation is unfinished. Submission answers `202` with `Location` set to the invocation read.
- **Rationale**: Matches [Client, Transport, And Runtime Protocol](../../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)
  and existing endpoints.

## R9 - Lineage and accepted advice storage

- **Decision**: `agent_lineage_entries` is append-only (sequence per invocation, kind, attempt, bounded JSON payload)
  and protected by `socalytics.reject_immutable_change()`; checkpoint notes live only in lineage payloads.
  `agent_accepted_advice` holds exactly one row per succeeded invocation (primary key `invocation_id`) and is
  immutable. Lineage reads return entries in sequence order with the advice.
- **Rationale**: The architecture requires append-oriented, ordered, idempotent lineage and exactly one accepted
  outcome; the immutable trigger already protects recording lineage.

## R10 - Simulated agent workload

- **Decision**: A new worker executable `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload` (not part of
  any production deployment) that refuses to start unless `DOTNET_ENVIRONMENT` is `Development` or `Test`. It holds a
  configured workload key, obtains tokens and (in `Jwt` mode) queue credentials, pulls notifications, claims, records
  one checkpoint, renews while working, and completes with deterministic advice ("Simulated advice from
  `<agentId>` …") and declared references `simulated`. Test-controlled behaviors are selected per invocation by a
  marker at the start of the question (`[simulate:fail-once]` fails the first attempt retryably and then succeeds,
    `[simulate:fail-retryable]` fails every attempt retryably, `[simulate:fail-permanent]` fails non-retryably, and
    `[simulate:stall]` stops renewing in the first attempt and succeeds in the next), which only the simulator interprets. Its runner class is also hosted in-process by the
  integration tests. The AppHost generates an ephemeral ECDSA key at start, passes the public JWK to the API's
  workload client configuration and the private key to the simulated workload, and starts it after the API in both
  normal and `e2e` mode.
- **Rationale**: FR-031 to FR-033; the simulated Analyst precedent is a Development and Test runtime of an otherwise
  external component; placing it under `src/platform/Tests` keeps it platform-owned validation tooling covered by the
  platform verification scope, while `src/agents/` stays reserved for Hermes.
- **Alternatives considered**: A hosted service inside the API (rejected: would bypass the workload authentication and
  HTTP protocol it must prove); a Python stand-in under `src/agents/` (rejected: Hermes is out of scope and the area is
  non-executable).

## R11 - Contracts

- **Decision**: Add `contracts/agent-orchestration/` with `work-notification/v1`, `execution-envelope/v1` (claim
  response), and `attempt-report/v1` (checkpoint, completion, and failure requests), each with a JSON Schema 2020-12
  document, `releases/1.0.0.schema.json`, and valid and invalid golden fixtures, registered in the contract index and
  validated by `SocAlytics.Platform.Contracts.Tests`. Member operations are described by the OpenAPI document only.
- **Rationale**: FR-036; follows the `contracts/analysis/*` layout and `ContractCatalog` rules.

## R12 - Audit and telemetry

- **Decision**: `AgentOrchestrationAuditEvents` records `agent.conversation.created`, `agent.participant.added`,
  `agent.invocation.submitted` (accepted, replayed, refused), `agent.attempt.claimed` (granted, refused),
  `agent.attempt.completed`, `agent.attempt.failed`, `agent.attempt.expired`, `agent.invocation.exhausted`, and
  `agent.attempt.scope-lost`, with identifiers only; the audit actor kind check gains `agent-workload`, and
  `PostgresAuditTrail` allow-lists the new detail keys. A new `AgentOrchestrationMetrics` meter adds counters and an
  observable gauge of queued invocations next to `AnalysisMetrics`.
- **Rationale**: FR-034 and FR-034a; follows the analysis and registry audit and metrics patterns.
