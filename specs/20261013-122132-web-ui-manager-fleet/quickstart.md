# Quickstart: Validate the Web UI Analyst Manager Fleet and Registration Approval

Development evidence only. This does not show deployment support or production
readiness.

## Prerequisites

- Web UI Foundation (`specs/archive/20261012-150657-web-ui-foundation`) and Analyst
  Manager Status Report and Fleet Read API (`specs/archive/20261012-153308-manager-fleet-status`)
  merged, with the foundation's prerequisites: Node.js 24, Kiota 1.36.0 on
  `PATH`, the Playwright 1.63.0 Chromium browser, the .NET SDK from
  `src/platform/global.json`, and a running Docker-compatible container runtime.

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build   # verification base option, typed registration DTOs, OpenAPI snapshot, AppHost settings
cd src/clients/web
npm ci
npm run build      # generated client now typed for the registration operations
npm test           # adapter mapping, fingerprint normalization, filters, refresh, returnTo, scripted Manager proofs against the golden fixtures
npm run test:e2e   # simulated: every scenario; platform: pair, approve, activate, status, revoke with the scripted Manager
```

| Command | Proves |
| --- | --- |
| `dotnet test` | [platform-changes.md](contracts/platform-changes.md): the verification address uses the Web UI origin when configured, and the registration operations are typed without changing their JSON |
| `npm test` | [data-model.md](data-model.md) rules and [platform-adapter.md](contracts/platform-adapter.md) mappings |
| `npm run test:e2e -- --project simulated` | Every acceptance scenario, role access (SC-005), identical pairing refusals (SC-004), no codes or fingerprints in addresses or storage (SC-007), CSP and axe (SC-008) |
| `npm run test:e2e -- --project platform` | A scripted Manager is paired through the pairing page, approved with the fingerprint confirmation, activated, reports its state, appears online in the fleet, and is revoked (SC-003) |

## Manual exploration

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost`; sign in to
   the Web UI as `club-admin` and give a second member the Registrar role.
2. Run the Analyst Manager (`dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager`)
   against the API's HTTPS endpoint and start registration with a host label.
3. Open the verification address the Manager shows; it opens the Web UI's
   pairing page. Sign in as the Registrar and submit the code and fingerprint.
4. As `club-admin`, see the pending count in the navigation, open the request,
   enter the last 6 fingerprint characters from the Manager's screen, and
   approve. Watch the Manager become active and online in the fleet.
5. Revoke it by typing its host label and a reason.
