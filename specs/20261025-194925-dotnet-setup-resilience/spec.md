# Feature Specification: Resilient .NET Environment Setup

**Feature Branch**: `20261025-194925-dotnet-setup-resilience`

**Created**: 2026-10-25

**Status**: Draft

**Input**: User description: "Environment spec: make the .NET setup of .github/actions/environment-setup resilient to failing .NET downloads. Only the two environment action folders change (environment-setup, and environment-verify only if needed); no product code, tests, spec folders, or workflows. Cause: the first step, 'Set up .NET', has no retry and no fallback, so a persistent HTTP 409 from the primary .NET download origin for Runtime 10.0.14 and SDK 10.0.403 fails the whole work job before any agent runs (three consecutive implementation runs, six attempts over about 30 minutes). Required: bounded retries with a pause that still honour src/platform/global.json, a fallback to an alternative download source or a cached toolchain when the primary keeps failing, a resolved SDK that still satisfies both pinned global.json files, a working 'dotnet-version' output, a visible failure when no source yields a satisfying SDK, unweakened verification, preserved action conventions, and offline tests of the retry and fallback decision logic. Needed by implementation pull request #309 of specs/20261024-070502-deterministic-finalized-run-test (twin #279), which cannot start T001 while setup fails."

## Clarifications

### Session 2026-10-25

- Q: When the runner already has an SDK that satisfies both pinned SDK files, should setup use it before contacting the primary download source, or only after the primary source has failed its bounded attempts? → A: Primary source first; an already available satisfying SDK is used only as a fallback after the primary source fails its bounded attempts.
- Q: Must a fallback source supply the exact SDK version pinned in the platform's global.json, or may it supply any newer SDK that the pinned roll-forward and prerelease policy allows? → A: Any SDK that satisfies both pinned files under their roll-forward and prerelease policy is accepted, and the resolved version is reported.

## User Scenarios & Testing *(mandatory)*

The users are contributors and implementation automation. Their need is that a temporary or persistent failure of one .NET download source does not stop every implementation and verification run before any work starts, without weakening what the verification proves.

### User Story 1 - Setup survives a failing primary download source (Priority: P1)

An implementation or verification run starts its environment setup while the primary .NET download source rejects or fails the requested SDK and runtime downloads. Setup retries within a bounded number of attempts with a pause between them, and, when the primary source keeps failing, obtains a satisfying SDK from an alternative source or from a toolchain that is already available. The run continues to its agent or verification instead of failing in the first setup step.

**Why this priority**: A persistent failure of the primary source currently fails every work job before any agent runs, and plain retries of the whole implementation cannot help. This blocks the stopped implementation of twin #279 and any other implementation that starts meanwhile.

**Independent Test**: Exercise the retry and fallback decision logic offline with simulated source outcomes (success, transient failure, persistent failure, and unavailable alternative) and confirm the decision, the number of attempts, the pauses, and the final resolved SDK, without any network access.

**Acceptance Scenarios**:

1. **Given** the primary source fails transiently and then succeeds, **When** setup runs, **Then** it succeeds within the bounded attempts, pauses between attempts, and every attempt honours the SDK requirement of the platform's pinned SDK file.
2. **Given** the primary source fails persistently on every bounded attempt, **When** setup runs, **Then** it moves on to an alternative source or an available cached toolchain and continues instead of failing.
3. **Given** the primary source works, **When** setup runs, **Then** the primary source is used (even when the runner already has a satisfying SDK), no fallback is attempted, and the run takes no longer than before beyond negligible overhead.
4. **Given** setup succeeded through a fallback, **When** the run summary and logs are read, **Then** they state that the fallback was used, which source supplied the SDK, and the resolved SDK version.

---

### User Story 2 - The resolved SDK still satisfies the pinned requirements (Priority: P1)

Whatever source supplies the SDK, the resolved SDK satisfies the version, roll-forward policy, and prerelease policy of the platform's pinned SDK file and of the Analyst Manager's pinned SDK file, so verification evidence stays equivalent to evidence from the primary source.

