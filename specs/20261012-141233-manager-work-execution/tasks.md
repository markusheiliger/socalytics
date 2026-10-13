---

description: "Task list for Analyst Manager Work Execution With a Simulated Analyst"
---

# Tasks: Analyst Manager Work Execution With a Simulated Analyst

**Input**: Design documents from `specs/20261012-141233-manager-work-execution/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/queue-credentials-api.md](contracts/queue-credentials-api.md), [contracts/queue-topology-and-permissions.md](contracts/queue-topology-and-permissions.md), [contracts/revocation-fencing.md](contracts/revocation-fencing.md), [contracts/local-control-active-attempts.md](contracts/local-control-active-attempts.md), [quickstart.md](quickstart.md)

**Tests**: Required. The constitution requires focused tests for new behavior, and the spec requires automated evidence for every acceptance scenario (SC-007) and an automated end-to-end loop (SC-010). Every task adds the tests for the code it creates in the same task, so both solutions build (warnings are errors) and their test suites stay green after each task. Tests that need SoftHSM2 skip with a reason where it is missing (Windows, macOS), as the Manager's key-store tests already do.

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Platform Persistence Foundation, Club and Identity Foundation, Recording Lineage and Upload, Durable Analysis Workflow (Logical Jobs, `ClaimLogicalJobCommand`, `RenewAttemptLeaseCommand`, `CompleteAttemptCommand`, `ExpireStaleAttemptsHandler` with `RequeueOrFailAsync`, the outbox publisher, `NatsTopology`, `NatsJetStreamTransport`, `NatsContainerFixture`, `AnalysisTestHost`, `TestManagerClient`, `RegisteredAnalystManagerClient`, contracts `contracts/analysis/analyst-job/v1` and `contracts/analysis/attempt-completion/v1`), and Analyst Manager Registration (`PlatformClient` with DPoP, `RegistrationStatusMonitor`, `OperatingStateMachine`, `AdmissionGate`, `DrainCoordinator` with `DrainTimeoutPolicy { CancelRequest, StopAndCleanUp }`, `IManagerWorkTracker` with `NoManagerWork`, `PreflightScheduler`, `RuntimeAdapterCatalog`, local control, `ManagerCli`, `StatusWindowViewModel`, `FakePlatform`, `ManagerTestHost`, `SoftHsmToken`, `GoldenFixtureTests`, `contracts/analyst-manager/registration/v1/`). Every `PackageVersion` or `PackageReference` addition applies only if the package is absent. The migration takes the next free number on an up-to-date default branch (`0014` at planning time).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US4)
- Every task names its exact file paths

## Path Conventions

- Platform solution `src/platform/SocAlytics.Platform.slnx`; Registry and Analysis folders in `src/platform/SocAlytics.Platform.Application/`, `src/platform/SocAlytics.Platform.Infrastructure/`, `src/platform/SocAlytics.Platform.Api/`; NATS code in `src/platform/SocAlytics.Platform.Infrastructure/Messaging/Nats/`; migrations in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/`; integration tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/`.
- Analyst Manager solution `src/analysts/manager/SocAlytics.Analysts.Manager.slnx` with `SocAlytics.Analysts.Manager.Core/`, `SocAlytics.Analysts.Manager/` (tray and CLI), and `Tests/SocAlytics.Analysts.Manager.Tests/`.
- Shared golden fixtures in repository-root `contracts/analyst-manager/queue-credentials/v1/`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Add the NATS key and client packages both solutions need.

- [X] T001 Add the NATS packages (plan.md § Technical Context; research.md § R1, § R3): in `src/platform/Directory.Packages.props` add central versions for `NATS.Jwt` and `NATS.NKeys` (latest stable 1.x from nats-io) and reference both from `src/platform/SocAlytics.Platform.Infrastructure/SocAlytics.Platform.Infrastructure.csproj`; in `src/analysts/manager/Directory.Packages.props` add `NATS.Net` 3.3.0 (the platform's version) and `NATS.NKeys` (same version as the platform) and reference both from `src/analysts/manager/SocAlytics.Analysts.Manager.Core/SocAlytics.Analysts.Manager.Core.csproj`. Restore and build both solutions; no code changes.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: NATS JWT authentication and per-capability consumers on the platform, the extended runtime boundary with the simulated Analyst and the platform client calls on the Manager, and the shared queue-credential fixtures. No user story can start before this phase is complete.

### Parallel foundations

- [X] T002 [P] Platform NATS JWT authentication mode (research.md § R1; data-model.md § Configuration; contracts/queue-topology-and-permissions.md § Topology): add `NatsAuthenticationOptions` (section `Nats:Authentication`: `Mode` = `Jwt` | `None`, `OperatorSeed`, `AccountSeed`, `SystemAccountSeed`) in `src/platform/SocAlytics.Platform.Infrastructure/Messaging/Nats/NatsAuthenticationOptions.cs`; a `NatsTrust` type that derives the operator, stamp account (`SOCALYTICS`, JetStream enabled), and system account keys and builds account JWTs with `NATS.Jwt` in `Messaging/Nats/NatsTrust.cs`; mint the platform's own user JWT (permissions for stream and consumer administration, publishing `analysis.>` and `matches.>`, the events consumer) at startup and use it in `Messaging/Nats/NatsConnectionProvider.cs` when `Mode` is `Jwt`; fail startup when `Mode` is `None` outside the `Development` and `Test` environments. All new types are `internal`. Add a JWT-mode fixture `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Infrastructure/NatsJwtContainerFixture.cs` that generates throwaway trust, writes a `nats.conf` with `operator`, `system_account`, a `full` resolver with a writable directory, the preloaded stamp account, and JetStream, and starts the same NATS image as `NatsContainerFixture`; register `NatsAuthenticationOptions` and `NatsTrust` in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Messaging/NatsJwtAuthenticationTests.cs`: the platform provisions topology, publishes ready work, and consumes notifications in JWT mode; a connection without credentials is refused; `None` is refused outside Development and Test.
- [x] T003 [P] Runtime boundary for attempts and the simulated runtime in Core (research.md § R5, § R6; data-model.md § Simulated Analyst profile; FR-007, FR-008): extend `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/IContainerRuntimeAdapter.cs` with `StartAttemptAsync(AttemptLaunch, CancellationToken)` returning `IAttemptExecution` (`Task<AttemptOutcome> Outcome`, `StopAsync`, `CleanUpAsync`); add `AttemptLaunch` (pinned capability, scope, Analyst-profile, image, and model digests, resources) and `AttemptOutcome` (succeeded with result reference and checksum, or failed with reason) in `Runtime/`; add `MaxParallelAttempts` to `RuntimeProbe` and carry it into `PreflightResult`. Move `SimulatedRuntimeAdapter` from `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Support/SimulatedRuntimeAdapter.cs` to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Simulated/SimulatedRuntimeAdapter.cs` (profile `simulated`) and add the simulated Analyst configured under `Runtime:Simulated` (`MaxParallelAttempts`; per capability `Outcome` = `succeed` | `fail` | `hang` | `crash`, `RunDuration`, `FailureReason`, `ResultReference`, `ResultChecksum`) in `Runtime/Simulated/SimulatedAnalystOptions.cs`; register it in `Hosting/ManagerServiceCollectionExtensions.cs` only when `IHostEnvironment` is `Development` or `Test`, and make startup fail with a diagnostic when `Runtime:Profile` is `simulated` in any other environment. Update existing tests to the moved type. Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Runtime/SimulatedRuntimeTests.cs`: every outcome, stop and clean-up, `MaxParallelAttempts` in the preflight result, and refusal outside Development and Test.
- [x] T004 [P] Platform client calls for queue credentials, claims, heartbeats, and completion (contracts/queue-credentials-api.md; research.md § R7; FR-004, FR-006, FR-010, FR-015): add to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs` DPoP-authenticated methods `RequestQueueCredentialsAsync(userPublicKey, capabilities)`, `ClaimAsync(logicalJobId, readinessOccurrenceId, messageId, retryKey)`, `SendHeartbeatAsync(attemptId, logicalJobId, fencingToken, observedAt, executionState)`, and `CompleteAsync(attemptId, completion, idempotencyKey)` that map responses to typed results: success, replayed, obsolete (`claim-obsolete`, `attempt-obsolete`), conflict, inactive (routed to `InactiveSignalSink` as today), and transient (network errors, 5xx, timeouts). Completion payloads follow `contracts/analysis/attempt-completion/v1`. Extend `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Support/FakePlatform.cs` with scriptable responses for these operations. Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Platform/WorkOperationsClientTests.cs`: request shapes, DPoP on every call, and every result mapping.
- [x] T005 [P] Shared queue-credential contract fixtures (contracts/queue-credentials-api.md): add `contracts/analyst-manager/queue-credentials/v1/queue-credentials.schema.json` (request and response, JSON Schema 2020-12), valid request and response examples, and invalid examples (seed instead of public key, missing capabilities, non-positive `maxConcurrentAnalysts`) under `contracts/analyst-manager/queue-credentials/v1/`; add the index row to `contracts/README.md`; validate them in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/QueueCredentialsContractTests.cs` and in the Manager's `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/GoldenFixtures/QueueCredentialsFixtureTests.cs` (link the folder in the test project file as the registration fixtures are linked).

### Manager consumers

- [X] T006 Per-capability Manager consumers and work options (research.md § R2; contracts/queue-topology-and-permissions.md § Topology; data-model.md § Configuration): add `AnalystWorkOptions` (section `AnalystWork`: `MaxConcurrentAnalystsPerManager` positive integer, `QueueCredentialLifetime`, `QueueUrl`, `ConsumerAckWait`, `UnfitRedeliveryDelay`; explicit non-production defaults) in `src/platform/SocAlytics.Platform.Application/Registry/QueueCredentials/AnalystWorkOptions.cs`; extend `src/platform/SocAlytics.Platform.Infrastructure/Messaging/Nats/NatsTopology.cs` to provision idempotently, on the existing `SOCALYTICS_ANALYSIS_JOBS` stream, one durable pull consumer per capability declared by the configured workflow definitions: name `analyst-<capabilityId>`, `FilterSubject` `analysis.jobs.ready.<capabilityId>`, explicit acknowledgement, `AckWait` = `ConsumerAckWait`, unlimited redelivery; bind `AnalystWorkOptions` in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` (also changed by T002, so this task runs after the group). Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Messaging/ManagerConsumerTopologyTests.cs`: consumers exist after provisioning, provisioning twice changes nothing, a ready job for a capability is delivered only through that capability's consumer.

**Checkpoint**: Both solutions build and test green; NATS runs in JWT mode in tests; consumers exist per capability; the Manager can start simulated attempts and call the work operations against the fake platform.

---

## Phase 3: User Story 1 - A registered Manager executes ready analysis work end to end (Priority: P1) 🎯 MVP

**Goal**: An Active, Running Manager pulls a compatible job, claims it, heartbeats, and reports exactly one completion that the platform accepts.

**Independent Test**: The end-to-end test registers a Manager, makes a Logical Job ready, and observes exactly one attempt, renewed leases, and one accepted result with the pinned digests and the Manager's identity (SC-001).

### Credential store and Manager queue access

- [X] T007 [P] [US1] Issued queue credentials store (data-model.md § Issued queue credential): add `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/0014_registry_queue_credentials.sql` (renumber to the next free number if `0014` is taken) creating `registry.analyst_manager_queue_credential` with `queue_credential_id` uuid PK, `registration_id` uuid FK to the registration table, `user_public_key` text unique, `issued_at` timestamptz, `expires_at` timestamptz, `revoked_at` timestamptz null, `revocation_pushed_at` timestamptz null, and an index on (`registration_id`, `expires_at`); classify the table (no `version`) in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`; add `IQueueCredentialStore` in `src/platform/SocAlytics.Platform.Application/Registry/QueueCredentials/IQueueCredentialStore.cs` (record issuance, list unexpired by registration, mark revoked, list revoked unexpired, mark pushed) and its internal Dapper implementation in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Registry/QueueCredentialStore.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/QueueCredentialStoreTests.cs`.
- [x] T008 [P] [US1] Queue access in the Manager (research.md § R1, § R3; data-model.md § Queue credential (Manager); FR-006): add `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Queue/QueueCredentialProvider.cs` (generates an ephemeral user NKey in memory per credential, requests credentials with the validated capabilities, renews at two thirds of the lifetime, discards credentials on revocation, unregistration, and exit, never persists or logs the seed or JWT) and `Queue/QueueConnection.cs` (connects with the user JWT and nonce signing, sets the inbox prefix, accepts `nats://` only in Development and Test and otherwise requires `tls://`, reconnects with renewed credentials, exposes fetch-one per consumer with acknowledge, negative acknowledge with delay, and negative acknowledge without delay). Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Queue/QueueCredentialProviderTests.cs` with `FakePlatform` and fake time: renewal timing, no request while inactive, discard on revocation, plaintext refusal outside Development and Test.

