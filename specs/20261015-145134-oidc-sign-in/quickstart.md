# Quickstart: External OpenID Connect Sign-In

Validation scenarios that prove the feature end to end. Commands run from the
repository root; a running Docker-compatible engine is required, as for the
existing platform tests.

## Automated verification

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build
cd src/clients/web
npm ci
npm run build
npm test
npm run test:e2e
```

| Suite | Proves |
| --- | --- |
| Integration tests with `OidcProviderContainerFixture` | Provider listing; start and callback with PKCE, state, nonce, and the browser binding; linked, unlinked, deactivated, invalid-response, provider-error, expired, and replayed sign-ins; link credential issue, redemption, refusal (expired, used, already linked); unlink ends sessions; member creation with either credential; no token or secret in responses, logs, or audit records (SC-002, SC-003, SC-005). |
| Unit tests of the ID-token validator | Issuer, audience, signature, lifetime, nonce, and key rotation with locally signed tokens. |
| Host tests | OpenAPI snapshot contains every operation of [contracts/external-sign-in-api.md](contracts/external-sign-in-api.md); startup validation of provider configuration. |
| Web UI unit tests | Provider buttons, link-account page, member page link section, creation choice, generic failure display. |
| Playwright `simulated` | The sign-in and link flows with an intercepted start call and a simulated callback; accessibility checks with axe. |
| Playwright `platform` | Against the AppHost in `e2e` mode with the test provider container: create a member with a link credential, redeem it through the provider, sign out, sign in through the provider, unlink, and be refused (SC-001, SC-004). |

Expected: every suite passes; with no provider configured, the existing
sign-in, bootstrap, and recovery tests pass unchanged (SC-005).

## Manual check with a real provider (optional)

Register a confidential client at a real provider with the redirect URI
`https://<host>/api/v1/session/external/callback`, add it under
`IdentityAccess:ExternalProviders:<id>` through user secrets, and repeat the
Playwright `platform` scenario by hand. This is not production-readiness
evidence.
