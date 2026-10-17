# Feature Specification: Analyst and Media Verification Environment

**Feature Branch**: `20261017-163801-analyst-media-environment`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Standalone environment prerequisite for the approved five-spec tranche: extend only the environment setup and verification action folders so contributors and automation can reproducibly build and test the planned Python Analyst SDK and capabilities and verify future media segmentation. Provide pinned compatible Python and uv and selected pinned media extraction and probe tooling independently of future project files. Verify existing Analyst Python projects from their committed lockfiles, build their packages, and run their tests; shared analysis contract changes reverify their consumers. Skip absent projects without claiming their source paths as covered, preserve existing verification behavior, and fail visibly with the project, failing stage, and recommended corrective action. Do not create product projects, schemas, models, media-service implementations, publication workflows, or new dependency-cache infrastructure."

## User Scenarios & Testing *(mandatory)*

The users are contributors and implementation automation. Their immediate need is trustworthy verification before the Analyst SDK and media-processing features start, rather than another product runtime.

### User Story 1 - Verify Analyst changes without a manual coverage exception (Priority: P1)

A contributor changes an existing Python Analyst project or a shared contract it consumes. Verification restores exactly the project's locked dependencies, builds its package, and runs its tests. A broken dependency declaration, package, or test fails the check rather than allowing the change to appear verified.

**Why this priority**: SDK and capability implementations cannot safely proceed through automated implementation without executable checks that cover their source.

**Independent Test**: Exercise verification against isolated fixture projects, without adding a product project. A passing project is checked and covered; dependency, build, and test failures each produce a failed verification with the responsible project and stage.

**Acceptance Scenarios**:

1. **Given** a Python Analyst project with a manifest and matching committed lockfile, **When** its files change in task or phase mode, **Then** its locked dependencies are restored, its package is built, and its tests run, and its owned files are reported as covered.
2. **Given** several existing Python Analyst projects and a change to only one, **When** verification runs in task or phase mode, **Then** only the affected Python project is checked, alongside any independently applicable existing checks.
3. **Given** existing Python Analyst projects, **When** shared analysis contracts or shared common contract definitions change, **Then** all existing Python Analyst projects are checked, and the platform's contract validation continues to own coverage of the contract files.
4. **Given** existing Python Analyst projects, **When** verification runs in finalize mode, **Then** all those projects are checked even if their source did not change.
5. **Given** a missing or inconsistent lockfile, a failed package build, or a failing test, **When** the affected project is verified, **Then** verification fails at the responsible stage and does not silently refresh dependency declarations, skip tests, or replace failure with success.
6. **Given** an Analyst-only change, **When** verification selects its checks, **Then** the existing Manager and Web UI checks are not triggered solely because the changed files share an ancestor source directory.

---

### User Story 2 - Start future Analyst and media work with reproducible tools (Priority: P2)

A contributor or implementation agent starts work in a fresh automated environment before any Python Analyst project or Segment Service exists. Setup supplies the approved, compatible versions of the Analyst language runtime, dependency tool, and media extraction and probe tools without requiring future project files.

**Why this priority**: The later features must not depend on unspecified runner-installed tools, downloads during media tests, or changes to their own verification environment.

**Independent Test**: Run setup with no Python Analyst project or Segment Service present. Confirm every new tool reports the approved version, then exercise extraction, probing, and decoding of a short synthetic fixture without downloading another executable.

**Acceptance Scenarios**:

1. **Given** a fresh supported automated runner and no future product projects, **When** setup completes, **Then** all newly required tools are available at explicitly pinned versions and setup has not read future project manifests to choose those versions.
2. **Given** a tool download that fails, fails its required integrity check, or provides the wrong version, **When** setup validates the installation, **Then** setup fails with the affected tool and a recommended corrective action instead of accepting another preinstalled version.
3. **Given** successful setup, **When** a media-tool smoke check creates, extracts, probes, and decodes a short synthetic media fixture, **Then** each operation succeeds without obtaining another executable or external media.
4. **Given** setup is invoked in its existing tools-disabled mode, **When** it runs, **Then** the new executable installation and media smoke checks respect that mode and do not change the existing trusted cache-population contract.
5. **Given** a future Python project declares an incompatible runtime requirement, **When** verification checks it, **Then** it fails explicitly rather than downloading an alternative runtime or silently selecting a different version.

---

### User Story 3 - Keep coverage and recovery guidance honest (Priority: P3)

