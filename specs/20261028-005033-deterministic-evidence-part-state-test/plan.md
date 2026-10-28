# Implementation Plan: Deterministic Evidence Part State Test

**Branch**: `20261028-005033-deterministic-evidence-part-state-test` | **Date**: 2026-10-28 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261028-005033-deterministic-evidence-part-state-test/spec.md`

## Summary

`EvidencePartStateTests.Never_materialized_segment_is_materializing_then_available_with_a_link_after_the_worker_ran` is flaky. Code reading shows the leading candidate: `EvidenceScenario` starts the API host with the analysis background workers at their default (`Analysis:Workers:Enabled` is `true`), so the real `SegmentMaterializationWorker` (poll interval 5 seconds) can claim and materialize segment rows between the test's own steps while the test drives the same rows by hand. The plan is evidence first (repeated runs alone and under load, plus a temporary probe that makes the worker act in the window), then the fix where the cause lives. The expected fix is in `EvidenceScenario`: the scenario owns the workers by turning them off by default (a caller's `extraSettings` can still override), so the explicit `MaterializeSegmentsAsync` call is the only materialization. Assertions, platform behavior, and limits stay unchanged.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`)

**Primary Dependencies**: xUnit v3, Shouldly, Dapper, Npgsql, Testcontainers (PostgreSQL and RustFS fixtures), the platform's fake `TimeProvider`

**Storage**: PostgreSQL (`materialized_segments`) and RustFS (S3-compatible) in disposable containers

**Testing**: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~Analysis.Evidence"`; repeated runs alone and under CPU load; removable probes (a worker acting between the steps, broken demand recording, `available` reported early)

**Target Platform**: Linux CI runner and developer machines with a Docker-compatible runtime and the pinned `ffmpeg`

**Project Type**: Test support code of the platform monolith (platform code only if the evidence shows a platform defect)

**Performance Goals**: N/A (no new limits, sleeps, or retries)

**Constraints**: Every assertion of the test and of the evidence test classes unchanged; documented part states, demand recording, and materialization unchanged; other users of `EvidenceScenario` stay deterministic and independent of timing

**Scale/Scope**: One shared helper (`EvidenceScenario.Settings`/`StartAsync`) and, only if the evidence demands it, the test or one platform file

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **I. Architecture authority**: Pass. No design change; part states, demand recording, and materialization semantics in `docs/architecture/` stay as they are (see Architecture Updates).
- **II. Source-area ownership**: Pass. All changes stay under `src/platform/Tests/`; no new `src/` child.
- **III. API-first control plane**: Pass. The test keeps using the `/api/v1/analysis/runs/{runId}/evidence-media` operation; its database reads are evidence queries only.
- **IV. Evidence over claims**: Pass. The fix is backed by repeated-run evidence and probes proving the test still fails on broken behavior and no longer on a racing worker. Results are development evidence only.
- **V. Focused, minimal changes**: Pass. Only the file the evidenced cause lives in changes; no new abstractions or dependencies.
- **Environment**: Pass. Docker-backed Integration tests already run through `environment-setup` and `environment-verify`; no environment spec is required.

Post-design re-check: unchanged; all gates pass; no Complexity Tracking entry.

## Project Structure

### Documentation (this feature)

```text
specs/20261028-005033-deterministic-evidence-part-state-test/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── checklists/requirements.md
└── tasks.md             # Created by /speckit-tasks
```

No `contracts/` folder: the feature adds and changes no interface.

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/
├── Support/EvidenceScenario.cs        # expected home of the fix: the scenario owns the workers
└── EvidencePartStateTests.cs          # only if the evidence points at the test's own steps
src/platform/SocAlytics.Platform.Application/Segments/ and Infrastructure/Workers/
                                       # only if the evidence shows a platform defect (then with a focused test)
```

**Structure Decision**: No new files in source areas. The fix edits the file the evidenced cause lives in (decision procedure in [research.md](research.md)); a platform fix would add a focused test under `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/`.

## Architecture Updates

None. The feature changes test support code only (or, if the evidence shows a platform defect, makes the code match the part state, demand, and materialization semantics that `docs/architecture/` already documents). It adds no component, technology, topology, protocol, contract, or term, and makes no planned, future-tense, or unimplemented statement true or false. No documentation task is required.

## Complexity Tracking

No constitution violations; nothing to justify.
