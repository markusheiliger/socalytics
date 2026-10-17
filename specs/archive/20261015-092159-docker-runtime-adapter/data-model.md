# Data Model: Docker Desktop Runtime Adapter for Analyst Execution

This feature adds no platform table. The platform derives everything it serves
from the resolved default workflow and its protected registry configuration.
The Manager keeps runtime facts in memory and one new local file.

## Manager configuration (`Runtime` section)

| Key | Type | Default | Rule |
| --- | --- | --- | --- |
| `Runtime:Profile` | string | none | `docker-desktop` selects this adapter (research R2). |
| `Runtime:Docker:Endpoint` | URI | `npipe://./pipe/docker_engine` | `npipe://` on Windows; `unix://` only in Development and Test; every other scheme refused. |
| `Runtime:Docker:Cpus` | decimal > 0 | host CPU count | Upper bound for all attempts together; preflight checks it against the runtime's `NCPU`. |
| `Runtime:Docker:MemoryBytes` | long > 0 | 75% of runtime `MemTotal` | Upper bound for all attempts together; preflight checks it against `MemTotal`. |
| `Runtime:Docker:CapabilityAllowlist` | list of `id@version` | empty (no narrowing) | Narrows the offer (FR-024). |
| `Runtime:Docker:ImageCacheBudgetBytes` | long > 0 | 20 GiB | Image cache budget (FR-027). |
| `Runtime:Docker:ImageCacheMaxIdle` | TimeSpan > 0 | 14 days | Unused period before removal (FR-027). |
| `Runtime:ProbeImage` | digest reference | none | Existing key; a digest inside the stamp's registry prefix used for the registry and image-platform checks. |
| `Runtime:CompatibilityListPath` | file path | none | Development and Test only: extra compatibility entries for manual verification; refused elsewhere. |

## Runtime probe (in memory)

`RuntimeProbe` (existing record) filled from `/version` and `/info`
(research R3), plus the offer decision:

| Field | Meaning |
| --- | --- |
| `Offered` | Capabilities (`id`, `version`) the Manager offers after filtering. |
| `NotOffered` | Each remaining workflow capability with its reason: `accelerator-required`, `exceeds-host-limits`, or `excluded-by-allowlist`. |

## Registry pull credential (in memory)

| Field | Meaning |
| --- | --- |
| `Registry` | Registry host the credential is for (the stamp's `RegistryHost`). |
| `Repositories` | Repositories under the stamp's prefix the token may pull. |
| `Kind` | `bearer` or `anonymous`. |
| `Token` | Bearer token (absent for `anonymous`); never logged, persisted, or passed to a container. |
| `ExpiresAt` | Renewal is due before this time (renew at 80% of the lifetime). |

## Analyst container (runtime object)

One container per Execution Attempt; created from the pinned image digest with
the settings in [contracts/analyst-container.md](contracts/analyst-container.md).

| Label | Value |
| --- | --- |
| `io.socalytics.manager` | The Manager's registration id. |
| `io.socalytics.logical-job` | The Logical Job id. |
| `io.socalytics.attempt` | The Execution Attempt id. |

State transitions as observed by the Manager:

```text
pulling → created → running → exited(code, oomKilled) → removed
              ↘ stopped (timeout, fencing, revocation, drain) → removed
```

## Attempt directory (host)

```text
{UpstreamStager.AttemptsRoot}/{attemptId}/   # %TEMP%/socalytics-analyst-attempts/{attemptId}
  job.json        # the Logical Job message (analyst-job v1), read-only in the container
  inputs/         # staged by UpstreamStager (AttemptLaunch.InputsDirectory), read-only in the container
  result/         # the result directory (moved here from socalytics-analyst-results), read-write; manifest.json + artifacts
  scratch/        # read-write working space
  container.log   # bounded log for the operator; never sent to the platform
```

The whole directory is deleted when the attempt ends and by reconciliation.

## Image cache record (`{StateDirectory}/image-cache.json`)

```json
{
  "version": 1,
  "images": [
    {
      "digest": "sha256:…",
      "repository": "ghcr.io/<owner>/<repo>/<analyst>",
      "sizeBytes": 123456789,
      "pulledAt": "2026-10-15T09:30:00Z",
      "lastUsedAt": "2026-10-15T10:05:00Z"
    }
  ]
}
```

- A digest is recorded only when this Manager pulled it (it was absent before
  the pull).
- Removal order: images not used by an active attempt, least recently used
  first, until the recorded total is within the budget; plus every image idle
  longer than the configured period.
- A failed removal stays recorded and is retried by the next cache pass.

## Platform responses (no persistence)

- **Registry credentials**: see
  [contracts/registry-credentials.md](contracts/registry-credentials.md).
- **Workflow capabilities**: see
  [contracts/workflow-capabilities.md](contracts/workflow-capabilities.md).
