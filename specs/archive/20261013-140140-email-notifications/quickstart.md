# Quickstart: Validate Member Email Addresses and Pending-Request Notifications

Development evidence only. This does not show deployment support or production
readiness.

## Prerequisites

- Web UI Foundation (`specs/archive/20261012-150657-web-ui-foundation`) merged, with
  its prerequisites (Node.js 24, Kiota, Playwright Chromium), the .NET SDK
  from `src/platform/global.json`, and a running Docker-compatible container
  runtime (PostgreSQL, NATS, RustFS, Mailpit).

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
cd src/clients/web
npm ci
npm run build
npm test
npm run test:e2e
```

| Suite | Proves |
| --- | --- |
| Integration tests (`EmailAddressTests`, `EmailConfirmationTests`, `PendingRequestNotificationTests`, `EmailDeliveryWorkerTests`, `EmailPrivacyTests`) | [email-api.md](contracts/email-api.md) behavior, tokens, coalescing, preferences, worker re-checks, relay outage recovery (SC-002, SC-003, SC-005), masking and visibility (SC-004, SC-008) |
| Host tests | The AppHost Mailpit resource and the `Email` and `Notifications` settings |
| Web UI `simulated` | Every Web UI scenario of [web-ui-routes.md](contracts/web-ui-routes.md), CSP, axe (SC-007) |
| Web UI `platform` | Set an address, read the link from Mailpit, confirm, trigger a pending request, see the Club Admin's message (SC-006) |

## Manual exploration

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost`; open the
   Mailpit web UI from the Aspire dashboard.
2. Sign in to the Web UI as `club-admin`, set an email address in the account
   view, open the confirmation message in Mailpit, follow the link, and click
   "Confirm this address".
3. Give a member the Registrar role, pair an Analyst Manager as that member,
   and see the "registration request waits for approval" message for
   `club-admin` in Mailpit; pair another within 15 minutes and see no second
   message.