**Why this priority**: A fallback that quietly installs a different or looser SDK would weaken the verification this environment exists to protect.

**Independent Test**: Run the decision logic offline against both pinned SDK files with candidate versions below the pin, at the pin, in a newer patch band, in a newer feature band, and prerelease, and confirm only satisfying candidates are accepted.

**Acceptance Scenarios**:

1. **Given** a candidate SDK from any source, **When** setup validates it, **Then** it is accepted only if it satisfies the platform's pinned SDK file (version, roll-forward policy, prerelease policy) and the Analyst Manager's pinned SDK file.
2. **Given** a candidate that does not satisfy a pinned SDK file, **When** setup validates it, **Then** it is rejected, never selected, and the next source is tried.
3. **Given** the Analyst Manager's pinned SDK file and the platform's pinned SDK file, **When** either changes, **Then** the same validation applies to the new values without editing the retry or fallback logic.
4. **Given** setup completed, **When** later steps use the existing `dotnet-version` output for cache keys, **Then** the output holds the resolved SDK version and the cache keys are computed as before.

---

### User Story 3 - Failure stays visible when no source works (Priority: P2)

A maintainer sees a clear, single failure when neither the primary source, the alternative source, nor any available toolchain yields a satisfying SDK, instead of a silently wrong or missing SDK.

**Why this priority**: Resilience must not turn an unavailable environment into a false success.

**Independent Test**: Simulate every source failing or yielding a non-satisfying SDK and confirm setup fails, names each source tried and its cause, and recommends one corrective action.

**Acceptance Scenarios**:

1. **Given** no source yields a satisfying SDK, **When** setup runs, **Then** it fails with a message naming each source tried, the cause of each failure, and one recommended next action.
2. **Given** a failure, **When** the result is read, **Then** it does not claim the environment is usable and no later step proceeds on a missing or non-satisfying SDK.
3. **Given** setup is invoked with `install-tools` set to `false` (the trusted cache population job), **When** it runs, **Then** the semantics of that input are unchanged: the .NET SDK is still resolved because the cache keys need it, and no executable tool installation is added or removed by this change.

---

### Edge Cases

- The primary source fails with a non-transient response (for example a persistent conflict response): the bounded attempts end and the fallback starts; setup does not retry indefinitely.
- The runner already has an SDK that satisfies both pinned files: it is not used before the primary source; it is used only as a fallback after the primary source has failed its bounded attempts, only through the same validation as any other source, and its use is reported.
- A fallback source supplies a newer SDK than the pinned version: it is accepted when it satisfies both pinned files under their roll-forward and prerelease policy, and the resolved version is reported; an exact match of the pinned version is not required.
- A cached toolchain is stale or does not satisfy the pinned files: it is rejected, never used.
- The alternative source offers the SDK but with a corrupted or truncated download: the download fails its integrity check or its version check and counts as a failed source.
- The pinned SDK files change to a version no source offers: setup fails visibly rather than loosening the version.
- Pinned SDK files whose requirements no single SDK can satisfy together: setup fails visibly and names both files.
- Total time of retries, pauses, and fallbacks must stay bounded so a hard outage fails the run in minutes, not tens of minutes.
- Nothing in the setup logs or summary contains credentials, and no secret is needed by any source.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Setup MUST retry the .NET installation a bounded number of times with a pause between attempts, and every attempt MUST honour the platform's pinned SDK file (version, roll-forward policy, prerelease policy).
- **FR-002**: Setup MUST try the primary download source first, even when the runner already has a satisfying SDK. Only when the primary source keeps failing after its bounded attempts MUST setup fall back to an alternative official download source or to an already available cached or installed toolchain (including an SDK already on the runner).
- **FR-003**: Setup MUST accept an SDK from any source only if it satisfies both the platform's pinned SDK file and the Analyst Manager's pinned SDK file under their roll-forward and prerelease policy, and MUST reject and report any other SDK. A fallback SDK need not equal the exact pinned version; any satisfying SDK is accepted and its resolved version reported (FR-005, FR-007).
- **FR-004**: Setup MUST NOT loosen, bypass, or rewrite any pinned SDK file, roll-forward policy, or prerelease policy to make a source succeed.
- **FR-005**: Setup MUST keep the `dotnet-version` output, holding the resolved SDK version, so the existing dependency cache keys keep working unchanged.
- **FR-006**: Setup MUST fail visibly when no source yields a satisfying SDK, naming each source tried, the cause of each failure, and one recommended next action.
- **FR-007**: Setup MUST report in the run summary or log which source supplied the SDK, whether a fallback was used, and how many attempts were needed.
- **FR-008**: The total time spent on retries, pauses, and fallbacks MUST be bounded by documented limits.
- **FR-009**: The retry and fallback decision logic MUST be covered by offline automated tests in the style of the action's existing toolchain tests, using simulated source outcomes and needing no network access, covering success, transient failure, persistent failure, fallback success, rejected non-satisfying SDKs, and total failure.
- **FR-010**: The `install-tools` input MUST keep its semantics; the .NET SDK resolution MUST still run when it is `false`, and no executable tool installation is added or removed by this change.
- **FR-011**: The change MUST keep the action's conventions: pinned versions, third-party actions pinned to a full commit, a cloud-neutral and secret-free setup, and no new credentials or permissions.
- **FR-012**: The verification MUST remain unweakened: no check removed, skipped, or relaxed, and `environment-verify` is changed only if the change strictly needs it.
- **FR-013**: Only the environment setup action folder (its action definition, helper scripts, tests, and README) and, only if needed, the environment verify action folder MAY change; no product code, product tests, spec folders, or workflows are touched.
- **FR-014**: The action's README MUST document the retry limits, the fallback order, the acceptance rule for resolved SDKs, the reporting, and the recovery guidance when no source works.

