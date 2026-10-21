# Feature Specification: Agents Python Verification Environment

**Feature Branch**: `20261018-110526-agents-python-environment`

**Created**: 2026-10-18

**Status**: Draft

**Input**: User description: "Agents Python Verification Environment: the intelligence-plane source area `src/agents/` will receive its first executable Python projects — the MCP server and the Hermes agent runtime — but the environment verification recognizes and covers Python projects only at `src/analysts/sdk/` and `src/analysts/capabilities/<name>/`, so agent code would be reported as uncovered and held for review, and its dependency and test failures would not be caught. This standalone environment feature extends ONLY the two environment action folders so that independent, locked Python projects directly under `src/agents/<name>/` (each with its own `pyproject.toml`, committed `uv.lock`, locked build backend and pytest in development groups) are recognized, selected, built, and tested exactly like the Analyst Python projects, reusing the already installed and verified CPython 3.12.15 and uv 0.13.1 toolchain and the same locked command sequence, with no new tool downloads unless the plan proves a need: own-source changes and declared local path dependencies select affected projects in task and phase mode, changes to the environment action folders and to shared contracts the agents consume (`contracts/agent-catalog/`, `contracts/common/`, and any agent-tool contract under `contracts/`) select all existing agent projects, finalize selects all; absent projects add no coverage; deleted or unrecognized manifests stay uncovered; local path dependencies may point only to recognized projects (agent projects may not depend on Analyst projects unless the plan finds a reason and states it); failures name the project, stage, and one corrective action; and every existing check, input, output, mode rule, and cache boundary is preserved. Environment-local tests extend the existing fixtures. Out of scope: creating any agent project, model providers or secrets, network access during tests beyond locked dependency installation, other source areas, workflow changes. Depends on the merged Analyst and Media Verification Environment. Implements no product architecture change (Architecture Updates: None, with the reason), only the environment's coverage for the planned `src/agents` Python projects."

## Clarifications

### Session 2026-10-18

- Q: Which shared contract folders should make verification check every existing agent project? → A: Every folder directly under `contracts/` whose name starts with `agent-` (today `agent-catalog`, `agent-orchestration`, and `agent-tools`), plus `contracts/common/`, so a later agent contract family needs no environment change.
- Q: Must verification actively block network access while agent tests run? → A: No; verification adds no network isolation and no network access, supplies no credentials or provider configuration, and an agent project's default test run must not depend on network services, model providers, or secrets.

## User Scenarios & Testing *(mandatory)*

The users are contributors and implementation automation. Their immediate need is that the first intelligence-plane Python projects, the MCP Server and the Hermes agent runtime, are verified like the Analyst Python projects from their first task, instead of being held for review as uncovered changes whose dependency and test failures go unnoticed.

### User Story 1 - Verify agent project changes without a manual coverage exception (Priority: P1)

A contributor or implementation agent changes an existing Python project directly under the agents source area. Verification restores exactly the project's locked dependencies, builds its package, and runs its tests with the already provisioned toolchain. A broken dependency declaration, package, or test fails the check, and the project's own files are reported as covered.

**Why this priority**: The MCP Server and Hermes agent runtime cannot proceed through automated implementation while every change to them is uncovered and unverified.

**Independent Test**: Exercise verification against isolated fixture projects placed under the agents area of a temporary workspace, without adding a product project. A passing project is checked and covered; dependency, build, and test failures each fail verification naming the project and stage.

**Acceptance Scenarios**:

1. **Given** a Python project directly under the agents area with a manifest and matching committed lockfile, **When** its files change in task or phase mode, **Then** its locked dependencies are restored, its package is built, and its tests run with the same locked command sequence as an Analyst Python project, and its own files are reported as covered.
2. **Given** several existing agent projects and a change to only one of them, **When** verification runs in task or phase mode, **Then** only that project and the agent projects that declare it as a local dependency, directly or transitively, are checked.
3. **Given** existing agent projects, **When** a shared contract the agents consume (an `agent-` contract family or the common contract definitions) changes, **Then** every existing agent project is checked, and the platform contract validation keeps owning the coverage of the contract files.
4. **Given** existing agent projects, **When** verification runs in finalize mode or either environment action folder changed, **Then** every existing agent project is checked even if its source did not change.
5. **Given** a missing or inconsistent lockfile, an incompatible runtime declaration, a failed package build, a failing test, or an empty test run, **When** the affected agent project is verified, **Then** verification fails at the responsible stage without refreshing dependency declarations, downloading another runtime, or skipping tests.

