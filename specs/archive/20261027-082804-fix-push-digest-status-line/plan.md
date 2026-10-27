# Implementation Plan: Read the Pushed Manifest Digest from Every Engine's Push Output

**Branch**: `20261027-082804-fix-push-digest-status-line` | **Date**: 2026-10-27 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261027-082804-fix-push-digest-status-line/spec.md`

## Summary

`DockerEngine.PushAsync` in the end-to-end test support reads the pushed manifest digest only from the `aux.Digest` property of the push stream and throws on engines that report it only in a status line (`<tag>: digest: sha256:<hex> size: <n>`). The plan moves the stream interpretation into a small internal pure function next to `DockerEngine`, keeps the `aux` path and the `errorDetail` failure, adds an anchored regular expression for the status line, and covers the parsing with Docker-free unit tests in the same test project. No production code, contract, or documentation changes.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`)

**Primary Dependencies**: `System.Text.Json` and `System.Text.RegularExpressions` from the base class library; xUnit v3 and Shouldly already referenced by the test project; no new packages

**Storage**: N/A

**Testing**: xUnit v3 unit tests of the parsing (no Docker); the Docker-backed `ShotBoundaryImageTests`, `ReferenceAnalystImageTests`, and the other tests that call the helper on a Linux engine

**Target Platform**: Linux engine socket `/var/run/docker.sock` (the helper's existing target)

**Project Type**: Test-support change in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`

**Performance Goals**: N/A

**Constraints**: Confined to end-to-end test support; callers (`ShotBoundaryAnalystImage.BuildAndPushAsync`, `TestAnalystImage.BuildAndPushAsync`, the Analyst image tests) unchanged; no test weakened or skipped; the error text `The push did not report a manifest digest` plus the push body is kept

**Scale/Scope**: One method (`PushAsync`), one new internal parser, one new test file

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / constraint | Assessment |
| ---------------------- | ---------- |
| I. Architecture is the design authority | Pass. Test support only; `docs/architecture/` states nothing the change makes false (see Architecture Updates). |
| II. Source-area ownership | Pass. The change lives in `src/platform/Tests/`; no new `src/` child, no new directory. |
| III. API-first control plane | Pass. No API, contract, or media handling is touched. |
| IV. Evidence over claims | Pass. Unit tests of every stream style plus the Docker-backed tests on a Linux engine; results are development evidence only. |
| V. Focused, minimal changes | Pass. One method and one test file; no new dependency or abstraction beyond the pure parser needed to test without Docker. |
| Environment tooling | Pass. Needs only the .NET 10 SDK and, for the Docker-backed evidence, the Docker engine that `environment-setup` and `environment-verify` already provide; no environment spec is needed. |

Post-design re-check: unchanged, all gates pass. No complexity tracking is needed.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261027-082804-fix-push-digest-status-line/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 output (/speckit-tasks, not created here)
```

`contracts/` is intentionally absent: the change exposes no interface to users or other systems; the push stream is the engine's contract, described in [data-model.md](data-model.md).

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/
├── Support/
│   └── TestAnalystImage.cs          # PushAsync delegates to the new parser PushOutput.ReadDigest
└── PushOutputTests.cs               # new: Docker-free tests of the parser
```

**Structure Decision**: Keep the parser in `Support/TestAnalystImage.cs` (the file FR-006 names) as an `internal static class PushOutput` beside `DockerEngine`, so `PushAsync` keeps its signature and the callers stay untouched. The tests sit in the project's root next to the other test classes.

## Complexity Tracking

No constitution violations.

## Architecture Updates

None. The change is confined to the end-to-end test support of the Analyst evidence; no architecture narrative describes the push-digest handling, and no implementation-status statement in `docs/architecture/` becomes true or false.
