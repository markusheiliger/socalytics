# Feature Specification: Web UI Verification Environment

**Feature Branch**: `20261012-145505-web-ui-environment`

**Created**: 2026-10-12

**Status**: Draft

**Input**: User description: "Environment feature that changes only the environment setup and verify extension points. It extends the automated build-and-verify environment so that the planned Web UI under the clients source area (one npm project at src/clients/web/ with a committed lockfile) is set up, built, and tested like the platform and the Analyst Manager, before the Web UI foundation feature starts. Setup installs, pinned and independent of project files that do not exist yet: Kiota for generating the TypeScript transport client from the platform's OpenAPI document into ignored temporary output, and the Playwright Chromium browser with its system dependencies; Node.js comes from the calling workflow (Node 24); the existing .NET SDK and Docker remain available for browser tests that need a running platform API. Verify adds a Web UI check, active only once src/clients/web/package.json exists: clean install from the lockfile, then the project's build (including type checking and client generation), unit tests, and browser tests; it fails naming the failing step, runs in task mode when files under src/clients/ change and always in phase and finalize mode, and files under src/clients/ count as covered only while the project exists. Out of scope: the Web UI project and its scripts, lint and formatting rules, container images and deployment, browsers other than Chromium, Kiota clients for C# and Python, product CI workflows."

## Clarifications

### Session 2026-10-12

- Q: Should a change to the platform API also run the Web UI check during task verification? → A: Yes: changes under the platform's API project (`src/platform/SocAlytics.Platform.Api/`), whose endpoints shape the OpenAPI document, also trigger the Web UI check; those files stay covered through the platform check.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Web UI changes are verified automatically (Priority: P1)

A contributor (a person or an automated implementation agent) changes the Web UI in the clients source area. The automated verification installs the project's dependencies exactly as locked, builds it, runs its unit tests and its browser tests, and fails, naming the failing step, when any of these fails. Changed Web UI files are not held for review merely because no check covers them.

**Why this priority**: The Web UI foundation is the first feature with executable code in the clients source area. Its tasks cannot be verified or merged automatically until the environment covers the Web UI.

**Independent Test**: Before the Web UI project exists, run the verification in finalize mode and confirm it succeeds exactly as before. Then, on a branch with a minimal Web UI project that has one passing and one failing unit test, confirm the Web UI check runs, reports the failure, and names the unit-test step.

**Acceptance Scenarios**:

1. **Given** the Web UI project does not exist yet, **When** verification runs in any mode, **Then** the Web UI check is skipped and verification succeeds or fails exactly as it did before this feature.
2. **Given** the Web UI project exists, **When** a change touches files of the Web UI project, **Then** the dependencies are installed from the lockfile, the project is built, its unit tests and browser tests run, and those files are not reported as uncovered.
3. **Given** the Web UI project exists, **When** verification runs in finalize mode, **Then** the Web UI check runs even if no clients file changed.
4. **Given** the Web UI project exists, **When** a change touches only files of the platform's API project, **Then** the platform check and the Web UI check both run, and the platform files are covered by the platform check.
5. **Given** the Web UI project exists, **When** its lockfile does not match its manifest, **Then** the install step fails and verification fails naming the Web UI check and the install step.
6. **Given** a Web UI change with a failing build, unit test, or browser test, **When** verification runs, **Then** it fails and names the Web UI check and the failing step.

---

### User Story 2 - The environment provides what the Web UI build and tests need (Priority: P2)

The setup step installs, at pinned versions, the tools that the Web UI build and tests need and that the runner does not provide: the generator that turns the platform's OpenAPI document into a TypeScript transport client, and a Chromium browser with its operating-system dependencies for browser tests. The build never depends on whatever happens to be preinstalled, and nothing is installed at test time.

**Why this priority**: The architecture fixes Kiota for generated clients and Playwright for browser workflows. Pinning them in the environment keeps every run reproducible, but the value appears only together with the check from User Story 1.

**Independent Test**: Inspect the setup output on any run and confirm the client generator and the browser are installed at their pinned versions; on a branch with a minimal Web UI project, confirm its client generation and a browser test run without downloading anything at test time.

**Acceptance Scenarios**:

1. **Given** the setup step runs, **When** the Web UI build generates the transport client, **Then** the pinned generator version is used and its output goes to ignored temporary output only.
2. **Given** the setup step runs, **When** the Web UI browser tests start, **Then** the pinned Chromium browser and its system dependencies are available without a test-time download.
3. **Given** the setup step runs before the Web UI project exists, **When** it completes, **Then** it succeeds, because it does not read Web UI project files.
4. **Given** browser tests that need a running platform API, **When** they run, **Then** the .NET SDK and the container runtime that the platform checks already use are available to them.

### Edge Cases

