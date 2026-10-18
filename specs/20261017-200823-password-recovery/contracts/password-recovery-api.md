# Contract: Password Recovery API and Web UI Routes

The operations are part of the platform's OpenAPI document (`/openapi/v1.json`) and its committed snapshot, which the
Web UI's Kiota client is generated from. All three are anonymous, accept only `application/json`, answer with
`Cache-Control: no-store`, and use the existing problem-details shape (`ProblemResults`).

## `POST /api/v1/password-recovery/requests` (`requestPasswordRecovery`)

Request body:

```json
{ "address": "member@example.org" }
```

| Status | When | Body |
| --- | --- | --- |
| `202 Accepted` | Every well-formed address, whether or not it belongs to an eligible member, and also when the per-member message limit is reached | empty |
| `400 Bad Request` | `address` missing, empty, or not a valid address | problem, field violation `address` (`required` or `invalid`) |
| `415 Unsupported Media Type` | Not JSON | problem |
| `429 Too Many Requests` | Per-client request limit (`password-recovery-rate-limited`), with `Retry-After` | problem |

Every `202` and `400` answer is returned no earlier than `PasswordRecovery:MinimumResponseDuration` after receipt.

## `POST /api/v1/password-recovery/checks` (`checkPasswordRecovery`)

Request body:

```json
{ "token": "<token from the link fragment>" }
```

| Status | When | Body |
| --- | --- | --- |
| `200 OK` | The recovery is usable | `{ "accountName": "jdoe" }` |
| `400 Bad Request` | Token missing, unknown, expired, used, superseded, or invalidated | problem `password-recovery-invalid` (one neutral code for all cases) |
| `415 Unsupported Media Type` | Not JSON | problem |
| `429 Too Many Requests` | Per-client token limit (`password-recovery-rate-limited`), with `Retry-After` | problem |

The check never uses the recovery; it only marks it `expired` or `invalidated` when it already is.

## `POST /api/v1/password-recovery/completions` (`completePasswordRecovery`)

Request body:

```json
{ "token": "<token from the link fragment>", "newPassword": "<new password>" }
```

| Status | When | Body |
| --- | --- | --- |
| `204 No Content` | Password replaced, recovery used, every session of the member ended | empty |
| `400 Bad Request` | `newPassword` missing or violates the password policy (token stays usable) | problem, field violation `newPassword` (`required` or `password-policy`) |
| `400 Bad Request` | Token missing, unknown, expired, used, superseded, or invalidated | problem `password-recovery-invalid` |
| `415 Unsupported Media Type` | Not JSON | problem |
| `429 Too Many Requests` | Per-client token limit (`password-recovery-rate-limited`), with `Retry-After` | problem |

A completion never establishes a session.

## Recovery message

- Type `password-recovery`, an account message: listed by `getOwnNotificationPreferences` with
  `accountMessage: true` and refused by `setOwnNotificationPreference` with `notification-type-not-configurable`.
- Link: `<Web UI base address>/password-recovery/complete#token=<token>`.
- Text: the kind of message, a note that it can be ignored when not requested, the link lifetime, and the link; no
  account name, password, roles, or club data.

## Web UI routes

| Route | Access | Purpose |
| --- | --- | --- |
| `/sign-in` | signed out | Adds the "Forgot your password?" link to `/password-recovery`. |
| `/password-recovery` | signed out (`RequireSignedOut`) | One address field; shows the generic confirmation after `202`, field problems after `400`, and the wait time after `429`. |
| `/password-recovery/complete` | public | Reads `token` from the fragment, removes it from the address bar, calls the check, shows the account name and the new-password form, completes, and leads to `/sign-in`; shows one neutral message with a link to `/password-recovery` for `password-recovery-invalid`. |
