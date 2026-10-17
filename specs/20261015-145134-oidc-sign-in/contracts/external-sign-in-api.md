# Contract: External Sign-In API

All operations are described in the OpenAPI snapshot
`src/platform/SocAlytics.Platform.Api/openapi/v1.json` (research.md § R2).
The three anonymous operations are classified as such in the architecture
test `EndpointSecurityTests` (`AnonymousOperations`).
Failures that a caller could use to probe accounts or links all map to one
generic outcome.

## `GET /api/v1/identity-providers` (anonymous, `listIdentityProviders`)

`200 OK`, `Cache-Control: no-store`:

```json
{ "providers": [ { "id": "club-entra", "displayName": "Club account" } ] }
```

An empty list when none is configured. No authority, client, or scope data.

## `POST /api/v1/session/external` (anonymous, `startExternalSignIn`)

Request, one of:

```json
{ "provider": "club-entra" }
```

```json
{ "provider": "club-entra", "accountName": "j.doe", "linkCredential": "<code>" }
```

- `200 OK` with `{ "authorizationUrl": "https://login.example/authorize?…" }`
  and `Set-Cookie: __Host-socalytics-external=…; Secure; HttpOnly; SameSite=Lax; Path=/`.
  The URL carries `response_type=code`, `client_id`, `redirect_uri` (the
  callback), `scope`, `state`, `nonce`, `code_challenge`, and
  `code_challenge_method=S256`.
- `400` (`invalid-request`) for an unknown provider or a malformed body.
- `400` (`sign-in-failed`, the existing generic sign-in failure) for any link
  credential that does not validate; nothing is consumed.
- `503` (`provider-unavailable`) when the provider's discovery document cannot
  be read.

## `GET /api/v1/session/external/callback` (anonymous, browser navigation, `completeExternalSignIn`)

Query from the provider: `code` and `state`, or `error`.

- Success: sets the existing session cookie, clears
  `__Host-socalytics-external`, ends any presented session (`replaced`), and
  `302` to `{WebUiBaseUrl}/`.
- Any failure: no session, clears the binding cookie, and `302` to
  `{WebUiBaseUrl}/sign-in?external=failed` (or `/link-account?external=failed`
  for a link redemption). The cause is only audited.

## Member operations (Club Admin, `MapMemberApi`, anti-forgery)

- `POST /api/v1/members`: optional `credential`
  `{ "kind": "set-password" }` (default, as today) or
  `{ "kind": "identity-link", "provider": "<id>" }`; the response's credential
  field carries the issued credential of the chosen kind.
- `POST /api/v1/members/{memberId}/credentials`: `purpose` gains
  `identity-link` with a required `provider`.
- `GET /api/v1/members/{memberId}`: gains
  `externalIdentities: [ { provider, displayName, linkedAt, providerConfigured } ]`
  for Club Admins; no `issuer` or `subject`.
- `DELETE /api/v1/members/{memberId}/external-identities/{provider}`: `204`;
  removes the link and ends the member's sessions; `404` when not linked; uses
  `If-Match` with the member version like other member changes.
- Non-admins receive the existing `403`.
