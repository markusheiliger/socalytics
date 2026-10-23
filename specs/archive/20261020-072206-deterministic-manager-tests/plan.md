# Implementation Plan: Deterministic Analyst Manager Queue Credential and Upstream Stager Tests

**Branch**: `20261020-072206-deterministic-manager-tests` | **Date**: 2026-10-20 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261020-072206-deterministic-manager-tests/spec.md`

## Summary

Replace the chance-dependent `ShouldNotContain("SU")` assertion with a shape check of the `userPublicKey` and an exact property list of the request body, and add a bounded wait helper to `UpstreamStagerTests` that the tests use before reading `_runtime.Attempts`.

## Technical Context

**Language/Version**: C# on the .NET 10 SDK selected by `src/analysts/manager/global.json`.

**Primary Dependencies**: existing xUnit v3, Shouldly, and `FakeTimeProvider`; no new package.

**Storage**: None.

**Testing**: `dotnet build` and `dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx`, with the two test classes repeated 20 times.

**Constraints**: test code only; no assertion is weakened or dropped.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes. | Pass |
| II. Source-area ownership | Only `src/analysts/manager/Tests`. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | Repeated runs verify determinism. | Pass |
| V. Focused, minimal changes | Two test files. | Pass |

## Project Structure

```text
specs/archive/20261020-072206-deterministic-manager-tests/
├── spec.md
├── plan.md
├── tasks.md
└── checklists/requirements.md

src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/
├── Queue/QueueCredentialProviderTests.cs
└── Work/UpstreamStagerTests.cs
```

## Architecture Updates

None. The change only makes two existing tests deterministic and alters no design decision or architecture statement.
