# Data Model: Web UI Verification Environment

## Verification scope (Web UI)

| Attribute | Value |
| --- | --- |
| Project marker | `src/clients/web/package.json` |
| Covered pattern | `^src/clients/web/` (joins `COVERED` only while the marker exists) |
| Trigger pattern | `^(src/clients/web\|src/platform/SocAlytics\.Platform\.Api)/` |
| Modes | task and phase: when the trigger matches; finalize: always (while the marker exists) |

Platform API files stay in the platform scope `^(src/platform|contracts)/`.

## Check: Web UI build and tests

Steps run in order and stop at the first failure.

| Step | Command (in `src/clients/web/`) | Fails when |
| --- | --- | --- |
| `install` | `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm ci` | Lockfile missing or not matching the manifest; registry unreachable |
| `playwright pin` | Read the installed `@playwright/test` version | It differs from 1.63.0 |
| `build` | `npm run build` | Type errors, client generation errors, build errors |
| `unit tests` | `npm test` | Any unit test fails |
| `browser tests` | `npm run test:e2e` | Any browser test fails |

## Pinned tools (setup)

| Tool | Version | Installed by |
| --- | --- | --- |
| Kiota (`Microsoft.OpenApi.Kiota`) | 1.36.0 | `dotnet tool install --global` |
| Playwright Chromium (with operating-system dependencies) | browser build of Playwright 1.63.0 | `npx --yes playwright@1.63.0 install --with-deps chromium` |