### Credential issuance and attempt runner

- [X] T009 [P] [US1] Queue-credential issuance operation (contracts/queue-credentials-api.md; contracts/queue-topology-and-permissions.md § Manager user permissions; FR-006, FR-019): add `IssueQueueCredentialsCommand` in `src/platform/SocAlytics.Platform.Application/Registry/QueueCredentials/IssueQueueCredentials.cs` (requires an Active registration, refuses a reused or malformed user public key, records the issuance in the same unit of work, returns `maxConcurrentAnalysts` from `AnalystWorkOptions`); add `IQueueCredentialIssuer` in `Application/Registry/QueueCredentials/` and its internal NATS implementation in `src/platform/SocAlytics.Platform.Infrastructure/Messaging/Nats/NatsQueueCredentialIssuer.cs` that signs a user JWT with the stamp account key: `sub` = user public key, `exp` = now + `QueueCredentialLifetime`, publish allow `$JS.API.CONSUMER.MSG.NEXT.SOCALYTICS_ANALYSIS_JOBS.analyst-*`, `$JS.ACK.SOCALYTICS_ANALYSIS_JOBS.>`, `$JS.API.CONSUMER.INFO.SOCALYTICS_ANALYSIS_JOBS.analyst-*`, subscribe allow `<inboxPrefix>.>`, everything else denied; map `POST /api/v1/analyst-managers/self/queue-credentials` with `RequireAnalystManager` in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs` with the refusals of the contract. Never log the JWT. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/QueueCredentialIssuanceTests.cs` against `NatsJwtContainerFixture`: issuance for an Active Manager, refusal for pending, revoked, and unregistered Managers, the issued JWT can pull and acknowledge from `analyst-*` consumers, and it cannot publish work, administer streams or consumers, or subscribe outside its inbox prefix.
- [x] T010 [P] [US1] Attempt runner (research.md § R7; data-model.md § Active attempt; FR-004, FR-005, FR-007, FR-010, FR-011, FR-015, FR-016): add `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRunner.cs` that, for one fetched delivery, claims with retry key `<messageId>:<readinessOccurrenceId>` (retries transient failures with the same key), acknowledges after a successful or `claim-obsolete` outcome and never otherwise, starts the attempt through `IContainerRuntimeAdapter.StartAttemptAsync` with the pinned values unchanged, runs `Work/HeartbeatLoop.cs` at lease ÷ 3 with execution state `preparing` → `running` → `finalizing`, records the last confirmed heartbeat, and reports the outcome once with idempotency key `completion:<attemptId>`, retrying lost responses with the same key until an outcome or `attempt-obsolete`; log claims, acknowledgements, heartbeat failures, fencing, completions, and clean-up with the Logical Job, attempt, and correlation identities through `ILogger` (FR-025); add `Work/ActiveAttempt.cs` per data-model.md. Fencing tokens are never logged. Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/AttemptRunnerTests.cs` with `FakePlatform`, an in-memory queue fake, the simulated runtime, and fake time: successful attempt, acknowledgement only after the claim outcome, obsolete claim acknowledged without work, lost claim and completion responses replayed with the same keys, heartbeat cadence keeps the lease alive, the log entries above asserted with `FakeLogCollector`.

### Work acquisition

- [x] T011 [US1] Work acquisition and attempt registry (research.md § R8, § R9; FR-001, FR-002, FR-003): add `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRegistry.cs` implementing `IManagerWorkTracker` (active count, idle wait, stop-and-clean-up) and replacing `NoManagerWork` in `Hosting/ManagerServiceCollectionExtensions.cs`; add `Work/WorkAcquisitionService.cs` (hosted service) that, while `AdmissionGate` admits and a slot below the effective limit is free, fetches one delivery at a time round-robin over the consumers of the validated capabilities from the credentials and hands it to an `AttemptRunner`; register the queue and work services. Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/WorkAcquisitionTests.cs`: no fetch while not admitted, one fetch per free slot, only validated capabilities are pulled, and a full attempt lifecycle in `ManagerTestHost` leaves no fencing token, user JWT, NKey seed, or access token in the collected logs or the state directory (SC-008).

