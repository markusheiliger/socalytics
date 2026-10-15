# Data Model: External OpenID Connect Sign-In

One forward-only migration `NNNN_identityaccess_external_identities.sql` (the
next free number on an up-to-date default branch; `0019` at planning time).

## `socalytics.external_identity_link` (versioned)

| Column | Type | Rule |
| --- | --- | --- |
| `id` | uuid | Primary key. |
| `account_id` | uuid | References `member_account`; the linked member. |
| `provider_id` | text | Configured provider identifier at link time. |
| `issuer` | text | The ID token's `iss`. |
| `subject` | text | The ID token's `sub`; never returned by any read. |
| `linked_at` | timestamptz | Platform time of the redemption. |
| `linked_by` | uuid | The Club Admin who issued the link credential. |
| `version` | bigint | `socalytics.attach_version_trigger`. |

- Unique `(issuer, subject)` (one identity, one member) and unique
  `(account_id, provider_id)` (one identity per member and provider).
- Deleted on unlink; the member's sessions end in the same unit of work.

## `socalytics.pending_external_sign_in` (no `version`; classified)

| Column | Type | Rule |
| --- | --- | --- |
| `id` | uuid | Primary key. |
| `state_hash` | bytea | SHA-256 of the `state` sent to the provider; unique. |
| `browser_binding_hash` | bytea | SHA-256 of the `__Host-socalytics-external` cookie value. |
| `nonce` | text | Sent in the authorization request; compared with the ID token. |
| `code_verifier` | text | PKCE verifier (`S256` challenge sent). |
| `provider_id` | text | Configured provider. |
| `link_credential_id` | uuid, nullable | The validated, not yet consumed link credential. |
| `created_at` | timestamptz | |
| `expires_at` | timestamptz | `created_at + IdentityAccess:ExternalSignIn:PendingLifetime` (default 5 minutes). |
| `consumed_at` | timestamptz, nullable | Set exactly once by the callback. |

- Completion: `UPDATE … SET consumed_at = now() WHERE id = … AND consumed_at IS NULL AND expires_at > now()`.
- Expired or consumed rows are deleted by the completion path and, in bounded batches (at most 100), whenever a new external sign-in starts.
- Entry in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`.

## `socalytics.one_time_credential` (existing)

- New nullable column `provider_id` (text), required when `purpose = 'identity-link'`
  (check constraint), absent for `set-password` and `password-reset`.

## Configuration (`IdentityAccess`)

| Key | Rule |
| --- | --- |
| `ExternalProviders:<id>:DisplayName` | Required; shown on the sign-in page. |
| `ExternalProviders:<id>:Authority` | Required issuer URL; HTTPS outside Development and Test. |
| `ExternalProviders:<id>:ClientId` | Required. |
| `ExternalProviders:<id>:ClientSecret` | Required; protected configuration only. |
| `ExternalProviders:<id>:Scopes` | Default `openid`. |
| `ExternalProviders:<id>:TokenEndpointAuthMethod` | `client_secret_basic` (default) or `client_secret_post`. |
| `ExternalSignIn:PendingLifetime` | Default 5 minutes. |
| `ExternalSignIn:WebUiBaseUrl` | Where the callback redirects; same origin as the Web UI. |

`<id>` is URL-safe (`[a-z0-9-]{1,32}`).

## Audit events

New event types (details allow-list in `PostgresAuditTrail` gains `provider`
and `cause`): `session.external-sign-in` (success), `session.external-sign-in-failed`
(`cause`: `unlinked`, `deactivated`, `invalid-response`, `provider-error`,
`expired`), `identity.linked`, `identity.link-refused` (`cause`:
`already-linked`, `credential-invalid`), `identity.unlinked`. Link credential
issue uses the existing `credential.issued` with `purpose = identity-link`.
No token, code, state, nonce, verifier, secret, subject, or credential value is
recorded.
