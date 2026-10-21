# Implementation Plan: Deterministic Unavailable-Lookup NAK Test

**Branch**: `20261021-143904-deterministic-run-creation-nak-test` | **Date**: 2026-10-21 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261021-143904-deterministic-run-creation-nak-test/spec.md`

## Summary

`RunCreationTests.UnavailableLookupNaksAndRunIsCreatedAfterRecovery` passes only on its rerun in some verifications. It sleeps a fixed three seconds as a stand-in for "the consumer attempted the lookup and postponed the notification", then polls in real time after recovery, on a NATS server and durable consumer shared by the whole `NatsFaultInjection` collection. The implementation reproduces the failure, evidences which race causes it, and removes that cause where it lives: the test makes the lookup wrapper an awaitable, counted record of its calls, waits on that signal instead of the sleep, isolates the shared consumer state, and awaits the exact-one-run outcome within a bounded wait. No assertion is weakened and no retry or longer limit is added.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3, Shouldly, NATS JetStream client (existing). No new dependency.

**Storage**: PostgreSQL, RustFS, and NATS in disposable test containers (existing).

**Testing**: `SocAlytics.Platform.Integration.Tests`, Docker-backed; the test run repeatedly alone and its class under CPU load.

**Target Platform**: Linux and developer machines with a Docker-compatible runtime.

**Project Type**: Test change in a platform monolith's test project; platform code only if the evidence puts the cause there.

**Performance Goals**: None.

**Constraints**: No assertion weakened, removed, or skipped; no retries or longer fixed limits or sleeps as the fix; the postponement, redelivery, idempotency, and single-run semantics of the recordings-finalized consumer stay as documented; other tests of `RunCreationTests` and the collection stay unchanged.

**Scale/Scope**: One test, the `SwitchLookup` wrapper and wait helper it uses, and the NATS test fixture state it shares.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/platform`; no new `src` child. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | The cause is evidenced by repeated runs and logs; probes must still fail the test. | Pass |
| V. Focused, minimal changes | One test path; no package or abstraction. | Pass |
| Environment features | Needs only the existing .NET SDK and Docker. No environment spec. | Pass |

Post-design re-check: Pass.

## Design

1. **Establish the cause.** Run the test repeatedly, alone and with CPU load, capturing the consumer and platform logs of failing runs, and compare with the candidates of the spec: the fixed sleep, shared NATS stream and durable consumer state left by earlier tests, redelivery timing after recovery, and an unobservable switch (see [research.md](research.md)).
2. **Remove the cause where it lives.** Fixes applied only for the evidenced cause:
   - Make `SwitchLookup` record each unavailable call (a counter of all unavailable calls, which is sufficient because the test owns its host and database, and an awaitable bounded wait for a minimum count) and replace the `Task.Delay` with that wait for at least two unavailable calls (which also proves one postponement and redelivery), then assert no receipt.
   - Isolate shared NATS state: the test always uses a test-specific consumer name through `Analysis:Notifications:ConsumerName`, and only on evidence additionally applies the mechanism chosen as described in [research.md](research.md) (a deliver-new or filtered policy of that consumer, or a fixture purge run after topology provisioning), so the consumer sees only this test's notification without affecting other tests of the collection.
   - After `flag.Throw = false`, await the exact-one-run condition with a condition-driven loop with an explicit finite deadline (for example a linked `CancellationTokenSource` with a stated timeout) that fails the test with a clear message on expiry, instead of a count of real-time polls, and read the run count only after that state. The deadline is a failure bound, not the expected duration. The shared `WaitAsync` keeps its behavior for the other tests; only this test uses the new wait or a new helper.
3. **Keep behavior.** Shared helpers keep the other tests of `RunCreationTests` and the collection unchanged; platform code changes only when the evidence requires it and then keeps the documented semantics.
4. **Prove it.** Run the test at least 20 times alone and its class at least 5 times under load; run probes (acknowledge while unavailable; two runs after recovery) to show the test still fails on its assertions; remove the probes. See [quickstart.md](quickstart.md).

## Project Structure

### Documentation (this feature)

```text
specs/20261021-143904-deterministic-run-creation-nak-test/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── spec.md
└── tasks.md             # created by /speckit-tasks
```

No `contracts/` folder: the feature exposes no interface.

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/
├── Analysis/RunCreationTests.cs
└── Infrastructure/NatsContainerFixture.cs
```

**Structure Decision**: Edit the files the evidenced cause lives in. `RunCreationTests.cs` is the expected file; `NatsContainerFixture.cs` changes only if isolation belongs in the fixture, and platform code only when the evidence requires it.

## Complexity Tracking

No Constitution Check violations.

## Architecture Updates

None. The fix removes a race in test code and changes no architecture narrative, design decision, or status statement in `docs/architecture/`; if the evidence puts the cause in platform code, the fix keeps the documented notification semantics and the implementation re-checks this section.
