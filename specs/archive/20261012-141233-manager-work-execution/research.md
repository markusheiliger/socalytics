# Research: Analyst Manager Work Execution With a Simulated Analyst

Each decision lists what was chosen, why, and what else was considered.

## R1. Queue credentials: NATS decentralized JWT authentication

- **Decision**: Run NATS in operator mode with a full resolver: one operator,
  the system account, and one stamp account (`SOCALYTICS`) with JetStream
  enabled. The platform holds the operator and account signing seeds as
  configuration and mints user JWTs with `NATS.Jwt` and `NATS.NKeys`:
  - its own user JWT at startup (stream and consumer administration, publish,
    the events consumer);
  - per-Manager user JWTs on request (FR-006), with `exp` = now + configured
    lifetime and the permissions in
    [queue-topology-and-permissions.md](contracts/queue-topology-and-permissions.md).

  Revocation adds the Manager's issued user public keys to the account JWT's
  `revocations` map, re-signs it with the operator key, and pushes it through
  `$SYS.REQ.CLAIMS.UPDATE`. The server then disconnects connected users with
  those keys and refuses them on reconnect (FR-023, SC-009).
- **Rationale**: This is the documented, built-in way to make revocation
  immediate, including disconnecting live connections, with no extra service.
  Short `exp` bounds every credential even if a push is delayed.
- **Alternatives considered**:
  - **Auth callout**: needs an always-on callout service, exposes raw client
    credentials on `$SYS.REQ.USER.AUTH`, and requires our own kick logic for
    live connections.
  - **Memory resolver**: cannot accept pushed revocations.
  - **Static users or tokens**: neither per-Manager nor expiring.
- **Modes**: `Nats:Authentication:Mode` is `Jwt` or `None`. `None` keeps the
  existing integration tests and is allowed only in Development and Test; the
  API refuses to start with `None` in any other environment. The AppHost and the
  new JWT-mode test fixture generate a throwaway operator, account, and system
  account per run. Production key custody, rotation, and lifetimes remain
  unresolved under `GOV-CRED-004`.
- **Transport security**: The architecture requires authenticated TLS for NATS
  connections. The Manager accepts `nats://` (plaintext) queue URLs only in
  Development and Test; otherwise it requires `tls://`. TLS server
  configuration is deployment work and out of scope.

## R2. Work topology: one durable pull consumer per capability

- **Decision**: The platform provisions, next to the existing
  `SOCALYTICS_ANALYSIS_JOBS` work-queue stream, one durable pull consumer per
  capability of the default workflow definition resolved from the Analyst
  Registry (the configured development workflow only seeds an empty Registry
  in Development and Test, with semantic capability versions such as `1.0.0`):
  - name `analyst-<capabilityId>`, filter `analysis.jobs.ready.<capabilityId>`;
  - explicit acknowledgement, configured `AckWait`, unlimited redelivery.

  A Manager pulls only from the consumers of capabilities its validated runtime
  offers (FR-003). A delivery whose pinned resource requirements the host
  cannot meet is negatively acknowledged with a configured delay and never
  claimed, so other Managers can take it.
- **Refinement (found by the phase verification of Phase 8)**: the Registry
  feature merged from the default branch moved workflow resolution from
  configuration to the database, and its seed normalizes capability versions
  to semantic versions (`1` becomes `1.0.0`). Consumers therefore exist only
  after the default workflow resolves, and a Manager is assigned consumers only
  for capabilities whose id and version equal those of the resolved definition.
  Topology tests that run without a database must supply the configured
  resolver, and the simulated capabilities must offer the semantic version.
  Rejected alternative: provisioning consumers from configuration only, which
  would bypass the Registry that now owns the default workflow.
- **Rationale**: Work-queue streams forbid overlapping consumer filters, so
  per-Manager consumers are impossible. Per-capability consumers deliver only
  compatible work and need no Manager-side filtering of foreign capabilities.
