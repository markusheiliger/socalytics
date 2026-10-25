# Tasks: Resilient .NET Environment Setup

**Input**: Design documents from `/specs/20261025-194925-dotnet-setup-resilience/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/dotnet-resolve-interface.md](contracts/dotnet-resolve-interface.md), [quickstart.md](quickstart.md)

**Tests**: Requested by FR-009: offline `node:test` tests of the decision logic, in the style of `.github/actions/environment-setup/toolchain.test.mjs`, with simulated source outcomes and no network access.

**Organization**: Tasks are grouped by user story. This is a standalone environment spec: only `.github/actions/environment-setup/` and the single list entry in `.github/actions/environment-verify/action.yml` change. No product code, `docs/architecture/`, workflow, `.github/scripts/`, `.specify/`, spec folder, or `global.json` file is touched, and `tasks.md` is not edited by the implementation. Most tasks change the same two files (`dotnet-resolve.mjs` and `dotnet-resolve.test.mjs`), so they are sequential; only tasks in different files are marked `[P]`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files are disjoint.
- **[Story]**: The user story a task belongs to (US1, US2, US3).

## Path Conventions

- Helper and tests: `.github/actions/environment-setup/dotnet-resolve.mjs` and `.github/actions/environment-setup/dotnet-resolve.test.mjs`
- Action definition and documentation: `.github/actions/environment-setup/action.yml` and `.github/actions/environment-setup/README.md`
- Verification list entry: `.github/actions/environment-verify/action.yml`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the two new files so later tasks only add to them

### Scaffolding

- [x] T001 Create `.github/actions/environment-setup/dotnet-resolve.mjs` as a Node ES module (built-ins only, no npm dependency) exporting the named limit constants of research R4 (alternative feed: one attempt, 240-second timeout counted over its install-script download and install together, with a 30-second request timeout for the download; a 30-second per-command timeout for the injected command runner that runs `dotnet --version` and `dotnet --list-sdks`, and one shared 120-second deadline for all such commands of one run), the alternative feed URL `https://ci.dot.net/public`, and placeholder exports `parseRequirement`, `satisfies`, `acceptable`, `decide`, `formatFailure`, `formatSummary`, with a `main` that runs only when the file is executed directly (so tests can import it without side effects)
- [x] T002 Create `.github/actions/environment-setup/dotnet-resolve.test.mjs` using `node:test` and `node:assert/strict`, importing the helper exports of T001, with a shared fixture builder for `global.json` contents (version, `rollForward`, `allowPrerelease`) and for simulated source adapters (success, transient failure, persistent failure, unavailable)
- [ ] T003 In `.github/actions/environment-verify/action.yml` add `${{ github.action_path }}/../environment-setup/dotnet-resolve.test.mjs` to the `ENVIRONMENT_TESTS` value so verification runs the new offline tests; change nothing else and remove, skip, or relax no check (FR-012)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The single acceptance rule and the requirement loading that every source and every story depends on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [ ] T004 Implement `parseRequirement(file, json)` in `.github/actions/environment-setup/dotnet-resolve.mjs` per [data-model.md](data-model.md) (SDK requirement): fields `file`, `version`, `rollForward` (absent means `latestPatch`), `allowPrerelease` (absent means `true` only for a prerelease pin); a missing file, unparseable JSON, or missing `sdk.version` throws an error naming `file`
- [ ] T005 Implement `satisfies(version, requirement)` and `acceptable(version, requirements)` in `.github/actions/environment-setup/dotnet-resolve.mjs`: `satisfies` applies the `global.json` rules for `patch`, `feature`, `minor`, `major`, `latestPatch`, `latestFeature`, `latestMinor`, `latestMajor`, and `disable` plus `allowPrerelease` (a prerelease candidate is rejected unless allowed); `acceptable` checks every requirement and returns the first violated requirement file or `null`; no policy is ever loosened (FR-003, FR-004)

**Checkpoint**: The acceptance rule exists; user stories can start

---

## Phase 3: User Story 1 - Setup survives a failing primary download source (Priority: P1) 🎯 MVP

**Goal**: Bounded retries with pauses on the primary source, then a documented fallback (runner toolchain, then alternative official feed), with the primary source always first and no fallback when it works.

**Independent Test**: `node --test .github/actions/environment-setup/dotnet-resolve.test.mjs` simulates success, transient failure, persistent failure, fallback success, and unavailable alternatives offline and checks the decision, the attempts, and the resolved SDK.