### End-to-end happy path

- [x] T012 [US1] End-to-end test project and happy path (research.md § R10; quickstart.md; SC-001, SC-010): create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/SocAlytics.Platform.EndToEnd.Tests.csproj` (xUnit v3 like the other platform test projects; references `../SocAlytics.Platform.Integration.Tests/SocAlytics.Platform.Integration.Tests.csproj` and `../../../analysts/manager/SocAlytics.Analysts.Manager.Core/SocAlytics.Analysts.Manager.Core.csproj`; add `InternalsVisibleTo` entries in those projects as needed) and add it to `src/platform/SocAlytics.Platform.slnx`; add `Support/EndToEndHarness.cs` that starts PostgreSQL and `NatsJwtContainerFixture`, hosts the API in-process with `Registry:AllowSoftwareKeys`, registers a Manager Core host in the `Test` environment through the real pairing, approval, activation, and DPoP path against the in-process API (HTTPS `TestServer` handler), with a SoftHSM device key and the `simulated` runtime, and skips with a reason where SoftHSM2 is missing; add `WorkExecutionEndToEndTests.cs` covering the happy path: a ready job is pulled, claimed, heartbeated, and completed, and exactly one accepted result with the Manager's identity and pinned digests exists within 30 seconds of readiness plus the configured simulated run time (SC-001).

**Checkpoint**: User Story 1 works end to end and is the MVP.

---

## Phase 4: User Story 2 - The host operator controls how much work the Manager takes (Priority: P2)

**Goal**: Admission follows Running, preflight, pause, timed pause, and the effective concurrency limit; status shows active attempts.

**Independent Test**: With more ready jobs than the limit, the Manager never exceeds the limit; pause, resume, and limit changes behave as specified; status shows each active attempt (SC-002, SC-003).

- [x] T013 [US2] Concurrency limit (data-model.md § Concurrency limit; contracts/local-control-active-attempts.md § New command; FR-002, FR-019, SC-002): persist the host-local limit (positive integer, default 1) in the signed local state under `src/analysts/manager/SocAlytics.Analysts.Manager.Core/LocalState/`; add `Work/ConcurrencyLimit.cs` computing `min(hostLocal, maxConcurrentAnalysts, MaxParallelAttempts)` from the local state, the latest credentials, and the latest preflight result; make `WorkAcquisitionService` use it; add the local-control `concurrency` command (read and set; refuse values below 1 with `invalid-concurrency`) in `LocalControl/LocalControlServer.cs` and `src/analysts/manager/contracts/local-control.schema.json` (backward-compatible minor version). Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/ConcurrencyLimitTests.cs`: never more active attempts than the effective limit, lowering never cancels, raising pulls more, a changed stamp maximum applies after the next credential renewal, invalid values refused.
- [X] T014 [US2] Admission and unfit deliveries (contracts/queue-topology-and-permissions.md § Manager behavior on deliveries; FR-003, FR-018, FR-020): in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/WorkAcquisitionService.cs` and `Work/AttemptRunner.cs`, negatively acknowledge without delay a delivery that was fetched while admission ended before the claim; negatively acknowledge with the delay from the credentials a delivery whose pinned resources the host cannot meet; keep heartbeating running attempts when preflight fails and the Manager enters Runtime unavailable. Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/AdmissionTests.cs`: pause stops new claims at once while active attempts complete, timed pause resumes at its end time, runtime unavailable stops pulling but keeps heartbeating, unfit deliveries are never claimed.
- [x] T015 [US2] Active attempts in status, CLI, and status window (contracts/local-control-active-attempts.md; FR-022): add `ActiveAttempts` and `Concurrency` to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Diagnostics/ManagerStatusSnapshot.cs`, fill them in `Hosting/ManagerControl.cs`, and serialize them in `LocalControl/LocalControlServer.cs` and `src/analysts/manager/contracts/local-control.schema.json`; render them in `src/analysts/manager/SocAlytics.Analysts.Manager/Cli/ManagerCli.cs` (`status`, `status --json`, and the new `concurrency [<n>]` command) and show the effective limit and one row per active attempt in `ViewModels/StatusWindowViewModel.cs` and `Views/StatusWindow.axaml`. No fencing token, credential, or checksum appears. Update `Tests/SocAlytics.Analysts.Manager.Tests/Support/FakeManagerControl.cs`; tests in `Tests/SocAlytics.Analysts.Manager.Tests/Ui/StatusWindowViewModelTests.cs`, `Tests/SocAlytics.Analysts.Manager.Tests/LocalControl/LocalControlActiveAttemptsTests.cs`, and `Tests/SocAlytics.Analysts.Manager.Tests/Cli/ManagerCliConcurrencyTests.cs`.

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 5: User Story 3 - Failures never produce duplicate or stale results (Priority: P3)

**Goal**: Failure, timeout, crash, connection loss, fencing, and restart are handled exactly once and leave recovery to the platform.

**Independent Test**: Simulated failures, hangs, and crashes, interrupted connections, and a stopped Manager recover with exactly one accepted result per completed Logical Job (SC-004, SC-006).

- [x] T016 [US3] Failure handling in the attempt runner (research.md § R7; FR-009, FR-011, FR-012, FR-013, FR-014, FR-017): in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRunner.cs` and `Work/HeartbeatLoop.cs`: report a failed outcome once when the simulated Analyst fails or crashes (after clean-up); enforce the claim's execution timeout with `StopAsync`, `CleanUpAsync`, and a failed completion with reason `execution-timeout`; retry transient heartbeat failures with exponential backoff capped so the next try precedes the lease expiry known from the last confirmed renewal, never assuming renewal; on `attempt-obsolete` from a heartbeat or completion, stop, clean up, send nothing more, and free the slot; never re-enqueue; start without any attempts from a previous process. Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/AttemptFailureTests.cs` with fake time covering every acceptance scenario of User Story 3 at the Manager level.
- [x] T017 [US3] End-to-end failure scenarios (SC-004, SC-006): add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/WorkFailureEndToEndTests.cs`: a simulated failure leads to a second attempt that succeeds and to exactly one accepted result; a healthy attempt running longer than three lease periods is never expired; a Manager host stopped mid-attempt leaves the attempt to lease expiry, after which a second Manager host claims and completes the same pinned Logical Job.

**Checkpoint**: User Stories 1–3 work independently.

---

## Phase 6: User Story 4 - Draining, exiting, unregistering, and revocation respect active work (Priority: P4)

**Goal**: Drain, safe exit, and unregister wait for active attempts; revocation fences attempts at once, disconnects the Manager from the queue, and stops its work.

**Independent Test**: Drain and unregister with active attempts behave per the drain policy; revoking a Manager with active attempts fences them in the same step, disconnects it within 5 seconds, and makes its jobs ready for other Managers (SC-005, SC-009).

### Revocation fencing and Manager stop

- [x] T018 [P] [US4] Revocation fences attempts and revokes queue credentials (contracts/revocation-fencing.md; research.md § R4; FR-023): extract the close-as-stale step from `src/platform/SocAlytics.Platform.Application/Analysis/Commands/ExpireStaleAttempts.cs` into a shared `Analysis/Commands/CloseAttemptAsStale.cs` (lock, close as `Stale`, invalidate token, `RequeueOrFailAsync`) used by expiry and by a new `Analysis/Commands/FenceManagerAttempts.cs` that fences every `Active` attempt of a Manager; call it, and `IQueueCredentialStore` mark-revoked, inside the existing unit of work of `src/platform/SocAlytics.Platform.Application/Registry/Decisions/RevokeRegistration.cs` and `src/platform/SocAlytics.Platform.Application/Registry/CompleteUnregistration.cs`; extend their audit events with the counts of fenced attempts and revoked credentials; count fenced attempts with reason `revoked` in `src/platform/SocAlytics.Platform.Application/Telemetry/AnalysisMetrics.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/RevocationFencingTests.cs`: every active attempt of the revoked Manager is fenced in the same step, budget is consumed like a stale attempt, a job whose last attempt was fenced fails permanently with blocking rules applied, other Managers' attempts are untouched, later claims, heartbeats, completions, and credential requests of the Manager are refused, unregistration completion revokes credentials.
- [x] T019 [P] [US4] Drain, exit, unregister, and revocation with active attempts (research.md § R9; FR-021, FR-024; SC-005): make `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRegistry.cs` serve `DrainCoordinator`: idle wait covers active attempts, and stop-and-clean-up stops every attempt and reports a failed completion with reason `drain-timeout` for each current attempt; when revocation is observed (`RegistrationStatusMonitor` or any inactive signal), stop and clean up all attempts immediately without reporting, discard queue credentials, and close the queue connection before stamp state is cleared. Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/DrainAndRevocationTests.cs`: safe exit and unregister wait for attempts; with `CancelRequest` the drain is cancelled and attempts continue; with `StopAndCleanUp` the remaining attempts stop and report `drain-timeout`; revocation stops everything within 10 seconds and sends nothing further.