- A change touches both platform and clients files: the platform check and the Web UI check each run once.
- A change touches the clients source area while the Web UI project was deleted: the files are reported as uncovered rather than silently passing (Markdown files stay covered by the Markdown check).
- A change touches a path in the clients source area outside the Web UI project: it stays uncovered and is held for review.
- The platform's API changes in a way that breaks the generated client, but no clients file changed: the Web UI check runs because the platform's API project changed, fails, and names the build step, so the break is caught in the task that caused it.
- A platform change outside the API project (for example persistence or tests) while the Web UI project exists: in task and phase mode the Web UI check does not run; in finalize mode it runs.
- The Node.js version provided by the calling workflow does not satisfy the version the Web UI project declares: the install or build fails and names the Web UI check.
- Network access to the package registry fails during the clean install: the check fails and names the install step; no partial result passes.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The verification MUST treat changes under the clients source area's Web UI project (`src/clients/web/`) as covered only while that project's manifest (`src/clients/web/package.json`) exists.
- **FR-002**: When the Web UI project exists, the verification MUST run the Web UI check in task and phase mode whenever a file under `src/clients/web/` or under the platform's API project (`src/platform/SocAlytics.Platform.Api/`) changed, and always in finalize mode, and MUST skip it otherwise. Platform API files remain covered only through the platform check.
- **FR-003**: The Web UI check MUST install dependencies only from the committed lockfile with a clean install that fails when lockfile and manifest disagree, then check that the project's Playwright version equals the pinned browser version, then run the project's build, unit-test, and browser-test steps in that order, stopping at the first failure.
- **FR-004**: The Web UI check MUST fail verification when any of its steps fails and MUST name the Web UI check and the failing step in the verification output.
- **FR-005**: While the Web UI project does not exist, the verification MUST skip the Web UI check without failing and MUST behave exactly as before this feature.
- **FR-006**: The setup MUST install, at pinned versions and without reading Web UI project files, the client generator (Kiota) and the Chromium browser used by the browser tests, including its operating-system dependencies, and MUST NOT install other tools unless the Web UI build or tests require them.
- **FR-007**: The setup MUST keep the .NET SDK, the software key-store token, and the Markdown tools it already installs, and the verification MUST keep the container runtime available, so that browser tests can start the platform API.
- **FR-008**: The feature MUST change only the environment setup and environment verify extension points; it MUST NOT add product code, tests, contracts, the Web UI project, or documentation outside those extension points.
- **FR-009**: The order of the existing checks, their names in the verification output, and the uncovered-file reporting MUST keep their existing behavior for platform, Analyst Manager, contract, and Markdown changes.
- **FR-010**: The descriptions inside the two extension points MUST describe the new Web UI coverage, the steps the Web UI project must provide (build, unit tests, browser tests), and that the check becomes active only once the project exists.

### Key Entities

- **Verification scope**: A path pattern that a check covers, active only while the project it verifies exists; here `src/clients/web/`. The Web UI check is also triggered by the platform's API project, which stays in the platform scope.
- **Check**: A named verification step with a pass or fail outcome; this feature adds the Web UI check with the steps install, build, unit tests, and browser tests.
- **Pinned tool**: A tool installed by the setup at a fixed version independent of project files: the client generator and the browser.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After this feature merges, 0 Web UI changes are held for review solely because no check covers them, once the Web UI project exists.
- **SC-002**: In 100% of verification runs before the Web UI project exists, verification succeeds or fails exactly as it did before this feature.
- **SC-003**: In 100% of runs where the Web UI install, build, unit tests, or browser tests fail, verification fails and names the Web UI check and the failing step.
- **SC-004**: 0 product, test, contract, or Web UI project files are added or changed by this feature.
- **SC-005**: In 100% of runs, the client generator and the browser report the same pinned versions.

## Assumptions

- **Dependents**: The Web UI foundation feature, authored next, depends on this feature, which is reviewed and merged before it starts, as the constitution's environment-feature rule requires.
- **Web UI location and toolchain**: The Web UI is one npm project at `src/clients/web/` with a committed `package-lock.json`, providing the scripts the check calls (build, unit tests, browser tests). The exact script names are defined by this feature's plan and documented inside the extension points, so the Web UI foundation feature can provide them.
- **Node.js**: The calling workflows already set up Node.js 24 before the extension points run; the environment does not install another Node.js version.
- **Browser matrix**: The architecture starts the supported-browser matrix with Chromium-based browsers; automated verification runs Chromium on the Linux runner. Other browsers and Windows-specific browser checks are not part of the automated environment.
- **Lint and formatting**: No product lint command exists yet; the Web UI check runs only what the project defines as build and tests. Type checking belongs to the build.
- **Self-test**: The merge workflow self-tests a branch's own environment actions, so this feature is verified by its own merge run; environment changes are held for human review by design.
- **Repository documentation**: The repository README describes only active coverage; the Web UI foundation feature updates it when it creates the Web UI project.
- **Architecture References**: [docs/architecture/client-applications.md](../../docs/architecture/client-applications.md) (Web UI; Delivery And Workspace), [docs/architecture/platform-implementation.md](../../docs/architecture/platform-implementation.md) (Client Technology; Test And Observability Baseline), [docs/architecture/contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md) (Generated Client Boundary).
