# Quickstart: Deterministic Stall Expiry of the Simulated Agent Workload

Prerequisites: .NET 10 SDK from `src/platform/global.json` and a running Docker-compatible runtime.

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
```

1. **One run**: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"` passes all four tests.
2. **Repetition (SC-001)**: run the filter `FullyQualifiedName~StallExpiresTheFirstAttemptAndSucceedsOnTheSecond` 20 times in a row, then the class 5 times while CPU burners run (for example one `yes > /dev/null` per core); every run passes.
3. **Probes (SC-002)**: temporarily make the first attempt never expire, then temporarily make the second attempt fail; each makes the test fail on its assertions or its bounded wait within the existing limits. Remove the probes and confirm `git diff` shows no probe.
4. **Neighbours (FR-005)**: `--filter "FullyQualifiedName~SimulatedAgentWorkloadTests"` and the other three behavior tests pass unchanged.
5. **Documentation**: `node .github/scripts/check-markdown.mjs --files specs/archive/20261023-150804-deterministic-stall-expiry-test/plan.md` reports no findings.

Passing runs are development evidence only, not deployment support or production readiness.
