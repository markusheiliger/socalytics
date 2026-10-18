# Data Model: Self-Service Password Recovery

Persistence follows the platform conventions in [src/platform/README.md](../../src/platform/README.md): forward-only DbUp
migrations, one migration file numbered at implementation time as the next free number
(`NNNN_identityaccess_password_recovery.sql`), and a `PersistedTableClassifications` entry for every new table without
`version`.

## PasswordRecovery (new table `socalytics.password_recoveries`)

One recovery attempt for one eligible member, created by a request that matched the member's verified address.

| Field | Type | Rules |
| --- | --- | --- |
| `id` | uuid | Primary key. |
| `account_id` | uuid | References `member_account (id)`; the member whose password the link resets. |
| `address_hash` | text | Hash of the normalized address the message was created for (same hash as `email_messages.address_hash`); never the address. |
| `security_stamp` | text | The member's security stamp when the request was accepted; a different current stamp invalidates the recovery. |
| `state` | text | `pending`, `issued`, `used`, `superseded`, `invalidated`, `expired`. |
| `token_hash` | bytea NULL | SHA-256 of the token, written by the delivery worker; unique when present; never the raw token. |
| `requested_at` | timestamptz | When the request created the recovery. |
| `issued_at` | timestamptz NULL | When the worker issued the current token. |
| `expires_at` | timestamptz NULL | `issued_at` plus `PasswordRecovery:LinkLifetime`. |
| `used_at` | timestamptz NULL | When the completion used it. |

Constraints mirror `email_confirmations`: `pending` has no token hash; `issued`, `used`, and `expired` carry token
hash, `issued_at`, and `expires_at`; `superseded` and `invalidated` carry them only when they were issued before; `used` exactly when `used_at` is set; unique index on
`token_hash` where present; index on `account_id` for open states (`pending`, `issued`).

Classification: unversioned ("single-use recovery state; the state transition is the guard").

### State transitions

```text
pending --(worker sends, issues token)--> issued
issued  --(worker resends attempt, new token)--> issued
pending|issued --(newer request for the member)--> superseded
issued --(completion succeeds)--> used
issued --(check or completion finds expiry)--> expired
pending|issued --(check, completion, or worker finds member inactive, no local password,
                  address changed/unverified, or security stamp changed)--> invalidated
```

`used`, `superseded`, `invalidated`, and `expired` are final.

## Email message (existing table `socalytics.email_messages`)

- New nullable column `password_recovery_id uuid REFERENCES socalytics.password_recoveries (id)`, set for messages of
  type `password-recovery` and null otherwise.
- `notification_type` gains the value `password-recovery` (a code in `NotificationTypes`, no check constraint change).
- The per-member hourly limit counts rows with `notification_type = 'password-recovery'` and `account_id` of the member
  created in the last hour.

## Member email address (existing table `socalytics.member_email_addresses`)

- New index on `address_normalized` for the request lookup. No column changes.

## Member account (existing, via ASP.NET Core Identity stores)

- Completion replaces the password hash, clears `password_change_required`, and changes the security stamp; failed
  sign-in count and lockout end are not changed.

## Member session (existing table `socalytics.member_session`)

- Completion ends every live session with the existing end reason `password-reset`.

## Security audit events (existing `security_audit_event`)

| Event type | Outcome | Resource | Details | Reason codes |
| --- | --- | --- | --- | --- |
| `password-recovery.requested` | succeeded | member, or none | `maskedAddress` | `queued`, `not-eligible`, `rate-limited`, `no-eligible-account` |
| `password-recovery.issued` | succeeded | member | `maskedAddress` | - |
| `password-recovery.completed` | succeeded | member | - | - |
| `password-recovery.completed` | failed | none | - | `recovery-invalid`, `password-policy` |

No event, log, or metric carries a token, password, full address, or message body.

## Configuration (`PasswordRecovery` section)

| Key | Development and test value | Production |
| --- | --- | --- |
| `LinkLifetime` | 60 minutes | Open / Blocking |
| `MaxMessagesPerHour` | 3 per member | Open / Blocking |
| `RequestsPerMinute` | 5 per client | Open / Blocking |
| `TokenAttemptsPerMinute` | 10 per client (checks and completions) | Open / Blocking |
| `MinimumResponseDuration` | 400 milliseconds | Open / Blocking |
