# Research: Web UI Verification Environment

## R1. Kiota as a pinned .NET global tool

- **Decision**: Install `Microsoft.OpenApi.Kiota` 1.36.0 (the latest release
  on GitHub, `microsoft/kiota` `v1.36.0`) with
  `dotnet tool install --global` in `environment-setup`, and add
  `~/.dotnet/tools` to `GITHUB_PATH`. The Web UI build calls `kiota generate`
  into ignored temporary output.
- **Rationale**: The .NET SDK is already installed for the platform. A global
  tool at a fixed version is independent of Web UI project files (FR-006).
- **Alternatives considered**:
  - **The npm package `@microsoft/kiota`**: also 1.36.0, but installing it
    globally adds a second download path, and pinning it inside the project
    would make the environment's pin redundant.
  - **A .NET local tool manifest**: it would need a repository file outside
    the action folders, which FR-008 forbids.

## R2. Playwright browser pinned in setup, project pin checked in verify

- **Decision**: `environment-setup` installs the Chromium build of Playwright
  1.63.0 (the latest stable release on npm) with
  `npx --yes playwright@1.63.0 install --with-deps chromium`. `environment-verify`
  sets `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1` for `npm ci`. It then checks that
  the installed `@playwright/test` version equals 1.63.0 and fails the
  `playwright pin` step otherwise.
- **Rationale**: Playwright browsers are tied to the Playwright version. A
  mismatched project would otherwise download a browser at test time or fail
  obscurely. The explicit check mirrors the Manager's SDK pin check and gives a
  clear failure (FR-006, SC-005).
- **Alternatives considered**: Running `npx playwright install` in verify
  after `npm ci`, using the project's version, would download at verification
  time and make the environment's pin meaningless.

## R3. Fixed npm script contract

- **Decision**: The Web UI project must provide `npm run build` (type checking,
  client generation, production build), `npm test` (unit tests, non-watch),
  and `npm run test:e2e` (Playwright browser tests). The check calls exactly
  these, in that order, after `npm ci`.
- **Rationale**: Fixed script names keep the environment independent of the
  Web UI's internal tool choices. The Web UI foundation feature provides the
  scripts.

## R4. Trigger, scope, and modes

- **Decision**:
  - The trigger is `^(src/clients/web|src/platform/SocAlytics\.Platform\.Api)/`
    (clarification).
  - Only `^src/clients/web/` joins `COVERED`, and only while
    `src/clients/web/package.json` exists. Platform API files stay covered by
    the platform scope.
  - The check uses the existing `applies` helper, so it runs in task and phase
    mode when the trigger matches and always in finalize mode.
  - The Web UI runs its full unit and browser tests in every mode; there is no
    per-test filtering as in the .NET `run_tests` helper.
- **Rationale**: This matches the existing checks. npm test runners have no
  stable equivalent of the per-class filter. The spec (FR-002) follows this
  convention.

## R5. Setup cost

- **Decision**: Install both tools in every setup run, without caching.
- **Rationale**: Reproducibility and simplicity. The Spec Kit workflows run
  setup per job, and caching across jobs would need cache keys and workflow
  changes outside the action folders.

## R6. Failure reporting

- **Decision**: Each step is logged as `Web UI step: <name>`. The first
  failure logs `Web UI step failed: <name>` and sets `status=1`. The check
  name `Web UI build and tests` is appended before the steps run, so it
  appears as the last check after a failure (FR-004, SC-003).
