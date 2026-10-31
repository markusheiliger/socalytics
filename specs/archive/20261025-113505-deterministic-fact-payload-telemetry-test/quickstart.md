# Quickstart: Validate the Deterministic Fact Payload Telemetry Test

Prerequisites: .NET 10 SDK from `src/platform/global.json`, a running Docker-compatible runtime. From the repository root:

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-restore
```

1. **Alone, 30 times** (SC-001): run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~FactPayloadTelemetryTests"` repeatedly; every run passes.
2. **With the analysis group in one run, 10 times** (SC-001, SC-002): run the same command with `--filter "FullyQualifiedName~SocAlytics.Platform.Integration.Tests.Analysis|FullyQualifiedName~MinimumVersionEnforcementTests"` (the filter must cover every class that changes); every run passes. The isolated collection runs after the parallel collections, so the deliberate reproduction of step 3 is the proof of isolation.
3. **Deliberate reproduction** (FR-001): temporarily raise `AnalysisMetrics.CompletionRejected("result-payload-invalid")` from a probe test in another parallel collection during the window; the old test fails and the new one passes. Remove the probe.
4. **Probes** (SC-003): temporarily count a rejection twice, with a different outcome, and with a payload value in a tag; the named test fails on its assertions each time. Remove the probes.
5. **Unchanged tests**: run the rest of the project; the tests that were not changed still pass.

The implementation may split steps 1, 2, and 5 across several bounded task attempts (for example the 10 grouped runs as 5 + 5); the required counts stay the same.