### Immediate queue revocation

- [ ] T020 [US4] Immediate queue revocation (contracts/queue-topology-and-permissions.md § Revocation; research.md § R1, § R4; FR-006, FR-023, SC-009): add `IQueueAccessRevoker` in `src/platform/SocAlytics.Platform.Application/Registry/QueueCredentials/IQueueAccessRevoker.cs` and its internal implementation in `src/platform/SocAlytics.Platform.Infrastructure/Messaging/Nats/NatsQueueAccessRevoker.cs` that rebuilds the stamp account JWT with the `revocations` of all revoked, unexpired issued keys, re-signs it with the operator key, pushes it through `$SYS.REQ.CLAIMS.UPDATE` as the system account, and marks the rows pushed; call it right after the revocation and unregistration-completion units of work commit; add `src/platform/SocAlytics.Platform.Infrastructure/Workers/QueueRevocationWorker.cs` that retries pending pushes with bounded backoff, including after restart. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/QueueRevocationTests.cs` against `NatsJwtContainerFixture`: a connected client with an issued credential is disconnected within 5 seconds of revocation and refused on reconnect; a failed push is retried by the worker; credentials of other Managers keep working.

### End-to-end revocation

- [ ] T021 [US4] End-to-end revocation (SC-005, SC-009, SC-010): add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/RevocationEndToEndTests.cs`: revoking a Manager with an active attempt fences it on the platform, disconnects the Manager from the queue, makes the Manager stop its simulated work and return to Unregistered, and lets a second Manager claim and complete the job.

