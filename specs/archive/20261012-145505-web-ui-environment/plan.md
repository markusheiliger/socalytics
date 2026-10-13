# Implementation Plan: Web UI Verification Environment

**Branch**: `20261012-145505-web-ui-environment` | **Date**: 2026-10-12 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261012-145505-web-ui-environment/spec.md`

## Summary

Extend the two composite actions that the Spec Kit workflows use for setup and
verification:

- `environment-setup` installs Kiota 1.36.0 as a .NET global tool and the
  Chromium browser of Playwright 1.63.0, with its operating-system
  dependencies.
- `environment-verify` gets a new "Web UI build and tests" check for the npm
  project `src/clients/web/`. The check:
  - is active only while `src/clients/web/package.json` exists;
  - runs when `src/clients/web/` or the platform's API project changed;
  - covers only `src/clients/web/`;
  - runs `npm ci`, a Playwright version check, `npm run build`, `npm test`,
    and `npm run test:e2e` in that order, naming the failing step.

Existing checks, their names, and uncovered reporting stay unchanged. See
[research.md](research.md) for the decisions.

## Technical Context

**Language/Version**: Bash inside GitHub composite actions (YAML). The checks
invoke npm with the Node.js 24 that the calling workflows set up, and the .NET
10 SDK pinned by `src/platform/global.json` (for the Kiota tool).

**Primary Dependencies**:

- `actions/setup-dotnet@v5`, already used;
- `dotnet tool install --global Microsoft.OpenApi.Kiota --version 1.36.0`, new;
- `npx --yes playwright@1.63.0 install --with-deps chromium`, new;
- npm from the Node.js the workflows provide.

**Storage**: N/A

**Testing**: The validation scenarios in [quickstart.md](quickstart.md). The
final task runs them in Bash against a scratch copy of the repository outside
the working tree, with a minimal probe Web UI project, and reports each
scenario's expected and actual outcome for the reviewer. The merge workflow's
self-test of the branch's own actions proves only the unchanged baseline
(SC-002), because the Web UI project does not exist when this feature merges.

**Target Platform**: GitHub-hosted `ubuntu-latest` runners used by the Spec Kit
workflows.

**Project Type**: CI extension point (composite actions).

**Performance Goals**: No additional runtime for changes outside the Web UI
trigger. The setup adds the Kiota tool install and one Chromium download per
job (about 1–2 minutes, cached by nothing). This is accepted for reproducible
pins (research.md § R4).

**Constraints**:

- Change only `.github/actions/environment-setup/` and
  `.github/actions/environment-verify/`.
- Keep inputs, outputs, check names, order, and uncovered reporting unchanged
  for existing scopes.
- No secrets or tokens in the verify job.
- No test-time downloads of browsers (`PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`
  during `npm ci`).

**Scale/Scope**: Two YAML files; one new check block, one new setup step pair.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / rule | Pre-design | Post-design | Evidence |
| --- | --- | --- | --- |
| I. Architecture is the design authority | PASS | PASS | Kiota (`contracts-and-compatibility.md` § Generated Client Boundary), Vitest and Playwright (`platform-implementation.md` § Test And Observability Baseline), Chromium first (`client-applications.md` § Delivery And Workspace), Web UI in `src/clients` (`platform-implementation.md` § Client Technology). No architecture change needed. |
| II. Source-area ownership | PASS | PASS | No source files added; only the environment extension points change. The `src/clients/web/` path is the location this feature fixes for the Web UI foundation. |
| III. API-first control plane | N/A | N/A | No product behavior. |
| IV. Evidence over claims | PASS | PASS | The merge self-test proves the unchanged baseline; Web UI coverage is proven by the quickstart scenarios that the final task runs and reports; a skipped check never reports success for code that exists. |
| V. Focused, minimal changes | PASS | PASS | Only the two tools the spec requires are added; npm and Node.js come from the workflows; the .NET SDK is reused for Kiota. |
| Technology: environment features | PASS | PASS | This *is* the environment feature for the Web UI foundation; it changes only the two action folders and is held for human review by design. |
| Workflow: documentation | PASS | PASS | The action descriptions document the coverage, the required npm scripts, and the pins (FR-010); the README follows when the Web UI foundation creates the project. |

No violations; Complexity Tracking is not needed.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261012-145505-web-ui-environment/
├── plan.md              # This file
├── research.md          # Decisions R1–R6
├── data-model.md        # Verification scope, check steps, pins
├── quickstart.md        # Validation scenarios
├── contracts/
│   └── verify-web-ui.md # Web UI check contract (trigger, scope, steps, scripts, pins)
├── checklists/requirements.md
└── tasks.md             # Created by /speckit-tasks
```

### Source Code (repository root)

```text
.github/actions/
├── environment-setup/
│   └── action.yml       # + Kiota 1.36.0 (.NET global tool), + Playwright 1.63.0 Chromium with dependencies
└── environment-verify/
    └── action.yml       # + WEB_* variables, Web UI scope in COVERED, Web UI check block
```

**Structure Decision**: Only the two composite actions change, as the
constitution's environment-feature rule requires.

## Design

See [contracts/verify-web-ui.md](contracts/verify-web-ui.md).

1. **Setup**: two steps after the existing ones.
   - "Install the Kiota client generator": `dotnet tool install --global
     Microsoft.OpenApi.Kiota --version 1.36.0`, adding `~/.dotnet/tools` to
     `GITHUB_PATH`.
   - "Install the Playwright Chromium browser":
     `npx --yes playwright@1.63.0 install --with-deps chromium`.

   Neither step reads `src/clients/web/` (FR-006). Each prints its version
   (SC-005).
2. **Verify variables**:
   - `WEB_DIR=src/clients/web`;
   - `WEB_SCOPE='^src/clients/web/'`;
   - `WEB_TRIGGER='^(src/clients/web|src/platform/SocAlytics\.Platform\.Api)/'`;
   - `WEB_PLAYWRIGHT=1.63.0`, which must equal the setup pin.

   When `$WEB_DIR/package.json` exists, only `WEB_SCOPE` joins `COVERED`
   (FR-001, FR-002).
3. **Verify block**: placed after the Manager check and before the Markdown
   check. It runs when `status` is 0, `applies "$WEB_TRIGGER"` holds, and the
   manifest exists. It appends the check name `Web UI build and tests` and
   runs, in `$WEB_DIR`, these steps, each prefixed in the log by
   `Web UI step: <name>`:
   1. `install`: `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm ci`.
   2. `playwright pin`: the installed `@playwright/test` version must equal
      `WEB_PLAYWRIGHT`, otherwise it fails with both versions.
   3. `build`: `npm run build`.
   4. `unit tests`: `npm test`.
   5. `browser tests`: `npm run test:e2e`.

   The first failure sets `status=1` and the log ends with
   `Web UI step failed: <name>` (FR-003, FR-004). While the manifest is
   missing, the block is skipped (FR-005).
4. **Descriptions**: both actions' `description` and "Extending" comments
   name the Web UI check, its trigger and scope, the five steps, the required
   npm scripts (`build`, `test`, `test:e2e`), the pins, and that the check is
   active only once the project exists (FR-010).

## Complexity Tracking

No constitution violations.
