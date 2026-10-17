# Tasks: Analyst and Media Verification Environment

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), [environment interface](contracts/environment-interface.md), and [quickstart.md](quickstart.md).

**Tests**: Required by FR-013 and the validation guide. Each implementation task includes its directly related tests so a task never lands deliberately failing tests.

**Organization**: Shared tooling foundations first, followed by one phase per user story, then environment-local documentation and final acceptance.

## Format And Boundaries

- Tasks use `- [ ] Tnnn [P?] [USn?] Description`.
- Consecutive unchecked `[P]` tasks under one heading run in parallel, each from the same commit; their predicted file changes must be disjoint and neither may depend on the other.
- Implementation changes are limited to `.github/actions/environment-setup/` and `.github/actions/environment-verify/`, plus this feature's own task markers.
- Do not create a product project, schema, source directory, workflow, root documentation change, or new cache infrastructure.
- Do not tick another task, infer production readiness, or weaken an existing check to make this feature pass.
- The plan's Architecture Updates outcome is `None`; no architecture-file change is required or permitted by this environment implementation.

## Phase 1: Setup

**Purpose**: Establish the exact environment-local toolchain declaration without creating a future product.

- [ ] T001 Create `.github/actions/environment-setup/analyst-media-toolchain.json` as the sole implementation source of the exact CPython, uv, and retained monthly LGPL FFmpeg/ffprobe pins from `research.md`, including complete archive URLs, committed SHA-256 values, expected executable layouts, version/build identity, supported Linux x64 host, and license/provenance locations; validate that it is valid JSON and introduces no floating selector or dependency-cache change.

## Phase 2: Foundational

**Purpose**: Supply the shared verified interpreter and tools before project verification is wired. The smoke-check assurance remains in US2.

- [ ] T002 Implement verified installation in `.github/actions/environment-setup/install-analyst-media.sh` with passing fixtures in `.github/actions/environment-setup/toolchain.test.mjs`: read the toolchain declaration, reject unsupported hosts, download into an isolated action-owned runner directory, validate every committed digest before safe extraction, reject path traversal and unexpected layouts, retain notices, assert exact executable versions, and fail explicitly with the tool and stage. Test successful installation and each rejection without downloading real archives in unit fixtures.
- [ ] T003 Integrate the installer into `.github/actions/environment-setup/action.yml` and extend `.github/actions/environment-setup/toolchain.test.mjs` to prove that `install-tools: false` performs no new executable download, successful setup publishes the verified tool paths and `SOCALYTICS_PYTHON` without new outputs, future project files are never consulted, and existing inputs, outputs, cache behavior, and tool setup are preserved. Exercise the real installation on the supported Linux runner before completing this task.

**Checkpoint**: A fresh supported environment has the exact toolchain, while absent products and tools-disabled mode remain supported.

## Phase 3: User Story 1 - Verify Analyst changes without a manual coverage exception (Priority: P1)

**Goal**: Check real existing Python project ownership, affected dependency consumers, locked package builds, and nonempty test suites.

**Independent Test**: Use isolated SDK and capability fixtures to prove project discovery, selective verification, locked command execution, and failed dependency/build/test handling, without adding any product project.

- [ ] T004 Implement recognized project discovery and validation in `.github/actions/environment-verify/analyst-projects.py` with passing tests in `.github/actions/environment-verify/analyst-projects.test.mjs`: recognize only the SDK and immediate capability manifest roots from the interface contract; normalize unique package identities; resolve declared local path dependencies; enforce "paths stay inside the repository and permitted ownership roots"; reject malformed manifests, unsupported workspace/layout declarations, missing local projects, duplicate identities, and dependency cycles; exclude the Manager, dependency environments, and generated output.
- [ ] T005 Add mode-aware selection, deterministic dependency ordering, and source ownership to `.github/actions/environment-verify/analyst-projects.py` and its `.github/actions/environment-verify/analyst-projects.test.mjs` tests: own-source changes select the project and reverse dependency closure, analysis/common contracts and environment changes select every existing project, finalize selects all, and each selected root appears once. Test direct/transitive consumers, all three modes, unrelated changes, absent/deleted manifests, and "path-component boundaries, not prefix similarity"; retain uncovered status for unowned source and platform ownership of contracts.
- [ ] T006 Integrate selected Python checks into `.github/actions/environment-verify/action.yml` with execution fixtures in `.github/actions/environment-verify/analyst-projects.test.mjs`: use the installed interpreter with downloads disabled, execute both locked sync stages exactly as specified in `contracts/environment-interface.md`, build the wheel without isolation, and run pytest without synchronization. Prove order and failure propagation for missing/stale locks, incompatible Python, missing external wheels or locked build backends, failed builds, missing pytest, failing tests, and no tests; include a real isolated package fixture that passes all stages without mutating declarations.

**Checkpoint**: The SDK/capability verification path works and fails honestly against isolated fixtures; no actual SDK or capability is claimed to exist.

## Phase 4: User Story 2 - Start future Analyst and media work with reproducible tools (Priority: P2)

**Goal**: Demonstrate that the installed media tools really work, independent of future products and external recordings.

**Independent Test**: Run setup without a Python product or Segment Service, produce and extract synthetic media, verify a nonempty video stream and positive finite duration, and decode the extracted output.

