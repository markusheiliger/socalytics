# Quickstart: Validate the Web UI Match Workflow and Recording Upload

Development evidence only. This does not show deployment support or production
readiness.

## Prerequisites

- Web UI Foundation (`specs/20261012-150657-web-ui-foundation`) merged, with its
  prerequisites: Node.js 24, Kiota 1.36.0 on `PATH`, the Playwright 1.63.0
  Chromium browser, the .NET SDK from `src/platform/global.json`, and a running
  Docker-compatible container runtime.

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build   # AppHost CORS and storage-origin settings
cd src/clients/web
npm ci
npm run build      # generated client, type checking, production build with SRI
npm test           # Vitest: part policy, digests, retry, grants, coordinator, journal, mapping conversion
npm run test:e2e   # Playwright: simulated (with the fake object store) and platform (AppHost e2e with RustFS)
```

| Command | Proves |
| --- | --- |
| Host tests | The AppHost allows the Web UI origin on RustFS and passes the storage origin to the Web UI ([development-storage.md](contracts/development-storage.md)) |
| `npm test` | Declarations match the platform's digest format and vectors; retries, grant renewal, refusal mapping, journal contents, and sign-out purge ([data-model.md](data-model.md), [upload-worker.md](contracts/upload-worker.md)) |
| `npm run test:e2e -- --project simulated` | Every acceptance scenario of Stories 1–5, the queue, recovery after reload, no recording bytes to `/api/v1` (SC-003), no grants in browser storage (SC-007), the CSP of [security-headers.md](contracts/security-headers.md) (SC-008), axe WCAG 2.2 AA on every new view, and responsiveness with a 1 GiB file (SC-006) |
| `npm run test:e2e -- --project platform` | Create a match, upload a three-part recording directly to RustFS, reload and resume, complete with a mapping, finalize, and see the analysis run state (SC-002) |

## Manual exploration

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` and open the
   Web UI endpoint from the Aspire dashboard; sign in as `club-admin`.
2. Create a season and a team, create a match on the team, and add two MP4
   files of a few hundred MiB.
3. Watch the queue: one file transfers at a time; in the browser's developer
   tools, the part `PUT` requests go to the RustFS origin and no request to
   `/api/v1` carries video bytes.
4. Reload during the first transfer, reopen the match, reselect the same file,
   and observe that only missing parts are sent; select a different file and
   observe the refusal.
5. Enter the match start time for each recording, complete, finalize both in
   order, and finalize the same selection again to see the confirmation.
6. Sign out and back in, and confirm that no pending upload is offered.
