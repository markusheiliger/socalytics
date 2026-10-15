# Data Model: Web UI Analyst Manager Fleet and Registration Approval

The Web UI holds no authoritative data; registrations are read through
`RegistrationAdapter` ([contracts/platform-adapter.md](contracts/platform-adapter.md))
and cached by TanStack Query. Nothing is persisted in the browser.

## Registration (client view)

Source: `AnalystManagerRegistrationDto` (research.md § R2).

| Field | Use |
| --- | --- |
| `registrationId`, `managerId`, `version` | Identity and display |
| `state` | `pairing`, `pendingApproval`, `approved`, `active`, `unregistering`, `unregistered`, `rejected`, `expired`, `revoked`, or `unknown` (mapped from the wire names of research.md § R3) |
| `deviceFingerprint` | Shown on every view; source of the approval and revocation confirmations (research.md § R5) |
| `deviceMetadata` | `os`, `osVersion`, `architecture`, `managerVersion`, `claimedKeyProtection` (`tpm`, `secure-enclave`, `pkcs11-token`, `software`, or `unknown`), `hostLabel` (optional) |
| `pairingOrigin` | `sourceAddress`, `requestedAt` (Club Admin views only) |
| `approvalNotice` | The platform's text, shown on pending requests |
| `submittedBy`, `submittedAt`, `decidedBy`, `decidedAt`, `decisionReason`, `revokedBy`, `endedAt`, `endReason`, `activatedAt` | Shown when present |
| `pairingExpiresAt`, `approvalExpiresAt`, `activationExpiresAt` | Deadlines |
| `status` (Club Admins only) | `operatingState` (`running`, `paused`, `runtimeUnavailable`, `draining`, or `unknown`), `pausedUntil`, `runtimeUnavailableReason`, `activeAnalysts`, `maxAnalysts`, `managerVersion`, `reportedAt`; absent before the first report or for Registrars |
| `connectivity` (Club Admins only) | `online`, `offline`, `notApplicable`, or `unknown` |

Derived: `isOwnSubmission` = `submittedBy` equals the session member's id;
`isCurrent` = state in `pairing`, `pendingApproval`, `approved`, `active`,
`unregistering`; `isEnded` = state in `unregistered`, `rejected`, `expired`,
`revoked`.

## Fleet filter

`current` (default) | `ended` | `all` | one registration state; ordering for
`current` and `ended`: `pendingApproval` first, then host label
(case-insensitive, missing labels last), then `createdAt` (research.md § R3).

## Pairing form

| Field | Rule |
| --- | --- |
| `pairingCode` | Required; trimmed; held in the form only, cleared after submission, never in an address or storage |
| `deviceFingerprint` | Required; trimmed; same handling |

## Decision forms

| Form | Fields and rules |
| --- | --- |
| Approve | `fingerprintConfirmation`: enabled only when the normalized input equals the last 6 normalized characters of the request's fingerprint (research.md § R5) |
| Reject | `reason`: required, 1–500 characters |
| Revoke | `reason`: required, 1–500 characters; `confirmation`: the host label (trimmed, exact) or, without a host label, the last 6 normalized fingerprint characters |

## Pending count

Number of registrations in `pendingApproval`, refreshed every 60 s while a
Club Admin is signed in (research.md § R4); hidden when zero.
