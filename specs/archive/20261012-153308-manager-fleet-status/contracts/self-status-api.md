# Contract: Manager Status Report

`POST /api/v1/analyst-managers/self/status`

This uses Manager machine authentication (`RequireAnalystManager`, DPoP) and
requires HTTPS, like the other `self` operations. The golden fixtures live in
`contracts/analyst-manager/status/v1/`.

## Request

```json
{
  "operatingState": "paused",
  "pausedUntil": "2026-10-12T18:00:00Z",
  "runtimeUnavailableReason": null,
  "activeAnalysts": 1,
  "maxAnalysts": 2,
  "managerVersion": "0.3.0"
}
```

## Response `200`

```json
{
  "managerId": "…",
  "stampId": "…",
  "state": "active",
  "clubName": "…",
  "keyThumbprint": "…",
  "activatedAt": "…",
  "statusCheckIntervalSeconds": 60,
  "reportAccepted": true,
  "reportViolations": []
}
```

- When the report is invalid: `reportAccepted: false`, and
  `reportViolations` lists `{ field, code }`. The stored report is unchanged
  and the registration information is still returned.
- An inactive or revoked Manager gets the same refusals as
  `GET …/self/registration` today (`401`), which the Manager treats as
  revocation.

`GET /api/v1/analyst-managers/self/registration` also returns
`statusCheckIntervalSeconds`.
