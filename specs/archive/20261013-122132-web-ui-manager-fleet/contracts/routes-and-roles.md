# Contract: Routes and Roles (additions)

Extends the foundation's
[routes-and-roles.md](../../20261012-150657-web-ui-foundation/contracts/routes-and-roles.md).

| Route | View | Allowed for | Platform operations |
| --- | --- | --- | --- |
| `/analyst-managers/pair` | Pairing page: code and fingerprint, result with host label, key protection, OS, fingerprint, approval deadline | Registrar or Club Admin; signed-out visitors go to `/sign-in?returnTo=/analyst-managers/pair` | `submitAnalystManagerPairingCode` |
| `/analyst-managers/requests` | My registration requests (list) | Registrar without Club Admin (Club Admins are redirected to the fleet) | `listAnalystManagerRegistrations` |
| `/analyst-managers/requests/:registrationId` | My request (detail, read-only) | Registrar who submitted it; others see "not found" as the platform answers | `getAnalystManagerRegistration` |
| `/analyst-managers` | Fleet: filters, list with status and connectivity, pending first | Club Admin | `listAnalystManagerRegistrations` |
| `/analyst-managers/:registrationId` | Registration detail; approve (fingerprint confirmation), reject (reason), revoke (reason and typed confirmation) | Club Admin | `getAnalystManagerRegistration`, `approveAnalystManagerRegistration`, `rejectAnalystManagerRegistration`, `revokeAnalystManagerRegistration` |

The fixed path `/analyst-managers/pair` is the platform's
`Registry:Pairing:VerificationPath`; it is registered before
`/analyst-managers/:registrationId`.

## Navigation

- Club Admins: "Analyst Managers" → `/analyst-managers`, with the pending count
  (hidden when zero), refreshed every 60 s.
- Registrars without Club Admin: "My registration requests" →
  `/analyst-managers/requests`, and "Pair a Manager" → `/analyst-managers/pair`.
- Coaches and Viewers without these roles: no entries; direct addresses show the
  not-permitted view.

## Sign-in return

`/sign-in?returnTo=<path>` returns to `<path>` after sign-in only when it starts
with exactly one `/` and matches a known route (research.md § R6).
