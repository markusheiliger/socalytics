# Quickstart: Web UI Analyst Registry Administration

Validation guide for the Registry area. Contracts:
[routes-and-roles](contracts/routes-and-roles.md),
[registry-adapter](contracts/registry-adapter.md),
[registry-openapi](contracts/registry-openapi.md). Models:
[data-model.md](data-model.md).

## Prerequisites

- .NET SDK from `src/platform/global.json`, Node.js 24, Kiota 1.36.0 on
  `PATH`, Playwright 1.63.0 Chromium, and a running Docker-compatible runtime
  (as for the existing Web UI and platform tests).
- From the repository root:

  ```powershell
  dotnet restore src/platform/SocAlytics.Platform.slnx
  dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
  ```

## 1. OpenAPI schemas (platform)

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter "AnalystRegistryOpenApiTests|OpenApiSnapshotTests"
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~Registry"
```

Expected: every Registry operation has request and response schemas; the
committed snapshot equals the running API's document; Registry response bodies
are unchanged. After changing the endpoints, the snapshot is refreshed with
`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
(never in the verification environment).

## 2. Web UI unit tests

```powershell
cd src/clients/web
npm ci
npm run build
npm test
```

Expected: the build regenerates the Kiota client with the Registry models;
adapter, middleware, form, and component tests pass.

## 3. Simulated browser tests

```powershell
cd src/clients/web
npm run test:e2e -- --project simulated
```

Expected (`registry.spec.ts`, `registry-access.spec.ts`): a Club Admin sees
the default workflow and the lists, opens records, registers an image (new,
already registered, each refusal), creates, approves, and retires profile
versions (with confirmations, self-approval note, refusals, stale conflict),
defines a workflow version (from scratch and from an existing version with a
flagged node, each violation), and makes it the default (with "has changed");
a Coach sees no navigation entry and the not-permitted view; axe reports no
violations; every action works by keyboard; offline shows the
connection-required state; no Registry response is in Cache Storage or browser
storage.

## 4. Platform browser test

```powershell
cd src/clients/web
npm run test:e2e -- --project platform-registry
```

Expected: against the AppHost `e2e` mode with the development Registry seed,
the Club Admin browses the seeded default workflow, creates a profile version
from a seeded declaration, approves it, defines a workflow version equivalent
to the seeded one that uses it, makes it the default, sees a tag reference
explained before submission, and registers a digest reference of a seeded
declaration and sees "already registered, nothing changed" (the platform finds
a registered digest without contacting an image registry).

## 5. Manual walkthrough (SC-001, SC-002)

```powershell
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

Sign in as the development Club Admin, open **Analyst Registry** from the
navigation, and follow User Stories 1, 3, and 4 of [spec.md](spec.md). The
default workflow is reachable in two steps from the home view.
