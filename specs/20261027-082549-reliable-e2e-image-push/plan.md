# Implementation Plan: Reliable End-to-End Analyst Image Push

**Branch**: `20261027-082549-reliable-e2e-image-push` | **Date**: 2026-10-27 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261027-082549-reliable-e2e-image-push/spec.md`

## Summary

The Docker-backed end-to-end support code accepts the manifest digest of a push only from an `aux` progress record and
throws when the response carries it elsewhere (the final status line `<tag>: digest: sha256:… size: …`), and it ends
every engine call on one shared five-minute client timeout. The plan changes only the end-to-end test support code, its
unit tests, and documentation:

1. A pure, Docker-free **push digest determination** (progress record, then final status line, then the local test
   registry's manifest, then the engine's image record; first valid digest wins; mismatches, errors, and malformed
   digests fail with the full response).
2. A named **engine call policy** (push limit, build limit, bounded attempts and retry delay, transient-only
   classification, cancellation by the test's token) replacing the client timeout as what ends a push or build.
3. A shared **build-and-push bound** (one process-wide semaphore with a documented value) and a **class-scoped image
   cache** that builds and pushes one variant once per test class, with failures reported to every waiting test.
4. The three build-and-push helpers (`TestAnalystImage`, `ReferenceAnalystImage`, `ShotBoundaryAnalystImage`) use these;
   a README of the project records the evidenced cause, the digest sources in order, the limits, the retry rule, the
   bound, the reuse rule, and the run commands. The cause is established with evidence (research.md
   `## Evidence` and the README's `## Cause`, FR-001), and the fix has to hold under either suspected cause. The README is created after the digest fix (T001 after T007), because a README change makes the task verification run all Docker-backed tests of the project, which fail until the fix landed.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`)

**Primary Dependencies**: existing only: Testcontainers, xunit.v3, Shouldly, the Integration.Tests registry fixture (`OciRegistryContainerFixture`, `registry:2`); no new package

**Storage**: N/A (an in-memory cache per test class; temporary build contexts as today)

**Testing**: xunit.v3 unit tests that run without Docker in `SocAlytics.Platform.EndToEnd.Tests` (recorded push responses, fake `HttpMessageHandler` engine and registry, a fake time provider, a concurrency probe); the existing Docker-backed tests, plus `SocAlytics.Platform.Images.Tests`, as one full verification run

**Target Platform**: Linux Docker Engine on the engine socket `/var/run/docker.sock` (Docker Desktop on Windows stays manual evidence)

**Project Type**: test support code of a .NET test project (no product change)

**Performance Goals**: no added fixed sleeps; the largest test image (about 214 MB `media-context-classification`) must finish within the named limits under load

**Constraints**: FR-013 (no product code, Dockerfiles, images, contracts, or workflows change); FR-010 (no test skipped, weakened, or loosened); no fixed sleep other than the named bounded `RetryDelay` of `EngineCallPolicy`, and none in tests; messages never contain credentials

**Scale/Scope**: about 33 end-to-end tests in 17 classes, of which 6 classes build and push images; 3 image helpers; 1 engine client

No NEEDS CLARIFICATION remain; the values in [research.md](research.md) are development values.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / constraint | Result |
| --- | --- |
| I. Architecture is the design authority | Pass. No design decision of `docs/architecture/` changes; no architecture statement is changed (see Architecture Updates); T027 only verifies that the two end-to-end evidence statements remain true. |
| II. Source-area ownership | Pass. Everything lives in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests` (platform area); no new `src/` child. The README is the project's first documentation artifact. |
| III. API-first control plane | Not affected (no API, contract, or video path). |
| IV. Evidence over claims | Pass. Unit tests without Docker plus one full Docker-backed run and the Images tests; stability evidence over repeated runs is recorded manually and is not presented as production readiness. |
| V. Focused, minimal changes | Pass. Four new support types and the moved `DockerEngine`, three edited helpers and their call sites; no abstraction beyond what the requirements name. |
| Environment tooling | Pass. Needs only the .NET 10 SDK, a Docker engine, and the packages the project already references; `environment-setup` and `environment-verify` are unchanged, so no environment spec is a dependency. |

**Post-design re-check**: unchanged; the design adds no project, package, or product dependency. The one touched
shared test project is `Integration.Tests` (not edited; its fixture is only used).

## Architecture Updates

None. The feature changes only the end-to-end test support code, its unit tests, and the project's documentation; no
component, technology, topology, protocol, contract, or term of the architecture changes. `docs/architecture/` mentions
the Docker-backed end-to-end tests only as evidence of what they cover
([platform-implementation.md](../../docs/architecture/platform-implementation.md), the Docker-backed end-to-end test
bullet of the evidence list, and [analysts-models-and-hardware.md](../../docs/architecture/analysts-models-and-hardware.md),
the sentence that the Docker-backed end-to-end tests register the development Analyst images); neither says how a test
image is pushed, which digest sources, limits, retries, or bounds apply, and no statement about planned, future, or
unimplemented work is made true or false by this change, so no architecture task is required. The push and build
behavior is documented in the end-to-end project's README (FR-012).

## Project Structure

### Documentation (this feature)

```text
specs/20261027-082549-reliable-e2e-image-push/
├── plan.md              # This file
├── research.md          # Phase 0 (decisions, Evidence section; the rest of the cause goes to the end-to-end README)
├── data-model.md        # Phase 1 (support types, states, rules)
├── quickstart.md        # Phase 1 (validation scenarios)
├── checklists/
└── tasks.md             # Phase 2 (/speckit-tasks, not created here)
```

`contracts/` is not created: the feature exposes no interface to users or other systems; the engine, registry, and
push-response shapes it consumes are external and recorded as test fixtures in the unit tests.

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/
├── README.md                          # new: cause, digest sources, limits, retry, bound, reuse, commands
├── Support/
│   ├── DockerEngine.cs                # moved out of TestAnalystImage.cs; named limits, cancellation, no client timeout
│   ├── PushDigest.cs                  # pure determination of the manifest digest from the four sources
│   ├── EngineCallPolicy.cs            # named limits, bounded attempts, transient classification, reasons report
│   ├── ImageBuildGate.cs              # process-wide bound on concurrent builds and pushes
│   ├── AnalystImageCache.cs           # class-scoped reuse of a built and pushed variant
│   ├── TestAnalystImage.cs            # edited: uses policy, gate, digest determination
│   ├── ReferenceAnalystImage.cs       # edited: same
│   └── ShotBoundaryAnalystImage.cs    # edited: same
├── ImagePush/                         # new: Docker-free unit tests
│   ├── PushDigestTests.cs
│   ├── EngineCallPolicyTests.cs
│   ├── ImageBuildGateTests.cs
│   ├── AnalystImageCacheTests.cs
│   └── DockerEngineLookupTests.cs     # registry and engine lookups through the DockerEngine constructor seam
└── (existing Docker-backed test classes; those that build images take the cached image where the rule allows)

src/platform/README.md                 # edited: points to the project README, corrects the test description
docs/architecture/platform-implementation.md  # edited only if the documentation task finds a false statement
```

**Structure Decision**: keep the change inside the existing end-to-end test project under `Support/` and a new
`ImagePush/` test folder; no new project, because the unit tests must reach the `internal` support types and the
project already exposes them to its own tests.

## Complexity Tracking

No constitution violations.