- **Alternatives considered**:
  - **One shared consumer with filtering in the Manager**: endless redelivery
    loops for incompatible work.
  - **Ephemeral consumers per Manager**: forbidden on work-queue streams.

## R3. Credential issuance with ephemeral user keys

- **Decision**: For each credential, the Manager generates an ephemeral NATS
  user NKey in memory and sends only its public key in the request. The
  platform:
  - verifies the registration is Active (FR-006);
  - records the issued public key, the Manager, and the expiry in
    `registry.analyst_manager_queue_credential`;
  - returns the user JWT, the queue URL, the stream, the consumers per
    capability, a per-Manager inbox prefix, and the stamp's
    `maxConcurrentAnalysts`.

  The Manager renews at two thirds of the lifetime and reconnects with the new
  credential.
- **Rationale**: The NKey seed never leaves the Manager process, so there is no
  durable or transmitted queue secret (FR-006, SC-008). Recording issued keys
  lets revocation name exactly the keys to revoke; entries can be pruned after
  expiry.
- **Alternatives considered**:
  - **Platform-generated seed returned to the Manager**: the secret travels
    over the network.
  - **One stable key per Manager**: it would have to be persisted, which is a
    durable secret.

## R4. Revocation fences attempts in the same unit of work

- **Decision**: `RevokeRegistration` (Club Admin) and `CompleteUnregistration`
  (local unregister) gain two steps in their existing unit of work:
  1. A new `FenceManagerAttempts` step locks and closes every active attempt
     of the Manager as stale and calls the existing `RequeueOrFailAsync`. The
     close-as-stale logic is extracted from `ExpireStaleAttempts` and shared,
     so the revocation consumes budget exactly like expiry (FR-023,
     clarification).
  2. The command marks the Manager's unexpired issued queue credentials as
     revoked with a pending push.

  After commit, the command pushes the rebuilt account JWT immediately.
  `QueueRevocationWorker` retries pending pushes with bounded backoff until NATS
  acknowledges them, and also after a platform restart.
- **Rationale**: The fencing is atomic with the registration state change. The
  account JWT is rebuilt from durable state, so pushes are idempotent and
  recoverable.
- **Alternatives considered**:
  - **Relying on lease expiry**: the architecture requires immediate fencing.
  - **Pushing inside the transaction**: an external call inside a database
    transaction.

## R5. Simulated runtime and Analyst in Manager Core, Development and Test only

- **Decision**: Move the simulated runtime adapter from the test project into
  `Core/Runtime/Simulated/` and extend it with a simulated Analyst. It is
  configured per capability under `Runtime:Simulated` with:
  - an outcome (`succeed`, `fail`, `hang`, `crash`);
  - a run duration and a failure reason;
  - a result reference and checksum;
  - the maximum number of parallel attempts it offers.

  It is registered only when `IHostEnvironment` is `Development` or `Test`.
  With profile `simulated` in any other environment, the Manager refuses to
  start with a diagnostic (FR-008).
- **Rationale**: The end-to-end test and local runs need the real Manager host
  with a simulated Analyst. The environment guard mirrors the registration
  feature's test-only key store.
- **Alternatives considered**:
  - **Keeping it in the test project**: the Manager host could not use it
    outside xUnit.
  - **A separate plug-in assembly**: the architecture forbids plug-in loading.

## R6. Runtime adapter boundary for attempts

- **Decision**: Extend `IContainerRuntimeAdapter` with
  `StartAttemptAsync(AttemptLaunch, CancellationToken)`. `AttemptLaunch` carries
  the pinned digests, capability, scope, and resources. The method returns an
  `IAttemptExecution` with:
  - `Outcome` (a task of succeeded with result reference and checksum, or
    failed with reason);
  - `StopAsync`;
  - `CleanUpAsync`.

  `RuntimeProbe` gains `MaxParallelAttempts`, which feeds the effective limit
  (FR-019).