---

### User Story 2 - Keep agent and Analyst verification independent (Priority: P2)

A maintainer relies on the established Analyst Python check while the agents area grows. Agent projects and Analyst projects are verified side by side, but neither area's changes, failures, or dependency declarations silently change the verification of the other.

**Why this priority**: The intelligence plane and the Analyst runtime are separate ownership areas with separate runtimes; mixing them would hide which area failed and would let one area's projects depend on the other's without an architecture decision.

**Independent Test**: Run selection and execution fixtures that contain both Analyst and agent projects, change paths in one area at a time, inject a failure in one area, and declare cross-area local dependencies.

**Acceptance Scenarios**:

1. **Given** existing Analyst and agent projects, **When** only an agent project changes in task mode, **Then** no Analyst project is checked, and vice versa.
2. **Given** an agent project, **When** it declares a local path dependency on an Analyst project, or an Analyst project declares one on an agent project, **Then** verification fails at project discovery with the project, the stage, and one corrective action.
3. **Given** an invalid agent project, **When** verification runs, **Then** the failure is reported as an agent project failure that names the agent project, and the established Analyst check keeps its name, selection, and failure behavior.
4. **Given** a shared analysis contract change, **When** verification runs in task or phase mode, **Then** the Analyst projects are checked as before and no agent project is selected solely by that change.

---

### User Story 3 - Keep coverage and recovery guidance honest (Priority: P3)

A maintainer reviews a verification result before deciding whether a pull request can merge. The result distinguishes checked agent projects from agent-area files that no check covers and names a concrete next step for every new failure.

**Why this priority**: The environment must help land pull requests through reliable evidence, not hide gaps behind a broad area rule.

**Independent Test**: Run the selection and reporting checks with absent, present, deleted, nested, and unrelated agent-area paths, then inject each failure stage and verify the coverage, the failed status, and the recommended action in the existing action outputs.

**Acceptance Scenarios**:

1. **Given** no agent project exists, **When** verification runs in any mode, **Then** no agent project check runs, no agent-area source is added to coverage, and every existing check behaves as before.
2. **Given** a changed agent-area file without an owning recognized project, including a deleted manifest, a manifest at the area root, or a manifest nested below a project, **When** coverage is reported, **Then** that file stays uncovered; Markdown keeps its existing treatment.
3. **Given** a manifest in an unsupported agents-area location, **When** verification runs, **Then** it fails at project discovery naming the manifest as an unsupported layout instead of silently dropping it from finalize verification.
4. **Given** any new failure, **When** the check reports its result, **Then** it names the agent project, the failed stage, and exactly one concrete recommended action, writes the established log and outputs, and never recommends bypassing checks or weakening tests.

### Edge Cases

