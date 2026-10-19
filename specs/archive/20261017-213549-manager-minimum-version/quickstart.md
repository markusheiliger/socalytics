# Quickstart: Analyst Manager Minimum Version

Validation guide for the feature. Contracts: [manager-minimum-version.md](contracts/manager-minimum-version.md); data:
[data-model.md](data-model.md).

## Prerequisites

- .NET 10 SDK from `src/platform/global.json`, a running Docker-compatible runtime, and on Linux SoftHSM2 for the
  Manager and end-to-end tests.
- Node.js 24, Kiota 1.36.0, and Playwright Chromium for the Web UI (see `AGENTS.md`).

## Automated checks

From the repository root:

```powershell
dotnet build src/platform/SocAlytics.Platform.slnx
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter "FullyQualifiedName~ManagerVersionOptionsTests|FullyQualifiedName~OpenApiSnapshotTests"
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~MinimumVersion|FullyQualifiedName~ManagerVersion"
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build --filter "FullyQualifiedName~MinimumVersion"
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build --filter "FullyQualifiedName~MinimumVersion|FullyQualifiedName~ManagerVersion"
```

In `src/clients/web`: `npm run build`, `npm test`, and `npm run test:e2e -- --project simulated fleet`.

Expected: every test passes; with no minimum configured, the full platform, Manager, and Web UI suites pass unchanged
(SC-006).

## Manual walk-through (Development)

1. Start the AppHost with `Registry__ManagerVersion__Minimum=99.0.0` set for the API resource (or in
   `appsettings.Development.json` locally, not committed), pair and approve a tray or headless Manager, and wait one
   registration-check interval.
2. `socalytics-manager-server status` (or the tray status window) shows `Update required: Manager 0.1.0 is below the
   stamp minimum 99.0.0.` and the operating state; the tray menu shows `Update required (minimum 99.0.0)`.
3. The Web UI fleet view, signed in as a Club Admin, marks the Manager with its version and the minimum.
4. Start an analysis run: the Manager claims no job; a direct API request for queue credentials as the Manager returns
   `403` with code `analyst-manager-version-below-minimum`.
5. Remove the setting and reload or restart the API: within one check interval the notice disappears and the Manager
   claims work again, without re-registration.

Starting the API with `Registry__ManagerVersion__Minimum=0.2` fails at start with a validation message naming the key.
