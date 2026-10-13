# Data Model: Member Email Addresses and Pending-Request Notifications

One forward-only migration `NNNN_identityaccess_email_notifications.sql`,
numbered at implementation time on an up-to-date default branch.

## `member_email_addresses` (versioned, one per account)

| Column | Type | Rule |
| --- | --- | --- |
| `account_id` | uuid | PK; FK `member_account` |
| `address` | text | Trimmed; ≤ 254 characters; syntactically valid; stored as entered |
| `address_normalized` | text | Lower-cased domain and local part, for hashing and comparison |
| `state` | text | `unverified` or `verified` |
| `verified_at` | timestamptz | Set when verified |
| `set_by` | uuid | Member or Club Admin who set it |
| `updated_at` | timestamptz | |
| `version` | bigint | `socalytics.attach_version_trigger` |

Removing an address deletes the row (audited). Changing it replaces `address`,
resets `state` to `unverified`, and supersedes open confirmations.

## `email_confirmations` (no `version`; classified)

| Column | Type | Rule |
| --- | --- | --- |
| `id` | uuid | PK |
| `account_id` | uuid | FK `member_account` |
| `address_hash` | text | SHA-256 of `address_normalized` at creation |
| `state` | text | `pending` → `issued` → `used`; or `superseded`, `expired` |
| `token_hash` | bytea | SHA-256 of the 256-bit token; set when issued by the delivery worker; replaced on re-issue |
| `issued_at`, `expires_at`, `used_at` | timestamptz | `expires_at` = issue + `Email:Confirmation:Lifetime` (development 24 h) |

## `notification_preferences` (no `version`; classified)

| Column | Type | Rule |
| --- | --- | --- |
| `account_id` | uuid | PK part; FK `member_account` |
| `notification_type` | text | PK part; a non-account notification type |
| `enabled` | boolean | Row present only when changed from the default |
| `updated_at` | timestamptz | |

## `email_messages` (transactional email outbox; no `version`; classified)

| Column | Type | Rule |
| --- | --- | --- |
| `id` | uuid | PK; message identity |
| `notification_type` | text | `email-confirmation` or `registration-pending` |
| `account_id` | uuid | Recipient member |
| `address_hash` | text | Hash of the address the message was created for |
| `confirmation_id` | uuid | For `email-confirmation` only |
| `state` | text | `queued` → `sending` → `sent`; or `failed`, `discarded` |
| `attempt_count` | int | ≥ 0 |
| `next_attempt_at`, `lease_until` | timestamptz | Backoff and sending lease |
| `last_error_category` | text | `transient`, `permanent`, `ineligible` |
| `created_at`, `sent_at` | timestamptz | |

No address, body, or token is stored (FR-016). Index on
`(notification_type, account_id, created_at)` for coalescing and the resend
limit; index on `(state, next_attempt_at)` for the worker.

## Notification types (code registry)

| Type | Audience | Account message | Link |
| --- | --- | --- | --- |
| `email-confirmation` | The address owner | Yes (always sent; no preference) | `{WebUiBaseUri}/email/confirm#token=…` |
| `registration-pending` | Active Club Admins | No (preference, on by default) | `{WebUiBaseUri}/analyst-managers` |

## Configuration (development and test values)

| Setting | Value |
| --- | --- |
| `Email:Smtp:Host`, `Port`, `Security`, `UserName`, `Password` | Mailpit `1025`, `StartTlsWhenAvailable` (Development only), no credentials |
| `Email:SenderAddress`, `Email:SenderName` | `socalytics@localhost`, `SocAlytics` |
| `Email:Confirmation:Lifetime` | `1.00:00:00` |
| `Email:Confirmation:MaxPerHour` | `3` |
| `Email:Confirmation:AttemptsPerMinute` | `10` (per client address, anonymous confirmation endpoint) |
| `Email:Delivery:MaxAttempts`, `PollInterval`, `LeaseDuration` | `8`, `00:00:05`, `00:02:00` |
| `Notifications:CoalescingWindow` | `00:15:00` |
| `Notifications:WebUiBaseUri` | The Web UI endpoint (AppHost) |

All are validated at start; `Security` other than `SslOnConnect` or
`StartTls` is refused outside Development. Production values are Open /
Blocking.
