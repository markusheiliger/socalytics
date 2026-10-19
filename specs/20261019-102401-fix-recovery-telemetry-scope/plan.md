# Implementation Plan: Deterministic Telemetry Capture in the Password Recovery Secret Test

**Branch**: `20261019-102401-fix-recovery-telemetry-scope` | **Date**: 2026-10-19 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261019-102401-fix-recovery-telemetry-scope/spec.md`

## Summary

The audit test `AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded` collects the tags of every activity and metric of the test process and asserts that no secret appears. Unrelated database-driver telemetry (pool names that carry connection strings) can contain the weak password `short`, so the test fails at random. The fix scopes the capture to the host under test: the activity listener listens only to the instrumentation scopes of the host and its clients and skips the database driver, the meter listener enables only the platform's own meters, the messaging meters of the host, and the ASP.NET Core and HTTP client meters, process-wide framework telemetry is always filtered by the host's identity, and the checked text is cleared of anything emitted outside the test window. Every secret assertion stays unchanged.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3 and Shouldly (existing); `System.Diagnostics.ActivityListener` and `MeterListener` (existing). No new dependency.

**Storage**: None.

**Testing**: `SocAlytics.Platform.Integration.Tests` (Docker-backed); the audit test run repeatedly alone and with the whole suite.

**Target Platform**: Linux and developer machines with a Docker-compatible runtime.

**Project Type**: Test-only change to a platform monolith's test project.

**Performance Goals**: None.

**Constraints**: Secret list, assertions, and failure messages unchanged; no production code change; no skipping or loosening.

**Scale/Scope**: One test method's listener setup, in one file.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/platform/Tests`; no new `src` child. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | The fix is evidenced by repeated runs and a deliberate leak probe that must still fail the test. | Pass |
| V. Focused, minimal changes | One test file, no new package or abstraction; no assertion weakened. | Pass |
| Environment features | Needs only the existing .NET SDK and Docker. No environment spec. | Pass |

Post-design re-check: Pass.

## Design

- `ActivityListener.ShouldListenTo` returns true for an `ActivitySource` only when its name starts with `Microsoft.AspNetCore`, `System.Net.Http`, `SocAlytics`, `MailKit`, or the host's application name, and never for `Npgsql*`. Platform sources that exist at implementation time are discovered from `src/platform` (`AddSource` in `ServiceDefaultsExtensions.cs`), not guessed.
- `MeterListener.InstrumentPublished` enables an instrument only when its meter name starts with `Microsoft.AspNetCore`, `System.Net.Http`, `SocAlytics`, or the name of a messaging (NATS) meter the host registers (discovered from `src/platform` like the sources, and confirmed by the test's own checked text containing at least one messaging measurement or by the plan's assumption that none exists), and never `Npgsql*`. MailKit and the host application name apply to activity sources only.
- The listeners are started before the host and disposed after the last check, so telemetry outside the test window is not recorded; the sinks are cleared when the host has started so that start-up telemetry of other tests in parallel does not enter the text. Because the ASP.NET Core and HTTP client instrumentation is process-wide, an activity or measurement from those scopes is always kept only when its `server.port`/`url.full` tag matches the host under test's address or its trace belongs to a request the test sent; this host-identity filter is unconditional, not a fallback. The implementation notes in the commit message of T001 which tag it matched on.
- The secrets list, `ShouldNotContain(..., Case.Insensitive, ...)`, audit and log text construction, and counts stay as they are.

## Project Structure

### Documentation (this feature)

```text
specs/20261019-102401-fix-recovery-telemetry-scope/
├── plan.md
├── spec.md
├── tasks.md
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/
└── SelfServicePasswordRecoveryLimitsTests.cs   # changed: listener scoping only
```

**Structure Decision**: Keep the change inside the failing test; a shared helper is created only if the implementation shows another test needs the same scoping, which is out of scope here.

## Architecture Updates

None. The fix changes only the capture of one integration test and no architecture narrative, design decision, or status statement in `docs/architecture/`.