- [ ] T007 Add the synthetic media smoke check to `.github/actions/environment-setup/install-analyst-media.sh` with passing tests in `.github/actions/environment-setup/toolchain.test.mjs`: create the planned two-second 64 by 64 native-MPEG-4 MP4, extract a nonempty keyframe-compatible interval, probe the video stream and finite positive duration, and decode it successfully using only the verified tools. Bound output, require no external media or executable download, and delete only the check's own temporary fixture directory.
- [ ] T008 Complete real-tool and smoke-failure evidence in `.github/actions/environment-setup/toolchain.test.mjs` and `.github/actions/environment-setup/action.yml` as needed: verify that failed extraction, absent/invalid probe evidence, empty output, and failed decode cannot pass; preserve the tools-disabled gate; and exercise the installed CPython, uv, FFmpeg, and ffprobe identities and successful smoke check on the supported Linux runner. Keep the mutable FFmpeg tag distinguished from its committed archive-hash integrity boundary and do not claim codec, model, or production support.

**Checkpoint**: Successful setup proves the installed tools, not merely the presence of a download URL or executable filename.

## Phase 5: User Story 3 - Keep coverage and recovery guidance honest (Priority: P3)

**Goal**: Give maintainers reliable outputs and one concrete corrective action for every new failure without changing existing verification ownership.

**Independent Test**: Exercise project deletion/unowned source and every injected failure through the real helper/action path; inspect failed status, responsible stage, recommendation, and preserved outputs.

- [ ] T009 Complete consistent failure guidance in `.github/actions/environment-setup/install-analyst-media.sh`, `.github/actions/environment-verify/analyst-projects.py`, and `.github/actions/environment-verify/action.yml`, updating their environment-local test files: name the tool or project, stage, and exactly one concrete recommended action for each installation, integrity, version, project-convention, compatibility, locked-sync, package, test, and smoke failure. Distinguish environment repair from consuming-project correction; never advise bypassing integrity, unlocking verification, weakening tests, or merging without evidence.
- [ ] T010 Add cross-check regression fixtures to `.github/actions/environment-verify/analyst-projects.test.mjs` and make only necessary corrections in `.github/actions/environment-verify/action.yml`: preserve existing platform/Manager/Web UI/Markdown selection, sequential platform tests, fail propagation, caller inputs/outputs, and contract coverage; prove absent/deleted/unowned project paths stay uncovered and new failures still write the established log, checks, and uncovered outputs. Stub external commands in action-execution fixtures rather than connecting to databases or recursively executing the self-test runner.
- [ ] T011 Wire the two environment-local Node test files into `.github/actions/environment-verify/action.yml` for changes to either environment action folder and in finalize mode, including when no Python project exists; extend `.github/actions/environment-verify/analyst-projects.test.mjs` to prove trigger selection, failure propagation, and nonrecursive fixture execution. Use existing caller-provided Node and retain the read-only verification boundary without adding workflow inputs or modifying workflows.

**Checkpoint**: New checks distinguish checked, failed, and uncovered work and provide actionable recovery, including when no downstream product exists.

## Phase 6: Polish And Cross-Cutting Acceptance

### Parallel Environment Documentation

- [ ] T012 [P] Document the implemented pinned distributions, artifact integrity and retention limits, notices, Linux x64 scope, tools-disabled behavior, synthetic smoke check, and one-action recovery guidance in `.github/actions/environment-setup/README.md`; describe only executable environment behavior and explicitly exclude product and production approval.
- [ ] T013 [P] Document the implemented independent project layout, local dependencies, locked backend and external-wheel expectations, exact command stages, mode/contract/environment triggers, coverage boundaries, self-tests, and corrective actions in `.github/actions/environment-verify/README.md`; do not advertise product commands for an SDK that does not exist.

### Final Acceptance

- [ ] T014 Execute the complete `quickstart.md` matrix against the implementation in `.github/actions/environment-setup/` and `.github/actions/environment-verify/`: run both environment-local test files, real setup and synthetic smoke, real passing/stale-lock/failing-test package fixtures, existing environment-feature self-verification, and Markdown validation. Correct missing evidence only inside those two folders; preserve committed declarations, output contracts, and all existing assertions. Confirm that environment changes still require a person's review before merge and that no product or production claim was introduced.

## Dependencies And Execution Order

```text
T001 -> T002 -> T003
                 |
                 +-> T004 -> T005 -> T006       US1
                 |
                 +-> T007 -> T008               US2

US1 and US2 complete -> T009 -> T010 -> T011     US3
US3 complete -> T012 and T013 -> T014
```

The shared verified toolchain is a prerequisite, not an already completed product story. US1 and US2 are independently testable once the foundation exists, but the automation executes their phases in priority order. They are not parallel task groups because their tests and shared action integration are refined serially. US3 verifies their combined reporting and regression behavior.

The implementation chain must merge all phases before downstream features rely on this environment. All tasks remain unchecked until their named implementation workflow completes them.

## Parallel Execution Examples

- **US1**: None. Discovery, dependency selection, and action integration share `.github/actions/environment-verify/analyst-projects.test.mjs` and must remain serialized.
- **US2**: None. The installer, setup action, and toolchain tests are shared.
- **US3**: None. Reporting, regression checks, and self-test wiring share the action/test surfaces.
- **Polish**: T012 and T013 may run together because they change different README files. T014 waits for both.

## Implementation Strategy

1. Establish the verified toolchain and preserve the absent-product baseline.
2. Deliver US1 as the first independently demonstrable increment using isolated project fixtures.
3. Prove actual synthetic-media processing in US2 without introducing the Segment Service.
4. Complete recovery guidance and existing-check regression coverage in US3.
5. Document the implemented environment, pass all final gates, and present the environment change for human review before merge.

The technical MVP is the shared foundation plus US1. It is not permission to merge an incomplete environment feature, request downstream implementation, or claim the later products work.
