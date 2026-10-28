# Quickstart: Validating the Deterministic Retryable Failure

Prerequisites: .NET 10 SDK from `src/platform/global.json`, a running Docker-compatible runtime (PostgreSQL and NATS containers), a restored and built solution.

## 1. Build

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
```

## 2. The behavior class passes

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"
```

Expected: all four tests pass; `FailRetryableEndsWithAttemptsExhausted` ends `failed`, `attempts-exhausted`, 3 attempts.

## 3. Repeated runs (SC-001)

Run the filter `FullyQualifiedName~FailRetryableEndsWithAttemptsExhausted` at least 20 times in a row, and the class filter at least 5 times in a row without synthetic CPU load. Expected: no failure. CPU-loaded runs are optional supplemental evidence.

## 4. Probes (SC-002, temporary, removed afterward)

- Stop the redelivery after the second attempt: expect the test to fail on its bounded wait with the diagnostic message (last read, attempts, runner log), not hang.
- Make the third attempt succeed: expect the test to fail on its unchanged assertions.

Confirm with `git diff` that no probe remains and no assertion is removed.

## 5. Neighbors and repository checks

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SimulatedAgentWorkload"
node .github/scripts/check-markdown.mjs
```

Expected: `SimulatedAgentWorkloadTests` and the sibling behavior tests pass unchanged.
