# Data Model: Analyst Manager Queue Depth and Host Utilization

No platform storage and no migration. Everything below is transient.

## Queue Depth Observation (platform response)

| Field | Type | Rules |
| --- | --- | --- |
| `observedAt` | timestamp (UTC) | platform time of the consumer read |
| `capabilities` | array of `{ id, waiting }` | only requested ids whose consumer the resolved default workflow defines; ordered by `id` |
| `capabilities[].id` | string | `[a-z0-9][a-z0-9.-]{0,63}` |
| `capabilities[].waiting` | integer ≥ 0 | JetStream consumer `NumPending` for `analyst-<id>` |
| `total` | integer ≥ 0 | sum of `waiting` |

Request: 1 to 64 `capability` query values; duplicates ignored; invalid ids
refused with `400` (`capability`: "invalid").

## Queue Depth State (Manager, in memory)

| Field | Meaning |
| --- | --- |
| `State` | `Available`, `Stale`, `NotYetAvailable`, `NoCapability`, `Unavailable` (request failed before any observation) |
| `ObservedAt` | platform `observedAt` of the latest observation |
| `ReceivedAt` | Manager clock when the latest observation arrived |
| `Capabilities`, `Total` | from the latest observation |
| `LastError` | reason of the latest failed request, if any |

Transitions:

- start, or registration becomes active → `NotYetAvailable`
- no consumer assignment → `NoCapability`
- successful response → `Available` (replaces the observation)
- failed request → `Stale` if an observation exists, else `Unavailable`
- `now − ReceivedAt > 2 × status check interval` → `Stale`
- revoked or unregistered → cleared (status shows no queue information)

## Host Utilization Sample (Manager, in memory)

| Field | Type | Rules |
| --- | --- | --- |
| `SampledAt` | timestamp | Manager clock |
| `CpuPercent` | decimal 0–100 or null | busy share of all CPUs since the previous reading; null on the first reading or when unreadable |
| `MemoryUsedBytes` | integer or null | Linux `MemTotal − MemAvailable`; Windows `ullTotalPhys − ullAvailPhys`; macOS (active + wired + compressed pages) × page size |
| `MemoryTotalBytes` | integer or null | physical memory |

Sampling period: 5 seconds (not configurable, not production-governed).

## Status Snapshot (Manager, `ManagerStatusSnapshot`)

Gains `QueueDepth` (the state above) and `HostUtilization` (the latest
sample). The status report to the platform is unchanged.