**Checkpoint**: All user stories work independently and together.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Development composition, documentation, and the final validation.

- [ ] T022 [P] AppHost in JWT mode (research.md § R1; quickstart.md § Manual exploration): in `src/platform/SocAlytics.Platform.AppHost/Program.cs` generate throwaway operator, stamp account, and system account keys per run, start NATS with a generated `nats.conf` (operator, system account, `full` resolver, preloaded stamp account, JetStream) instead of plain JetStream, and pass `Nats:Authentication` (`Mode` = `Jwt` and the seeds) and `AnalystWork:QueueUrl` to the API. Extend `src/platform/Tests/SocAlytics.Platform.Host.Tests/PlatformHostTests.cs` to assert the API resource receives JWT-mode NATS settings.
- [ ] T023 [P] Documentation (constitution § I, § Development Workflow): record the NATS credential mechanism (decentralized JWT, per-Manager user JWTs with pull-and-acknowledge permissions, account revocations pushed by the platform, `Mode` `None` only in Development and Test) in the Analyst Technology or messaging section of `docs/architecture/platform-implementation.md`, keeping production key custody and lifetimes unresolved under `GOV-CRED-004`; update `src/platform/README.md` (NATS authentication and `AnalystWork` options, migration 0014, the end-to-end test project and its SoftHSM2 requirement), `src/analysts/manager/README.md` (work execution, the `simulated` profile for Development and Test, the `concurrency` command), the root `README.md` (the end-to-end test command), and the "Current State" in `AGENTS.md`.
- [ ] T024 Run the validation in [quickstart.md](quickstart.md): restore, build, and test both solutions, including `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`, and run `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (Phase 1)**: none.
- **Foundational (Phase 2)**: depends on Setup; blocks all user stories.
- **User Story 1 (Phase 3)**: depends on Foundational.
- **User Story 2 (Phase 4)**: depends on User Story 1 (it controls the work that User Story 1 acquires).
- **User Story 3 (Phase 5)**: depends on User Story 1.
- **User Story 4 (Phase 6)**: depends on User Story 1; T018 also needs T007.
- **Polish (Phase 7)**: depends on all user stories.

### Within user stories

- Platform tasks before the platform tasks that build on them (T007 → T009; T018 → T020).
- Manager queue access (T008) before the attempt runner (T010) before acquisition (T011).
- End-to-end tasks (T012, T017, T021) last in their phases.

### Parallel opportunities

- Phase 2: T002–T005 touch disjoint files (platform NATS infrastructure, Manager runtime, Manager platform client, shared contracts) and run in parallel; T006 follows because it also changes `InfrastructureServiceCollectionExtensions.cs`.
- Phase 3: T007 (platform store) with T008 (Manager queue access), then T009 (platform issuance) with T010 (attempt runner).
- Phase 6: T018 (platform revocation fencing) with T019 (Manager drain and revocation).
- Phase 7: T022 and T023 can run in parallel.

## Parallel Example: Phase 2

```text
T002 Platform NATS JWT authentication mode
T003 Runtime boundary for attempts and the simulated runtime in Core
T004 Platform client calls for queue credentials, claims, heartbeats, and completion
T005 Shared queue-credential contract fixtures
```

## Implementation Strategy

### MVP first (User Story 1)

1. Phase 1 and Phase 2.
2. Phase 3 (User Story 1).
3. Stop and validate with the end-to-end happy path (T012).

### Incremental delivery

1. Add User Story 2 (admission and status), validate.
2. Add User Story 3 (failure handling), validate with T017.
3. Add User Story 4 (drain and revocation), validate with T021.
4. Polish: AppHost, documentation, full validation.
