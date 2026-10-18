# Quickstart: Self-Service Password Recovery

Validation guide for the feature. Requirements and contracts are in [spec.md](spec.md),
[data-model.md](data-model.md), and [contracts/password-recovery-api.md](contracts/password-recovery-api.md).

## Prerequisites

- .NET 10 SDK from `src/platform/global.json`, a running Docker-compatible container runtime (PostgreSQL, NATS, and
  Mailpit containers), Node.js 24, Kiota 1.36.0, and Playwright 1.63.0 Chromium, as in `AGENTS.md`.

## Automated validation

From the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SelfServicePasswordRecovery"
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected: the self-service recovery integration tests pass (request answers, eligibility, limits, token lifecycle,
completion, sessions, audit, and privacy), the persistence structure tests accept the new table and column, and the
OpenAPI snapshot test passes with the refreshed snapshot.

From `src/clients/web`:

```powershell
npm ci
npm run build
npm test
npm run test:e2e -- --project simulated
npm run test:e2e
```

Expected: unit tests of the adapter and both pages pass; the `simulated` project covers the request page, the neutral
invalid-link page, and the completion against the fake API; the `platform` project requests a recovery, reads the
message in Mailpit, completes it, and signs in with the new password.

## Manual scenarios (AppHost)

Start `dotnet run --project src/platform/SocAlytics.Platform.AppHost`, sign in as the development Club Admin, set and
verify an address for a member through the Mailpit message, and sign out.

1. **Happy path**: On the sign-in page choose "Forgot your password?", enter the member's address, and send. The page
   shows the generic confirmation. Mailpit shows one recovery message with a `/password-recovery/complete#token=` link.
   Open it: the page shows the member's account name. Enter a policy-compliant password twice and confirm; the page
   leads to sign-in, the old password is refused, and the new one is accepted.
2. **No enumeration**: Request recoveries for an unknown address and for an unverified address. Both show the same
   confirmation after at least the minimum duration; Mailpit receives nothing.
3. **Supersession**: Request twice for the same member; the first link shows the neutral invalid message, the second
   works.
4. **Invalidation**: Request a recovery, then change the member's password while signed in; the link shows the neutral
   invalid message.
5. **Limits**: Request four times within an hour for the same member; Mailpit shows three messages and every answer is
   the generic confirmation. Send six requests within a minute from one browser; the sixth is refused with the wait
   time.
6. **Ineligible members**: A deactivated member and a member linked only to an external provider receive no message.
7. **Audit**: Inspect `socalytics.security_audit_event` for `password-recovery.*` events; none contains a full address
   or token.
