# Contract: Web UI Check of environment-verify

This contract describes what the Web UI foundation feature can rely on and
what it must provide. Row numbers are referenced by the quickstart scenarios.

| # | Situation | Expected `checks` | Expected `uncovered` | Exit |
| --- | --- | --- | --- | --- |
| 1 | `src/clients/web/package.json` missing, any mode | Unchanged from today (no Web UI check) | Unchanged from today | Unchanged |
| 2 | Project present, task mode, change under `src/clients/web/` | `Web UI build and tests` | Empty for those files | 0 when all steps pass |
| 3 | Project present, task mode, change only under `src/platform/SocAlytics.Platform.Api/` | `platform build and tests,Web UI build and tests` | Empty | 0 when both pass |
| 4 | Project present, task mode, change only elsewhere under `src/platform/` | `platform build and tests` | Empty | 0 |
| 5 | Project present, finalize mode, no change | Every active check including `Web UI build and tests` | Empty | 0 |
| 6 | Project present, lockfile does not match the manifest | Ends with `Web UI build and tests` | – | 1; log ends with `Web UI step failed: install` |
| 7 | Project present, `@playwright/test` ≠ 1.63.0 | Ends with `Web UI build and tests` | – | 1; log names both versions and `Web UI step failed: playwright pin` |
| 8 | Project present, a failing unit test | Ends with `Web UI build and tests` | – | 1; `Web UI step failed: unit tests` |
| 9 | Project present, a failing browser test | Ends with `Web UI build and tests` | – | 1; `Web UI step failed: browser tests` |
| 10 | Project missing, non-Markdown change under `src/clients/web/` | Unchanged | Those files | Unchanged |
| 11 | Change under `src/clients/` outside `web/` (non-Markdown) | Unchanged | Those files | Unchanged |

## What the Web UI project must provide

- `src/clients/web/package.json` and a committed `src/clients/web/package-lock.json`.
- npm scripts:
  - `build`: type checking, Kiota client generation into ignored temporary
    output, and the production build;
  - `test`: unit tests, non-watch;
  - `test:e2e`: Playwright browser tests on Chromium.
- `@playwright/test` at exactly 1.63.0.
- Kiota is called as `kiota` from `PATH` (version 1.36.0).
- Browser tests that need the platform API start it themselves (for example
  through the platform test infrastructure or `dotnet run`); the environment
  provides the .NET SDK and Docker.
