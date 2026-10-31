# Tasks: Docker Push Digest from the Status Line

**Input**: Design documents from `/specs/archive/20261027-082724-docker-push-digest-status-line/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md (no `contracts/`: the feature adds no external interface)

**Tests**: Requested by FR-006 and SC-001 to SC-003: deterministic Docker-free parsing tests, plus the Docker-backed image tests on a Linux engine.

**Organization**: One user story (US1, P1). No foundational phase is needed: the change is one helper, one call site, and one test class in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/`. No `docs/architecture/` task is required (plan.md, Architecture Updates: None).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story the task belongs to

## Phase 1: User Story 1 - Image-pushing End-to-End tests pass on either engine (Priority: P1)

**Goal**: `DockerEngine.PushAsync` returns the pushed manifest digest from an `aux.Digest` entry, from the status line `<tag>: digest: sha256:<64 hex> size: <n>`, or from both (`aux` wins), and still fails clearly on an `errorDetail` entry or when no well-formed digest is reported.

**Independent Test**: `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~PushDigestReaderTests"` passes without Docker in under 5 seconds; `--filter "FullyQualifiedName~ReferenceAnalystImage|FullyQualifiedName~ShotBoundaryImage"` passes on a Linux engine.

### Implementation

- [x] T002 [US1] First restore and build the project (`dotnet restore` then `dotnet build --no-restore` in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`) and read the current `DockerEngine.PushAsync` (reading only `aux.Digest`). Then, in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/TestAnalystImage.cs`, add `internal static class PushDigestReader` with `string Read(string body)` beside `DockerEngine`, and make `PushAsync` keep the HTTP call and return `PushDigestReader.Read(body)`. Parse each non-empty line as JSON: an `errorDetail` property throws `InvalidOperationException("Pushing the Test Analyst image failed: " + error.GetRawText())` immediately wherever it appears; an `aux.Digest` string sets the `aux` digest (last wins); a `status` property that is a string matching the compiled anchored `Regex` `^[A-Za-z0-9_][A-Za-z0-9_.-]*: digest: (sha256:[0-9a-f]{64}) size: [0-9]+\z` sets the status digest (last wins), while an absent or non-string `status` is ignored and the tag is not compared with the pushed one (the pattern ends with `\z`, so a trailing newline or space after the size is rejected). A line that is not JSON throws `JsonException`, and a JSON line that is not an object, an `aux` that is not an object, or an `aux.Digest` that is not a string (including JSON null) throws `InvalidOperationException`; a null never overwrites an earlier `aux` digest. Return the `aux` digest when present, otherwise the status digest, otherwise throw `InvalidOperationException("The push did not report a manifest digest:\n" + body)`. No retries, sleeps, or timeout changes
- [x] T003 [US1] Create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/PushDigestReaderTests.cs` (xUnit v3, Shouldly, Docker-free, same assembly so no `InternalsVisibleTo`) with tests that each assert on the returned digest or the thrown exception: aux only; status line only; both (aux wins); neither (exception message contains the engine output); `errorDetail` before and after a digest, with the exception message containing the `errorDetail` text; malformed status digests (63 and 65 hex characters, upper-case hex, `sha512:`, extra text before or after, a trailing newline or space after the size, non-hex characters) each failing as reporting no digest; unrelated progress lines (`Preparing`, `Pushed`, layer progress) not mistaken for a digest; a non-string `status` ignored; a non-JSON line throwing `JsonException`; a non-object JSON line, a non-object `aux`, a non-string `aux.Digest`, and a null `aux.Digest` (also after a valid `aux` digest, which it must not overwrite) throwing `InvalidOperationException`; a different tag still accepted; last matching status line wins

**Checkpoint**: US1 is complete and independently verified.

---

## Dependencies & Execution Order

- T001 and T004 to T007 were removed because a task that only runs commands or reads files changes nothing and cannot land (the land job rejects an empty change). The restore, build, and read steps are the start of T002; the Docker-free test run (SC-001), the Linux-engine image tests (SC-002, FR-008), and the scope check (SC-004) are covered by the phase verification, the convergence, and the final merge verification. The regression probes (SC-003) run no automated task: they are satisfied by the assertion design of T003 (each parsing test asserts the exact digest and the refusal reasons, so ignoring the status line or accepting a 63-character digest fails at least one test), and the convergence may confirm that design by inspection. T002 → T003; T003 depends on the helper of T002.
- US1 is the only story and the MVP.

## Parallel Opportunities

None worth the conflict risk: the change is two files and T003 needs the helper of T002.

## Implementation Strategy

MVP is US1 in full: extract the parser and test it without Docker; the verification and the convergence run the tests and the Linux-engine image tests. SC-005 is post-merge evidence observed when `specs/archive/20261025-003459-deterministic-agent-workload-test` is implemented and has no task here.
