# Contract: Email Messages

Rendered by the delivery worker from the platform's English message resources
(`src/platform/SocAlytics.Platform.Infrastructure/Email/Messages.en.json`),
as plain text with a minimal HTML alternative; no images, tracking pixels, or
external resources.

| Type | Subject (key) | Body content | Link |
| --- | --- | --- | --- |
| `email-confirmation` | "Confirm your email address for {clubName}" | That an address was added to a SocAlytics account and how to confirm it; the link's lifetime; that nothing happens if the recipient ignores it | `{WebUiBaseUri}/email/confirm#token={token}` |
| `registration-pending` | "An Analyst Manager registration request waits for approval" | That one or more Analyst Manager registration requests wait for a Club Admin's decision | `{WebUiBaseUri}/analyst-managers` |

- `{clubName}` is the club's display name; no other data is inserted.
- Messages contain no host label, fingerprint, submitting member, team, match,
  player data, or credential (FR-012); only the confirmation link carries its
  single-use token.
- Headers: `From` = `Email:SenderName <Email:SenderAddress>`, `To` = the
  recipient's current address at send time, `Auto-Submitted: auto-generated`,
  and a `Message-ID` derived from the message identity.
