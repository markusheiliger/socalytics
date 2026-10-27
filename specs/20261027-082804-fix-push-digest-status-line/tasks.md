# Tasks: Read the Pushed Manifest Digest from Every Engine's Push Output

**Input**: Design documents from `specs/20261027-082804-fix-push-digest-status-line/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md)

**Tests**: Included. The spec requires Docker-free tests of the parsing (SC-002) and the Docker-backed end-to-end evidence (SC-001).

**Organization**: One user story. The change is confined to the end-to-end test support, so there is no setup or foundational phase: the project, the test framework (xUnit v3, Shouldly), and the file `Support/TestAnalystImage.cs` already exist.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel: consecutive `[P]` tasks under one heading run in parallel, each from the same commit, so they change disjoint files and depend on none of each other
- **[Story]**: Which user story this task belongs to
- Paths are relative to the repository root; `E2E` below means `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`

## Phase 1: User Story 1 - Pushed Analyst images work on every engine (Priority: P1) 🎯 MVP

**Goal**: `DockerEngine.PushAsync` obtains the pushed manifest digest from either the `aux.Digest` record or a status line `<tag>: digest: sha256:<hex> size: <n>`, without changing any caller.

**Independent Test**: `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~PushOutputTests"` passes without Docker; on a Linux engine `ShotBoundaryImageTests` and `ReferenceAnalystImageTests` pass and no test fails with `The push did not report a manifest digest`.

### Parser

- [x] T001 [US1] Add `internal static class PushOutput` with `static string ReadDigest(string body)` beside `DockerEngine` in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/TestAnalystImage.cs`, following the resolution rules of `data-model.md`: split the body on `\n` (skipping empty entries), skip a line that is not valid JSON or not a JSON object (no `JsonException`), throw `InvalidOperationException("Pushing the Test Analyst image failed: " + <raw errorDetail>)` for any line with `errorDetail` (wins over any digest), take a string `aux.Digest` as is (`aux` is used only when it is a JSON object and `Digest` only when it is a JSON string, otherwise that part of the line is ignored without an exception) (wins over a status digest), otherwise take capture group 1 of a string `status` matching the anchored `[GeneratedRegex]` `^\S+: digest: (sha256:[0-9a-f]{64}) size: \d+$` (exactly 64 lowercase hex characters, `sha256:<hex>` form kept), else throw `InvalidOperationException("The push did not report a manifest digest:\n" + body)`.

### Wiring and tests

- [ ] T002 [P] [US1] Make `DockerEngine.PushAsync` in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/TestAnalystImage.cs` keep its signature and request, read the response body, and `return PushOutput.ReadDigest(body)`, replacing the inline line loop (depends on T001, changes only this file; callers `ShotBoundaryAnalystImage.BuildAndPushAsync` and `TestAnalystImage.BuildAndPushAsync` stay untouched).
- [ ] T003 [P] [US1] Add Docker-free xUnit v3 and Shouldly tests of `PushOutput.ReadDigest` in the new `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/PushOutputTests.cs` (depends on T001), covering: status-line style returns `sha256:<hex>`; `aux.Digest` style unchanged; both together return the `aux` digest; two lines carrying an `aux.Digest` return the last one; `errorDetail` stream throws the push-failure error, with and without a digest line; status lines that are not a push digest (layer progress, "Layer already exists", a `sha256:` value inside another property) are ignored; 63 and 65 hex characters and uppercase hex are rejected; a tag containing `.` and `-` is accepted; stray non-JSON and non-object lines neither hide a later digest nor raise `JsonException`; a line with a non-object `aux` or a non-string `Digest` neither throws nor hides a later digest; a stream without a digest throws `The push did not report a manifest digest` followed by the push body.

**Checkpoint**: The parsing tests pass without Docker and the Docker-backed callers need no change.

---

## Phase 2: Polish & Cross-Cutting Concerns

**Purpose**: Verify the change against the success criteria and the scope constraint.

- [ ] T004 Run `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~PushOutputTests"` and fix any failure in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/TestAnalystImage.cs` or `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/PushOutputTests.cs`.
- [ ] T005 Run the Docker-backed evidence on a Linux engine, `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ShotBoundaryImage|FullyQualifiedName~ReferenceAnalystImage"` and then the whole project, and confirm that no end-to-end test fails with `The push did not report a manifest digest` (SC-001); no test is weakened or skipped. The whole-project run must show no failure with `The push did not report a manifest digest` and no newly failing test compared with the base branch; unrelated failures are reported but do not fail the task.
- [ ] T006 Confirm the scope (FR-006, SC-003) with `git diff --name-only` against the base branch: only files of `specs/20261027-082804-fix-push-digest-status-line/`, `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/TestAnalystImage.cs`, and `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/PushOutputTests.cs` changed; no production source, contract, or `docs/architecture/` file changed (the plan's Architecture Updates section is `None`).

---

## Dependencies & Execution Order

- T001 → T002 and T003 (both need `PushOutput.ReadDigest`) → T004 → T005 → T006.
- No user story other than US1 exists. T002 and T003 are the only parallel group: T002 changes `Support/TestAnalystImage.cs`, T003 creates `PushOutputTests.cs`, and neither uses anything the other creates. T001 and T002 change the same file, so T001 runs alone first.

## Implementation Strategy

MVP is the whole feature: T001 to T003 deliver the fix and its Docker-free proof; T004 to T006 give the evidence required by SC-001 to SC-003. SC-004 is post-merge evidence and has no task.