- An agent project has a manifest but no lockfile: discovery fails; verification never regenerates the lockfile.
- A changed agent project is deleted or its manifest disappears: its source stays uncovered instead of being skipped as verified.
- A shared agent contract changes while no agent project exists: the existing platform contract checks still run, and no agent check or coverage is added.
- An agent project and a shared agent library it depends on both live under the agents area: a change to the library selects the library and every agent project that declares it.
- Two projects, one under the Analysts area and one under the agents area, declare the same normalized project name: discovery fails as a duplicate identity, because both areas share the one provisioned toolchain and a name must identify one project.
- A project folder name under the agents area resembles another (for example `mcp` and `mcp-server`): path matching uses complete path components, never prefixes.
- An agents-area folder contains a dependency environment, generated output, cache, hidden folder, or symbolic link: it is never recognized as a project or searched for manifests.
- An agent project's tests need a model provider, credential, network service, or other external service: verification supplies none and adds no network isolation, so such tests fail or behave unreliably; the project must keep them out of its default test run or replace the dependency with a fake.
- A new contract family such as `contracts/agent-conversations/` appears later: it selects every agent project without an environment change, while a similarly named folder such as `contracts/agentic/` does not.
- A check before the agent check fails: the agent check does not run, and the established `checks`, `uncovered`, and log outputs are still written.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Implementation changes MUST be confined to the existing `.github/actions/environment-setup/` and `.github/actions/environment-verify/` folders, apart from this feature's task completion markers. Tests, fixtures, and environment-local documentation added by implementation MUST stay inside those folders.
- **FR-002**: Agent projects MUST be verified with the already provisioned and verified Analyst Python toolchain, the pinned interpreter and dependency tool of the merged [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md). This feature MUST NOT add a tool download, change a pin, or change the setup action's inputs, outputs, cache behavior, or tools-disabled mode, unless the plan proves a need and states it.
- **FR-003**: Verification MUST recognize as agent projects only independent Python projects whose manifest lies directly in an immediate, non-hidden folder of the agents source area, excluding dependency environments, generated output, caches, and symbolic links. It MUST NOT create a project, grant blanket coverage to the agents area, or recognize a manifest at the area root or nested below a project.
- **FR-004**: An agents-area Python manifest outside the recognized locations MUST fail discovery as an unsupported layout that names the manifest, and its source MUST stay uncovered.
- **FR-005**: Each recognized agent project MUST satisfy the same project convention as an Analyst Python project: a unique normalized static project name, a runtime declaration that admits the provisioned interpreter, its own committed consistent lockfile, a package build definition whose required backends are locked in its development dependencies, locked pytest, and at least one collectable test. Violations MUST fail discovery or the responsible stage.
- **FR-006**: An agent project MAY declare local path dependencies only on other recognized agent projects. A local dependency on an Analyst project, outside the repository, on a missing or invalid project, under a mismatched name, or forming a cycle MUST fail discovery. Analyst projects MUST likewise keep failing on local dependencies outside their own recognized roots, including agent projects.
- **FR-007**: Project names MUST be unique across Analyst and agent projects; a duplicate MUST fail discovery naming both projects.
- **FR-008**: In task and phase mode, verification MUST select an agent project when its own source changed, when a recognized agent project it declares as a local dependency (directly or transitively) is selected by its own source change, when either environment action folder changed, or when a shared contract the agents consume changed. In finalize mode, it MUST select every existing agent project. Each agent project MUST be checked at most once per verification invocation, in dependency order.
- **FR-009**: The shared contracts that select every existing agent project MUST be every contract folder directly under `contracts/` whose name starts with `agent-` (today the agent catalog, agent orchestration, and agent tool contracts) and the shared common contract definitions in `contracts/common/`. Other contract folders, including the analysis contracts, MUST NOT select agent projects. Contract coverage and canonical ownership MUST stay with the platform contract checks.
- **FR-010**: Each selected agent project MUST run the same locked command sequence, stages, environment, and failure semantics as an Analyst Python project: restore locked external dependencies, install the project and its local dependencies with the locked backends, build its distributable package, and run its tests, in that order. Verification MUST NOT rewrite committed declarations, download an alternative runtime, build external source distributions, or accept an empty test run.
- **FR-011**: Verification MUST supply agent tests with no model-provider configuration, credentials, secrets, or service endpoints, and MUST NOT add network access beyond the locked dependency installation that the existing sequence already performs. It adds no network isolation either; the documented project convention MUST state that an agent project's default test run must not depend on network services, model providers, or secrets.
- **FR-012**: An agent project's source scope MUST be covered only while its project is recognized and valid. Deleted or missing manifests, unrecognized manifests, and agents-area files outside recognized project roots MUST stay uncovered, subject to the existing repository-wide metadata and Markdown rules.
- **FR-013**: Agent project verification MUST be reported as its own check, distinct from the Analyst Python check, and an agent discovery or stage failure MUST fail that check without renaming, reordering, or changing the selection of the existing checks.
- **FR-014**: Every new failure MUST name the agent project or manifest and the failed stage, propagate a failing status through the existing action contract, and supply exactly one concrete recommended corrective action. Guidance MUST distinguish a missing environment from a failing project and MUST NOT recommend bypassing checks or weakening tests.
- **FR-015**: Existing platform, Analyst Manager, Web UI, Analyst Python, environment action test, and Markdown checks, the mode rules, the log, the `checks` and `uncovered` outputs, the setup action's interface, and the trusted cache boundaries MUST be preserved except for the explicit additional agent check and its coverage.
- **FR-016**: Automated environment-local evidence MUST extend the existing fixtures to exercise absent and present agent projects; multiple agent projects and local dependencies; cross-area dependency rejection and duplicate names; shared contract, environment, and unrelated triggers; all three verification modes; unsupported layouts and coverage boundaries; each failing stage; and preservation of the existing checks and action interface.
- **FR-017**: The environment action folders MUST document the agent project convention, its selection and coverage rules, and its corrective actions next to the existing Analyst Python documentation. Product setup and usage documentation for the MCP Server and Hermes belongs to the features that introduce them.

