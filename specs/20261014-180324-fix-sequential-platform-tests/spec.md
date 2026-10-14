# Feature Specification: Sequential Platform Test Projects in Environment Verification

**Feature Branch**: `20261014-180324-fix-sequential-platform-tests`

**Created**: 2026-10-14

**Input**: User description: "In `.github/actions/environment-verify/action.yml`, run the platform test projects one after another in phase and finalize mode, so Host, Integration and EndToEnd tests no longer start Docker containers at the same time and fail with 'failed to bind host port ... address already in use'. Change only the environment action folders."

**Fix for**: #72 (implementation of `specs/20261012-141233-manager-work-execution`)

## Problem

The verification of the implementation of `specs/20261012-141233-manager-work-execution` stopped three times in the merge with `platform build and tests failed`, always because Docker refused to publish a host port for a test container (`failed to bind host port ... address already in use`), never because of an assertion of the feature:

- Attempt 1: Integration fixtures failed on Ryuk (`8080/tcp`) and on the Migrator exit code tests (`9000/tcp`).
- Attempt 3: `WorkFailureEndToEndTests` failed on NATS (`4222/tcp`) while Contracts (110/110), Architecture (39/39) and Host (9/9) passed.
- The earlier Phase 9 failure of the same kind and the single-project runs that pass confirm a start-up race, not a defect of one test.

Cause: in phase and finalize mode, `run_tests` of `.github/actions/environment-verify/action.yml` runs `dotnet test "$solution" --no-build` for the whole platform solution, so the Architecture, Contracts, Host, Integration, and EndToEnd test projects run concurrently. Host (AppHost with DCP), Integration (Testcontainers) and EndToEnd start containers on the one Docker daemon of the runner at the same time and race for host ports.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Docker-backed test projects never run at the same time (Priority: P1)

A maintainer or the implementation automation runs the verification of a phase or the merge. Every test project of a solution runs after the previous one has finished, so only one Docker-backed test project uses the daemon at a time, and the verification result depends on the code, not on a port race.

**Why this priority**: The race stops the merge of an otherwise converged implementation and wastes attempts.

**Independent Test**: Run the finalize verification on a solution with several test projects and observe from the log that each project's `dotnet test` starts only after the previous one ended; run it repeatedly on the platform solution with Docker and no port conflict occurs.

**Acceptance Scenarios**:

1. **Given** phase or finalize mode, **When** a solution has several test projects, **Then** each runs in its own `dotnet test <project> --no-build` invocation, one after another in a stable order.
2. **Given** one test project fails, **When** the verification ends, **Then** the remaining projects still ran, the check is reported as failed, and the log shows every project's output.
3. **Given** task mode, **When** tests are selected, **Then** the selection rules (always-run projects, changed test classes, whole project) behave as before.
4. **Given** the Analyst Manager solution, **When** it is verified in phase or finalize mode, **Then** its test projects also run one after another.

### Edge Cases

- A solution without any `*Tests.csproj` runs no tests and does not fail the verification by itself.
- Projects under `bin/` and `obj/` are never picked up as test projects.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: In phase and finalize mode, `environment-verify` MUST run the test projects of a solution one after another, each with its own `dotnet test <project> --no-build` invocation, instead of one solution-wide invocation.
- **FR-002**: The test projects MUST be discovered the same way as in task mode (`*Tests.csproj` below the solution folder, excluding `bin` and `obj`) and run in a stable sorted order.
- **FR-003**: A failing test project MUST NOT prevent the remaining projects from running; the check fails if any project failed.
- **FR-004**: Task mode behavior is unchanged.
- **FR-005**: Only the folders `.github/actions/environment-setup` and `.github/actions/environment-verify` change; no test, threshold, timeout, retry, or product code is changed, weakened, or skipped.

## Success Criteria *(mandatory)*

- **SC-001**: Every `*Tests.csproj` of the platform solution (four on the default branch, five with the EndToEnd project of #72) runs in its own invocation with strictly no overlap, shown by the finalize verification log.
- **SC-002**: The stopped merge verification of #72 passes without `address already in use` failures.
- **SC-003**: A deliberately failing test project does not hide the results of the later projects and fails the check.

## Assumptions

- The smallest fix is a per-project loop in the existing `run_tests` of `environment-verify`, reusing the discovery of task mode. Parallelism within one test project is unchanged; the observed conflicts are between projects.
- Retries or fixed port assignments are not used, because they would hide the race instead of removing it.
- Dependencies: the owner of the verification action is [Environment Verification Coverage](../archive/20261007-115855-environment-verification-coverage/spec.md); the implementation this fix unblocks is #72 (`specs/20261012-141233-manager-work-execution`). This is an environment spec: its implementation changes nothing but the two action folders and is held for a person's review.