- **Rationale**: One boundary for the coming real Docker adapter and the
  simulated one. The Manager never chooses images, models, or preprocessing
  (FR-007).

## R7. Claim, heartbeat, and completion behavior

- **Claim retry key**: `<messageId>:<readinessOccurrenceId>` (FR-004), stable
  across retries of the same delivery by the same Manager.
- **Acknowledgement**:
  - after a successful claim: acknowledge;
  - after `claim-obsolete`: acknowledge without work;
  - after a transient failure: no acknowledgement, retry with the same key
    until the `AckWait` margin, then let redelivery happen (FR-005).
- **Heartbeat cadence**: lease ÷ 3 from the claim response. Transient failures
  retry with exponential backoff capped so the next try still precedes the
  lease expiry known from the last confirmed renewal. Renewal is never assumed
  (FR-010, FR-011).
- **Completion idempotency key**: `completion:<attemptId>`. Lost responses are
  retried with the same key. `attempt-obsolete` stops the attempt and frees the
  slot (FR-012, FR-015, FR-016).
- **Execution timeout**: from the claim response; on expiry `StopAsync`,
  `CleanUpAsync`, and a failed completion with reason `execution-timeout`
  (FR-009).
- **Rationale**: These reuse the platform's existing retry-key and idempotency
  semantics without contract changes.

## R8. Effective concurrency and pulling

- **Decision**: The effective limit is
  `min(hostLocal, stampMax, runtime.MaxParallelAttempts)`:
  - `hostLocal` is a persisted positive integer, default 1, changed through the
    CLI and local control;
  - `stampMax` comes with the queue credentials (`AnalystWork:MaxConcurrentAnalystsPerManager`
    on the platform);
  - `runtime.MaxParallelAttempts` comes from the validated runtime.

  The acquisition loop fetches one message at a time per free slot, round-robin
  over the Manager's capability consumers, only while `AdmissionGate` admits
  (FR-001, FR-002, FR-018, FR-019, FR-020).
- **Rationale**: Fetching single messages guarantees the limit is never
  exceeded, and admission stays in one place.

## R9. Drain and the drain-timeout policy

- **Decision**: The Manager's `IManagerWorkTracker` becomes the attempt
  registry: its active count, idle wait, and stop-and-clean-up. The existing
  `DrainCoordinator` keeps its two policies from the registration feature:
  - **`StopAndCleanUp`**: stops the remaining attempts, cleans up, and reports
    a failed completion with reason `drain-timeout` for each current attempt
    (FR-021).
  - **`CancelRequest`** (the current default): cancels the drain request and
    returns to the previous intent, so attempts continue.
- **FR-021 covers both policies**: on drain timeout the Manager applies the configured policy, as FR-021 and User Story 4 scenarios 2 and 3 specify.

## R10. End-to-end verification across both solutions

- **Decision**: Add a new test project
  `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`. It references:
  - `SocAlytics.Platform.Integration.Tests`, for `PlatformApiFactory`,
    PostgreSQL and NATS fixtures, and `RegisteredAnalystManagerClient`
    patterns;
  - Manager Core.

  The test starts JWT-mode NATS and PostgreSQL containers and hosts the API
  in-process with `Registry:AllowSoftwareKeys` set. It runs a real Manager Core
  host in the `Test` environment against the in-process API through an HTTPS
  `TestServer` handler, with a SoftHSM device key and the simulated runtime.

  The project is Linux-only: it is skipped with a clear reason where SoftHSM2
  is missing, like the Manager's key-store tests. The verification
  environment's platform test run finds it automatically. Phase and merge runs
  include it.
- **Rationale**: The platform side already has the heavy fixtures and the real
  DPoP registration path. Manager Core is a UI-free library, so referencing it
  from a test project crosses no production boundary.
- **Alternatives considered**:
  - **Placing the test in the Manager solution**: it would have to reference
    platform test support across solutions.
  - **Shared fixtures and a manual run**: rejected by the clarification.