### Key Entities

- **Agent project**: An existing independent Python product project directly under the agents source area, with its manifest, committed lock, package build, tests, owned source scope, and local dependencies on other agent projects.
- **Project area**: The ownership area a recognized project belongs to (Analyst or agent), which decides its permitted local dependencies, its shared contract triggers, and the check that reports it.
- **Verification result**: The selected checks, their outcomes and failed stages, the covered and uncovered changed paths, and the recommended corrective action when a check fails.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All defined agent selection cases pass: absent, present, deleted, nested, and multiple projects; direct and local-dependency changes; shared contract, environment, and unrelated changes; and each of the three verification modes.
- **SC-002**: Every injected agent discovery, dependency, compatibility, package, test, and empty-test failure produces a failed result naming the agent project or manifest, the stage, and one recommended action.
- **SC-003**: No absent, deleted, or unowned agents-area source is reported as covered, and no agent project is checked more than once in one invocation.
- **SC-004**: All existing environment action regression cases, including every Analyst Python case, continue to pass unchanged, and an agent-only change selects no Analyst project and the reverse.
- **SC-005**: Verification of agent projects needs no additional tool download, credential, or secret, and leaves committed dependency declarations unchanged.

## Assumptions

### Dependencies

- [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md) provides the pinned interpreter and dependency tool, the locked command sequence, the project convention, and the Analyst Python check that this feature reuses and preserves; it is implemented.
- [Environment Verification Coverage](../archive/20261007-115855-environment-verification-coverage/spec.md) provides the setup and verification extension points; it is implemented.
- The MCP Server and the Hermes agent runtime specs of this batch are downstream consumers, not prerequisites. This feature MUST merge before their implementation adds agent projects.
- The shared agent contracts are added by [Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md) and [Agent Evidence Tools](../20261018-104924-agent-evidence-tools/spec.md) of this batch and by [Agent Orchestration Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md), which is published and being implemented; the trigger rule applies whether or not those folders exist yet.

### Architecture Alignment

- [Source and Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline) assigns future intelligence-agent runtimes to `src/agents`, and [Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md) describes Hermes as a separate Python service and the MCP server as future work.
- [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md) governs shared schema authority and independent consumer validation.
- No product component, topology, model provider, or production behavior is decided by this environment change; the agents source area stays non-executable until a product feature adds its first project.

### Scope Boundaries

- Creating any agent project, model providers, secrets or credentials, network access during tests beyond locked dependency installation, container images, other source areas, workflow changes, new caches, and new tool pins are out of scope.
- The existing Linux automated runner and the toolchain of the merged environment feature remain the baseline. Whether a future agent dependency supports the provisioned interpreter is verified by the feature that adds it; an incompatible need requires a subsequent environment change.
- No product command becomes supported solely through this environment change. Successful environment checks are not evidence of agent quality, MCP or Hermes behavior, or production readiness.
