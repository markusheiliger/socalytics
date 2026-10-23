# Quickstart: Web UI Workflow Operations for Club Admins

Validation guide for the Operations area. Routes, confirmation, and adapter behavior are defined in
[contracts/routes-and-roles.md](contracts/routes-and-roles.md) and [contracts/operations-adapter.md](contracts/operations-adapter.md).

## Prerequisites

- Node.js 24, Kiota 1.36.0 on `PATH`, Playwright 1.63.0 Chromium, the .NET 10 SDK, and a running Docker-compatible
  container runtime (as in the repository `README.md`).

## Automated checks

From the repository root:

```powershell
dotnet build src/platform/SocAlytics.Platform.slnx
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter WorkflowOperationsOpenApiTests
```

Expected: the five operations reference their typed response schemas and declare their query parameters.

From `src/clients/web`:

```powershell
npm ci
npm run build
npm test
npm run test:e2e -- --project simulated
npm run test:e2e -- --project platform-operations
```

Expected:

- Unit tests pass for the adapter mapping, filters, paging, summary, lists, detail, and reconciliation dialog.
- `simulated`: a Club Admin reaches every Operations view and action by keyboard; axe reports no violations; filters
  and paging return the fixture pages; the dialog sends one request and shows the outcome, the `503` explanation, and
  the not-permitted view on `403`; a Coach sees no navigation entry and the not-permitted view; no operations response
  is in Cache Storage or browser storage; offline shows the connection-required state.
- `platform-operations` (chained after the other `platform` projects, which run first as its dependencies): against
  the AppHost, the seeded Club Admin reads the summary, opens both lists (and a message when one exists), and runs a
  reconciliation that shows counts and times.

## Manual walkthrough

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` and open the Web UI as a Club Admin.
2. Create a match, upload and finalize a recording set, then open **Operations**: the summary shows run counts and the
   observation time; **Refresh** updates the time.
3. Open the pending or failed count's link and a message's detail; check that no payload is shown and identifiers can
   be copied.
4. Open **Notification receipts** filtered by outcome.
5. Choose **Run reconciliation**, read the confirmation, confirm, and check the outcome and the refreshed summary.
6. Sign in as a Coach: no **Operations** entry; `/operations` shows the not-permitted view.
