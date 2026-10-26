# Quickstart: Validating the Deterministic Conformance Test

## Prerequisites

A running Docker-compatible container runtime and the .NET 10 SDK; restore and build first:

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
```

## Scenario 1: The class passes repeatedly alone

Run from the repository root, at least 30 consecutive times (SC-001, SC-002):

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~PayloadConformanceTests"
```

Expected: every run passes both tests.

## Scenario 2: The class passes under CPU load

Start one busy-loop process per core, run the same command at least 10 consecutive times, then stop the load processes. Expected: every run passes.

## Scenario 3: Deliberate race widening

Temporarily delay the publication of all but the first ready-work row of the run test-side (for example not-yet-due outbox rows) and run the old test: it fails. Run the fixed test: it passes. Remove the delay afterward (FR-001).

## Scenario 4: Probes still fail the test

Each of these temporary probes makes `PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity` fail on its assertions (SC-003): an invalid job payload, a missing segment identity field, a segment on a non-segment job. Remove all probes afterward.

## Scenario 5: Unpublished messages are named

With a row of the run that cannot be published (a probe), the test fails naming the row and its last failure category instead of hanging or asserting on a partial set. Also hold one row of the run `pending` (not due): the test fails through the wait's own failure naming the row (id, `message_type`, `subject`, `attempt_count`, `failure_category`, `next_attempt_at`) instead of the generic xUnit timeout. Remove the probes.

## Scenario 6: Test order and mid-test failure isolation

Run each test of the class alone first and the other afterwards, in both orders, in separate `--filter` invocations; then make `PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity` fail mid-test with a temporary edit and run the other test immediately afterwards. Expected: the other test passes. Remove the temporary edit.

## Scenario 7: Other users of a changed helper

If a shared helper changed, run the integration tests that use it (`--filter "FullyQualifiedName~Analysis"` as needed) and confirm they pass.
