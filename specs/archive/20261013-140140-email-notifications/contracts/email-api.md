# Contract: Email and Notification API

All operations are under `/api/v1`, JSON, with the foundation's session,
anti-forgery, `If-Match`, and problem conventions unless stated.

## Own address and preferences (any active member)

| Operation | Request | Responses |
| --- | --- | --- |
| `GET /me/email` (`getOwnEmail`) | – | `200 { address, state, verifiedAt, version }` or `404` when none |
| `PUT /me/email` (`setOwnEmail`) | `{ address }`, `If-Match` when one exists | `200` (state `unverified`; confirmation queued); `400 email-address-invalid`; `409`/`412` version conflict |
| `DELETE /me/email` (`removeOwnEmail`) | `If-Match` | `204` |
| `POST /me/email/confirmation-requests` (`resendOwnEmailConfirmation`) | – | `202`; `409 email-already-verified`; `404` when no address; `429 email-confirmation-rate-limited` with `Retry-After` |
| `GET /me/notification-preferences` (`getOwnNotificationPreferences`) | – | `200 { items: [ { type, enabled, accountMessage } ] }` for the types that apply to the member's roles |
| `PUT /me/notification-preferences/{type}` (`setOwnNotificationPreference`) | `{ enabled }` | `200`; `400 notification-type-not-configurable` for account messages or types outside the member's roles |

## Confirmation (anonymous)

| Operation | Request | Responses |
| --- | --- | --- |
| `POST /email-confirmations` (`confirmEmail`) | `{ token }` | `204`; `400 email-confirmation-invalid` for every refusal (expired, used, superseded, unknown); `429` when more than `Email:Confirmation:AttemptsPerMinute` attempts come from one client address |

No session or anti-forgery token is required; a present session is ignored.

## Members' addresses (Club Admin)

| Operation | Request | Responses |
| --- | --- | --- |
| `GET /members/{memberId}/email` (`getMemberEmail`) | – | `200` or `404` |
| `PUT /members/{memberId}/email` (`setMemberEmail`) | `{ address }`, `If-Match` when one exists | as `setOwnEmail` |
| `DELETE /members/{memberId}/email` (`removeMemberEmail`) | `If-Match` | `204` |

Other members: `403`. `GET /members` and `GET /members/{memberId}` include
`email` (`{ address, state }`) only for Club Admins (FR-022).

## Delivery side effects

- `setOwnEmail`, `setMemberEmail`, and `resendOwnEmailConfirmation` queue one
  `email-confirmation` message in the same unit of work.
- `submitAnalystManagerPairingCode` (existing) additionally queues one
  `registration-pending` message per eligible Club Admin outside the
  coalescing window, in the same unit of work (FR-010, FR-011).
