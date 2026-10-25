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

Temporarily delay the publication of one message of the run test-side (for example a not-yet-due outbox row) and run the old test: it fails. Run the fixed test: it passes. Remove the delay afterward (FR-001).

## Scenario 4: Probes still fail the test

Each of these temporary probes makes `PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity` fail on its assertions (SC-003): an invalid job payload, a missing segment identity field, a segment on a non-segment job. Remove all probes afterward.

## Scenario 5: Unpublished messages are named

With a row of the run that cannot be published (a probe), the test fails naming the row and its last failure category instead of hanging or asserting on a partial set. Remove the probe.

## Scenario 6: Other users of a changed helper

If a shared helper changed, run the integration tests that use it (`--filter "FullyQualifiedName~Analysis"` as needed) and confirm they pass.
