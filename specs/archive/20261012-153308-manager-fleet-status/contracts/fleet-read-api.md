# Contract: Fleet Read

The existing operations `GET /api/v1/analyst-manager-registrations` (paged,
optional `state` filter) and `GET /api/v1/analyst-manager-registrations/{registrationId}`
are extended.

| Caller | List | Detail |
| --- | --- | --- |
| Club Admin (`ClubPermission.Administer`) | All registrations of the club, each with `status` (or `null`) and `connectivity` (`online`, `offline`, `not-applicable`) | Any registration with `status` and `connectivity` |
| Registrar without Club Admin (`ClubPermission.Register`) | Only registrations the member submitted, without `status` and `connectivity` | Own submitted registration; any other is `404` |
| Other members | `403` | `403` |

- `status` = `{ operatingState, pausedUntil, runtimeUnavailableReason, activeAnalysts, maxAnalysts, managerVersion, reportedAt }`.
- `deviceMetadata.hostLabel`, `claimedKeyProtection`, and the fingerprint are
  unchanged and visible to both callers.
- No new actions: approval, rejection, and revocation stay as they are
  (FR-009).
- `POST /api/v1/analyst-manager-registrations` (Registrar submission) already
  returns the DTO with `deviceMetadata.hostLabel` and `deviceFingerprint`;
  tests assert both (FR-011).
