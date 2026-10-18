# Quickstart: Member Invitations by Email

Validation guide for the feature; see [spec.md](spec.md) for the requirements,
[contracts/member-invitations-api.md](contracts/member-invitations-api.md) for the operations, and
[data-model.md](data-model.md) for states and configuration.

## Prerequisites

- .NET 10 SDK (`src/platform/global.json`), Node.js 24, Kiota 1.36.0, Playwright 1.63.0 Chromium, and a running
  Docker-compatible container runtime (PostgreSQL, NATS, Mailpit).
- Development and test configuration provide the `Invitations` section (link lifetime 7 days, 3 resends per
  invitation per hour, 10 link checks and acceptances per client per minute). These are not production values.

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected: the invitation integration tests (`MemberInvitation*Tests`) pass for creation, conflicts, delivery and
token issue, check, acceptance, resend, revoke, expiry, rate limits, authorization, and audit privacy; the
`OpenApiSnapshotTests` pass against the refreshed snapshot; architecture tests pass.

```powershell
cd src/clients/web
npm ci
npm run build
npm test
npm run test:e2e -- --project simulated
npm run test:e2e
```

Expected: unit tests for the invite dialog, invitations section, invitation page, and adapter pass; the `simulated`
Playwright tests cover the members-area flows and the invitation page states; the `platform` tests invite an address,
read the message from Mailpit, accept, and sign in.

## Manual walkthrough

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` and sign in to the Web UI as the bootstrap Club
   Admin.
2. In **Members**, choose **Invite member**, enter an address, an account name, and a Coach role on one team; the
   invitation shows as pending.
3. Open Mailpit from the Aspire dashboard; the message names the club, the lifetime, and one link, and no account
   name or roles.
4. Resend the invitation; the first link now shows the neutral "not valid" message and the second link shows the club
   and account name.
5. Set a password that meets the policy and accept; sign in with the account name and password and see the team as
   Coach. In **Members**, the invitation shows as accepted and the member's address as verified.
6. Invite another address and revoke it; its link shows the neutral message. Inviting the first address again is
   refused with "address already in use".
