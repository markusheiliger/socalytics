---

description: "Task list for Environment Verification Coverage"
---

# Tasks: Environment Verification Coverage

**Input**: Design documents from `specs/20261007-115855-environment-verification-coverage/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/verify-coverage.md](contracts/verify-coverage.md), [quickstart.md](quickstart.md)

**Tests**: This is an environment feature. Per FR-007 and the constitution's environment-feature rule, tasks change ONLY `.github/actions/environment-setup/action.yml` and `.github/actions/environment-verify/action.yml` (plus ticking this file). No test, README, docs, contract, or source files are added; coverage is proven by the merge workflow's self-test of the branch's own actions and by the [quickstart.md](quickstart.md) scenarios.

**Organization**: Tasks are grouped by user story. Every task must leave both composite actions valid YAML with unchanged inputs (`workspace`, `mode`, `changed-files`, `log-file`) and outputs (`checks`, `uncovered`), and must keep today's behavior for platform and Markdown changes (FR-008): check names `platform build and tests` and `Markdown check`, their order, and the uncovered reporting stay as they are.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- Verify action: `.github/actions/environment-verify/action.yml` (composite action; one Bash `run` step with id `verify`)
- Setup action: `.github/actions/environment-setup/action.yml` (composite action; steps `Set up .NET` and `Install the Markdown linters`)
- Platform solution: `src/platform/SocAlytics.Platform.slnx` (exists); platform SDK pin: `src/platform/global.json`
- Contracts folder: `contracts/` at the repository root (created later by Recording Lineage and Upload)
- Analyst Manager solution: `src/analysts/manager/SocAlytics.Analysts.Manager.slnx` with its own `src/analysts/manager/global.json` (created later by Analyst Manager Registration)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

No setup tasks: both composite actions already exist and this feature adds no project, tool configuration, or folder.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Behavior-preserving preparation of the verify script that the contract and Manager coverage build on

- [ ] T001 In `.github/actions/environment-verify/action.yml`, inside the `run` script of the `Run the checks` step, introduce a variable `PLATFORM_SCOPE='^src/platform/'` directly after `PLATFORM_SOLUTION=src/platform/SocAlytics.Platform.slnx`, and use it in both places that currently hard-code `'^src/platform/'`: the conditional `COVERED+=(...)` line (`[ -f "$WORKSPACE/$PLATFORM_SOLUTION" ] && COVERED+=("$PLATFORM_SCOPE")`) and the platform check condition (`applies "$PLATFORM_SCOPE"`). This is a pure refactor with no behavior change (FR-008, plan.md § Design step 1): the check name `platform build and tests`, its restore/build/test commands, the `status` gate, the Markdown check, and the `checks`/`uncovered` outputs stay byte-for-byte equivalent in effect. Do not edit any other file.

**Checkpoint**: Verify action behaves exactly as before; the platform scope is defined in one place.

---

## Phase 3: User Story 1 - Contract changes are verified automatically (Priority: P1) 🎯 MVP

**Goal**: Changes under the repository-root `contracts/` folder are covered by, and trigger, the existing platform restore, build, and test check while the platform solution exists, and run that check only once when platform and contract files change together.

**Independent Test**: Per [quickstart.md](quickstart.md) scenarios 1–3: `MODE=finalize` on the current tree still yields `checks=platform build and tests,Markdown check` and exit 0; with only `contracts/README.md` and a sample schema in `CHANGED_FILES` and `MODE=task`, the platform check runs once and `uncovered` is empty; with `src/platform/SocAlytics.Platform.slnx` deleted in a scratch copy, no platform check runs and the contract files appear in `uncovered`.

### Implementation for User Story 1

- [ ] T002 [US1] In `.github/actions/environment-verify/action.yml`, change the value of `PLATFORM_SCOPE` (introduced by T001) from `'^src/platform/'` to `'^(src/platform|contracts)/'`, so that `contracts/` joins `COVERED` only while `src/platform/SocAlytics.Platform.slnx` exists and a contract change (or `mode: finalize`) runs the single existing `platform build and tests` check (FR-001, FR-002; research.md § R1, § R2; data-model.md § Verification scopes row `^(src/platform\|contracts)/`; contracts/verify-coverage.md rows 1–3). Because one scope drives one check, a change touching both areas runs the platform check once. Update the comment above `COVERED=(` so it states that the platform scope also covers the repository-root `contracts/` folder, whose schemas the platform contract tests validate, and that it is covered only while the platform solution exists (FR-009). Keep the check name, commands, order, and outputs unchanged (FR-008). Do not edit any other file.

**Checkpoint**: Contract changes are verified by the platform check, or reported as uncovered when the platform solution is missing.

---

## Phase 4: User Story 2 - Analyst Manager changes are verified automatically (Priority: P2)

**Goal**: A separate `Analyst Manager build and tests` check restores, builds, and tests `src/analysts/manager/SocAlytics.Analysts.Manager.slnx` when a Manager file changed or in finalize mode, is skipped while that solution does not exist, and fails fast when the Manager's SDK pin differs from the platform's.

**Independent Test**: Per [quickstart.md](quickstart.md) scenarios 4–8: without the Manager solution, `MODE=finalize` shows no Manager check and exits 0; with a minimal Manager solution (one test project, `global.json` identical to the platform's) and a Manager file in `CHANGED_FILES`, `checks` contains `Analyst Manager build and tests`, `uncovered` is empty, exit 0; a failing test gives exit 1 with the Manager check last; a different Manager SDK version gives exit 1 with a message naming both versions; a non-Markdown file under `src/analysts/` outside `manager/` appears in `uncovered`.

### Implementation for User Story 2

- [ ] T003 [US2] In `.github/actions/environment-verify/action.yml`, inside the `run` script: (1) add `MANAGER_SOLUTION=src/analysts/manager/SocAlytics.Analysts.Manager.slnx` and `MANAGER_SCOPE='^src/analysts/manager/'` after the platform variables; (2) after the existing conditional platform `COVERED+=` line, add `[ -f "$WORKSPACE/$MANAGER_SOLUTION" ] && COVERED+=("$MANAGER_SCOPE")` so Manager paths count as covered only while the Manager solution exists (FR-003; data-model.md § Verification scopes); (3) add a new check block AFTER the platform check block and BEFORE the Markdown check block: `if [ "$status" -eq 0 ] && applies "$MANAGER_SCOPE" && [ -f "$MANAGER_SOLUTION" ]; then checks+=("Analyst Manager build and tests"); ...; fi`, which runs, with the working directory `src/analysts/manager` (use `pushd`/`popd` so that directory's `global.json` selects the SDK, and return to `$WORKSPACE` afterwards even on failure), `step dotnet restore SocAlytics.Analysts.Manager.slnx`, `step dotnet build SocAlytics.Analysts.Manager.slnx --no-restore`, and `step dotnet test SocAlytics.Analysts.Manager.slnx --no-build`, chained with `&&` and setting `status=1` on any failure (FR-004; research.md § R3; data-model.md § Checks order 2; contracts/verify-coverage.md rows 4, 5, 7, 8, 9). While the solution is missing the block is skipped without failing, in every mode (FR-005). Paths under `src/analysts/` outside `manager/` must stay uncovered. Add a short comment above the block stating that it is active only once the Manager solution exists. Keep the platform and Markdown checks, their names, and the order platform → Manager → Markdown unchanged (FR-008). Do not edit any other file.
- [ ] T004 [US2] In `.github/actions/environment-verify/action.yml`, inside the `Analyst Manager build and tests` block added by T003 and before `dotnet restore`, compare the SDK pins (FR-006; plan.md § Design step 4; research.md § R4; Risk ENV-R2; contracts/verify-coverage.md row 6): read `sdk.version` from `src/platform/global.json` and from `src/analysts/manager/global.json` with Node.js (provided by the calling workflow), for example `node -p "require(process.argv[1]).sdk.version" "$PWD/src/platform/global.json"` using absolute paths; when the Manager `global.json` is missing or unreadable, or the two versions differ, write a clear message naming both values (for example `Analyst Manager SDK pin <manager> in src/analysts/manager/global.json differs from the platform pin <platform> in src/platform/global.json`) to the log via `tee -a "$LOG_FILE"`, set `status=1`, and skip restore, build, and test, leaving `Analyst Manager build and tests` as the last entry of `checks`. When the pins match, run restore, build, and test exactly as in T003. Do not add a second SDK install. Do not edit any other file.

**Checkpoint**: The Manager check runs, fails, and is skipped exactly as contracts/verify-coverage.md specifies; existing checks are unaffected.

---

## Phase 5: User Story 3 - The environment provides what the Manager build needs (Priority: P3)

**Goal**: The setup action installs the SoftHSM2 software PKCS#11 token the Manager's Linux key-provider tests use and documents that the Manager reuses the platform's pinned SDK, without installing any other tool.

**Independent Test**: Per [quickstart.md](quickstart.md) scenario 9: after `environment-setup` runs on `ubuntu-latest`, `softhsm2-util --version` succeeds and `/usr/lib/softhsm/libsofthsm2.so` exists; the `Set up .NET` step output shows the SDK version pinned by `src/platform/global.json`.

### Implementation for User Story 3

- [ ] T005 [P] [US3] In `.github/actions/environment-setup/action.yml`, add a new composite step after `Install the Markdown linters` named `Install the SoftHSM2 PKCS#11 token` with `shell: bash` and `run: sudo apt-get update && sudo apt-get install --yes softhsm2`, so the Analyst Manager's PKCS#11 key-provider tests find `libsofthsm2.so` and `softhsm2-util` without test-time installation; the step creates no token, because each test run creates its own through `SOFTHSM2_CONF` (FR-006; plan.md § Design `environment-setup`; research.md § R4a; Risk ENV-R3). Extend the action's `description` to state that the Analyst Manager (`src/analysts/manager`) pins the same SDK version as `src/platform/global.json`, so the existing `Set up .NET` step serves both solutions, and that SoftHSM2 serves the Manager's Linux key-provider tests (FR-006, FR-009). Keep the `workspace` input and the existing two steps unchanged. Do not edit any other file.

**Checkpoint**: Setup provides the platform SDK and SoftHSM2; nothing else is installed.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: In-action documentation of the new coverage and final validation (FR-007, FR-009)

- [ ] T006 In `.github/actions/environment-verify/action.yml`, update the action `description` and the `# Extending:` comment block so they describe the current coverage (FR-009): the platform check (`platform build and tests`) covers `src/platform/` and the repository-root `contracts/` folder and is active only while `src/platform/SocAlytics.Platform.slnx` exists; the `Analyst Manager build and tests` check covers `src/analysts/manager/`, is active only while `src/analysts/manager/SocAlytics.Analysts.Manager.slnx` exists, and fails when its `global.json` SDK pin differs from the platform's; other paths under `src/analysts/` stay uncovered. Change only comments and the `description` text; leave the inputs, outputs, and script behavior unchanged. Do not edit any other file.
- [ ] T007 Validate both actions and fix any deviation only in `.github/actions/environment-setup/action.yml` or `.github/actions/environment-verify/action.yml`: parse both files as YAML; run `bash -n` on the extracted `run` script of `.github/actions/environment-verify/action.yml`; then, in Bash on a scratch copy of the repository with `GITHUB_OUTPUT` set to a temporary file and `WORKSPACE`, `MODE`, `CHANGED_FILES`, `LOG_FILE`, and `MARKDOWN_CHECK` set as the action defines, run the [quickstart.md](quickstart.md) scenarios that the runner supports (1–8, and 9 after running the setup commands) and confirm each outcome in [contracts/verify-coverage.md](contracts/verify-coverage.md) (SC-001, SC-002, SC-003). Confirm `git diff --name-only` for this feature lists only the two action files and this `tasks.md` (FR-007, SC-004). Do not record results in `quickstart.md` and do not commit the scratch copy.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No tasks.
- **Foundational (Phase 2)**: T001 introduces `PLATFORM_SCOPE`; it blocks T002 and sets the variable pattern T003 follows.
- **User Story 1 (Phase 3)**: T002 depends on T001.
- **User Story 2 (Phase 4)**: T003 depends on T001 (shares the script layout); T004 depends on T003.
- **User Story 3 (Phase 5)**: T005 changes only the setup action and has no dependency on other tasks.
- **Polish (Phase 6)**: T006 depends on T002–T004 (it describes their behavior); T007 depends on all previous tasks.

### User Story Dependencies

- **User Story 1 (P1)**: Independent after T001.
- **User Story 2 (P2)**: Independent of US1 in behavior; edits the same file, so it runs after US1 in file order.
- **User Story 3 (P3)**: Fully independent (different file).

### Within Each User Story

- Each task leaves both actions valid and existing platform and Markdown behavior unchanged.
- In US2, the check block (T003) exists before the SDK pin comparison is inserted into it (T004).

### Parallel Opportunities

- T005 [P] touches only `.github/actions/environment-setup/action.yml` and can run alongside any verify-action task.
- All other tasks edit `.github/actions/environment-verify/action.yml` and run sequentially.

---

## Parallel Example: User Story 3 alongside User Story 1

```bash
# Different files, no shared state:
Task: "T002 [US1] Extend PLATFORM_SCOPE to ^(src/platform|contracts)/ in .github/actions/environment-verify/action.yml"
Task: "T005 [P] [US3] Install softhsm2 in .github/actions/environment-setup/action.yml"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete T001 (behavior-preserving refactor).
2. Complete T002 (contract coverage).
3. **STOP and VALIDATE**: quickstart scenarios 1–3.

### Incremental Delivery

1. T001 → baseline unchanged.
2. T002 → contract changes verified (MVP, unblocks Recording Lineage and Upload).
3. T003–T004 → Manager check with SDK pin guard (unblocks Analyst Manager Registration).
4. T005 → SoftHSM2 available for Manager key-provider tests.
5. T006–T007 → in-action documentation and full validation; the merge workflow then self-tests the branch's actions and holds the pull request for human review.

---

## Notes

- Only `.github/actions/environment-setup/action.yml`, `.github/actions/environment-verify/action.yml`, and this `tasks.md` may change (FR-007).
- The repository README and other documentation are updated by the dependent features when coverage becomes active (research.md § R6).
- The `[P]` marker means different files and no dependencies; the `[Story]` label maps a task to its user story.
