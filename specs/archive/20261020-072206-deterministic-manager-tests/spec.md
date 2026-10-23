# Feature Specification: Deterministic Analyst Manager Queue Credential and Upstream Stager Tests

**Feature Branch**: `20261020-072206-deterministic-manager-tests`

**Created**: 2026-10-20

**Input**: User description: "Make two flaky Analyst Manager tests deterministic without weakening them: `QueueCredentialProviderTests.Requests_a_credential_with_an_ephemeral_public_key_and_renews_at_two_thirds` asserts the request body does not contain \"SU\", which random base32 key text contains in about 5 percent of runs; `UpstreamStagerTests.Pages_are_staged_verified_and_the_analyst_launches_with_the_inputs_mount` reads `_runtime.Attempts.Single()` before the simulated attempt is registered."

**Fix for**: #195 (implementation of `specs/20261018-110526-agents-python-environment`, twin #174)

## Problem

The merge verification of #195 failed in the Analyst Manager tests (2 of 679 failed) although that implementation changes no product code.

1. `QueueCredentialProviderTests.Requests_a_credential_with_an_ephemeral_public_key_and_renews_at_two_thirds` asserts `body.ToJsonString().ShouldNotContain("SU")` to show that no NATS user seed (which starts with `SU`) is sent. The request body contains the random ephemeral user public key (56 base32 characters), which contains the pair `SU` by chance in about 5 percent of runs (observed: `UBVFUIXUESDWJGVF7MUKWVT5Z7T4DP6T64HLPZFCZSUOUVQDA6DI3HZZ`).
2. `UpstreamStagerTests.Pages_are_staged_verified_and_the_analyst_launches_with_the_inputs_mount` failed with "Sequence contains no elements" at `_runtime.Attempts.Single()` (line 271, 7 ms into the test): `RunAsync` returns when the attempt run ends, and the test then reads the simulated runtime's attempt list before an attempt is visible there. The same unsynchronized read is used by the other tests of the file (`_runtime.Attempts.Single()` and `_runtime.Attempts.Count` assertions).

Both tests belong to merged features ([Manager Work Execution](../20261012-141233-manager-work-execution/spec.md) and [Partial-Coverage Barriers](../20261017-212004-partial-coverage-barriers/spec.md)). Earlier repair attempts of #195 failed in other unrelated tests, so plain retries do not converge while chance-dependent tests stay in the Manager verification.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Manager tests pass independent of random text and timing (Priority: P1)

A maintainer or the verification environment runs the Analyst Manager tests. The two tests give the same result on every run: the credential test does not depend on the characters of the random key, and the stager tests observe the launched attempt only once it is registered.

**Why this priority**: The chance failures block verification and merge of every implementation, including #195.

**Independent Test**: Run `dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build --filter "FullyQualifiedName~QueueCredentialProviderTests|FullyQualifiedName~UpstreamStagerTests"` repeatedly (for example 20 times); every run passes.

**Acceptance Scenarios**:

1. **Given** a request body whose `userPublicKey` contains `SU`, **When** the credential test asserts, **Then** it passes.
2. **Given** a request body that contains a seed field or a value that is not a well-formed user public key, **When** the credential test asserts, **Then** it fails (the seed-never-sent intent stays checked).
3. **Given** the runner has finished an attempt, **When** a stager test reads the simulated attempts, **Then** it observes the attempt that was launched, on every run.

### Edge Cases

- A stager test that expects no attempt (`Attempts.Count` is 0 after a failed staging) must still prove that no attempt was started, so it is not changed to wait for an attempt.
- The wait for an attempt is bounded, so a missing attempt fails the test with a clear timeout instead of hanging.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `QueueCredentialProviderTests.Requests_a_credential_with_an_ephemeral_public_key_and_renews_at_two_thirds` MUST NOT assert on random text. It MUST assert that the `userPublicKey` value is a well-formed NATS user public key (starts with `U`, 56 characters, only characters of the base32 alphabet `A-Z` and `2-7`) and that the request body has exactly the properties `userPublicKey` and `capabilities` (no seed or other secret field).
- **FR-002**: The tests of `UpstreamStagerTests` that read an attempt from `_runtime.Attempts` after `RunAsync` MUST first wait, bounded by a timeout, until the attempt is registered, through one shared helper, so the read no longer races the runner.
- **FR-003**: No other file changes outside this spec folder and `specs/README.md`; no test is skipped, removed, or loosened, no assertion of the two test files is dropped, and no product code changes.

### Key Entities

- **User public key**: the ephemeral NATS NKey public key the Manager sends to request a queue credential; a 56-character base32 string starting with `U`.
- **Simulated attempt**: the in-memory attempt that the simulated runtime adapter registers when the runner launches an Analyst.

## Success Criteria *(mandatory)*

- **SC-001**: The two named tests and every other test of the two files pass in 20 consecutive runs of the filtered command above.
- **SC-002**: `dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build` passes.
- **SC-003**: The diff outside this spec folder and `specs/README.md` touches only `QueueCredentialProviderTests.cs` and `UpstreamStagerTests.cs` under `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/`, and removes no assertion (FR-003).

## Assumptions

- A NATS user public key is 56 characters of base32 starting with `U` (the NKey prefix); the test's expected length and alphabet follow that format.
- The request body of the queue credential request has only the properties `userPublicKey` and `capabilities` (`QueueCredentialsRequest` in `PlatformClient`); a test that lists them exactly also fails if a seed field is added.
- Dependencies: the code under test belongs to [Manager Work Execution](../20261012-141233-manager-work-execution/spec.md) and [Partial-Coverage Barriers](../20261017-212004-partial-coverage-barriers/spec.md). The implementation this fix unblocks is #195 ([Agents Python Verification Environment](../../20261018-110526-agents-python-environment/spec.md), `specs/20261018-110526-agents-python-environment`), which depends on this spec and waits for it.
