# Contract: Platform Adapter Boundary

Generated code lives in `src/generated/platform-api/` (ignored). Only
`src/platform/` imports it.

## Adapters

| Adapter | Operations | Returns |
| --- | --- | --- |
| `SessionAdapter` | `signIn(accountName, password)`, `restore()`, `signOut()`, `changeOwnPassword(current, next)`, `redeemCredential(accountName, code, newPassword)` | `Session` model (data-model.md) or a `PlatformFailure` |
| `ClubAdapter` | `get()`, `update(details, version)` | `Club` |
| `SeasonAdapter` | `list()`, `get(id)`, `create(input)`, `activate(id, version)`, `archive(id, version)` | `Season` |
| `TeamAdapter` | `listMine()`, `listBySeason(seasonId)`, `get(id)`, `create(seasonId, input)`, `update(id, input, version)` | `Team` |
| `MemberAdapter` | `list()`, `get(id)`, `create(input)`, `issueCredential(id)`, `deactivate(id)`, `reactivate(id)`, `unlock(id)`, `endSessions(id)`, `assignClubRole(id, role)`, `removeClubRole(id, role)`, `assignTeamRole(id, teamId, role)`, `removeTeamRole(id, teamId)` | `Member`, or a `CredentialCode` shown once |

The Web UI's own models (`Club`, `Season`, `Team`, `Member`, `Session`) are
plain TypeScript types mapped from the generated ones, so generated types never
leak into features (FR-002).

## Error mapping (`PlatformFailure`)

| Platform response | `PlatformFailure.kind` | Web UI behavior |
| --- | --- | --- |
| `401`, session-ended problem types | `sessionEnded` | Clear the cache, go to sign-in with a message (FR-009) |
| `403` password change required | `passwordChangeRequired` | Go to `/password-change` (FR-007) |
| `403` other | `notPermitted` | Not-permitted view (FR-012) |
| `400` with field errors | `validation` | Show reasons next to fields (Story 3 scenario 5) |
| `409`/`412` version conflict | `conflict` | "Changed by someone else", reload action (FR-017) |
| `428` version required | `programmingError` | Generic error; covered by unit tests |
| Anti-forgery refusal | `sessionProblem` | Session problem message, offer to sign in again (Story 1 scenario 8) |
| Network failure, `502`–`504` | `unreachable` | Connectivity state (FR-013) |
| Other `5xx` | `unexpected` | Generic message with the correlation reference (edge case) |
| Unknown response shape | `incompatible` | Ask to reload (edge case: outdated build) |

## Middlewares (Kiota request pipeline)

1. `AntiforgeryHeaderHandler`: adds `X-CSRF-Token` from the in-memory session
   to `POST`, `PUT`, `PATCH`, and `DELETE`.
2. `SessionOutcomeHandler`: raises the session-ended and
   password-change-required events.
3. `ConnectivityHandler`: reports network failures to the connectivity
   provider.

Requests use `credentials: 'same-origin'` and the base URL `/api/v1` of the
Web UI origin.
