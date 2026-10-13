# Contract: Web UI Routes (additions)

Extends the foundation's
[routes-and-roles.md](../../20261012-150657-web-ui-foundation/contracts/routes-and-roles.md).

| Route | View | Allowed for | Platform operations |
| --- | --- | --- | --- |
| `/account` (extended) | Email section: address, state, set, change, remove, resend; notification preferences for the member's roles | Signed in | `getOwnEmail`, `setOwnEmail`, `removeOwnEmail`, `resendOwnEmailConfirmation`, `getOwnNotificationPreferences`, `setOwnNotificationPreference` |
| `/email/confirm` | Reads `#token=` from the fragment, removes it from the address, shows one "Confirm this address" button; success, invalid, and rate-limited states | Signed in or out | `confirmEmail` |
| `/members/:memberId` (extended) | Email row with state; set, change, remove | Club Admin | `getMemberEmail`, `setMemberEmail`, `removeMemberEmail` |

- The token never enters browser storage and is removed from the address before
  the button is shown; the confirmation call happens only on the click.
- Errors follow the foundation's `PlatformFailure` mapping, plus
  `email-address-invalid` (field error), `email-confirmation-invalid` (one
  generic message with an offer to send a new link when signed in), and
  `email-confirmation-rate-limited` (wait time).
