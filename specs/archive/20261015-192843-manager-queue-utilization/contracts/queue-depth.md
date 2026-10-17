# Contract: Queue Depth and Host Utilization

## 1. `GET /api/v1/analyst-managers/self/queue-depth`

Authentication and filters as the other self operations
(`AuthorizationPolicyNames.AnalystManager`, DPoP, HTTPS).

Request: `?capability=person-detection&capability=ball-tracking` (1–64 values,
each `[a-z0-9][a-z0-9.-]{0,63}`).

`200`:

```json
{
  "observedAt": "2026-10-15T19:40:00Z",
  "capabilities": [
    { "id": "ball-tracking", "waiting": 0 },
    { "id": "person-detection", "waiting": 5 }
  ],
  "total": 5
}
```

| Case | Response |
| --- | --- |
| No or more than 64 `capability` values, or an invalid id | `400` validation problem, field `capability` |
| Inactive registration | `401` `analyst-manager-registration-inactive` (DPoP handler, as today) |
| Registration not active | `403` `manager-not-active` |
| Consumer info cannot be read | `503` `urn:socalytics:problem:queue-unavailable` |
| Requested id is not a consumer of the resolved default workflow, or its consumer does not exist | omitted from `capabilities` |

The operation is read-only, unaudited, and never changes admission or
assignment. Schema and examples:
`contracts/analyst-manager/queue-depth/v1/queue-depth.schema.json` and
`contracts/analyst-manager/queue-depth/v1/examples/`; the OpenAPI snapshot
`src/platform/SocAlytics.Platform.Api/openapi/v1.json` gains the operation.

## 2. Local-control `status` (additive, `src/analysts/manager/contracts/local-control.schema.json`)

```json
{
  "queueDepth": {
    "state": "available",
    "observedAt": "2026-10-15T19:40:00Z",
    "ageSeconds": 12,
    "capabilities": [ { "id": "person-detection", "waiting": 5 } ],
    "total": 5
  },
  "hostUtilization": {
    "sampledAt": "2026-10-15T19:40:10Z",
    "cpuPercent": 37.5,
    "memoryUsedBytes": 8589934592,
    "memoryTotalBytes": 34359738368
  }
}
```

`state` is `available`, `stale`, `not-yet-available`, `no-capability`, or
`unavailable`; `observedAt`, `ageSeconds`, `capabilities`, and `total` are
present only when an observation exists. Utilization values are `null` when
not available.

Text output:

```text
Queue (approximate guidance, observed 12 s ago):
  person-detection: 5
  Total: 5
CPU: 37.5 %
Memory: 8.0 GiB of 32.0 GiB
```

A stale observation reads `observed 3 min ago, stale`; the other states read
`Queue: no information yet`, `Queue: no capability assigned`, or
`Queue: unavailable (<reason>)`; unreadable values read `not available`.

## 3. Status window

A "Waiting work (approximate)" section with the per-capability counts, the
total, the observation age, and a stale marker; a "Host" section with CPU and
memory. Same states and wording as the CLI.

## 4. Status report (unchanged)

`POST /api/v1/analyst-managers/self/status` carries no queue depth or
utilization field; a test asserts it.
