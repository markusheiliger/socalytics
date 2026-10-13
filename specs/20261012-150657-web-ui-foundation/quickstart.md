# Quickstart: Validate the Web UI Foundation

Development evidence only. This does not show deployment support or production
readiness.

## Prerequisites

- The Web UI verification environment (`specs/archive/20261012-145505-web-ui-environment`)
  merged: Kiota 1.36.0 on `PATH` and the Playwright 1.63.0 Chromium browser.
  Locally:
  - `dotnet tool install --global Microsoft.OpenApi.Kiota --version 1.36.0`;
  - `npx playwright@1.63.0 install chromium`.
- Node.js 24, the .NET SDK from `src/platform/global.json`, and a running
  Docker-compatible container runtime.

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build   # includes the OpenAPI snapshot test
cd src/clients/web
npm ci
npm run build      # type checking, Kiota generation into src/generated/, production build with SRI
npm test           # Vitest unit tests
npm run test:e2e   # Playwright: simulated project and platform project (starts the AppHost in e2e mode)
```

| Command | Proves |
| --- | --- |
| Host tests | The committed OpenAPI snapshot matches the API |
| `npm run build` | The generated client compiles against the Web UI; SRI attributes are emitted |
| `npm test` | Middlewares, adapters, error mapping, guards, translation-key coverage, views |
| `npm run test:e2e -- --project simulated` | Every acceptance scenario, role navigation, axe WCAG 2.2 AA, CSP violations, no secrets in storage |
| `npm run test:e2e -- --project platform` | Sign-in, required password change, session end, role navigation, and create flows against the real platform |

## Manual exploration

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` and open
   the Web UI endpoint from the Aspire dashboard.
2. Sign in as `club-admin` with the generated first-admin password from the
   dashboard's parameters, then change it.
3. Create a season, activate it, create two teams, create a member, issue a
   credential code, and set the member's password on `/set-password` in a
   private window.
4. Give the member the Coach role on a team, sign in as them, and check the
   navigation.

## Updating the OpenAPI snapshot after API changes

```powershell
$env:SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT='1'; dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter OpenApiSnapshotTests; Remove-Item Env:SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT
```