### Tests for User Story 1

- [ ] T006 [US1] Add offline `decide` tests to `.github/actions/environment-setup/dotnet-resolve.test.mjs`: primary success on attempt 1 (`fallbackUsed` false, no fallback adapter called); transient failure then success within the bounded attempts; persistent primary failure (all three attempt outcomes `failure`) falling back to the runner toolchain and then to the alternative feed; primary success preferred even when the runner has a satisfying SDK (SC-002); the highest satisfying runner SDK chosen; no more than three primary attempts ever requested (FR-001, FR-008; the pauses are `action.yml` steps asserted in T013, not here)

### Implementation for User Story 1

- [ ] T007 [US1] Implement `decide(sources, requirements, limits)` in `.github/actions/environment-setup/dotnet-resolve.mjs` per [data-model.md](data-model.md) (Decision and state transitions): ordered injected adapters `primary`, `runner`, `alternative`; `primary` always first; each candidate passes `acceptable` before it is selected and a rejection moves on to the next source; the returned Decision lists every attempt in order with `source`, `number`, `outcome` (`success`, `failure`, `cancelled`, `skipped`, `rejected`), `version`, and a credential-free `cause`
- [ ] T008 [US1] Implement the primary adapter in `.github/actions/environment-setup/dotnet-resolve.mjs`: parse `DOTNET_ATTEMPT_OUTCOMES` and `DOTNET_ATTEMPT_VERSIONS` (comma-separated, three entries, empty when none) into setup attempts, where the first `success` with a version is the primary candidate
- [ ] T009 [US1] Implement the runner-toolchain adapter in `.github/actions/environment-setup/dotnet-resolve.mjs`: list SDKs with `dotnet --list-sdks` for the `dotnet` on `PATH`, `DOTNET_ROOT`, and `/usr/share/dotnet` through an injectable command runner that applies the per-command timeout constant and the shared command deadline of T001 (a timed-out command, or one started after the shared deadline is spent, rejects its candidate or root with a cause), and offer the highest version that satisfies every requirement, restricted to SDKs the `dotnet` on `PATH` reports; an SDK found only under another root, that is, one the `dotnet` on `PATH` does not report, is offered only after that root is exported through `GITHUB_PATH` and `DOTNET_ROOT` like the alternative source in T017 and cross-checked with that `dotnet` (a runner SDK the `dotnet` on `PATH` reports needs no export); the command runner is injected so tests need no `dotnet`, and an offline test covers an SDK listed under a second root, and an offline test covers a stale or non-satisfying listed SDK being rejected
- [ ] T010 [US1] Implement the alternative-feed adapter in `.github/actions/environment-setup/dotnet-resolve.mjs` per research R3 and R6: download the official `dotnet-install.sh` from the immutable commit-pinned URL of the `dotnet/install-scripts` repository (`https://raw.githubusercontent.com/dotnet/install-scripts/<commit>/src/dotnet-install.sh`, the commit fixed in T011; never the mutable `https://dot.net/v1/dotnet-install.sh`; the download has a 30-second request timeout that counts in the 240-second alternative budget), verify it against a SHA-256 digest committed in the file (fail the source on mismatch, never run an unverified script), install the latest GA SDK of the pinned band (`--channel` derived from the pinned major.minor and feature band, `--quality ga`, the options `--azure-feed https://ci.dot.net/public` and `--uncached-feed https://ci.dot.net/public` so neither the script's primary origin nor its built-in legacy fallback is used) into `$RUNNER_TEMP/dotnet-fallback` with the `timeout 240` limit, and report the installed version; downloader and process runner are injected for tests, an offline test asserts that a downloaded script with a non-matching digest fails the alternative source with a cause and is never executed, and an offline test asserts the install-script argument vector contains the feed options and no primary-origin URL
- [ ] T011 [US1] Pin the install script in `.github/actions/environment-setup/dotnet-resolve.mjs`: resolve a full commit of the `dotnet/install-scripts` repository (for example with `gh api repos/dotnet/install-scripts/commits/main`), commit the commit-pinned raw script URL used by T010 and the real SHA-256 digest of the script at that commit, starting from the pin resolved on 2026-10-25 in [research.md](research.md) R3 (commit `e5cf1dd2d1540ed05ac84f8eb8c5cdec2807621e`, SHA-256 `082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e`): this task verifies that pin (re-fetch the script at that commit and compare the digest, or move to a newer commit only with a re-computed digest) rather than discovering it, and record the URL, commit, and date in a short comment so the digest is a reviewed pin and a later update is a visible change; also record in that comment how the independence of the alternative feed from the primary origin was checked at implementation time (the command run and its result) for the README of T021; if the download or the check is impossible, stop the task visibly instead of committing a placeholder digest
- [ ] T012 [US1] Implement `formatSummary(decision)` in `.github/actions/environment-setup/dotnet-resolve.mjs`: the `.NET SDK` Markdown table with the columns `Source`, `Fallback used`, `Attempts`, `Resolved SDK`, followed by one line per rejected or failed attempt with its cause (FR-007, SC-005); add a `formatSummary` test to `.github/actions/environment-setup/dotnet-resolve.test.mjs` for primary success and for a fallback result that states the source, `yes`, the attempt count, and the resolved version

### Action wiring for User Story 1

- [ ] T013 [US1] In `.github/actions/environment-setup/action.yml` replace the `Set up .NET` step with the unrolled sequence of [contracts/dotnet-resolve-interface.md](contracts/dotnet-resolve-interface.md): `Set up .NET (attempt 1)` to `(attempt 3)` using `actions/setup-dotnet` pinned to the full commit of its `v5` tag with the comment `# v5` (verify the commit with `gh api repos/actions/setup-dotnet/git/ref/tags/v5` before pinning; research R1 names `26b0ec14cb23fa6904739307f278c14f94c95bf1`), each with `global-json-file: ${{ inputs.workspace }}/src/platform/global.json`, `continue-on-error: true`, `timeout-minutes: 3`, and ids `dotnet1` to `dotnet3`; attempts 2 and 3 run only when the previous attempts did not succeed, with `Pause before .NET attempt 2` (10 s) and `Pause before .NET attempt 3` (30 s) steps under the same condition; also in `action.yml` add the `Resolve the .NET SDK` step with `id: dotnet` and no `if` (so it runs for both `install-tools` values), `shell: bash`, `env` of `DOTNET_WORKSPACE`, `DOTNET_ATTEMPT_OUTCOMES` (the three `steps.dotnetN.outcome` values), and `DOTNET_ATTEMPT_VERSIONS` (the three `steps.dotnetN.outputs.dotnet-version` values), running `node "$GITHUB_ACTION_PATH/dotnet-resolve.mjs" resolve`; leave `Calculate dependency cache keys` and its `CACHE_DOTNET_VERSION: ${{ steps.dotnet.outputs.dotnet-version }}` untouched, and update the header comment that mentions the `Set up .NET` step; and update `.github/actions/environment-setup/toolchain.test.mjs`: change the expected step name list of `keeps every existing executable tool step gated and in place` to the new ordered names (`Set up .NET (attempt 1)`, `Pause before .NET attempt 2`, `Set up .NET (attempt 2)`, `Pause before .NET attempt 3`, `Set up .NET (attempt 3)`, `Resolve the .NET SDK`, then the unchanged later steps), and add assertions for the `actions/setup-dotnet` reference pinned to a full 40-character commit, the step id `dotnet` on `Resolve the .NET SDK` with no `if`, and the unchanged inputs and outputs of `action.yml`, and for the bounds of FR-001 and FR-008: exactly three attempt steps, each with `continue-on-error: true` and `timeout-minutes: 3`; the pauses run `sleep 10` and `sleep 30`; attempts 2 and 3 and their pauses run only when the earlier attempts did not succeed (`steps.dotnetN.outcome` conditions), without weakening any existing assertion (SC-007)

**Checkpoint**: User Story 1 is functional and testable offline: a failing primary source no longer fails setup

---

## Phase 4: User Story 2 - The resolved SDK still satisfies the pinned requirements (Priority: P1)

**Goal**: Every source's candidate satisfies both `src/platform/global.json` and `src/analysts/manager/global.json` (version, roll-forward, prerelease), cross-checked by the real `dotnet` muxer, and the `dotnet-version` output holds the resolved version.

**Independent Test**: The acceptance tests run candidates below the pin, at the pin, in a newer patch band, in a newer feature band, and prerelease against both pinned files and accept only satisfying ones.

### Tests for User Story 2

- [ ] T014 [US2] Add acceptance tests to `.github/actions/environment-setup/dotnet-resolve.test.mjs`: `parseRequirement` defaults and errors naming the file; `satisfies` for candidates below the pin, at the pin, in a newer patch band, in a newer feature band, in a newer minor and major, and prerelease, across every `rollForward` policy and `allowPrerelease` values; `acceptable` returning the violated file for either pin and `null` otherwise; differing pin values (a candidate must satisfy both); changed pin values accepted without logic changes (User Story 2 scenario 3); a rejected candidate never selected and the next source tried (FR-003; SC-003)

### Implementation for User Story 2

- [ ] T015 [US2] Implement requirement loading in the `resolve` command of `.github/actions/environment-setup/dotnet-resolve.mjs`: read `src/platform/global.json` and `src/analysts/manager/global.json` under `DOTNET_WORKSPACE` through `parseRequirement`; when no candidate can satisfy both pins, the resulting failure names both files; never rewrite a pin file
- [ ] T016 [US2] Implement the muxer cross-check in `.github/actions/environment-setup/dotnet-resolve.mjs`: after `acceptable` passes, run `dotnet --version` with the working directory set to each pinned folder (through the injectable command runner with the per-command timeout of T001; for the alternative source and for a runner SDK under another root the runner executes it with the candidate's install directory first on `PATH` and `DOTNET_ROOT` set to it in the child environment, so the muxer cross-checked is the one later steps will use) and require the version it reports to satisfy every requirement through `acceptable` (not to equal the candidate), publish that muxer-reported version as the resolved `dotnet-version` and in the summary, and reject the candidate with a cause naming the folder otherwise; add tests to `.github/actions/environment-setup/dotnet-resolve.test.mjs` for a muxer reporting a higher satisfying patch (accepted, primary stays the source) for a muxer reporting a non-satisfying version (rejected), and for a candidate whose runner-`PATH` muxer reports a non-satisfying version while the fallback-directory muxer reports a satisfying one (accepted) and the reverse (rejected)
- [ ] T017 [US2] Implement the success effects of `resolve` in `.github/actions/environment-setup/dotnet-resolve.mjs`: write `dotnet-version=<version>` to `GITHUB_OUTPUT`, append `formatSummary` to `GITHUB_STEP_SUMMARY`, and for the alternative source also append the install directory to `GITHUB_PATH` and `DOTNET_ROOT=<dir>` to `GITHUB_ENV`, and do the same for a runner SDK that is not the one the `dotnet` on `PATH` reports, and for no other runner SDK; add tests with temporary files for the output, summary, path, and environment contents, including both cases (the alternative source and a runner SDK under another root write `GITHUB_PATH` and `DOTNET_ROOT`; a primary result and a runner SDK the `dotnet` on `PATH` reports write neither) and that no secret or token value is written (FR-005, FR-011)

**Checkpoint**: User Stories 1 and 2 work together; the resolved SDK is validated and reported

---

## Phase 5: User Story 3 - Failure stays visible when no source works (Priority: P2)

**Goal**: One clear failure when no source yields a satisfying SDK, naming each source, its cause, and one recommended action, with no `dotnet-version` output and no false success.

**Independent Test**: Simulating every source failing or yielding non-satisfying SDKs ends in a non-zero exit with the failure block.

### Tests for User Story 3

- [ ] T018 [US3] Add offline failure tests to `.github/actions/environment-setup/dotnet-resolve.test.mjs`: every source failing; every source yielding a non-satisfying SDK; pins that no source offers; unreadable or disagreeing pin files naming both files; the failure block naming the primary attempts, the runner toolchain, and the alternative feed with their causes and exactly one `Recommended action` (download failure: restore access to the .NET download origins and rerun; rejected candidate: correct the pins through a standalone environment change, never by loosening a `global.json` policy); the block is ASCII-only and contains no credentials; no `dotnet-version` written on failure; and a bounded number of attempts and adapter calls in the total-failure case (SC-004, FR-008)

### Implementation for User Story 3

- [ ] T019 [US3] Implement `formatFailure(decision)` in `.github/actions/environment-setup/dotnet-resolve.mjs` per the failure message contract of [contracts/dotnet-resolve-interface.md](contracts/dotnet-resolve-interface.md)
- [ ] T020 [US3] Implement the failure effects of `resolve` in `.github/actions/environment-setup/dotnet-resolve.mjs`: print the failure block to stderr, append it to `GITHUB_STEP_SUMMARY`, write no `dotnet-version`, and exit with code 1 so no later step proceeds on a missing or non-satisfying SDK (FR-006); confirm that `install-tools: "false"` still resolves the SDK because the step has no `if` (FR-010); this task completes the `resolve` command wiring: `main` dispatches `resolve`, which builds the primary, runner, and alternative adapters, calls `decide()`, and dispatches to the success effects of T017 or these failure effects, with a test using injected adapters and temporary `GITHUB_*` files

**Checkpoint**: All three user stories work; failure is explicit

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Documentation, verification coverage, and the final checks

### Documentation and verification coverage

- [ ] T021 Update `.github/actions/environment-setup/README.md` with a `.NET SDK Resolution` section (and corrections to the existing `What Setup Does` and `Failure And Recovery` text where they describe the `Set up .NET` step): the retry limits (3 attempts of 3 minutes, pauses of 10 s and 30 s, a 30-second timeout per `dotnet` command), the fallback order (primary, runner toolchain, alternative feed at `https://ci.dot.net/public` with the digest-verified install script), the acceptance rule for both pinned files and the muxer cross-check, the reporting in the run summary, the bounded total time (about 1 minute for fast-failing responses, about 16 minutes worst case: 3 x 3 min + 10 s + 30 s + 120 s shared deadline for `dotnet` commands + 240 s alternative budget, a development value), the recovery guidance, the explicit residual risks that the alternative feed serving the band cannot be proven offline and that its origin may overlap the one `setup-dotnet`'s install script consults, that the install script digest is a reviewed pin of a commit-pinned URL (a needed update is a standalone environment change), how the independence of the alternative feed from the primary origin was checked at implementation time (from the comment of T011), and that the runner toolchain is the fallback expected to carry the resilience goal while the alternative feed is best effort, and that the limits are development values (FR-014)

### Final validation

- [ ] T022 Run `node --test .github/actions/environment-setup/dotnet-resolve.test.mjs .github/actions/environment-setup/toolchain.test.mjs .github/actions/environment-verify/analyst-projects.test.mjs` and `node --test .github/scripts/dependency-cache.test.mjs`, and fix failures in the changed files (`dotnet-resolve.mjs`, `dotnet-resolve.test.mjs`, `action.yml`, and the updated `toolchain.test.mjs` of T013) without weakening any test (SC-007)
- [ ] T023 Run `node .github/scripts/check-markdown.mjs --files .github/actions/environment-setup/README.md` and fix findings; confirm with `git diff --stat` that only `.github/actions/environment-setup/` and `.github/actions/environment-verify/action.yml` changed and that `src/platform/global.json` and `src/analysts/manager/global.json` are untouched (quickstart steps 2 and 3)

---

## Dependencies & Execution Order

- Phase 1 (T001, T002, then T003) → Phase 2 (T004, T005) → Phase 3 (US1) → Phase 4 (US2) → Phase 5 (US3) → Phase 6.
- US1 uses the Foundational acceptance rule; US2 adds loading, the muxer cross-check, and the output effects to the same helper; US3 adds the failure path. They share `dotnet-resolve.mjs` and `dotnet-resolve.test.mjs`, so the stories run in priority order, not in parallel.
- T013 changes `action.yml` (the attempt steps and the `Resolve the .NET SDK` step) together with its shape test `toolchain.test.mjs`, so the existing test keeps passing when it lands; it depends on the `resolve` command of T015 to T017 and T020 existing before the action can pass on GitHub, but the tasks may land in this order because the offline tests do not run the action.
- T011 depends on T010. T003 follows T002 so the new offline tests run in the verification of every later task. T021 runs after Phase 5. T022 and T023 run last.

## Parallel Opportunities

- None: the remaining tasks share files or depend on earlier ones.

## Implementation Strategy

- **MVP**: Phase 1, Phase 2, and User Story 1 (T001 to T013): a failing primary source no longer stops setup. Because User Story 2 completes loading and reporting of the resolved version, merge only after at least Phase 4 so the `dotnet-version` output and both-pin validation are in place.
- **Incremental delivery**: add User Story 2 (validation, output), then User Story 3 (visible failure), then documentation, verification coverage, and the final checks. The manual GitHub evidence of [quickstart.md](quickstart.md) (step 4) follows the merge and is not part of these tasks.