A maintainer reviews a verification result before deciding whether a pull request can merge. The result distinguishes checked files from files without an executable check and gives a concrete next step when setup or verification fails.

**Why this priority**: The environment must help get pull requests merged through reliable evidence and useful guidance, not hide gaps behind a successful exit.

**Independent Test**: Run the selection and reporting checks with absent, present, deleted, and unrelated project paths, then inject each failure stage. Verify coverage, failed status, and the recommended next action in the existing check outputs.

**Acceptance Scenarios**:

1. **Given** no Python Analyst project exists, **When** verification runs, **Then** the new project checks are skipped, their source paths are not added to coverage, and the existing checks retain their established behavior.
2. **Given** a changed Analyst source path without an owning project manifest, including a deleted manifest, **When** coverage is reported, **Then** that source remains uncovered rather than being approved by a broad Analyst-area rule; Markdown retains its existing treatment.
3. **Given** a change spanning multiple existing Python projects, **When** verification runs, **Then** each applicable project is checked once, while files outside any verified project remain uncovered.
4. **Given** a setup, dependency, build, runtime-compatibility, or test failure, **When** the check reports its result, **Then** it identifies the tool or project, the failed stage, and one concrete recommended next action, without claiming the pull request is ready to merge.
5. **Given** unchanged platform, Manager, Web UI, contract, and Markdown inputs, **When** verification runs in each supported mode, **Then** the existing selection rules, checks, output fields, and failure propagation are preserved.

### Edge Cases

- A Python project has a manifest but no lockfile: its check fails; its existence is not treated as permission to regenerate the lockfile.
- A changed Python project is deleted or its manifest disappears: its source changes are uncovered, not skipped as verified.
- A shared schema changes while no Python project exists: the existing platform contract checks still run, without creating a Python check or granting additional coverage.
- A change affects both an SDK and a capability that uses it: both consuming projects must be selected when the dependency is declared, not only the project whose source path changed.
- A project has no discoverable tests or its declared test command is unavailable: verification fails instead of presenting an empty test run as evidence.
- A source directory, dependency environment, generated package, or cache contains unrelated manifests: it must not become an independently verified product project or expand coverage.
- Media tools exist on the runner at another version: only the approved toolchain satisfies setup.
- External package or tool repositories are unavailable: the responsible stage fails explicitly; no mutable or unlocked fallback is allowed.
- A new check fails before later checks execute: existing status, log, checks, and uncovered-file outputs are still written according to the established action contract.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Implementation changes MUST be confined to the existing `.github/actions/environment-setup/` and `.github/actions/environment-verify/` folders, apart from this feature's task completion markers. Tests, fixtures, and environment-local documentation added by implementation MUST stay inside those folders.
- **FR-002**: Setup MUST provide explicitly pinned, mutually compatible Python, `uv`, and selected media extraction and probe tools on the existing supported automated runner, independently of future project files.
- **FR-003**: Setup MUST verify the installed versions and the integrity of downloaded artifacts where the chosen distribution supplies artifact verification. A failed installation, integrity check, or version check MUST fail setup; a differently versioned preinstalled tool MUST NOT satisfy the requirement.
- **FR-004**: Setup MUST demonstrate media extraction, metadata probing, and successful decoding using a short synthetic fixture, without external recordings or an additional executable download during verification. The fixture and smoke check MUST NOT implement the Segment Service.
- **FR-005**: Setup MUST preserve the existing tools-disabled mode and the caller-facing inputs, outputs, trusted cache boundaries, and provision of existing tools. New dependency-download caches and changes to trusted cache-population workflows are out of scope.
- **FR-006**: Verification MUST identify existing Python Analyst product projects only within `src/analysts/`, excluding the Manager's existing solution, generated output, dependency environments, and caches. It MUST NOT grant blanket coverage to the source area or create missing projects.
- **FR-007**: In task and phase mode, verification MUST check Python projects affected by their own changes, shared analysis or common contract changes, or a changed declared local Python dependency. In finalize mode, it MUST check every existing Python Analyst project. Each project MUST be checked at most once per verification invocation.
- **FR-008**: Each selected project MUST restore its dependencies from a committed, consistent lockfile, build its distributable package, and run its tests, in that order. Missing or inconsistent dependency declarations, incompatible runtime requirements, failed builds, failed tests, and empty test runs MUST fail verification. Verification MUST NOT rewrite committed dependency declarations or download an alternative runtime.
- **FR-009**: A source scope MUST be considered covered only when its owning project exists and has an applicable executable check. Missing or deleted manifests and source outside verified project ownership MUST remain uncovered, subject to the existing repository-wide metadata and Markdown rules.
- **FR-010**: Shared contract changes MUST trigger Python consumer verification without transferring coverage or canonical-contract ownership away from the platform contract checks.
- **FR-011**: Every new failure MUST identify the tool or project and failed stage, propagate a failing status through the existing action contract, and supply one concrete recommended corrective action. Guidance MUST distinguish a broken environment from a failing consuming project and MUST NOT recommend bypassing checks or weakening tests.
- **FR-012**: Existing platform, Manager, Web UI, Markdown, mode-selection, logging, output, and uncovered-file behavior MUST be preserved except for the explicit additional Python-project checks and tooling.
- **FR-013**: Automated environment-local evidence MUST exercise absent and present projects; multiple projects and local dependencies; shared contract triggers; all three verification modes; missing or stale lockfiles; runtime incompatibility; failed builds, tests, and empty test runs; coverage boundaries; tool failures; and preservation of the existing action interface.
- **FR-014**: The environment action folders MUST document their pinned tooling, supported project and command expectations, selection and coverage behavior, smoke checks, and corrective actions. Future product setup and usage documentation belongs to the features that introduce those products.

