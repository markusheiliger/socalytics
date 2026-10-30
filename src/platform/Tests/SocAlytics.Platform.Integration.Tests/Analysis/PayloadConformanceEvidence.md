# Payload Conformance Evidence

## Evidence

### T001 Baseline

- Docker: available (`docker info` succeeded).
- `dotnet restore src/platform/SocAlytics.Platform.slnx`: succeeded.
- `dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests`: succeeded, 0 warnings, 0 errors (26 s).
- `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~PayloadConformanceTests"`: Passed 2, Failed 0, Skipped 0, Total 2; test duration 8 s (wall clock 35 s including startup).

### T002 Repeated runs

Unchanged class, `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~PayloadConformanceTests"`, Docker available, 4 cores.

- Alone: 30 runs, 30 passed (2 of 2 tests each), 0 failed, 0 skipped; test duration 9 s (28 runs) or 10 s (2 runs); wall clock 19-20 s per run. No failing assertion lines.
- Under CPU load (one busy-loop process per core, 4 processes, load average rose to about 11): 10 runs, 10 passed, 0 failed, 0 skipped; test duration 15-18 s; wall clock 32-34 s per run. No failing assertion lines. The load processes were stopped afterward.
- Result: the failure did not reproduce in 40 runs in this environment. The observed maximum test duration is 18 s (loaded), the basis for the hang-guard timeout of T006 (at least ten times that, 180 s).

### T003 Premature read

Temporary test-side edit of `StartAsync` in `PayloadConformanceTests.cs` (reverted completely; `git status` shows no change to the file): after finalization a background task repeatedly set `next_attempt_at` to the fake clock plus two hours on the pending `analysis.ready-work` outbox rows (`socalytics.outbox_messages`, no publisher change), so those rows were not due. Command: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~PayloadConformanceTests.PublishedMessagesConform"`.

- Variant 1, all ready-work rows except the first by `created_at, id` delayed: Passed 1 (6 s). The wait returned once one ready-work message existed, so it accepts whatever is published at that moment as the complete set; the delayed rows were never read or asserted on.
- Variant 2, only rows whose payload `executionScope` is `segment` delayed: Failed 1 (3 m 8 s) with `System.TimeoutException : No ready-work message was published.` at `WaitForReadyWorkAsync` (line 151). The wait has no condition on the publication state of the run: it succeeds when any ready-work message happens to be visible and otherwise gives up after 360 iterations of fake-clock advances and 500 ms delays, so the outcome depends on the elapsed time and on publication order. The `jobs.ShouldContain(... "segment")` assertion was not reached.
- Result: the premature read and the wait on elapsed time are demonstrated as test-side causes; the wait returns on the first ready-work message, not on a complete set of the run's messages.

### T004 Staggered reads

Temporary test-side edit of `ReadMessagesAsync` in `PayloadConformanceTests.cs` (reverted completely with `git checkout`; `git status` shows no change to the file): after the outbox id snapshot and before each of the two stream reads (`SOCALYTICS_ANALYSIS_JOBS`, then `SOCALYTICS_EVENTS`), 20 fake-clock advances of 3 s with a 250 ms delay let the publisher run, and each call logged the snapshot size and the number of messages read. Command: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~PayloadConformanceTests"`.

- Result: Passed 2, Failed 0 (47 s). Each test's first read returned snapshot=1, read=1, ready=0, events=0; its later read returned snapshot=3, read=3, ready=1, events=1.
- Interpretation: messages published between the snapshot and a stream read are not missed or split, because every outbox id of the snapshot was found in a stream and the read is filtered by that fixed id set. Rows created after the snapshot are simply absent from it, so a read is a consistent view of the snapshot only. The old test is therefore not shown to split messages through staggered reads; the staggered reads are ruled out as a demonstrated cause. The set the old read takes is only as complete as the moment of the snapshot, which is the premature-read cause of T003, and is why T007 reads both streams against one set taken after the run's publication state is complete.

### T005 Shared state and production code
