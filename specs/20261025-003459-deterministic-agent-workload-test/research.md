# Research: Deterministic Simulated Agent Workload Invocation Test

## Findings from reading the code

- `AgentOrchestrationFixture.StartAsync` builds a `PlatformApiFactory` whose `Time` is a `MutableTimeProvider`
  (`IdentityAccess/Support/MutableTimeProvider.cs`): `GetUtcNow()` returns a stored value that the fixture sets once
  to `DateTimeOffset.UtcNow` and nothing advances. Its timers are the base `TimeProvider` timers, so
  `Task.Delay(delay, timeProvider, ct)` still waits in real time.
- The API, its workers (`WorkerLoop`, outbox publisher, `AgentAttemptMonitorWorker`), the runner (token and DPoP `iat`,
  `_tokenExpiresAt`, `_queueExpiresAt`), and stored timestamps (`next_attempt_at`, `lease_expires_at`, `deadline_at`)
  all read this frozen clock, while the test's wait loop bounds itself with `DateTime.UtcNow` (wall clock) and the
  NATS server redelivers with its own wall clock.
- `OutboxPublicationStore` selects `next_attempt_at IS NULL OR next_attempt_at <= @now`. With a frozen `@now`, a retry
  scheduled after a failed first publication attempt never becomes due.
- `Messaging:Nats:ProvisionTopology` provisions stream and consumer lazily in `NatsJetStreamTransport` on the first
  publish, while the runner calls `GetConsumerAsync` on the consumer name; an early runner iteration fails and is
  retried after `RetryDelay` (real timer), which is harmless on its own.
- The test waits for `succeeded` only; any other state (`failed`, redelivery, lease loss) is polled until the 120 s
  bound and reported as `invocation.ShouldNotBeNull()` without the state or reason.
- Streams and consumers are named with a per-fixture GUID, but the NATS container (`NatsContainerFixture`) is shared by
  parallel test classes, and the Monitor, reconciliation, and outbox workers of every API instance run at 1 to 2 s
  intervals.

## Decision 1: Establish the cause before fixing

**Decision**: Reproduce first (30 runs alone, 10 under one busy-loop process per core), then demonstrate each suspect
by widening its window test-side (delay the topology provisioning, hold the first publication, skew the fixture clock),
and record the outcome per suspect in `## Evidence`.

**Rationale**: FR-001 and SC-001 require evidence; the failure never reproduced in the three verifications except by
chance.

**Alternatives considered**: Fix the leading hypothesis without evidence (rejected: FR-001); raise the 120 s bound or
add a retry (rejected: FR-002).

## Decision 2: Fix candidates by evidenced cause

| Evidenced cause | Fix |
| --- | --- |
| Frozen fixture clock gates a retry or lease | One clock: let the fixture clock follow real time (a `TimeProvider` that adds real elapsed time to the set value) or advance it explicitly on an observable condition, shared by the API, workers, runner, and the test's wait. |
| Topology not provisioned before the first publish or fetch | Provision stream and consumer in the fixture before the runner starts (the transport's own ensure method), so start order no longer matters. |
| Wait ends on a guessed bound or hides the state | Poll until the invocation is terminal (`succeeded`, `failed`, `lifecycle-suppressed`) and fail with the state and the recorded reason; the outer cancellation stays only as a safety net for a non-terminal state and names it. |
| Leftover or shared state | Per-test stream, consumer, and database names (already unique) and disposal of the runner and the NATS objects in the fixture. |
| Covered production code | A minimal change that keeps the documented behavior (FR-006), with a focused test. |

**Rationale**: Each row matches one suspected cause of the spec and one rule of FR-002.

**Alternatives considered**: A fixed `Task.Delay` before starting the runner or a rerun attribute (rejected: FR-002).

## Decision 3: Verification

**Decision**: `SeedTests` (all five tests), `SimulatedAgentWorkloadTests`, `SimulatedAgentWorkloadBehaviorTests`, and
every test that uses a changed helper run in the same repeated runs. Probes (wrong advice text, wrong digest, missing
artifact kind) show the assertions still fail, and are removed afterwards.

## Evidence

To be filled in during implementation: the repeated-run results alone and under load, the demonstration per suspected
cause (wall-clock versus fake time, start order, polling loop, shared state, covered production code) with the result
"cause" or "ruled out", and the probe results. The same text goes into a code comment next to the fix.
