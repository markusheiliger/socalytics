---

description: "Task list for Deterministic Analyst Manager Queue Credential and Upstream Stager Tests"
---

# Tasks: Deterministic Analyst Manager Queue Credential and Upstream Stager Tests

**Input**: Design documents from `specs/20261020-072206-deterministic-manager-tests/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The tasks change tests; their verification is repeated runs of the changed test classes.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: User Story 1 - Manager tests pass independent of random text and timing (Priority: P1) 🎯 MVP

### Credential test

- [x] T001 [P] [US1] In `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Queue/QueueCredentialProviderTests.cs`, in `Requests_a_credential_with_an_ephemeral_public_key_and_renews_at_two_thirds`, replace `body.ToJsonString().ShouldNotContain("SU")` with assertions that the `userPublicKey` value has length 56, starts with `U`, and matches `^[A-Z2-7]+$`, and that the body's property names are exactly `userPublicKey` and `capabilities` (no seed field). Keep every other assertion. Verify: `dotnet test src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests --filter "FullyQualifiedName~QueueCredentialProviderTests"` passes 20 consecutive times.

### Stager tests

- [x] T002 [P] [US1] In `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/UpstreamStagerTests.cs`, add one private helper that waits, bounded by a 10-second timeout, until `_runtime.Attempts` holds the expected number of attempts and returns them, and use it before every `_runtime.Attempts.Single()` read (`Pages_are_staged_verified_and_the_analyst_launches_with_the_inputs_mount`, `Job_without_upstream_dependencies_has_no_inputs_and_no_read`, `Grant_urls_never_appear_in_the_launch`) and before the positive `Attempts.Count.ShouldBe(1)` assertion in `Storage_unavailability_is_retried_with_backoff_and_then_succeeds`. Leave the `Attempts.Count.ShouldBe(0)` assertions as they are. Keep every other assertion. Verify: `dotnet test src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests --filter "FullyQualifiedName~UpstreamStagerTests"` passes 20 consecutive times.

## Phase 2: Verification

- [x] T003 [US1] Verify the whole Manager solution: `dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore` has 0 warnings and `dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build` passes. Change no file.

## Dependencies & Execution Order

T001 and T002 change different files and can run in parallel; T003 runs after both.
