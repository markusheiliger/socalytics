# Data Model: Analysis Result Read for Jobs

No new tables. The read is served from existing records:

| Source | Use |
| --- | --- |
| `analysis_logical_job_inputs` (`InputSnapshotEntry`: `UpstreamNodeKey`, `EdgeKind`, `Status`, `AcceptedResultIds`) | The job's upstream dependencies and the exact accepted results it was given |
| `analysis_accepted_results` + `analysis_accepted_result_manifests` (spec 3) | Result identity, scope, manifest reference and checksum, artifacts |
| `analysis_logical_jobs` of the upstream node | Per-segment state for coverage of non-accepted segments |
| Object storage `results/{runId}/{logicalJobId}/{attemptId}/` | Manifest and artifact bytes, read through grants |

## Coverage state mapping (research.md § R2)

| Situation | Coverage state |
| --- | --- |
| A snapshot accepted result covers the segment | `accepted` |
| Snapshot status `not-applicable` | `skipped` |
| Upstream logical job for the segment is `Blocked` | `blocked` |
| Upstream logical job for the segment is `Failed` | `failed` |
| Anything else, including results accepted after the snapshot | `missing` |

## Read query

| Field | Rule |
| --- | --- |
| `fencingToken` | Required; must equal the current attempt's token |
| `capabilityId`, `capabilityVersion`, `schemaVersion` | Required; must match one upstream dependency of the job |
| `segmentIds` or `matchStartMs`/`matchEndMs` | Optional; mutually exclusive; range half-open |
| `pageSize` | 1 to `Analysis:Reads:MaxPageSize` (development value 100) |
| `continuationToken` | Opaque; from the previous page |

## Configuration

| Setting | Development and test value | Rule |
| --- | --- | --- |
| `Analysis:Reads:GrantLifetime` | `00:10:00` | > 0, ≤ 1 hour; grants also never outlive the lease |
| `Analysis:Reads:MaxPageSize` | `100` | 1–1000 |
| Manager `Work:Staging:MaxBytesPerAttempt` | `2147483648` (2 GiB) | ≥ 1 MiB |

## Staged inputs (Manager, per attempt)

```text
{attemptDir}/inputs/{capabilityId}/coverage.json
{attemptDir}/inputs/{capabilityId}/{acceptedResultId}/manifest.json
{attemptDir}/inputs/{capabilityId}/{acceptedResultId}/{artifact name}
```

Deleted with `{attemptDir}` when the attempt ends; all attempt directories are
deleted at Manager start.

## Staging failures

| Local reason (Manager diagnostics and logs) | Completion `failure.category` (attempt-completion contract 1.0.0) |
| --- | --- |
| `staging-digest-mismatch` | `input-unavailable` |
| `staging-storage-unavailable` | `input-unavailable` |
| `staging-read-refused` | `input-unavailable` |
| `staging-limit-exceeded` | `resource-exhausted` |
| `staging-disk-full` | `resource-exhausted` |

A read refused as obsolete stops the attempt without a completion.