### Key Entities

- **SDK requirement**: The version, roll-forward policy, and prerelease policy declared in the platform's pinned SDK file and in the Analyst Manager's pinned SDK file; the single acceptance rule for any source.
- **Download source**: The primary source, an alternative official source, or an already available cached or installed toolchain, tried in a documented order with the primary source always first.
- **Setup attempt**: One try of one source, with its outcome, cause on failure, and the pause before the next try.
- **Resolved SDK**: The SDK version that satisfied the requirement, reported through the `dotnet-version` output and the run summary.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: With the primary source failing on every attempt, setup still ends with a satisfying SDK whenever an alternative source or cached toolchain provides one, in 100% of simulated cases.
- **SC-002**: With the primary source working, setup uses it with no fallback in 100% of simulated cases, including cases where the runner already has a satisfying SDK.
- **SC-003**: No SDK that fails either pinned SDK file is ever accepted, in 100% of simulated version cases.
- **SC-004**: When no source yields a satisfying SDK, setup fails in 100% of simulated cases within the documented time bound, with each tried source named and one recommended action.
- **SC-005**: A reader of the run summary can tell, without opening other logs, which source supplied the SDK and whether a fallback was used.
- **SC-006**: The stopped implementation of twin #279 can start its first task after this change is merged, without a manual setup workaround.
- **SC-007**: The offline tests for the retry and fallback logic pass without network access, and the existing environment action tests continue to pass.

## Assumptions

- At least one alternative official .NET download endpoint or an already available toolchain exists for the pinned SDK band; which one is chosen is a planning decision. If none does, failure stays visible (User Story 3).
- Bounded retry counts, pause lengths, and total time limits are development values chosen in planning and documented in the action README; they are not production commitments.
- The primary source's persistent failure for the current SDK patch is an external condition; this spec makes setup resilient to it and does not fix the source.
- The existing `dotnet-version` consumers (the dependency cache key calculation) need only the resolved SDK version string.
- The Analyst Manager's pinned SDK file and the platform's pinned SDK file currently hold the same values and are required to keep doing so by their own conventions; this spec only requires that a resolved SDK satisfy both.
- This is a standalone environment spec: it is reviewed by a person and merged before specs that need it start, and the spec `specs/20261024-070502-deterministic-finalized-run-test` depends on it.
