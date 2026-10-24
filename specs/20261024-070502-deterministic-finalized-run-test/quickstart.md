# Quickstart: Validating the Deterministic Test

Prerequisites: .NET 10 SDK (`src/platform/global.json`) and a running Docker-compatible runtime.

1. Build: `dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests`
2. Run the class: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~RunCreationTests"`. Expected: every test passes.
3. Repeat the named test (`--filter "FullyQualifiedName~FinalizedRecordingSetFlowsThroughNatsToExactlyOneRun"`) at least 30 times alone and at least 10 times with one busy-loop process per core. Expected: 100% pass (SC-001).
4. Demonstrate the race (temporary, test-side only): delay only the publisher's commit by decorating `IUnitOfWork` (its scope) through the test's `configureServices` hook in `FinalizationSetup`, and also decorating `IOutboxPublicationStore` so that `MarkPublishedAsync` sets a flag on a per-scope object shared with the decorated `IUnitOfWork` scope; that scope's `CommitAsync` awaits a fixed 2-second delay only when the flag is set, so only the publisher's scope is delayed and the consumer's run creation is not. Do not hold a competing lock on the outbox row: the publisher claims rows with `FOR UPDATE SKIP LOCKED` and would skip it. Expected: the previous test version fails on the `published` count and the new version passes (FR-001).
5. Probes (temporary, removed afterward): a consumer that creates a second run for a duplicate, and one that writes a second receipt. Expected: the named test fails on its run and receipt assertions and does not hang (SC-003).
6. Confirm `git diff` touches only `RunCreationTests.cs` and this spec folder, with no removed assertion and no `Task.Delay` added (SC-004).

Passing results are development evidence only, not deployment support or production readiness.