### Key Entities

- **Verified project**: An existing Analyst-owned Python product project with its manifest, committed dependency lock, package build, tests, owned source scope, and declared local dependencies.
- **Pinned toolchain**: The approved runtime, dependency tool, and media tools, with exact versions and installation-validation evidence.
- **Verification result**: The selected checks, their outcomes and failed stages, covered and uncovered source paths, and the recommended corrective action when a check fails.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All defined selection cases pass: absent, present, deleted, and multiple projects; direct and local-dependency changes; shared contract changes; unrelated changes; and each of the three verification modes.
- **SC-002**: Every injected dependency, compatibility, package, test, empty-test-run, installation, integrity, and version failure produces a failed result naming the responsible tool or project, the stage, and one recommended corrective action.
- **SC-003**: No absent or unowned source scope is reported as verified, and no applicable project is verified more than once in one invocation.
- **SC-004**: A fresh supported environment with no future product project can complete setup and the synthetic media smoke check using only the approved toolchain, with no additional executable download during the smoke check.
- **SC-005**: All existing environment-interface and check-selection regression cases continue to pass, and verification leaves committed dependency declarations unchanged.

## Assumptions

### Dependencies

- [Environment Verification Coverage](../archive/20261007-115855-environment-verification-coverage/spec.md) provides the existing setup and verification extension points; it is implemented.
- [Sequential Platform Test Projects in Environment Verification](../archive/20261014-180324-fix-sequential-platform-tests/spec.md) provides the established sequential platform-test execution that this feature preserves; it is implemented.
- [Web UI Verification Environment](../archive/20261012-145505-web-ui-environment/spec.md) provides the existing Web UI check and pinned tooling that this feature preserves; it is implemented.
- The Python Analyst SDK and Segment Service are downstream consumers, not prerequisites. This feature MUST merge before their implementation requires the new tooling. The Docker Desktop runtime feature, now implemented, is not a prerequisite for environment setup or synthetic smoke checks.

### Architecture Alignment

- [Analyst Technology](../../docs/architecture/platform-implementation.md#analyst-technology) and [Analytics](../../docs/architecture/tenancy-and-technology.md#analytics) establish the Python, locked-project, and canonical-contract direction.
- [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md) governs shared schema authority and independent consumer validation.
- [On-Demand Video Segmentation](../../docs/architecture/match-data-pipeline.md#on-demand-video-segmentation) motivates the media tooling; this feature does not select or implement the service architecture.
- Exact tool versions and the extraction distribution are planning decisions, supported by compatibility, provenance, licensing, and installation evidence. No named model, dataset, production profile, or quality threshold is promoted by installing tools.

### Scope Boundaries

- The existing Linux automated runner, provided Node.js, selected .NET SDK, and available container runtime remain the environment baseline. This feature adds no operating-system support matrix.
- Future Python projects provide their own committed dependency declarations, package definitions, test suites, and supported product commands. This feature defines the consuming environment's expectations without creating placeholder products.
- Product code, canonical schemas, the SDK, Analyst capabilities, media-service implementation, model or image publication, new workflows, accelerators, offline data, deployment, production governance, and new cache infrastructure are out of scope.
- No product command becomes supported solely through this environment change. Successful environment checks are not evidence of real Analyst quality, Segment Service behavior, or production readiness.
