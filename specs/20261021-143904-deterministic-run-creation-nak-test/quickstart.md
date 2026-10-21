# Quickstart: Validating the Deterministic NAK Test

Prerequisites: .NET 10 SDK (`src/platform/global.json`) and a running Docker-compatible runtime. Run from the repository root.

## Build

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-restore
```

## Scenario 1: The test passes repeatedly (SC-001)

Run the test at least 20 times alone and expect every run to pass:

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~RunCreationTests.UnavailableLookupNaksAndRunIsCreatedAfterRecovery"
```

Run the class at least 5 times with CPU load (for example `stress-ng --cpu 0`, or a portable generator such as parallel busy-loop processes started by a shell loop) and expect every run to pass:

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~RunCreationTests"
```

## Scenario 2: Wrong behavior still fails (SC-002)

Apply each probe temporarily, expect the test to fail on its assertions without hanging, then remove it:

- Acknowledge the notification while the lookup is unavailable: the "no receipt" assertion or the exact-one-run wait fails.
- Create two runs after recovery: the exact-one-run assertion fails.

## Scenario 3: Static checks (SC-003)

- The test contains no fixed-duration `Task.Delay` standing in for a condition, and no assertion is textually removed (`git diff` of `RunCreationTests.cs`).
- A code comment next to the fix names the evidenced cause, and the task result or pull request comment summarizes the reproduction and log evidence.
- The change touches only the files the cause lives in plus this spec folder.

If the environment limits the run counts of Scenario 1 (20 runs alone, 5 under load), they may be reduced to a documented minimum, and the reduction is reported.

Evidence is development evidence only; it does not establish deployment support or production readiness. Validated contracts: none (no interface changes).
